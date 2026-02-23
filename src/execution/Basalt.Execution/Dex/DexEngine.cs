using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex.Math;
using Basalt.Storage;

namespace Basalt.Execution.Dex;

/// <summary>
/// Protocol-native DEX engine implementing constant-product AMM with limit orders.
/// All operations execute directly against <see cref="IStateDatabase"/> — no smart contract
/// dispatch overhead, no reentrancy risks. The chain IS the exchange.
///
/// For native BST token pairs, balance transfers happen via direct account state modification.
/// For BST-20 token pairs, the engine delegates to the contract runtime.
///
/// This engine handles:
/// <list type="bullet">
/// <item><description>Pool creation with configurable fee tiers</description></item>
/// <item><description>Liquidity add/remove with geometric mean share calculation</description></item>
/// <item><description>Single swaps via constant-product formula</description></item>
/// <item><description>Limit order placement and cancellation</description></item>
/// </list>
///
/// Batch auction settlement is handled by <see cref="BatchAuctionSolver"/> and
/// <see cref="BatchSettlementExecutor"/> (Phase B).
/// </summary>
public sealed class DexEngine
{
    private readonly DexState _state;

    /// <summary>
    /// Creates a new DEX engine backed by the given state.
    /// </summary>
    /// <param name="state">The DEX state reader/writer.</param>
    public DexEngine(DexState state)
    {
        _state = state;
    }

    /// <summary>
    /// Create a new liquidity pool for a token pair with a specified fee tier.
    /// Tokens are sorted canonically (lower address = token0).
    /// </summary>
    /// <param name="sender">The address creating the pool.</param>
    /// <param name="tokenA">One of the tokens in the pair.</param>
    /// <param name="tokenB">The other token in the pair.</param>
    /// <param name="feeBps">Swap fee in basis points (must be in AllowedFeeTiers).</param>
    /// <returns>A <see cref="DexResult"/> with the new pool ID on success.</returns>
    public DexResult CreatePool(Address sender, Address tokenA, Address tokenB, uint feeBps)
    {
        // Validate tokens are different
        if (tokenA == tokenB)
            return DexResult.Error(BasaltErrorCode.DexInvalidPair, "Identical tokens");

        // Validate fee tier
        if (!Array.Exists(DexLibrary.AllowedFeeTiers, f => f == feeBps))
            return DexResult.Error(BasaltErrorCode.DexInvalidFeeTier, $"Fee tier {feeBps} not allowed");

        // Sort tokens canonically
        var (token0, token1) = SortTokens(tokenA, tokenB);

        // Check pool doesn't already exist
        if (_state.LookupPool(token0, token1, feeBps) != null)
            return DexResult.Error(BasaltErrorCode.DexPoolAlreadyExists, "Pool already exists for this pair and fee tier");

        var poolId = _state.CreatePool(token0, token1, feeBps);

        var logs = new List<EventLog>
        {
            MakeEventLog("PoolCreated", poolId, token0, token1, feeBps),
        };

        return DexResult.PoolCreated(poolId, logs);
    }

