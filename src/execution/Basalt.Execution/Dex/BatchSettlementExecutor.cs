using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex.Math;
using Basalt.Execution.VM;
using Basalt.Storage;

namespace Basalt.Execution.Dex;

/// <summary>
/// Executes batch auction settlements within the block building pipeline.
/// Takes the <see cref="BatchResult"/> from <see cref="BatchAuctionSolver"/> and applies
/// all balance transfers and state updates atomically.
///
/// Settlement flow:
/// <list type="number">
/// <item><description>For each fill: debit input token from participant, credit output token</description></item>
/// <item><description>Update limit orders (reduce or delete filled amounts)</description></item>
/// <item><description>Update AMM reserves for the residual routed through the pool</description></item>
/// <item><description>Update TWAP accumulator with the clearing price</description></item>
/// <item><description>Generate receipts for each participating transaction</description></item>
/// </list>
/// </summary>
public static class BatchSettlementExecutor
{
    /// <summary>
    /// Execute a batch settlement, applying all fills and state updates.
    /// </summary>
    /// <param name="result">The batch result from the solver.</param>
    /// <param name="stateDb">The state database to apply changes to.</param>
    /// <param name="dexState">The DEX state writer.</param>
    /// <param name="blockHeader">The current block header (for timestamps and proposer).</param>
    /// <param name="intentTxMap">Map from intent tx hash → original transaction (for receipt generation).</param>
    /// <returns>Receipts for all participating transactions.</returns>
    public static List<TransactionReceipt> ExecuteSettlement(
        BatchResult result,
        IStateDatabase stateDb,
        DexState dexState,
        BlockHeader blockHeader,
        Dictionary<Hash256, Transaction> intentTxMap,
        IContractRuntime? runtime = null,
        ChainParameters? chainParams = null)
    {
        var receipts = new List<TransactionReceipt>();

        // Apply fills
        foreach (var fill in result.Fills)
        {
            try
            {
                // Determine token addresses from pool metadata
                var meta = dexState.GetPoolMetadata(result.PoolId);
                if (meta == null) continue;

                var m = meta.Value;

                // For swap intents: debit input from sender, credit output to sender
                if (!fill.IsLimitOrder)
                {
                    // Check if this fill's TxHash maps to an intent
                    if (intentTxMap.TryGetValue(fill.TxHash, out var intentTx))
                    {
                        var intent = ParsedIntent.Parse(intentTx);
                        if (intent == null)
                        {
                            receipts.Add(new TransactionReceipt
                            {
                                TransactionHash = fill.TxHash,
                                BlockHash = blockHeader.Hash,
                                BlockNumber = blockHeader.Number,
                                TransactionIndex = receipts.Count,
                                From = fill.Participant,
                                To = DexState.DexAddress,
                                GasUsed = chainParams?.DexSwapGas ?? 80_000,
                                Success = false,
                                ErrorCode = BasaltErrorCode.DexInvalidData,
                                PostStateRoot = Hash256.Zero,
                                Logs = [],
                                EffectiveGasPrice = intentTx.EffectiveGasPrice(blockHeader.BaseFee),
                            });
                            continue;
                        }

                        // Debit input tokens from sender
                        DexEngine.TransferSingleTokenIn(stateDb, fill.Participant, intent.Value.TokenIn, fill.AmountIn, runtime);

                        // Credit output tokens to sender
                        DexEngine.TransferSingleTokenOut(stateDb, fill.Participant, intent.Value.TokenOut, fill.AmountOut, runtime);

                        // Generate receipt
                        var logs = new List<EventLog>
                        {
                            MakeBatchFillLog(result.PoolId, fill, result.ClearingPrice),
                        };

                        receipts.Add(new TransactionReceipt
                        {
                            TransactionHash = fill.TxHash,
                            BlockHash = blockHeader.Hash,
                            BlockNumber = blockHeader.Number,
                            TransactionIndex = receipts.Count,
                            From = fill.Participant,
                            To = DexState.DexAddress,
                            GasUsed = chainParams?.DexSwapGas ?? 80_000,
                            Success = true,
                            ErrorCode = BasaltErrorCode.Success,
                            PostStateRoot = Hash256.Zero,
                            Logs = logs,
                            EffectiveGasPrice = intentTx.EffectiveGasPrice(blockHeader.BaseFee),
                        });
                    }
                }
                else
                {
                    // H-01: Limit order fill — use IsBuy to determine correct token directions
                    var outputToken = fill.IsBuy ? m.Token0 : m.Token1;
                    var inputToken = fill.IsBuy ? m.Token1 : m.Token0;

                    // H-02: Transfer escrowed input from DEX → pool reserves
                    DexEngine.TransferSingleTokenIn(stateDb, DexState.DexAddress, inputToken, fill.AmountIn, runtime);

                    // Credit output to order owner
                    DexEngine.TransferSingleTokenOut(stateDb, fill.Participant, outputToken, fill.AmountOut, runtime);

                    // CR-3a: Update order remaining amount (subtract filled amount, not wipe to zero)
                    if (fill.OrderId > 0)
                    {
                        var existingOrder = dexState.GetOrder(fill.OrderId);
                        if (existingOrder != null)
                        {
                            var remaining = existingOrder.Value.Amount > fill.AmountIn
                                ? UInt256.CheckedSub(existingOrder.Value.Amount, fill.AmountIn)
                                : UInt256.Zero;
                            if (remaining.IsZero)
                                dexState.DeleteOrder(fill.OrderId);
                            else
                                dexState.UpdateOrderAmount(fill.OrderId, remaining);
                        }
                    }

                    // CR-3b: Generate deterministic receipt hash for limit order fills
                    // BLAKE3(blockHash || poolId || orderId || fillIndex) — unique per fill
                    var receiptHashData = new byte[32 + 8 + 8 + 4];
                    blockHeader.Hash.WriteTo(receiptHashData.AsSpan(0, 32));
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(receiptHashData.AsSpan(32, 8), result.PoolId);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(receiptHashData.AsSpan(40, 8), fill.OrderId);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(receiptHashData.AsSpan(48, 4), receipts.Count);
                    var limitOrderReceiptHash = Blake3Hasher.Hash(receiptHashData);

                    // L-08: Generate receipts for limit order fills
                    receipts.Add(new TransactionReceipt
                    {
                        TransactionHash = limitOrderReceiptHash,
                        BlockHash = blockHeader.Hash,
                        BlockNumber = blockHeader.Number,
                        TransactionIndex = receipts.Count,
                        From = fill.Participant,
                        To = DexState.DexAddress,
                        GasUsed = chainParams?.DexLimitOrderGas ?? 40_000,
                        Success = true,
                        ErrorCode = BasaltErrorCode.Success,
                        PostStateRoot = Hash256.Zero,
                        Logs = [MakeBatchFillLog(result.PoolId, fill, result.ClearingPrice)],
                        EffectiveGasPrice = UInt256.Zero,
                    });
                }
            }
            catch (Exception ex) when (ex is BasaltException or OverflowException or ArgumentException)
            {
                // L-10: Skip this fill — don't abort remaining settlements.
                // A single user's insufficient balance (OverflowException from UInt256.CheckedSub)
                // or invalid AMM input (ArgumentException from DexLibrary) should not prevent other fills.
                continue;
            }
        }

        // Update reserves
        dexState.SetPoolReserves(result.PoolId, result.UpdatedReserves);

        // Pay solver reward (if external solver won the auction)
        if (result.WinningSolver != null && chainParams != null && !result.AmmVolume.IsZero)
        {
            PaySolverReward(result, stateDb, dexState, chainParams, runtime);
        }

        // Update TWAP
        dexState.UpdateTwapAccumulator(result.PoolId, result.ClearingPrice, blockHeader.Number);

        return receipts;
    }

