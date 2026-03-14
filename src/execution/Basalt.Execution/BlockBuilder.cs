using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using Basalt.Storage;
using Microsoft.Extensions.Logging;

namespace Basalt.Execution;

/// <summary>
/// Builds blocks by selecting transactions from a pool, executing them, and computing the block header.
/// </summary>
public sealed class BlockBuilder
{
    private readonly ChainParameters _chainParams;
    private readonly TransactionValidator _validator;
    private readonly TransactionExecutor _executor;
    private readonly ILogger<BlockBuilder>? _logger;

    /// <summary>
    /// DKG group public key for the current epoch, used to validate encrypted swap intents.
    /// Set by NodeCoordinator after DKG completion.
    /// </summary>
    public BlsPublicKey? DkgGroupPublicKey { get; set; }

    /// <summary>
    /// DKG group secret key for the current epoch, used to decrypt encrypted swap intents.
    /// This is the threshold-reconstructed secret (32-byte BLS scalar, big-endian).
    /// Set by NodeCoordinator after DKG reconstruction.
    /// </summary>
    public byte[]? DkgGroupSecretKey { get; set; }

    /// <summary>
    /// Current DKG epoch number for encrypted intent validation.
    /// Set by NodeCoordinator after DKG completion.
    /// </summary>
    public ulong CurrentDkgEpoch { get; set; }

    /// <summary>
    /// Optional external settlement provider. When set, the block builder will prefer
    /// external solver settlements over the built-in BatchAuctionSolver when the external
    /// solver produces higher surplus for users.
    /// </summary>
    public Func<ulong, List<ParsedIntent>, List<ParsedIntent>, PoolReserves, uint,
        Dictionary<Hash256, UInt256>, IStateDatabase, DexState, Dictionary<Hash256, Transaction>,
        BatchResult?>? ExternalSolverProvider { get; set; }

    public BlockBuilder(ChainParameters chainParams, ILogger<BlockBuilder>? logger = null)
        : this(chainParams, new TransactionExecutor(chainParams), logger) { }

    /// <summary>
    /// HIGH-01: Accept a shared TransactionExecutor that has staking/compliance dependencies.
    /// Without this, the block builder's internal executor lacks IStakingState and IComplianceVerifier,
    /// causing staking transactions to fail and compliance checks to be skipped during block building.
    /// </summary>
    public BlockBuilder(ChainParameters chainParams, TransactionExecutor executor, ILogger<BlockBuilder>? logger = null)
    {
        _chainParams = chainParams;
        _validator = new TransactionValidator(chainParams);
        _executor = executor;
        _logger = logger;
    }