    /// <summary>
    /// Add liquidity to an existing pool. The actual amounts deposited may differ from the
    /// desired amounts to maintain the pool's price ratio.
    /// </summary>
    /// <param name="sender">The liquidity provider.</param>
    /// <param name="poolId">The target pool.</param>
    /// <param name="amount0Desired">Desired amount of token0 to deposit.</param>
    /// <param name="amount1Desired">Desired amount of token1 to deposit.</param>
    /// <param name="amount0Min">Minimum acceptable amount of token0.</param>
    /// <param name="amount1Min">Minimum acceptable amount of token1.</param>
    /// <param name="stateDb">The state database for balance transfers.</param>
    /// <returns>A <see cref="DexResult"/> with actual deposit amounts and minted LP shares.</returns>
    public DexResult AddLiquidity(
        Address sender, ulong poolId,
        UInt256 amount0Desired, UInt256 amount1Desired,
        UInt256 amount0Min, UInt256 amount1Min,
        IStateDatabase stateDb)
    {
        var meta = _state.GetPoolMetadata(poolId);
        if (meta == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool does not exist");

        var reserves = _state.GetPoolReserves(poolId);
        if (reserves == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool reserves not found");

        var res = reserves.Value;

        // Compute optimal deposit amounts
        UInt256 amount0;
        UInt256 amount1;
        UInt256 shares;

        if (res.Reserve0.IsZero && res.Reserve1.IsZero)
        {
            // First deposit — use desired amounts directly
            amount0 = amount0Desired;
            amount1 = amount1Desired;
            shares = DexLibrary.ComputeInitialLiquidity(amount0, amount1);

            // Lock MINIMUM_LIQUIDITY permanently by adding it to total supply
            // but not crediting it to any address
            res.TotalSupply = DexLibrary.MinimumLiquidity;
        }
        else
        {
            // Subsequent deposit — maintain ratio
            var amount1Optimal = DexLibrary.Quote(amount0Desired, res.Reserve0, res.Reserve1);
            if (amount1Optimal <= amount1Desired)
            {
                if (amount1Optimal < amount1Min)
                    return DexResult.Error(BasaltErrorCode.DexSlippageExceeded, "Insufficient token1 amount");
                amount0 = amount0Desired;
                amount1 = amount1Optimal;
            }
            else
            {
                var amount0Optimal = DexLibrary.Quote(amount1Desired, res.Reserve1, res.Reserve0);
                if (amount0Optimal > amount0Desired)
                    return DexResult.Error(BasaltErrorCode.DexSlippageExceeded, "Insufficient amounts");
                if (amount0Optimal < amount0Min)
                    return DexResult.Error(BasaltErrorCode.DexSlippageExceeded, "Insufficient token0 amount");
                amount0 = amount0Optimal;
                amount1 = amount1Desired;
            }

            shares = DexLibrary.ComputeLiquidity(amount0, amount1, res.Reserve0, res.Reserve1, res.TotalSupply);
        }

        if (shares.IsZero)
            return DexResult.Error(BasaltErrorCode.DexInsufficientLiquidity, "Zero LP shares minted");

        // Transfer tokens from sender to DEX
        var transferResult = TransferTokensIn(stateDb, sender, meta.Value.Token0, meta.Value.Token1, amount0, amount1);
        if (!transferResult.Success)
            return transferResult;

        // Update reserves
        res.Reserve0 = UInt256.CheckedAdd(res.Reserve0, amount0);
        res.Reserve1 = UInt256.CheckedAdd(res.Reserve1, amount1);
        res.TotalSupply = UInt256.CheckedAdd(res.TotalSupply, shares);
        res.KLast = UInt256.CheckedMul(res.Reserve0, res.Reserve1);
        _state.SetPoolReserves(poolId, res);

        // Credit LP shares to sender
        var currentLp = _state.GetLpBalance(poolId, sender);
        _state.SetLpBalance(poolId, sender, UInt256.CheckedAdd(currentLp, shares));

        var logs = new List<EventLog>
        {
            MakeEventLog("LiquidityAdded", poolId, sender, amount0, amount1, shares),
        };

        return DexResult.LiquidityAdded(poolId, amount0, amount1, shares, logs);
    }

    /// <summary>
    /// Remove liquidity from a pool by burning LP shares.
    /// Returns proportional amounts of both tokens.
    /// </summary>
    /// <param name="sender">The LP token holder.</param>
    /// <param name="poolId">The target pool.</param>
    /// <param name="sharesToBurn">Number of LP shares to burn.</param>
    /// <param name="amount0Min">Minimum acceptable amount of token0 to receive.</param>
    /// <param name="amount1Min">Minimum acceptable amount of token1 to receive.</param>
    /// <param name="stateDb">The state database for balance transfers.</param>
    /// <returns>A <see cref="DexResult"/> with the amounts returned.</returns>
    public DexResult RemoveLiquidity(
        Address sender, ulong poolId,
        UInt256 sharesToBurn, UInt256 amount0Min, UInt256 amount1Min,
        IStateDatabase stateDb)
    {
        var meta = _state.GetPoolMetadata(poolId);
        if (meta == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool does not exist");

        var reserves = _state.GetPoolReserves(poolId);
        if (reserves == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool reserves not found");

        var res = reserves.Value;

        // Check LP balance
        var lpBalance = _state.GetLpBalance(poolId, sender);
        if (lpBalance < sharesToBurn)
            return DexResult.Error(BasaltErrorCode.DexInsufficientLiquidity, "Insufficient LP shares");

        // Compute proportional amounts:
        // amount0 = sharesToBurn * reserve0 / totalSupply
        // amount1 = sharesToBurn * reserve1 / totalSupply
        var amount0 = FullMath.MulDiv(sharesToBurn, res.Reserve0, res.TotalSupply);
        var amount1 = FullMath.MulDiv(sharesToBurn, res.Reserve1, res.TotalSupply);

        if (amount0 < amount0Min)
            return DexResult.Error(BasaltErrorCode.DexSlippageExceeded, "Insufficient token0 output");
        if (amount1 < amount1Min)
            return DexResult.Error(BasaltErrorCode.DexSlippageExceeded, "Insufficient token1 output");

        // Burn LP shares
        _state.SetLpBalance(poolId, sender, lpBalance - sharesToBurn);

        // Update reserves
        res.Reserve0 = res.Reserve0 - amount0;
        res.Reserve1 = res.Reserve1 - amount1;
        res.TotalSupply = res.TotalSupply - sharesToBurn;
        res.KLast = UInt256.CheckedMul(res.Reserve0, res.Reserve1);
        _state.SetPoolReserves(poolId, res);

        // Transfer tokens from DEX to sender
        TransferTokensOut(stateDb, sender, meta.Value.Token0, meta.Value.Token1, amount0, amount1);

        var logs = new List<EventLog>
        {
            MakeEventLog("LiquidityRemoved", poolId, sender, amount0, amount1, sharesToBurn),
        };

        return DexResult.LiquidityRemoved(poolId, amount0, amount1, sharesToBurn, logs);
    }

    /// <summary>
    /// Execute a single swap through a pool's constant-product AMM.
    /// This is used for immediate swaps and as the residual router in batch settlements.
    /// </summary>
    /// <param name="sender">The address executing the swap.</param>
    /// <param name="poolId">The target pool.</param>
    /// <param name="tokenIn">The input token address.</param>
    /// <param name="amountIn">The input amount.</param>
    /// <param name="minAmountOut">Minimum acceptable output (slippage protection).</param>
    /// <param name="stateDb">The state database for balance transfers.</param>
    /// <returns>A <see cref="DexResult"/> with the output amount.</returns>
    public DexResult ExecuteSwap(
        Address sender, ulong poolId,
        Address tokenIn, UInt256 amountIn, UInt256 minAmountOut,
        IStateDatabase stateDb)
    {
        var meta = _state.GetPoolMetadata(poolId);
        if (meta == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool does not exist");

        var reserves = _state.GetPoolReserves(poolId);
        if (reserves == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool reserves not found");

        var res = reserves.Value;
        var m = meta.Value;

        // Determine swap direction
        bool isToken0In = tokenIn == m.Token0;
        if (!isToken0In && tokenIn != m.Token1)
            return DexResult.Error(BasaltErrorCode.DexInvalidPair, "Token not in pool");

        var reserveIn = isToken0In ? res.Reserve0 : res.Reserve1;
        var reserveOut = isToken0In ? res.Reserve1 : res.Reserve0;

        if (reserveIn.IsZero || reserveOut.IsZero)
            return DexResult.Error(BasaltErrorCode.DexInsufficientLiquidity, "Pool has no liquidity");

        // Compute output
        var amountOut = DexLibrary.GetAmountOut(amountIn, reserveIn, reserveOut, m.FeeBps);

        if (amountOut < minAmountOut)
            return DexResult.Error(BasaltErrorCode.DexSlippageExceeded, "Insufficient output amount");

        // Transfer input from sender to DEX
        TransferSingleTokenIn(stateDb, sender, tokenIn, amountIn);

        // Transfer output from DEX to sender
        var tokenOut = isToken0In ? m.Token1 : m.Token0;
        TransferSingleTokenOut(stateDb, sender, tokenOut, amountOut);

        // Update reserves
        if (isToken0In)
        {
            res.Reserve0 = UInt256.CheckedAdd(res.Reserve0, amountIn);
            res.Reserve1 = res.Reserve1 - amountOut;
        }
        else
        {
            res.Reserve0 = res.Reserve0 - amountOut;
            res.Reserve1 = UInt256.CheckedAdd(res.Reserve1, amountIn);
        }
        res.KLast = UInt256.CheckedMul(res.Reserve0, res.Reserve1);
        _state.SetPoolReserves(poolId, res);

        var logs = new List<EventLog>
        {
            MakeSwapEventLog(poolId, sender, tokenIn, amountIn, tokenOut, amountOut),
        };

        return DexResult.SwapExecuted(poolId, amountOut, logs);
    }

    /// <summary>
    /// Place a persistent limit order in the DEX order book.
    /// The order's input tokens are escrowed (transferred to the DEX address).
    /// </summary>
    /// <param name="sender">The order placer.</param>
    /// <param name="poolId">The target pool.</param>
    /// <param name="price">Limit price (scaled by 2^128).</param>
    /// <param name="amount">Amount of input tokens.</param>
    /// <param name="isBuy">True for buy orders (buying token0 with token1).</param>
    /// <param name="expiryBlock">Block number when the order expires.</param>
    /// <param name="stateDb">The state database for token escrow.</param>
    /// <returns>A <see cref="DexResult"/> with the assigned order ID.</returns>
    public DexResult PlaceOrder(
        Address sender, ulong poolId,
        UInt256 price, UInt256 amount, bool isBuy, ulong expiryBlock,
        IStateDatabase stateDb)
    {
        var meta = _state.GetPoolMetadata(poolId);
        if (meta == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool does not exist");

        if (amount.IsZero)
            return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Order amount is zero");
        if (price.IsZero)
            return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Order price is zero");

        // Escrow input tokens: buy orders escrow token1, sell orders escrow token0
        var escrowToken = isBuy ? meta.Value.Token1 : meta.Value.Token0;
        TransferSingleTokenIn(stateDb, sender, escrowToken, amount);

        var orderId = _state.PlaceOrder(sender, poolId, price, amount, isBuy, expiryBlock);

        var logs = new List<EventLog>
        {
            MakeEventLog("OrderPlaced", orderId, sender, poolId, price, amount, isBuy),
        };

        return DexResult.OrderPlaced(orderId, logs);
    }

    /// <summary>
    /// Cancel an existing limit order and return escrowed tokens to the owner.
    /// Only the order owner can cancel.
    /// </summary>
    /// <param name="sender">The address attempting to cancel (must be order owner).</param>
    /// <param name="orderId">The order to cancel.</param>
    /// <param name="stateDb">The state database for token return.</param>
    /// <returns>A <see cref="DexResult"/> confirming cancellation.</returns>
    public DexResult CancelOrder(Address sender, ulong orderId, IStateDatabase stateDb)
    {
        var order = _state.GetOrder(orderId);
        if (order == null)
            return DexResult.Error(BasaltErrorCode.DexOrderNotFound, "Order does not exist");

        if (order.Value.Owner != sender)
            return DexResult.Error(BasaltErrorCode.DexUnauthorized, "Only order owner can cancel");

        var meta = _state.GetPoolMetadata(order.Value.PoolId);
        if (meta == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool does not exist");

        // Return escrowed tokens
        var escrowToken = order.Value.IsBuy ? meta.Value.Token1 : meta.Value.Token0;
        TransferSingleTokenOut(stateDb, sender, escrowToken, order.Value.Amount);

        _state.DeleteOrder(orderId);

        var logs = new List<EventLog>
        {
            MakeEventLog("OrderCanceled", orderId, sender),
        };

        return DexResult.OrderCanceled(orderId, logs);
    }

    // ────────── Token Transfers ──────────
    //
    // For native BST (Address.Zero), transfers happen via direct account balance modification.
    // This is the same pattern used by ExecuteTransfer in TransactionExecutor.
    // BST-20 token transfers would go through the contract runtime, but for Phase A we handle
    // native BST only. BST-20 support is added in Phase D integration.

    private static DexResult TransferTokensIn(
        IStateDatabase stateDb, Address sender,
        Address token0, Address token1,
        UInt256 amount0, UInt256 amount1)
    {
        // Debit sender for both tokens
        if (!amount0.IsZero)
        {
            var result = DebitAccount(stateDb, sender, token0, amount0);
            if (!result.Success) return result;
            CreditDexAccount(stateDb, token0, amount0);
        }
        if (!amount1.IsZero)
        {
            var result = DebitAccount(stateDb, sender, token1, amount1);
            if (!result.Success) return result;
            CreditDexAccount(stateDb, token1, amount1);
        }
        return DexResult.PoolCreated(0); // dummy success
    }

    private static void TransferTokensOut(
        IStateDatabase stateDb, Address recipient,
        Address token0, Address token1,
        UInt256 amount0, UInt256 amount1)
    {
        if (!amount0.IsZero)
        {
            DebitDexAccount(stateDb, token0, amount0);
            CreditAccount(stateDb, recipient, token0, amount0);
        }
        if (!amount1.IsZero)
        {
            DebitDexAccount(stateDb, token1, amount1);
            CreditAccount(stateDb, recipient, token1, amount1);
        }
    }

    internal static void TransferSingleTokenIn(IStateDatabase stateDb, Address sender, Address token, UInt256 amount)
    {
        if (amount.IsZero) return;
        if (token == Address.Zero)
        {
            // Native BST: debit sender, credit DEX address
            var senderState = stateDb.GetAccount(sender) ?? AccountState.Empty;
            stateDb.SetAccount(sender, senderState with
            {
                Balance = UInt256.CheckedSub(senderState.Balance, amount),
            });
            var dexState = stateDb.GetAccount(DexState.DexAddress) ?? AccountState.Empty;
            stateDb.SetAccount(DexState.DexAddress, dexState with
            {
                Balance = UInt256.CheckedAdd(dexState.Balance, amount),
            });
        }
        // BST-20: would call contract runtime TransferFrom here (Phase D)
    }

    internal static void TransferSingleTokenOut(IStateDatabase stateDb, Address recipient, Address token, UInt256 amount)
    {
        if (amount.IsZero) return;
        if (token == Address.Zero)
        {
            // Native BST: debit DEX address, credit recipient
            var dexAcct = stateDb.GetAccount(DexState.DexAddress) ?? AccountState.Empty;
            stateDb.SetAccount(DexState.DexAddress, dexAcct with
            {
                Balance = UInt256.CheckedSub(dexAcct.Balance, amount),
            });
            var recipientState = stateDb.GetAccount(recipient) ?? AccountState.Empty;
            stateDb.SetAccount(recipient, recipientState with
            {
                Balance = UInt256.CheckedAdd(recipientState.Balance, amount),
            });
        }
        // BST-20: would call contract runtime Transfer here (Phase D)
    }

    private static DexResult DebitAccount(IStateDatabase stateDb, Address addr, Address token, UInt256 amount)
    {
        if (token == Address.Zero)
        {
            var acct = stateDb.GetAccount(addr) ?? AccountState.Empty;
            if (acct.Balance < amount)
                return DexResult.Error(BasaltErrorCode.InsufficientBalance, "Insufficient balance for DEX deposit");
            stateDb.SetAccount(addr, acct with { Balance = UInt256.CheckedSub(acct.Balance, amount) });
        }
        return DexResult.PoolCreated(0); // success sentinel
    }

    private static void CreditAccount(IStateDatabase stateDb, Address addr, Address token, UInt256 amount)
    {
        if (token == Address.Zero)
        {
            var acct = stateDb.GetAccount(addr) ?? AccountState.Empty;
            stateDb.SetAccount(addr, acct with { Balance = UInt256.CheckedAdd(acct.Balance, amount) });
        }
    }

    private static void CreditDexAccount(IStateDatabase stateDb, Address token, UInt256 amount)
    {
        if (token == Address.Zero)
        {
            var dex = stateDb.GetAccount(DexState.DexAddress) ?? AccountState.Empty;
            stateDb.SetAccount(DexState.DexAddress, dex with
            {
                Balance = UInt256.CheckedAdd(dex.Balance, amount),
            });
        }
    }

    private static void DebitDexAccount(IStateDatabase stateDb, Address token, UInt256 amount)
    {
        if (token == Address.Zero)
        {
            var dex = stateDb.GetAccount(DexState.DexAddress) ?? AccountState.Empty;
            stateDb.SetAccount(DexState.DexAddress, dex with
            {
                Balance = UInt256.CheckedSub(dex.Balance, amount),
            });
        }
    }

    // ────────── Helpers ──────────

    /// <summary>
    /// Sort two token addresses into canonical order (lower address first).
    /// </summary>
    internal static (Address token0, Address token1) SortTokens(Address a, Address b)
    {
        return a.CompareTo(b) < 0 ? (a, b) : (b, a);
    }

    /// <summary>
    /// Create an event log for DEX operations.
    /// Event signature is BLAKE3("Dex." + eventName).
    /// </summary>
    private static EventLog MakeEventLog(string eventName, params object[] args)
    {
        var sigBytes = System.Text.Encoding.UTF8.GetBytes("Dex." + eventName);
        var eventSig = Blake3Hasher.Hash(sigBytes);

        // Serialize args to data payload
        var data = SerializeEventArgs(args);

        return new EventLog
        {
            Contract = DexState.DexAddress,
            EventSignature = eventSig,
            Topics = [],
            Data = data,
        };
    }

    private static EventLog MakeSwapEventLog(
        ulong poolId, Address sender, Address tokenIn,
        UInt256 amountIn, Address tokenOut, UInt256 amountOut)
    {
        var sigBytes = System.Text.Encoding.UTF8.GetBytes("Dex.Swap");
        var eventSig = Blake3Hasher.Hash(sigBytes);

        // Pack: [8B poolId][20B sender][20B tokenIn][32B amountIn][20B tokenOut][32B amountOut]
        var data = new byte[8 + 20 + 20 + 32 + 20 + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0, 8), poolId);
        sender.WriteTo(data.AsSpan(8, 20));
        tokenIn.WriteTo(data.AsSpan(28, 20));
        amountIn.WriteTo(data.AsSpan(48, 32));
        tokenOut.WriteTo(data.AsSpan(80, 20));
        amountOut.WriteTo(data.AsSpan(100, 32));

        return new EventLog
        {
            Contract = DexState.DexAddress,
            EventSignature = eventSig,
            Topics = [],
            Data = data,
        };
    }

    private static byte[] SerializeEventArgs(object[] args)
    {
        using var ms = new System.IO.MemoryStream();
        foreach (var arg in args)
        {
            switch (arg)
            {
                case ulong u:
                {
                    Span<byte> buf = stackalloc byte[8];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buf, u);
                    ms.Write(buf);
                    break;
                }
                case uint u:
                {
                    Span<byte> buf = stackalloc byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, u);
                    ms.Write(buf);
                    break;
                }
                case Address a:
                {
                    Span<byte> buf = stackalloc byte[Address.Size];
                    a.WriteTo(buf);
                    ms.Write(buf);
                    break;
                }
                case UInt256 v:
                {
                    ms.Write(v.ToArray());
                    break;
                }
                case bool b:
                    ms.WriteByte(b ? (byte)1 : (byte)0);
                    break;
            }
        }
        return ms.ToArray();
    }
}
