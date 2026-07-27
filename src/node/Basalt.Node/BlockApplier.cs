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
    /// Sync-durability hook (H4). Given the just-executed forked state and its new root, persists the
    /// batch's new trie nodes and returns a fresh disk-backed canonical state at that root. When null
    /// (in-memory / dev mode) the forked overlay is adopted directly. Without this, synced trie nodes
    /// stay in memory only, so a node that catches up via sync cannot survive a restart, and its state
    /// stops persisting (every later consensus write also lands in the in-memory overlay).
    /// </summary>
    private readonly Func<IStateDatabase, Hash256, IStateDatabase>? _syncStateCommit;

    /// <summary>
    /// Present so replay advances the nullifier retention window the way finalization does.
    ///
    /// COMPL-07 prunes nullifiers outside the window once per block, and it was called from exactly one
    /// place, the consensus callback. Replay never called it, so on a replaying node the set grew without
    /// bound and its window never applied. A nullifier the proposer had legitimately forgotten would read
    /// as a duplicate, and a transaction that finalized as success would replay as failure, which is a
    /// state divergence dressed as a compliance decision.
    /// </summary>
    private readonly IComplianceVerifier? _complianceVerifier;

    /// <summary>Points compliance lookups at the state being executed rather than at canonical.</summary>
    private readonly ExecutionStateRef? _executionState;

    /// <summary>Used to roll consumed nullifiers back with a batch that gets refused.</summary>
    private readonly Basalt.Compliance.ComplianceEngine? _complianceEngine;

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
        ILogger logger,
        Func<IStateDatabase, Hash256, IStateDatabase>? syncStateCommit = null,
        IComplianceVerifier? complianceVerifier = null,
        ExecutionStateRef? executionState = null,
        Basalt.Compliance.ComplianceEngine? complianceEngine = null)
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
        _syncStateCommit = syncStateCommit;
        _complianceVerifier = complianceVerifier;
        _executionState = executionState;
        _complianceEngine = complianceEngine;
    }

    /// <summary>
    /// Execute transactions and DEX settlement against the given state database.
    /// Does NOT add the block to the chain or persist — the caller controls that.
    /// Returns receipts (or null if no transactions).
    /// </summary>
    public List<TransactionReceipt>? ExecuteBlock(Block block, IStateDatabase stateDb)
    {
        // Before the block's transactions, matching the order finalization uses.
        _complianceVerifier?.ResetNullifiers(block.Number);

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

        // Materialise this block's state before moving to the next.
        //
        // Computing the root is also what folds each contract's storage root into its account, and the
        // batch path used to defer that to the end of the batch while the live path did it per block.
        // The two then landed on different states from the same blocks, so a node replaying history
        // refused at its first batch and could never join. Folding here makes both paths agree by
        // construction rather than by coincidence.
        stateDb.ComputeStateRoot();

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

        // Materialise the post-block state root.
        //
        // This is not just a check. ComputeStateRoot is what flushes pending storage-trie changes back
        // into account states and writes the resulting nodes, including the root itself. Without it the
        // applying node never creates a node for this block's state root: the header carries the root the
        // proposer computed, and the applier simply never materialises it. Historical state in the
        // retention window was therefore not walkable, which is what wedged trie pruning permanently.
        //
        // The cost is bounded: TrieStateDb caches the root and returns it directly when nothing has been
        // written since the last computation, so an empty block pays almost nothing.
        var computedStateRoot = stateDb.ComputeStateRoot();
        if (computedStateRoot != block.Header.StateRoot)
        {
            // Refused. This used to log and apply the block anyway, on the reasoning that the block was
            // already BFT-agreed and that refusing would halt production over something never observed.
            // It was then observed on every block carrying a contract call, and the cause was a stale
            // account cache rather than anything about the block. With that fixed, the roots agree, and
            // a node reaching a different state than the header claims has no business serving it.
            //
            // Halting is the point. A node that cannot reproduce the state cannot check anyone's work,
            // and one that continues anyway turns the state root into decoration. The sync path has
            // always refused; this makes the two paths answer the same anomaly the same way.
            _logger.LogCritical(
                "State root divergence at block #{Number}: computed {Computed}, header {Header}. "
                + "Refusing the block. This node will not advance until the cause is understood.",
                block.Number, computedStateRoot.ToHexString(), block.Header.StateRoot.ToHexString());
            MetricsEndpoint.RecordStateRootDivergence();

            // The block was executed before it was checked, so those mutations are already on the state
            // while the chain stays one block behind them. Shutdown flushes the flat cache, which would
            // write that mismatch to disk and reload it on the next start: the node would then diverge
            // again from a state no peer shares, with no way back. The trie on disk is still the one the
            // last accepted block was checked against, so refusing to persist keeps a restart recoverable.
            var backing = stateDb is StateDbRef reference ? reference.Inner : stateDb;
            (backing as FlatStateDb)?.MarkInconsistent();

            return new BlockApplyResult
            {
                Success = false,
                Error = $"State root divergence at block #{block.Number}: computed "
                        + $"{computedStateRoot.ToHexString()}, header {block.Header.StateRoot.ToHexString()}",
            };
        }

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

        // Compact canonical state DB tracking sets to prevent unbounded memory growth.
        // _dirtyStorageKeys/_dirtyAccounts are only used by fork→parent merge and grow
        // forever on the canonical instance. _deletedStorage/_deletedAccounts are redundant
        // after the trie's Delete() removed the keys. Without this, these HashSets accumulate
        // entries every block (especially from TWAP snapshot writes/prunes) and eventually
        // cause Gen 2 GC pressure → 100% CPU on long-running nodes.
        stateDb.ClearDirtyTracking();
        stateDb.CompactDeletedSets();

        // Process completed unbonding entries to prevent queue growth
        _stakingState?.ProcessUnbonding(block.Number);

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

        // Fast path: if the first block is already applied by consensus, check if ALL
        // are already applied. This avoids the expensive Fork() → ComputeStateRoot() call
        // that otherwise runs on every sync attempt even when consensus already finalized
        // these blocks. On a 4-validator idle chain, this saves ~3 Merkle trie rehashes
        // per block per node.
        var firstBlock = blocks[0].Block;
        if (firstBlock.Number <= _chainManager.LatestBlockNumber)
        {
            var allAlreadyApplied = true;
            foreach (var (block, raw, bitmap) in blocks)
            {
                var existing = _chainManager.GetBlockByNumber(block.Number);
                if (existing == null || existing.Hash != block.Hash)
                {
                    allAlreadyApplied = false;
                    break;
                }
            }

            if (allAlreadyApplied)
            {
                // Still record epoch data for these blocks
                foreach (var (block, _, bitmap) in blocks)
                {
                    _epochManager?.RecordBlockSigners(block.Number, bitmap);
                    var newSet = _epochManager?.OnBlockFinalized(block.Number);
                    if (newSet != null)
                        ApplyEpochTransition(newSet, block.Number);
                }
                return blocks.Count;
            }
        }

        var forkedState = stateDbRef.Fork();
        // Canonical height the fork is based on. Used below to tell a benign consensus/sync race (the
        // canonical tip advanced into this batch, so the fork re-applies an already-applied prefix) from
        // a genuine state divergence.
        var forkBaseHeight = _chainManager.LatestBlockNumber;
        var applied = 0;

        // Compliance resolves verifying keys out of contract storage, and a key registered by an
        // earlier block in this batch lives on the fork, not on canonical, until phase 3 swaps it.
        using var _executionScope = _executionState?.Use(forkedState);

        // Nullifiers live on the verifier and not in the fork, so a refused batch would otherwise keep
        // them consumed and every retry would replay as a duplicate.
        var nullifierSnapshot = _complianceEngine?.SnapshotNullifiers();

        // Phase 1: Execute all blocks on forked state
        var phase1Complete = true;
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
                phase1Complete = false;
                break;
            }
        }

        // A block failed to execute mid-batch: the fork holds only a prefix, so do NOT advance the chain
        // with a partial batch (the old code applied the prefix to the chain index but skipped the state
        // swap, splitting chain height from state). This is an execution failure, not a divergence, so it
        // is already logged at Warning above; just abort the batch.
        if (!phase1Complete)
        {
            _complianceEngine?.RestoreNullifiers(nullifierSnapshot);
            return 0;
        }

        // Phase 1.5: State-root gate (soundness). Verify our recomputed state matches the finalized chain
        // BEFORE advancing the chain. The replay path used the single-arg AddBlock, so ChainManager's
        // state-root check (which only fires when a computed root is supplied) never ran on sync — any
        // execution divergence was silently accepted, forking the node off canonical. Checking here makes
        // the batch atomic: a mismatch aborts with the node still at its previous canonical state, rather
        // than advancing the chain (Phase 2) without swapping the state (Phase 3).
        var computedRoot = forkedState.ComputeStateRoot();
        var expectedRoot = blocks[^1].Block.Header.StateRoot;
        if (computedRoot != expectedRoot)
        {
            if (firstBlock.Number <= forkBaseHeight)
            {
                // Benign race: consensus finalized part of this batch while we synced, so re-executing the
                // already-applied prefix on the fork double-applies it and the root no longer matches.
                // Self-corrects on the next poll with a clean forward batch. Not a divergence.
                _logger.LogInformation(
                    "Sync batch ending #{Height} superseded by consensus (root mismatch from re-applied " +
                    "prefix); retrying with a forward batch.", blocks[^1].Block.Number);
            }
            else
            {
                _logger.LogCritical(
                    "Sync state-root divergence at batch ending #{Height}: expected header root {Expected}, " +
                    "computed {Computed}. Refusing the batch; node stays at #{Current} (no chain advance).",
                    blocks[^1].Block.Number, expectedRoot.ToHexString(), computedRoot.ToHexString(),
                    _chainManager.LatestBlockNumber);
            }

            // The fork is discarded here, so the nullifiers consumed on it go back too. Both the benign
            // race and the real divergence retry, and a retry that saw its own proofs as replays would
            // never succeed.
            _complianceEngine?.RestoreNullifiers(nullifierSnapshot);
            return 0;
        }

        // Phase 2: Add executed blocks to chain and persist
        var skippedAsAlreadyApplied = 0;
        foreach (var (block, raw, bitmap) in blocks)
        {
            if (block.Receipts == null && block.Transactions.Count > 0)
                break; // This block wasn't executed (failed in phase 1)

            var result = _chainManager.AddBlock(block);
            if (!result.IsSuccess)
            {
                // Race tolerance: if consensus applied this block concurrently,
                // it will already be in the chain with the same hash. Treat as
                // progress so the sync loop doesn't stall.
                var existing = _chainManager.GetBlockByNumber(block.Number);
                if (existing != null && existing.Hash == block.Hash)
                {
                    applied++;
                    skippedAsAlreadyApplied++;
                    // Still process epoch transitions for this block
                    _epochManager?.RecordBlockSigners(block.Number, bitmap);
                    var newSet2 = _epochManager?.OnBlockFinalized(block.Number);
                    if (newSet2 != null)
                        ApplyEpochTransition(newSet2, block.Number);
                    continue;
                }

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

        // Phase 3: Atomically swap state only if ALL blocks succeeded and at least
        // one was genuinely new (not already applied by consensus). If every block
        // was skipped as already-applied, the canonical state is already correct and
        // swapping our fork (based on a potentially stale snapshot) could regress it.
        var newlyApplied = applied - skippedAsAlreadyApplied;
        if (applied == blocks.Count && newlyApplied > 0)
        {
            // Compact tracking sets on the fork before it becomes canonical.
            // Without this, _dirtyStorageKeys/_deletedStorage accumulate on
            // sync-only nodes (which never call ApplyBlock's cleanup path).
            forkedState.ClearDirtyTracking();
            forkedState.CompactDeletedSets();

            if (_syncStateCommit != null)
            {
                // H4: the batch's new trie nodes currently live only in the in-memory fork overlay.
                // Persist them and adopt a fresh disk-backed canonical state at the new root, so a
                // synced node survives a restart and does not accumulate an unbounded overlay stack
                // (which would also stop every subsequent consensus write from persisting).
                // Reuse the root already computed and verified by the Phase 1.5 gate above.
                stateDbRef.Swap(_syncStateCommit(forkedState, computedRoot));
            }
            else
            {
                stateDbRef.Swap(forkedState);
            }
            _logger.LogInformation("Synced {Count} blocks, now at #{Height}",
                newlyApplied, _chainManager.LatestBlockNumber);
        }
        else if (applied == blocks.Count && skippedAsAlreadyApplied > 0)
        {
            _logger.LogDebug("All {Count} synced blocks already applied by consensus", skippedAsAlreadyApplied);
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