    /// <summary>
    /// Build a block from the pending transactions.
    /// </summary>
    public Block BuildBlock(
        IReadOnlyList<Transaction> pendingTransactions,
        IStateDatabase stateDb,
        BlockHeader parentHeader,
        Address proposer)
    {
        var validTxs = new List<Transaction>();
        var receipts = new List<TransactionReceipt>();
        ulong totalGasUsed = 0;

        // Compute EIP-1559 base fee from parent block
        var baseFee = BaseFeeCalculator.Calculate(
            parentHeader.BaseFee, parentHeader.GasUsed, parentHeader.GasLimit, _chainParams);

        // Create a preliminary header for receipt generation
        var blockNumber = parentHeader.Number + 1;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var preliminaryHeader = new BlockHeader
        {
            Number = blockNumber,
            ParentHash = parentHeader.Hash,
            StateRoot = Hash256.Zero, // Will be computed after execution
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = timestamp,
            Proposer = proposer,
            ChainId = _chainParams.ChainId,
            GasUsed = 0,
            GasLimit = _chainParams.BlockGasLimit,
            BaseFee = baseFee,
        };

        foreach (var tx in pendingTransactions)
        {
            if (validTxs.Count >= (int)_chainParams.MaxTransactionsPerBlock)
                break;

            if (totalGasUsed + tx.GasLimit > _chainParams.BlockGasLimit)
                continue;

            // L-5: Signature was already verified at mempool admission (M-2).
            // Skip signature re-verification during block building for performance.
            var validation = _validator.Validate(tx, stateDb, baseFee, skipSignature: true);
            if (!validation.IsSuccess)
            {
                _logger?.LogWarning("BuildBlock skipped tx {Hash}: {Error}",
                    tx.Hash.ToHexString()[..18] + "...", validation.Message);
                continue;
            }

            var receipt = _executor.Execute(tx, stateDb, preliminaryHeader, validTxs.Count);
            validTxs.Add(tx);
            receipts.Add(receipt);
            totalGasUsed += receipt.GasUsed;
        }

        // Compute roots
        var stateRoot = stateDb.ComputeStateRoot();
        var txRoot = ComputeTransactionsRoot(validTxs);
        var receiptsRoot = ComputeReceiptsRoot(receipts);

        var header = new BlockHeader
        {
            Number = blockNumber,
            ParentHash = parentHeader.Hash,
            StateRoot = stateRoot,
            TransactionsRoot = txRoot,
            ReceiptsRoot = receiptsRoot,
            Timestamp = timestamp,
            Proposer = proposer,
            ChainId = _chainParams.ChainId,
            GasUsed = totalGasUsed,
            GasLimit = _chainParams.BlockGasLimit,
            BaseFee = baseFee,
        };

        // MED-02: Update receipt BlockHash to match the final header.
        // Receipts were created with the preliminary header (zero roots),
        // so their BlockHash was incorrect.
        foreach (var receipt in receipts)
            receipt.BlockHash = header.Hash;

        return new Block
        {
            Header = header,
            Transactions = validTxs,
            Receipts = receipts,
        };
    }

    /// <summary>
    /// Build a block with three-phase DEX pipeline:
    /// <list type="number">
    /// <item><description>Phase A: Execute all non-intent transactions (transfers, staking, liquidity, orders)</description></item>
    /// <item><description>Phase B: Batch auction — group DexSwapIntent txs by pair, compute uniform clearing prices</description></item>
    /// <item><description>Phase C: Settlement — apply fills at clearing price, update reserves, emit receipts</description></item>
    /// </list>
    /// </summary>
    /// <param name="pendingTransactions">Non-intent transactions from the mempool.</param>
    /// <param name="pendingDexIntents">DexSwapIntent transactions from the intent pool.</param>
    /// <param name="stateDb">The canonical state database.</param>
    /// <param name="parentHeader">The parent block header.</param>
    /// <param name="proposer">The block proposer address.</param>
    /// <returns>The built block.</returns>
    public Block BuildBlockWithDex(
        IReadOnlyList<Transaction> pendingTransactions,
        IReadOnlyList<Transaction> pendingDexIntents,
        IStateDatabase stateDb,
        BlockHeader parentHeader,
        Address proposer)
    {
        try
        {
        return BuildBlockWithDexCore(pendingTransactions, pendingDexIntents, stateDb, parentHeader, proposer);
        }
        finally
        {
            // L-11: Zero the DKG secret key after each block build (exception-safe)
            if (DkgGroupSecretKey != null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(DkgGroupSecretKey);
                DkgGroupSecretKey = null;
            }
        }
    }

