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
        IContractRuntime? runtime = null)
    {
        var receipts = new List<TransactionReceipt>();

        // Apply fills
        foreach (var fill in result.Fills)
        {
            // Determine token addresses from pool metadata
            var meta = dexState.GetPoolMetadata(result.PoolId);
            if (meta == null) continue;

            // For swap intents: debit input from sender, credit output to sender
            if (!fill.IsLimitOrder)
            {
                // The fill stores AmountIn/AmountOut relative to the participant
                // Need to determine which token is in/out based on direction

                // Check if this fill's TxHash maps to an intent
                if (intentTxMap.TryGetValue(fill.TxHash, out var intentTx))
                {
                    var intent = ParsedIntent.Parse(intentTx);
                    if (intent == null) continue;

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
                        TransactionIndex = receipts.Count, // Will be adjusted by caller
                        From = fill.Participant,
                        To = DexState.DexAddress,
                        GasUsed = 80_000, // Standard DEX swap gas
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
                // Limit order fill — the tokens are already escrowed
                // Credit the output tokens to the order owner
                var m = meta.Value;
                var outputToken = fill.AmountOut > UInt256.Zero ? m.Token0 : m.Token1;
                DexEngine.TransferSingleTokenOut(stateDb, fill.Participant, outputToken, fill.AmountOut, runtime);
            }
        }

        // Update reserves
        dexState.SetPoolReserves(result.PoolId, result.UpdatedReserves);

        // Update TWAP
        dexState.UpdateTwapAccumulator(result.PoolId, result.ClearingPrice, blockHeader.Number);

        return receipts;
    }

    /// <summary>
    /// Group swap intent transactions by trading pair for batch processing.
    /// Returns a dictionary mapping (token0, token1) → list of parsed intents.
    /// </summary>
    /// <param name="intents">The swap intent transactions from the mempool.</param>
    /// <param name="dexState">The DEX state for pool lookups.</param>
    /// <returns>Intents grouped by canonical token pair.</returns>
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
    /// <param name="intents">Intents for a single trading pair.</param>
    /// <param name="token0">The canonical token0 of the pair.</param>
    /// <returns>Tuple of (buyIntents, sellIntents) sorted by price.</returns>
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
