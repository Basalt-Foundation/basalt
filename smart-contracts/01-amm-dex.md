# Automated Market Maker (AMM DEX)

## Category

Decentralized Finance (DeFi) -- Core Infrastructure

## Summary

A constant-product (x * y = k) automated market maker enabling permissionless token swaps between any BST-20 token pair on the Basalt network. Liquidity providers deposit token pairs into pools and receive LP tokens (BST-20) representing their proportional share. This contract serves as the foundational trading primitive for the entire Basalt DeFi ecosystem.

## Why It's Useful

- **Permissionless Liquidity**: Anyone can create a trading pair or provide liquidity without gatekeepers, enabling a free market for token discovery and price formation.
- **Foundation for DeFi Composability**: Nearly every DeFi protocol depends on swap functionality -- yield aggregators need to swap harvested rewards, liquidation bots need to convert collateral, and stablecoin protocols need price references.
- **Passive Income for LPs**: Liquidity providers earn trading fees proportional to their share of the pool, creating a decentralized market-making income stream.
- **On-Chain Price Oracle**: Pool reserves serve as a time-weighted average price (TWAP) oracle for other contracts, reducing reliance on external oracle infrastructure.
- **Bootstrap New Token Markets**: New projects launching on Basalt can immediately create liquid markets without needing centralized exchange listings.

## Key Features

- **Pool Factory**: Create new trading pair pools permissionlessly. Each pool is uniquely identified by the sorted pair of token addresses.
- **Constant-Product Invariant**: x * y = k ensures that trades always have a deterministic price impact based on pool depth.
- **LP Token Minting/Burning**: Depositing liquidity mints BST-20 LP tokens; withdrawing burns them and returns proportional reserves.
- **Configurable Fee Per Pool**: Pool creators set the swap fee (e.g., 0.3% default, with options for 0.05%, 0.1%, 1%). Fee accrues to LP token holders.
- **Protocol Fee Switch**: Governance-controllable protocol fee (fraction of swap fee) directed to the treasury or staking rewards.
- **TWAP Oracle**: Cumulative price accumulators updated on each swap, enabling other contracts to compute time-weighted average prices.
- **Flash Swaps**: Borrow tokens from a pool and repay within the same transaction (with fee), enabling arbitrage and liquidation without upfront capital.
- **Minimum Liquidity Lock**: First liquidity deposit locks a small amount of LP tokens to prevent manipulation of empty pools.
- **Multi-Hop Routing**: A router contract computes optimal paths across multiple pools for swaps without a direct pair.
- **Slippage Protection**: Users specify minimum output amounts; transactions revert if price moves beyond tolerance.

## Basalt-Specific Advantages

- **AOT-Compiled Execution**: Basalt SDK contracts compile ahead-of-time to native code, eliminating interpreter overhead. Swap execution is significantly faster than EVM-based AMMs, reducing gas costs and enabling higher throughput during peak trading.
- **ZK Compliance Gating**: Pool creation or large swaps can optionally require ZK compliance proofs via the SchemaRegistry and IssuerRegistry, enabling regulated token pairs (e.g., security tokens) to have compliant AMM pools without revealing user identity on-chain.
- **BST-3525 SFT LP Positions**: Instead of fungible LP tokens, liquidity positions can be represented as BST-3525 semi-fungible tokens with metadata encoding the price range, entry price, and fee tier -- enabling richer position management and secondary markets for LP positions.
- **Confidential Swap Amounts**: Pedersen commitment support allows swap amounts to be hidden while still proving the constant-product invariant holds via range proofs, protecting traders from front-running and sandwich attacks.
- **Ed25519 Signature Efficiency**: Transaction signing with Ed25519 is faster than ECDSA (secp256k1), reducing the overhead of high-frequency trading bots and arbitrageurs interacting with the AMM.
- **BLS Aggregate Signatures**: Multi-party pool governance actions (e.g., fee changes on managed pools) can use BLS signature aggregation, reducing on-chain verification cost for DAO-managed pools.

## Token Standards Used

- **BST-20**: LP tokens minted to liquidity providers; all tradeable tokens in the pool are BST-20.
- **BST-3525 (SFT)**: Optional advanced LP position representation with slot-level metadata (fee tier, price range, accrued fees).
- **BST-4626 (Vault)**: LP token deposits into yield aggregators can wrap via BST-4626 for composability.

## Integration Points

- **BNS (Basalt Name Service)**: Pools and the router can be registered under human-readable BNS names (e.g., `amm.basalt`, `bst-wbslt-pool.amm.basalt`).
- **Governance**: Protocol fee switch activation, fee tier additions, and emergency pause functionality governed by the Governance contract (0x0102).
- **Escrow**: Flash swap repayment verification and multi-step swap settlement can use Escrow for atomicity guarantees.
- **SchemaRegistry / IssuerRegistry**: ZK compliance proof verification for regulated token pools.
- **BridgeETH**: Bridged ERC-20 tokens (via BridgeETH at 0x...1008) can be immediately traded in AMM pools, providing liquidity for cross-chain assets.
- **StakingPool**: Protocol fee revenue can be directed to StakingPool for distribution to BST stakers.