    private Block BuildBlockWithDexCore(
        IReadOnlyList<Transaction> pendingTransactions,
        IReadOnlyList<Transaction> pendingDexIntents,
        IStateDatabase stateDb,
        BlockHeader parentHeader,
        Address proposer)
    {
        var validTxs = new List<Transaction>();
        var receipts = new List<TransactionReceipt>();
        ulong totalGasUsed = 0;

        var baseFee = BaseFeeCalculator.Calculate(
            parentHeader.BaseFee, parentHeader.GasUsed, parentHeader.GasLimit, _chainParams);

        var blockNumber = parentHeader.Number + 1;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var preliminaryHeader = new BlockHeader
        {
            Number = blockNumber,
            ParentHash = parentHeader.Hash,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = timestamp,
            Proposer = proposer,
            ChainId = _chainParams.ChainId,
            GasUsed = 0,
            GasLimit = _chainParams.BlockGasLimit,
            BaseFee = baseFee,
        };

        // ═══ Phase A: Non-DEX transactions + immediate DEX ops ═══
        // DexAddLiquidity, DexRemoveLiquidity, DexLimitOrder, DexCancelOrder execute immediately.
        // DexCreatePool also executes immediately.
        foreach (var tx in pendingTransactions)
        {
            if (validTxs.Count >= (int)_chainParams.MaxTransactionsPerBlock)
                break;

            if (totalGasUsed + tx.GasLimit > _chainParams.BlockGasLimit)
                continue;

            var validation = _validator.Validate(tx, stateDb, baseFee, skipSignature: true);
            if (!validation.IsSuccess)
            {
                _logger?.LogWarning("BuildBlock skipped tx {Hash}: {Error}",
                    tx.Hash.ToHexString()[..18] + "...", validation.Message);
                continue;
            }

            var receipt = _executor.Execute(tx, stateDb, preliminaryHeader, validTxs.Count);
            validTxs.Add(tx);
            receipts.Add(receipt);
            totalGasUsed += receipt.GasUsed;
        }

        // ═══ TWAP carry-forward: update accumulators for all pools using current price ═══
        RunTwapCarryForward(stateDb, blockNumber);

        // ═══ Phase B: Batch auction — group intents by pair, compute clearing prices ═══
        var batchResults = new List<BatchResult>();
        var processedIntents = new List<Transaction>();
        var settledPoolIds = new HashSet<ulong>(); // Track pools already settled via intents

        if (pendingDexIntents.Count > 0)
        {
            var dexState = new DexState(stateDb);

            // P-2: Skip batch auction when DEX is paused — intents should not settle.
            if (dexState.IsDexPaused())
            {
                _logger?.LogWarning("DEX is paused — skipping batch auction for {Count} intents",
                    pendingDexIntents.Count);
                goto SkipBatchAuction;
            }

            // Decrypt encrypted intents and merge with plaintext intents
            var allParsedIntents = new List<ParsedIntent>();
            foreach (var tx in pendingDexIntents)
            {
                if (tx.Type == TransactionType.DexEncryptedSwapIntent)
                {
                    if (DkgGroupSecretKey == null)
                    {
                        _logger?.LogWarning("Skipping encrypted intent {Hash}: no DKG group secret key available",
                            tx.Hash.ToHexString()[..18] + "...");
                        continue;
                    }
                    var encrypted = EncryptedIntent.Parse(tx);
                    if (encrypted == null) continue;
                    var decrypted = encrypted.Value.Decrypt(DkgGroupSecretKey, CurrentDkgEpoch);
                    if (decrypted == null)
                    {
                        _logger?.LogWarning("Skipping encrypted intent {Hash}: decryption failed",
                            tx.Hash.ToHexString()[..18] + "...");
                        continue;
                    }
                    allParsedIntents.Add(decrypted.Value);
                }
                else
                {
                    var intent = ParsedIntent.Parse(tx);
                    if (intent != null)
                        allParsedIntents.Add(intent.Value);
                }
            }

            // Build intent tx map for failure receipts
            var intentTxMap = new Dictionary<Hash256, Transaction>();
            foreach (var tx in pendingDexIntents)
                intentTxMap.TryAdd(tx.Hash, tx);

            // Group intents by trading pair
            var groups = GroupParsedIntentsByPair(allParsedIntents, dexState);

            foreach (var ((token0, token1), intents) in groups)
            {
                // Find pool for this pair
                ulong? poolId = null;
                uint poolFeeBps = 30;
                foreach (var tier in Dex.Math.DexLibrary.AllowedFeeTiers)
                {
                    poolId = dexState.LookupPool(token0, token1, tier);
                    if (poolId != null)
                    {
                        poolFeeBps = tier;
                        break;
                    }
                }

                if (poolId == null)
                {
                    _logger?.LogWarning("No pool found for pair {T0}/{T1}, skipping {Count} intents",
                        token0, token1, intents.Count);
                    continue;
                }

                var reserves = dexState.GetPoolReserves(poolId.Value);
                if (reserves == null) continue;

                OrderBook.CleanupExpiredOrders(dexState, stateDb, poolId.Value, blockNumber);

                // Split into buy/sell sides
                var (buys, sells) = BatchSettlementExecutor.SplitBuySell(intents, token0);

                // Filter expired intents
                buys.RemoveAll(i => i.Deadline > 0 && blockNumber > i.Deadline);
                sells.RemoveAll(i => i.Deadline > 0 && blockNumber > i.Deadline);

                // Compute settlement — prefer external solver when available
                BatchResult? result = null;
                if (ExternalSolverProvider != null)
                {
                    // Build intent min-amounts map for surplus scoring
                    var intentMinAmounts = new Dictionary<Hash256, UInt256>();
                    var intentTxMapForSolver = new Dictionary<Hash256, Transaction>();
                    foreach (var intent in buys.Concat(sells))
                    {
                        intentMinAmounts[intent.TxHash] = intent.MinAmountOut;
                        intentTxMapForSolver[intent.TxHash] = intent.OriginalTx;
                    }

                    result = ExternalSolverProvider(
                        poolId.Value, buys, sells, reserves.Value, poolFeeBps,
                        intentMinAmounts, stateDb, dexState, intentTxMapForSolver);
                }

                // Gather active limit orders for this pool to include in the settlement
                var (activeBuyOrders, activeSellOrders) = GetAllActiveOrders(dexState, poolId.Value, blockNumber);

                // Fall back to built-in solver
                result ??= BatchAuctionSolver.ComputeSettlement(
                    buys, sells,
                    activeBuyOrders, activeSellOrders,
                    reserves.Value, poolFeeBps, poolId.Value, dexState);

                if (result != null)
                {
                    batchResults.Add(result);
                    settledPoolIds.Add(poolId.Value);

                    // Track which intents were processed
                    foreach (var intent in buys)
                        processedIntents.Add(intent.OriginalTx);
                    foreach (var intent in sells)
                        processedIntents.Add(intent.OriginalTx);
                }
                else
                {
                    // Generate failure receipts for unsettled intents
                    foreach (var intent in buys.Concat(sells))
                    {
                        if (intentTxMap.TryGetValue(intent.TxHash, out var intentTx))
                        {
                            receipts.Add(new TransactionReceipt
                            {
                                TransactionHash = intent.TxHash,
                                BlockHash = Hash256.Zero,
                                BlockNumber = blockNumber,
                                TransactionIndex = receipts.Count,
                                From = intent.Sender,
                                To = DexState.DexAddress,
                                GasUsed = _chainParams.DexSwapGas,
                                Success = false,
                                ErrorCode = BasaltErrorCode.DexInsufficientLiquidity,
                                PostStateRoot = Hash256.Zero,
                                Logs = [],
                                EffectiveGasPrice = UInt256.Zero,
                            });
                            totalGasUsed += _chainParams.DexSwapGas;
                            validTxs.Add(intentTx);
                        }
                    }
                }
            }
        }
        SkipBatchAuction:

        // ═══ Phase B2: Standalone limit order matching for pools not covered by swap intents ═══
        var standaloneResults = RunStandaloneLimitOrderMatching(stateDb, blockNumber, settledPoolIds);
        batchResults.AddRange(standaloneResults);

        // ═══ Phase C: Settlement — apply fills, update reserves, generate receipts ═══
        bool gasLimitReached = false;
        foreach (var result in batchResults)
        {
            if (gasLimitReached) break;

            var dexState = new DexState(stateDb);
            var intentTxMap = new Dictionary<Hash256, Transaction>();
            foreach (var tx in processedIntents)
                intentTxMap.TryAdd(tx.Hash, tx);

            var batchReceipts = BatchSettlementExecutor.ExecuteSettlement(
                result, stateDb, dexState, preliminaryHeader, intentTxMap, _executor.ContractRuntime, _chainParams);

            // Add batch-settled intents as valid transactions and their receipts
            foreach (var r in batchReceipts)
            {
                // Charge gas for each intent tx
                var intentTx = intentTxMap.GetValueOrDefault(r.TransactionHash);
                if (intentTx != null)
                {
                    var gasUsed = _chainParams.DexSwapGas;
                    // M-07: Check block gas limit before adding intent — exit outer loop too
                    if (totalGasUsed + gasUsed > _chainParams.BlockGasLimit)
                    {
                        gasLimitReached = true;
                        break;
                    }
                    totalGasUsed += gasUsed;
                    validTxs.Add(intentTx);
                }
                receipts.Add(r);
            }
        }

        // Serialize TWAP data for block header ExtraData
        var dexStateForTwap = new DexState(stateDb);
        var effectiveTwapWindow = dexStateForTwap.GetEffectiveTwapWindowBlocks(_chainParams);
        var extraData = batchResults.Count > 0
            ? TwapOracle.SerializeForBlockHeader(
                batchResults, dexStateForTwap, blockNumber, _chainParams.MaxExtraDataBytes, effectiveTwapWindow)
            : [];

        // Compute roots
        var stateRoot = stateDb.ComputeStateRoot();
        var txRoot = ComputeTransactionsRoot(validTxs);
        var receiptsRoot = ComputeReceiptsRoot(receipts);

        var header = new BlockHeader
        {
            Number = blockNumber,
            ParentHash = parentHeader.Hash,
            StateRoot = stateRoot,
            TransactionsRoot = txRoot,
            ReceiptsRoot = receiptsRoot,
            Timestamp = timestamp,
            Proposer = proposer,
            ChainId = _chainParams.ChainId,
            GasUsed = totalGasUsed,
            GasLimit = _chainParams.BlockGasLimit,
            BaseFee = baseFee,
            ExtraData = extraData,
        };

        foreach (var receipt in receipts)
            receipt.BlockHash = header.Hash;

        return new Block
        {
            Header = header,
            Transactions = validTxs,
            Receipts = receipts,
        };
    }

