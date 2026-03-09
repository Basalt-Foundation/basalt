using Basalt.Consensus;
using Basalt.Consensus.Staking;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;
using Basalt.Storage.RocksDb;
using Basalt.Api.Rest;
using Microsoft.Extensions.Logging;

namespace Basalt.Node;

/// <summary>
/// Result of applying a single block to state.
/// </summary>
public sealed class BlockApplyResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public List<TransactionReceipt>? Receipts { get; init; }
}

/// <summary>
/// Shared block-application logic used by NodeCoordinator (consensus finalization + sync)
/// and BlockSyncService (RPC node HTTP sync). Encapsulates:
/// <list type="number">
/// <item>Transaction execution via <see cref="TransactionExecutor"/></item>
/// <item>DEX settlement via <see cref="BlockBuilder.ApplyDexSettlement"/></item>
/// <item>Chain state update via <see cref="ChainManager.AddBlock"/></item>
/// <item>Mempool pruning and base fee update</item>
/// <item>RocksDB persistence (blocks + receipts)</item>
/// <item>Epoch transitions via <see cref="EpochManager"/></item>
/// <item>WebSocket broadcast and Prometheus metrics</item>
/// </list>
/// </summary>
public sealed class BlockApplier
{
    private readonly ChainParameters _chainParams;
    private readonly ChainManager _chainManager;
    private readonly Mempool _mempool;
    private readonly TransactionExecutor _txExecutor;
    private readonly BlockBuilder? _blockBuilder;
    private readonly BlockStore? _blockStore;
    private readonly ReceiptStore? _receiptStore;
    private readonly EpochManager? _epochManager;
    private readonly StakingState? _stakingState;
    private readonly IStakingPersistence? _stakingPersistence;
    private readonly WebSocketHandler _wsHandler;
    private readonly ILogger _logger;

    /// <summary>
    /// Fired when an epoch transition occurs. The caller (NodeCoordinator) can hook this
    /// to rewire consensus-specific components (leader selector, consensus engine).
    /// Provides the new ValidatorSet and the block number at which the transition occurred.
    /// </summary>
    public event Action<ValidatorSet, ulong>? OnEpochTransition;

    public BlockApplier(
        ChainParameters chainParams,
        ChainManager chainManager,
        Mempool mempool,
        TransactionExecutor txExecutor,
        BlockBuilder? blockBuilder,
        BlockStore? blockStore,
        ReceiptStore? receiptStore,
        EpochManager? epochManager,
        StakingState? stakingState,
        IStakingPersistence? stakingPersistence,
        WebSocketHandler wsHandler,
        ILogger logger)
    {
        _chainParams = chainParams;
        _chainManager = chainManager;
        _mempool = mempool;
        _txExecutor = txExecutor;
        _blockBuilder = blockBuilder;
        _blockStore = blockStore;
        _receiptStore = receiptStore;
        _epochManager = epochManager;
        _stakingState = stakingState;
        _stakingPersistence = stakingPersistence;
        _wsHandler = wsHandler;
        _logger = logger;
    }

    /// <summary>
    /// Execute transactions and DEX settlement against the given state database.
    /// Does NOT add the block to the chain or persist — the caller controls that.
    /// Returns receipts (or null if no transactions).
    /// </summary>
    public List<TransactionReceipt>? ExecuteBlock(Block block, IStateDatabase stateDb)
    {
        List<TransactionReceipt>? receipts = null;

        if (block.Transactions.Count > 0)
        {
            receipts = new List<TransactionReceipt>(block.Transactions.Count);
            for (int i = 0; i < block.Transactions.Count; i++)
            {
                var receipt = _txExecutor.Execute(block.Transactions[i], stateDb, block.Header, i);
                receipts.Add(receipt);
            }
        }

        // Run DEX settlement (TWAP carry-forward + limit order matching)
        if (_blockBuilder != null)
        {
            var dexReceipts = _blockBuilder.ApplyDexSettlement(stateDb, block.Header);
            if (dexReceipts.Count > 0)
            {
                receipts ??= new List<TransactionReceipt>();
                receipts.AddRange(dexReceipts);
            }
        }

        return receipts;
    }