## Technical Sketch

```csharp
// ============================================================
// AmmFactory -- Pool creation and registry
// ============================================================

[BasaltContract(TypeId = 0x0200)]
public partial class AmmFactory : SdkContract
{
    // fee tier => allowed (governance-controlled)
    private StorageMap<uint, bool> _allowedFeeTiers;

    // sorted(tokenA, tokenB) hash => pool address
    private StorageMap<Hash256, Address> _pools;

    // protocol fee numerator (e.g., 5 = 1/5 of swap fee goes to protocol)
    private StorageValue<uint> _protocolFeeDivisor;

    // treasury address receiving protocol fees
    private StorageValue<Address> _protocolFeeRecipient;

    /// <summary>
    /// Creates a new liquidity pool for a token pair.
    /// Reverts if the pool already exists or the fee tier is not allowed.
    /// </summary>
    public Address CreatePool(Address tokenA, Address tokenB, uint feeBasisPoints)
    {
        // Sort tokens to ensure canonical pair ordering
        var (token0, token1) = SortTokens(tokenA, tokenB);
        var pairHash = ComputePairHash(token0, token1);

        Require(_pools.Get(pairHash) == Address.Zero, "POOL_EXISTS");
        Require(_allowedFeeTiers.Get(feeBasisPoints), "INVALID_FEE_TIER");

        // Deploy pool contract (deterministic address from pair + fee)
        var poolAddress = DerivePoolAddress(token0, token1, feeBasisPoints);
        _pools.Set(pairHash, poolAddress);

        EmitEvent("PoolCreated", token0, token1, feeBasisPoints, poolAddress);
        return poolAddress;
    }

    /// <summary>
    /// Governance-only: add or remove an allowed fee tier.
    /// </summary>
    public void SetFeeTier(uint feeBasisPoints, bool allowed)
    {
        RequireGovernance();
        _allowedFeeTiers.Set(feeBasisPoints, allowed);
    }

    /// <summary>
    /// Governance-only: set protocol fee divisor (0 = disabled).
    /// </summary>
    public void SetProtocolFee(uint divisor, Address recipient)
    {
        RequireGovernance();
        _protocolFeeDivisor.Set(divisor);
        _protocolFeeRecipient.Set(recipient);
    }

    public Address GetPool(Address tokenA, Address tokenB)
    {
        var (token0, token1) = SortTokens(tokenA, tokenB);
        return _pools.Get(ComputePairHash(token0, token1));
    }

    private (Address, Address) SortTokens(Address a, Address b)
    {
        return a.CompareTo(b) < 0 ? (a, b) : (b, a);
    }

    private Hash256 ComputePairHash(Address token0, Address token1) { /* ... */ }
    private Address DerivePoolAddress(Address t0, Address t1, uint fee) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}


// ============================================================
// AmmPool -- Individual liquidity pool
// ============================================================

[BasaltContract(TypeId = 0x0201)]
public partial class AmmPool : SdkContract
{
    private StorageValue<Address> _token0;
    private StorageValue<Address> _token1;
    private StorageValue<UInt256> _reserve0;
    private StorageValue<UInt256> _reserve1;
    private StorageValue<uint> _feeBasisPoints;

    // TWAP oracle accumulators
    private StorageValue<UInt256> _price0CumulativeLast;
    private StorageValue<UInt256> _price1CumulativeLast;
    private StorageValue<ulong> _blockTimestampLast;

    // LP token (BST-20) total supply and balances
    private StorageValue<UInt256> _totalSupply;
    private StorageMap<Address, UInt256> _lpBalances;

    // Minimum liquidity permanently locked
    private const ulong MINIMUM_LIQUIDITY = 1000;

    /// <summary>
    /// Add liquidity to the pool. Mints LP tokens proportional to
    /// the lesser ratio of deposited amounts to existing reserves.
    /// </summary>
    public UInt256 AddLiquidity(
        UInt256 amount0Desired,
        UInt256 amount1Desired,
        UInt256 amount0Min,
        UInt256 amount1Min,
        Address to,
        ulong deadline)
    {
        Require(Context.BlockNumber <= deadline, "EXPIRED");

        var reserve0 = _reserve0.Get();
        var reserve1 = _reserve1.Get();

        UInt256 amount0;
        UInt256 amount1;

        if (reserve0.IsZero && reserve1.IsZero)
        {
            amount0 = amount0Desired;
            amount1 = amount1Desired;
        }
        else
        {
            var amount1Optimal = Quote(amount0Desired, reserve0, reserve1);
            if (amount1Optimal <= amount1Desired)
            {
                Require(amount1Optimal >= amount1Min, "INSUFFICIENT_1_AMOUNT");
                amount0 = amount0Desired;
                amount1 = amount1Optimal;
            }
            else
            {
                var amount0Optimal = Quote(amount1Desired, reserve1, reserve0);
                Require(amount0Optimal >= amount0Min, "INSUFFICIENT_0_AMOUNT");
                amount0 = amount0Optimal;
                amount1 = amount1Desired;
            }
        }

        // Transfer tokens from sender to pool
        TransferTokenIn(_token0.Get(), Context.Sender, amount0);
        TransferTokenIn(_token1.Get(), Context.Sender, amount1);

        // Mint LP tokens
        UInt256 liquidity;
        var totalSupply = _totalSupply.Get();

        if (totalSupply.IsZero)
        {
            liquidity = Sqrt(amount0 * amount1) - MINIMUM_LIQUIDITY;
            _lpBalances.Set(Address.Zero, MINIMUM_LIQUIDITY); // lock min liquidity
            _totalSupply.Set(MINIMUM_LIQUIDITY);
        }
        else
        {
            var liq0 = (amount0 * totalSupply) / reserve0;
            var liq1 = (amount1 * totalSupply) / reserve1;
            liquidity = UInt256.Min(liq0, liq1);
        }

        Require(!liquidity.IsZero, "INSUFFICIENT_LIQUIDITY_MINTED");

        _lpBalances.Set(to, _lpBalances.Get(to) + liquidity);
        _totalSupply.Set(_totalSupply.Get() + liquidity);

        UpdateReserves(reserve0 + amount0, reserve1 + amount1);
        EmitEvent("LiquidityAdded", Context.Sender, amount0, amount1, liquidity);
        return liquidity;
    }

    /// <summary>
    /// Remove liquidity by burning LP tokens. Returns proportional reserves.
    /// </summary>
    public (UInt256 amount0, UInt256 amount1) RemoveLiquidity(
        UInt256 lpAmount,
        UInt256 amount0Min,
        UInt256 amount1Min,
        Address to,
        ulong deadline)
    {
        Require(Context.BlockNumber <= deadline, "EXPIRED");
        Require(_lpBalances.Get(Context.Sender) >= lpAmount, "INSUFFICIENT_LP");

        var totalSupply = _totalSupply.Get();
        var reserve0 = _reserve0.Get();
        var reserve1 = _reserve1.Get();

        var amount0 = (lpAmount * reserve0) / totalSupply;
        var amount1 = (lpAmount * reserve1) / totalSupply;

        Require(amount0 >= amount0Min, "INSUFFICIENT_0_AMOUNT");
        Require(amount1 >= amount1Min, "INSUFFICIENT_1_AMOUNT");

        _lpBalances.Set(Context.Sender, _lpBalances.Get(Context.Sender) - lpAmount);
        _totalSupply.Set(totalSupply - lpAmount);

        TransferTokenOut(_token0.Get(), to, amount0);
        TransferTokenOut(_token1.Get(), to, amount1);

        UpdateReserves(reserve0 - amount0, reserve1 - amount1);
        EmitEvent("LiquidityRemoved", Context.Sender, amount0, amount1, lpAmount);
        return (amount0, amount1);
    }

    /// <summary>
    /// Swap exact input amount of one token for a minimum output of the other.
    /// </summary>
    public UInt256 SwapExactInput(
        Address tokenIn,
        UInt256 amountIn,
        UInt256 amountOutMin,
        Address to,
        ulong deadline)
    {
        Require(Context.BlockNumber <= deadline, "EXPIRED");
        Require(amountIn > UInt256.Zero, "ZERO_INPUT");

        var token0 = _token0.Get();
        var token1 = _token1.Get();
        bool isToken0 = tokenIn == token0;
        Require(isToken0 || tokenIn == token1, "INVALID_TOKEN");

        var reserve0 = _reserve0.Get();
        var reserve1 = _reserve1.Get();

        var (reserveIn, reserveOut) = isToken0
            ? (reserve0, reserve1)
            : (reserve1, reserve0);

        var amountOut = GetAmountOut(amountIn, reserveIn, reserveOut);
        Require(amountOut >= amountOutMin, "SLIPPAGE_EXCEEDED");

        TransferTokenIn(tokenIn, Context.Sender, amountIn);
        var tokenOut = isToken0 ? token1 : token0;
        TransferTokenOut(tokenOut, to, amountOut);

        // Update reserves
        if (isToken0)
            UpdateReserves(reserve0 + amountIn, reserve1 - amountOut);
        else
            UpdateReserves(reserve0 - amountOut, reserve1 + amountIn);

        EmitEvent("Swap", Context.Sender, tokenIn, amountIn, tokenOut, amountOut, to);
        return amountOut;
    }

    /// <summary>
    /// Flash swap: borrow tokens, execute callback, verify repayment.
    /// </summary>
    public void FlashSwap(
        UInt256 amount0Out,
        UInt256 amount1Out,
        Address to,
        byte[] callbackData)
    {
        Require(amount0Out > UInt256.Zero || amount1Out > UInt256.Zero, "ZERO_OUTPUT");

        var reserve0 = _reserve0.Get();
        var reserve1 = _reserve1.Get();

        if (amount0Out > UInt256.Zero)
            TransferTokenOut(_token0.Get(), to, amount0Out);
        if (amount1Out > UInt256.Zero)
            TransferTokenOut(_token1.Get(), to, amount1Out);

        // Callback to borrower
        Context.Call(to, "OnFlashSwap", Context.Sender, amount0Out, amount1Out, callbackData);

        // Verify invariant (with fee) is maintained
        var balance0 = GetTokenBalance(_token0.Get());
        var balance1 = GetTokenBalance(_token1.Get());

        var fee = _feeBasisPoints.Get();
        var adjusted0 = balance0 * 10000 - amount0Out * fee;
        var adjusted1 = balance1 * 10000 - amount1Out * fee;

        Require(adjusted0 * adjusted1 >= reserve0 * reserve1 * 10000 * 10000, "K_INVARIANT");

        UpdateReserves(balance0, balance1);
    }

    // --- Query Methods ---

    public (UInt256 reserve0, UInt256 reserve1) GetReserves()
        => (_reserve0.Get(), _reserve1.Get());

    public UInt256 GetAmountOut(UInt256 amountIn, UInt256 reserveIn, UInt256 reserveOut)
    {
        var fee = _feeBasisPoints.Get();
        var amountInWithFee = amountIn * (10000 - fee);
        var numerator = amountInWithFee * reserveOut;
        var denominator = reserveIn * 10000 + amountInWithFee;
        return numerator / denominator;
    }

    public UInt256 Quote(UInt256 amountA, UInt256 reserveA, UInt256 reserveB)
        => (amountA * reserveB) / reserveA;

    // --- Internal Helpers ---

    private void UpdateReserves(UInt256 newReserve0, UInt256 newReserve1)
    {
        // Update TWAP accumulators before changing reserves
        var blockTimestamp = Context.BlockNumber;
        var timeElapsed = blockTimestamp - _blockTimestampLast.Get();
        if (timeElapsed > 0 && !_reserve0.Get().IsZero && !_reserve1.Get().IsZero)
        {
            _price0CumulativeLast.Set(
                _price0CumulativeLast.Get() + (_reserve1.Get() / _reserve0.Get()) * timeElapsed);
            _price1CumulativeLast.Set(
                _price1CumulativeLast.Get() + (_reserve0.Get() / _reserve1.Get()) * timeElapsed);
        }

        _reserve0.Set(newReserve0);
        _reserve1.Set(newReserve1);
        _blockTimestampLast.Set(blockTimestamp);
    }

    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private UInt256 GetTokenBalance(Address token) { /* ... */ }
    private UInt256 Sqrt(UInt256 x) { /* Newton's method */ }
}


// ============================================================
// AmmRouter -- Multi-hop swap routing
// ============================================================

[BasaltContract(TypeId = 0x0202)]
public partial class AmmRouter : SdkContract
{
    private StorageValue<Address> _factory;

    /// <summary>
    /// Swap through a path of token addresses, using intermediate pools.
    /// </summary>
    public UInt256 SwapExactTokensForTokens(
        UInt256 amountIn,
        UInt256 amountOutMin,
        Address[] path,
        Address to,
        ulong deadline)
    {
        Require(path.Length >= 2, "INVALID_PATH");
        // Iteratively swap through each pair in the path
        // ...
        return finalAmountOut;
    }

    /// <summary>
    /// Compute expected output amounts along a multi-hop path.
    /// </summary>
    public UInt256[] GetAmountsOut(UInt256 amountIn, Address[] path)
    {
        // Query each pool's reserves along the path
        // ...
        return amounts;
    }
}
```

## Complexity

**Medium** -- The constant-product math is well-understood and deterministic, but correct implementation requires careful handling of rounding, minimum liquidity locking, TWAP accumulator overflow, fee accounting, flash swap invariant verification, and multi-hop routing. The factory/pool/router architecture adds deployment complexity.

## Priority

**P0** -- The AMM DEX is the single most critical DeFi primitive. Without on-chain swap capability, no other DeFi protocol (lending, stablecoins, yield aggregation, liquidations) can function. It must be the first contract deployed after core system contracts.