    /// <summary>
    /// Update TWAP accumulators for all pools using current price.
    /// </summary>
    private static void RunTwapCarryForward(IStateDatabase stateDb, ulong blockNumber)
    {
        var dexState = new DexState(stateDb);
        var poolCount = dexState.GetPoolCount();
        for (ulong pid = 0; pid < poolCount; pid++)
        {
            var acc = dexState.GetTwapAccumulator(pid);
            if (acc.LastBlock >= blockNumber) continue;

            var concState = dexState.GetConcentratedPoolState(pid);
            UInt256 currentPrice;
            if (concState != null && !concState.Value.SqrtPriceX96.IsZero)
            {
                currentPrice = FullMath.MulDiv(concState.Value.SqrtPriceX96,
                    concState.Value.SqrtPriceX96, TickMath.Q96);
            }
            else
            {
                var reserves = dexState.GetPoolReserves(pid);
                if (reserves == null || reserves.Value.Reserve0.IsZero) continue;
                currentPrice = BatchAuctionSolver.ComputeSpotPrice(reserves.Value.Reserve0, reserves.Value.Reserve1);
            }
            TwapOracle.CarryForwardAccumulator(dexState, pid, currentPrice, blockNumber);
        }
    }

    /// <summary>
    /// Match limit orders for pools not already settled by swap intents.
    /// </summary>
    private static List<BatchResult> RunStandaloneLimitOrderMatching(
        IStateDatabase stateDb, ulong blockNumber, HashSet<ulong> excludePoolIds)
    {
        var results = new List<BatchResult>();
        var dexState = new DexState(stateDb);
        if (dexState.IsDexPaused()) return results;

        var poolCount = dexState.GetPoolCount();
        for (ulong pid = 0; pid < poolCount; pid++)
        {
            if (excludePoolIds.Contains(pid)) continue;

            var reserves = dexState.GetPoolReserves(pid);
            if (reserves == null) continue;

            var meta = dexState.GetPoolMetadata(pid);
            if (meta == null) continue;

            OrderBook.CleanupExpiredOrders(dexState, stateDb, pid, blockNumber);

            var (activeBuyOrders, activeSellOrders) = GetAllActiveOrders(dexState, pid, blockNumber);
            if (activeBuyOrders.Count == 0 || activeSellOrders.Count == 0) continue;

            var result = BatchAuctionSolver.ComputeSettlement(
                [], [],
                activeBuyOrders, activeSellOrders,
                reserves.Value, meta.Value.FeeBps, pid, dexState);

            if (result != null)
                results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Run DEX settlement phases (TWAP carry-forward + limit order matching + settlement execution)
    /// on the given state database. Called during block finalization and sync to ensure canonical
    /// state reflects DEX activity.
    /// </summary>
    public List<TransactionReceipt> ApplyDexSettlement(IStateDatabase stateDb, BlockHeader blockHeader)
    {
        var allReceipts = new List<TransactionReceipt>();

        RunTwapCarryForward(stateDb, blockHeader.Number);

        var batchResults = RunStandaloneLimitOrderMatching(stateDb, blockHeader.Number, new HashSet<ulong>());
        if (batchResults.Count == 0)
            return allReceipts;

        var emptyIntentMap = new Dictionary<Hash256, Transaction>();
        foreach (var result in batchResults)
        {
            var dexState = new DexState(stateDb);
            var batchReceipts = BatchSettlementExecutor.ExecuteSettlement(
                result, stateDb, dexState, blockHeader, emptyIntentMap,
                _executor.ContractRuntime, _chainParams);
            allReceipts.AddRange(batchReceipts);
        }

        return allReceipts;
    }

    /// <summary>
    /// Group already-parsed intents by canonical token pair (used when encrypted intents have been decrypted).
    /// </summary>
    /// <summary>
    /// Get all active (non-expired, non-zero) limit orders for a pool, split by side.
    /// </summary>
    private static (List<(ulong Id, LimitOrder Order)> Buys, List<(ulong Id, LimitOrder Order)> Sells) GetAllActiveOrders(
        DexState dexState, ulong poolId, ulong currentBlock)
    {
        var buys = new List<(ulong Id, LimitOrder Order)>();
        var sells = new List<(ulong Id, LimitOrder Order)>();

        var orderId = dexState.GetPoolOrderHead(poolId);
        while (orderId != ulong.MaxValue)
        {
            var order = dexState.GetOrder(orderId);
            var next = dexState.GetOrderNext(orderId);

            if (order != null && !order.Value.Amount.IsZero)
            {
                if (order.Value.ExpiryBlock == 0 || currentBlock <= order.Value.ExpiryBlock)
                {
                    if (order.Value.IsBuy) buys.Add((orderId, order.Value));
                    else sells.Add((orderId, order.Value));
                }
            }

            orderId = next;
        }

        return (buys, sells);
    }

    private static Dictionary<(Address, Address), List<ParsedIntent>> GroupParsedIntentsByPair(
        List<ParsedIntent> intents, DexState dexState)
    {
        var groups = new Dictionary<(Address, Address), List<ParsedIntent>>();
        foreach (var intent in intents)
        {
            var (t0, t1) = DexEngine.SortTokens(intent.TokenIn, intent.TokenOut);
            if (!groups.TryGetValue((t0, t1), out var list))
            {
                list = [];
                groups[(t0, t1)] = list;
            }
            list.Add(intent);
        }
        return groups;
    }

    /// <summary>
    /// Compute the Merkle root of transaction hashes.
    /// </summary>
    public static Hash256 ComputeTransactionsRoot(List<Transaction> transactions)
    {
        if (transactions.Count == 0)
            return Hash256.Zero;

        var hashes = transactions.Select(tx => tx.Hash).ToList();
        return ComputeMerkleRoot(hashes);
    }

    /// <summary>
    /// Compute the Merkle root of receipt hashes.
    /// Each receipt is hashed from its content (status + gasUsed + logs hash) rather than
    /// the transaction hash, ensuring receipt integrity is independently verifiable.
    /// </summary>
    public static Hash256 ComputeReceiptsRoot(List<TransactionReceipt> receipts)
    {
        if (receipts.Count == 0)
            return Hash256.Zero;

        var hashes = receipts.Select(ComputeReceiptHash).ToList();
        return ComputeMerkleRoot(hashes);
    }

    private static Hash256 ComputeReceiptHash(TransactionReceipt receipt)
    {
        // Fixed-size format: [1 byte success][8 bytes gasUsed][32 bytes txHash][32 bytes logsHash]
        // logsHash is Hash256.Zero when there are no logs — eliminates variable-length ambiguity.
        Hash256 logsHash = Hash256.Zero;
        if (receipt.Logs.Count > 0)
        {
            using var logsHasher = Blake3Hasher.CreateIncremental();
            Span<byte> logEntry = stackalloc byte[Address.Size + Hash256.Size];
            foreach (var log in receipt.Logs)
            {
                log.Contract.WriteTo(logEntry[..Address.Size]);
                log.EventSignature.WriteTo(logEntry[Address.Size..]);
                logsHasher.Update(logEntry);
                if (log.Data.Length > 0)
                    logsHasher.Update(log.Data);
            }
            logsHash = logsHasher.Finalize();
        }

        // Fixed-size buffer: 1 + 8 + 32 + 32 = 73 bytes (always)
        Span<byte> buffer = stackalloc byte[1 + 8 + Hash256.Size + Hash256.Size];
        buffer[0] = receipt.Success ? (byte)1 : (byte)0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], receipt.GasUsed);
        receipt.TransactionHash.WriteTo(buffer[9..]);
        logsHash.WriteTo(buffer[(9 + Hash256.Size)..]);

        return Blake3Hasher.Hash(buffer);
    }