    /// <summary>
    /// Apply a single finalized block to canonical state. Used by consensus finalization path.
    /// Executes transactions, adds to chain, prunes mempool, persists, checks epochs, broadcasts.
    /// </summary>
    /// <returns>Result indicating success/failure.</returns>
    public BlockApplyResult ApplyBlock(Block block, IStateDatabase stateDb,
        byte[] rawBlockData, ulong commitBitmap = 0)
    {
        // Execute transactions + DEX settlement
        var receipts = ExecuteBlock(block, stateDb);
        if (receipts != null)
            block.Receipts = receipts;

        // Add to chain
        var result = _chainManager.AddBlock(block);
        if (!result.IsSuccess)
        {
            return new BlockApplyResult { Success = false, Error = result.Message };
        }

        // Prune mempool
        _mempool.RemoveConfirmed(block.Transactions);
        var pruned = _mempool.PruneStale(stateDb, block.Header.BaseFee);
        if (pruned > 0)
            _logger.LogInformation("Pruned {Count} unexecutable transactions from mempool", pruned);
        _mempool.UpdateBaseFee(block.Header.BaseFee);

        // Prometheus metrics
        MetricsEndpoint.RecordBlock(block.Transactions.Count, block.Header.Timestamp);
        MetricsEndpoint.RecordBaseFee(block.Header.BaseFee.IsZero ? 0 : (long)(ulong)block.Header.BaseFee);
        MetricsEndpoint.RecordConsensusView((long)block.Number);
        MetricsEndpoint.RecordDexIntentCount(_mempool.DexIntentCount);

        // WebSocket broadcast
        _ = _wsHandler.BroadcastNewBlock(block);

        // Persist block + receipts
        PersistBlock(block, rawBlockData, commitBitmap);
        PersistReceipts(block.Receipts);

        // Record commit participation
        _epochManager?.RecordBlockSigners(block.Number, commitBitmap);

        // Check epoch transition
        var newSet = _epochManager?.OnBlockFinalized(block.Number);
        if (newSet != null)
        {
            ApplyEpochTransition(newSet, block.Number);
        }

        return new BlockApplyResult
        {
            Success = true,
            Receipts = block.Receipts,
        };
    }

    /// <summary>
    /// Apply a batch of blocks on a forked state database, then atomically swap canonical state.
    /// Used by sync paths (P2P sync and RPC HTTP sync).
    /// </summary>
    /// <param name="blocks">Ordered list of (Block, RawBytes, CommitBitmap) tuples.</param>
    /// <param name="stateDbRef">The shared state reference to fork and swap.</param>
    /// <returns>Number of blocks successfully applied.</returns>
    public int ApplyBatch(IReadOnlyList<(Block Block, byte[] Raw, ulong CommitBitmap)> blocks,
        StateDbRef stateDbRef)
    {
        if (blocks.Count == 0)
            return 0;

        var forkedState = stateDbRef.Fork();
        var applied = 0;

        // Phase 1: Execute all blocks on forked state
        foreach (var (block, raw, bitmap) in blocks)
        {
            try
            {
                var receipts = ExecuteBlock(block, forkedState);
                if (receipts != null)
                    block.Receipts = receipts;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to execute synced block #{Number}", block.Number);
                break;
            }
        }

        // Phase 2: Add executed blocks to chain and persist
        foreach (var (block, raw, bitmap) in blocks)
        {
            if (block.Receipts == null && block.Transactions.Count > 0)
                break; // This block wasn't executed (failed in phase 1)

            var result = _chainManager.AddBlock(block);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Failed to apply synced block #{Number}: {Error}",
                    block.Number, result.Message);
                break;
            }

            _mempool.RemoveConfirmed(block.Transactions);
            PersistBlock(block, raw, bitmap);
            PersistReceipts(block.Receipts);
            applied++;

            _epochManager?.RecordBlockSigners(block.Number, bitmap);