    /// <summary>
    /// Compute and pay the winning solver's reward from AMM fee revenue.
    /// L-01: Determine the correct reserve to deduct from based on AMM swap direction.
    /// </summary>
    private static void PaySolverReward(
        BatchResult result, IStateDatabase stateDb, DexState dexState,
        ChainParameters chainParams, IContractRuntime? runtime)
    {
        var meta = dexState.GetPoolMetadata(result.PoolId);
        if (meta == null || result.WinningSolver == null) return;

        // AMM fee in token0 units: ammVolume * feeBps / 10000
        var ammFee = Math.FullMath.MulDiv(result.AmmVolume, new UInt256(meta.Value.FeeBps), new UInt256(10_000));
        if (ammFee.IsZero) return;

        // Solver reward: ammFee * effectiveBps / 10000 (governance override → ChainParameters fallback)
        var effectiveBps = dexState.GetEffectiveSolverRewardBps(chainParams);
        var reward = Math.FullMath.MulDiv(ammFee, new UInt256(effectiveBps), new UInt256(10_000));
        if (reward.IsZero) return;

        // L-01: Determine which reserve to deduct from based on AMM swap direction.
        // AMM fees are collected from the INPUT side. When AmmBoughtToken0 (sell pressure),
        // the AMM received token0 as input → fee in token0 → deduct from Reserve0.
        // When !AmmBoughtToken0 (buy pressure), AMM received token1 → fee in token1 → deduct from Reserve1.
        Address rewardToken;
        UInt256 reserveBalance;

        if (result.AmmBoughtToken0)
        {
            // Sell pressure: AMM bought token0 (received token0 input) → fees in token0
            rewardToken = meta.Value.Token0;
            reserveBalance = result.UpdatedReserves.Reserve0;
        }
        else
        {
            // Buy pressure: AMM sold token0 (received token1 input) → fees in token1
            rewardToken = meta.Value.Token1;
            reserveBalance = result.UpdatedReserves.Reserve1;
        }

        if (reward > reserveBalance) return;

        var updatedReserves = result.UpdatedReserves;
        if (rewardToken == meta.Value.Token0)
        {
            updatedReserves = new PoolReserves
            {
                Reserve0 = UInt256.CheckedSub(result.UpdatedReserves.Reserve0, reward),
                Reserve1 = result.UpdatedReserves.Reserve1,
                TotalSupply = result.UpdatedReserves.TotalSupply,
                KLast = result.UpdatedReserves.KLast,
            };
        }
        else
        {
            updatedReserves = new PoolReserves
            {
                Reserve0 = result.UpdatedReserves.Reserve0,
                Reserve1 = UInt256.CheckedSub(result.UpdatedReserves.Reserve1, reward),
                TotalSupply = result.UpdatedReserves.TotalSupply,
                KLast = result.UpdatedReserves.KLast,
            };
        }
        dexState.SetPoolReserves(result.PoolId, updatedReserves);

        // Credit reward to solver
        DexEngine.TransferSingleTokenOut(stateDb, result.WinningSolver.Value, rewardToken, reward, runtime);
    }