    /// <summary>
    /// Compute a binary Merkle tree root from a list of hashes.
    /// LOW-04: Uses domain separation bytes to distinguish leaves (0x00) from internal nodes (0x01),
    /// preventing second pre-image attacks where a leaf could be mistaken for an internal node.
    /// L-6: When the leaf count is odd, the last hash is promoted without re-hashing.
    /// </summary>
    private static Hash256 ComputeMerkleRoot(List<Hash256> hashes)
    {
        if (hashes.Count == 0)
            return Hash256.Zero;
        if (hashes.Count == 1)
            return hashes[0];

        // LOW-04: Hash leaves with 0x00 domain prefix
        var current = new List<Hash256>(hashes.Count);
        Span<byte> leafBuf = stackalloc byte[1 + Hash256.Size];
        leafBuf[0] = 0x00; // Leaf domain separator
        foreach (var h in hashes)
        {
            h.WriteTo(leafBuf[1..]);
            current.Add(Blake3Hasher.Hash(leafBuf));
        }

        // LOW-04: Hash internal nodes with 0x01 domain prefix
        Span<byte> nodeBuf = stackalloc byte[1 + Hash256.Size * 2];
        nodeBuf[0] = 0x01; // Internal node domain separator
        while (current.Count > 1)
        {
            var next = new List<Hash256>();
            for (int i = 0; i < current.Count; i += 2)
            {
                if (i + 1 < current.Count)
                {
                    current[i].WriteTo(nodeBuf[1..]);
                    current[i + 1].WriteTo(nodeBuf[(1 + Hash256.Size)..]);
                    next.Add(Blake3Hasher.Hash(nodeBuf));
                }
                else
                {
                    next.Add(current[i]); // L-6: Odd leaf promoted without re-hashing
                }
            }
            current = next;
        }

        return current[0];
    }
}