            var newSet = _epochManager?.OnBlockFinalized(block.Number);
            if (newSet != null)
                ApplyEpochTransition(newSet, block.Number);
        }

        // Phase 3: Atomically swap state only if ALL blocks succeeded
        if (applied == blocks.Count && applied > 0)
        {
            stateDbRef.Swap(forkedState);
            _logger.LogInformation("Synced {Count} blocks, now at #{Height}",
                applied, _chainManager.LatestBlockNumber);
        }
        else if (applied > 0)
        {
            _logger.LogWarning(
                "Partial sync: applied {Applied}/{Total} blocks — state not adopted",
                applied, blocks.Count);
        }

        // WebSocket broadcast + metrics for the latest synced block
        if (applied > 0)
        {
            var lastApplied = blocks[applied - 1].Block;
            _ = _wsHandler.BroadcastNewBlock(lastApplied);
            MetricsEndpoint.RecordBlock(lastApplied.Transactions.Count, lastApplied.Header.Timestamp);
            MetricsEndpoint.RecordBaseFee(lastApplied.Header.BaseFee.IsZero ? 0 : (long)(ulong)lastApplied.Header.BaseFee);
            MetricsEndpoint.RecordConsensusView((long)lastApplied.Number);
        }

        // Prune mempool after sync with current base fee
        var latestBlock = _chainManager.LatestBlock;
        if (latestBlock != null && applied > 0)
        {
            var pruned = _mempool.PruneStale(stateDbRef, latestBlock.Header.BaseFee);
            if (pruned > 0)
                _logger.LogInformation("Pruned {Count} unexecutable transactions from mempool after sync", pruned);
            _mempool.UpdateBaseFee(latestBlock.Header.BaseFee);
        }

        return applied;
    }

    private void ApplyEpochTransition(ValidatorSet newSet, ulong blockNumber)
    {
        // Flush staking state
        if (_stakingPersistence != null && _stakingState != null)
        {
            try
            {
                _stakingState.FlushToPersistence(_stakingPersistence);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush staking state after epoch transition");
            }
        }

        _logger.LogInformation(
            "Epoch transition at block #{Block}: {NewCount} validators, quorum: {Quorum}",
            blockNumber, newSet.Count, newSet.QuorumThreshold);

        // Notify caller (NodeCoordinator) to rewire consensus-specific components
        OnEpochTransition?.Invoke(newSet, blockNumber);
    }

    private void PersistBlock(Block block, byte[] serializedBlockData, ulong? commitBitmap = null)
    {
        if (_blockStore == null)
            return;

        try
        {
            var blockData = new BlockData
            {
                Number = block.Number,
                Hash = block.Hash,
                ParentHash = block.Header.ParentHash,
                StateRoot = block.Header.StateRoot,
                TransactionsRoot = block.Header.TransactionsRoot,
                ReceiptsRoot = block.Header.ReceiptsRoot,
                Timestamp = block.Header.Timestamp,
                Proposer = block.Header.Proposer,
                ChainId = block.Header.ChainId,
                GasUsed = block.Header.GasUsed,
                GasLimit = block.Header.GasLimit,
                BaseFee = block.Header.BaseFee,
                ProtocolVersion = block.Header.ProtocolVersion,
                ExtraData = block.Header.ExtraData,
                TransactionHashes = block.Transactions.Select(t => t.Hash).ToArray(),
            };
            _blockStore.PutFullBlock(blockData, serializedBlockData, commitBitmap);
            _blockStore.SetLatestBlockNumber(block.Number);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist block #{Number}", block.Number);
        }
    }

    private void PersistReceipts(List<TransactionReceipt>? receipts)
    {
        if (_receiptStore == null || receipts == null || receipts.Count == 0)
            return;

        try
        {
            var receiptDataList = receipts.Select(r => new ReceiptData
            {
                TransactionHash = r.TransactionHash,
                BlockHash = r.BlockHash,
                BlockNumber = r.BlockNumber,
                TransactionIndex = r.TransactionIndex,
                From = r.From,
                To = r.To,
                GasUsed = r.GasUsed,
                Success = r.Success,
                ErrorCode = (int)r.ErrorCode,
                PostStateRoot = r.PostStateRoot,
                EffectiveGasPrice = r.EffectiveGasPrice,
                Logs = (r.Logs ?? []).Select(l => new LogData
                {
                    Contract = l.Contract,
                    EventSignature = l.EventSignature,
                    Topics = l.Topics ?? [],
                    Data = l.Data ?? [],
                }).ToArray(),
            });
            _receiptStore.PutReceipts(receiptDataList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {Count} receipts", receipts.Count);
        }
    }
}