    /// <summary>
    /// Group swap intent transactions by trading pair for batch processing.
    /// Returns a dictionary mapping (token0, token1) → list of parsed intents.
    /// </summary>
    public static Dictionary<(Address, Address), List<ParsedIntent>> GroupByPair(
        IReadOnlyList<Transaction> intents, DexState dexState)
    {
        var groups = new Dictionary<(Address, Address), List<ParsedIntent>>();

        foreach (var tx in intents)
        {
            var intent = ParsedIntent.Parse(tx);
            if (intent == null) continue;

            var (t0, t1) = DexEngine.SortTokens(intent.Value.TokenIn, intent.Value.TokenOut);

            if (!groups.TryGetValue((t0, t1), out var list))
            {
                list = [];
                groups[(t0, t1)] = list;
            }
            list.Add(intent.Value);
        }

        return groups;
    }

    /// <summary>
    /// Separate intents into buy/sell sides relative to the canonical token0.
    /// Buy intents are buying token0 (their tokenOut == token0).
    /// Sell intents are selling token0 (their tokenIn == token0).
    /// </summary>
    public static (List<ParsedIntent> Buys, List<ParsedIntent> Sells) SplitBuySell(
        List<ParsedIntent> intents, Address token0)
    {
        var buys = new List<ParsedIntent>();
        var sells = new List<ParsedIntent>();

        foreach (var intent in intents)
        {
            if (intent.IsBuyingSide(token0))
                buys.Add(intent);
            else
                sells.Add(intent);
        }

        // Sort buys by decreasing limit price (most eager buyers first)
        buys.Sort((a, b) => b.LimitPrice.CompareTo(a.LimitPrice));
        // Sort sells by increasing limit price (cheapest sellers first)
        sells.Sort((a, b) => a.LimitPrice.CompareTo(b.LimitPrice));

        return (buys, sells);
    }

    private static EventLog MakeBatchFillLog(ulong poolId, FillRecord fill, UInt256 clearingPrice)
    {
        var sigBytes = System.Text.Encoding.UTF8.GetBytes("Dex.BatchFill");
        var eventSig = Blake3Hasher.Hash(sigBytes);

        // Pack: [8B poolId][20B participant][32B amountIn][32B amountOut][32B clearingPrice]
        var data = new byte[8 + 20 + 32 + 32 + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0, 8), poolId);
        fill.Participant.WriteTo(data.AsSpan(8, 20));
        fill.AmountIn.WriteTo(data.AsSpan(28, 32));
        fill.AmountOut.WriteTo(data.AsSpan(60, 32));
        clearingPrice.WriteTo(data.AsSpan(92, 32));

        return new EventLog
        {
            Contract = DexState.DexAddress,
            EventSignature = eventSig,
            Topics = [],
            Data = data,
        };
    }
}
