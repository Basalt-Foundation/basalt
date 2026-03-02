using System.Runtime.InteropServices;
using Basalt.Core;
using Basalt.Execution.Dex;
using Basalt.Storage;

namespace Basalt.Execution;

/// <summary>
/// Transaction mempool for pending transactions.
/// Ordered by gas price (descending) then nonce (ascending).
/// </summary>
public sealed class Mempool
{
    private readonly object _lock = new();
    private readonly Dictionary<Hash256, Transaction> _transactions = new();
    private readonly SortedSet<MempoolEntry> _orderedEntries = new(MempoolEntryComparer.Instance);
    private readonly int _maxSize;

    // M-1: Per-sender transaction limit to prevent single-sender DoS
    private readonly Dictionary<Address, int> _perSenderCount = new();
    private const int MaxTransactionsPerSender = 64;

    /// <summary>
    /// Separate queue for DexSwapIntent transactions.
    /// Swap intents are batch-settled by the BlockBuilder rather than executed individually,
    /// so they need separate tracking for efficient retrieval during block building.
    /// </summary>
    private readonly Dictionary<Hash256, Transaction> _dexIntentTransactions = new();
    private readonly SortedSet<MempoolEntry> _dexIntentEntries = new(MempoolEntryComparer.Instance);

    /// <summary>
    /// Optional transaction validator for pre-admission validation (M-2).
    /// </summary>
    private readonly TransactionValidator? _validator;
    private readonly IStateDatabase? _validationStateDb;

    private UInt256 _currentBaseFee = UInt256.Zero;
    private readonly uint _maxTransactionDataBytes;

    /// <summary>
    /// Fired when a transaction is successfully added to the mempool.
    /// Used to trigger gossip for locally-submitted transactions.
    /// </summary>
    public event Action<Transaction>? OnTransactionAdded;

    public Mempool(int maxSize = 10_000)
    {
        _maxSize = maxSize;
    }

    /// <summary>
    /// M-2: Create a mempool with pre-admission transaction validation.
    /// </summary>
    public Mempool(int maxSize, TransactionValidator validator, IStateDatabase stateDb)
    {
        _maxSize = maxSize;
        _validator = validator;
        _validationStateDb = stateDb;
    }

    /// <summary>
    /// Create a mempool with pre-admission validation and data size limits.
    /// </summary>
    public Mempool(int maxSize, TransactionValidator validator, IStateDatabase stateDb, uint maxTransactionDataBytes)
        : this(maxSize, validator, stateDb)
    {
        _maxTransactionDataBytes = maxTransactionDataBytes;
    }

    public int Count
    {
        get { lock (_lock) return _transactions.Count + _dexIntentTransactions.Count; }
    }

    /// <summary>
    /// Add a transaction to the mempool.
    /// </summary>
    /// <param name="tx">The transaction to add.</param>
    /// <param name="raiseEvent">Whether to fire the OnTransactionAdded event. Set to false when
    /// the caller handles gossip separately (e.g., peer-received transactions).</param>
    public bool Add(Transaction tx, bool raiseEvent = true)
    {
        // CR-4: Read base fee under lock to prevent torn reads on multi-word UInt256
        UInt256 baseFee;
        lock (_lock) { baseFee = _currentBaseFee; }
        if (!baseFee.IsZero && tx.EffectiveMaxFee < baseFee)
            return false;
        if (_maxTransactionDataBytes > 0 && tx.Data.Length > _maxTransactionDataBytes)
            return false;

        // M-2: Pre-admission validation (signature, nonce, balance, gas)
        // MED-01: Fork the state DB to isolate validation reads from concurrent block execution writes.
        if (_validator != null && _validationStateDb != null)
        {
            var snapshot = _validationStateDb.Fork();
            var validation = _validator.Validate(tx, snapshot);
            if (!validation.IsSuccess)
                return false;
        }

        // Pre-validate plaintext intents: reject unparseable intents early
        if (tx.Type == TransactionType.DexSwapIntent && ParsedIntent.Parse(tx) == null)
            return false;

        // Route DexSwapIntent and DexEncryptedSwapIntent transactions to the separate intent pool
        if (tx.Type == TransactionType.DexSwapIntent || tx.Type == TransactionType.DexEncryptedSwapIntent)
            return AddToDexIntentPool(tx, raiseEvent);

        bool added;
        lock (_lock)
        {
            if (_transactions.ContainsKey(tx.Hash))
                return false;

            // M-1: Per-sender transaction limit
            _perSenderCount.TryGetValue(tx.Sender, out var senderCount);
            if (senderCount >= MaxTransactionsPerSender)
                return false;

            if (_transactions.Count >= _maxSize)
            {
                // M-3: Evict lowest-fee transaction if new tx has higher fee
                var lowestEntry = _orderedEntries.Max; // SortedSet: highest fee first, lowest last
                if (lowestEntry.Transaction != null && tx.EffectiveMaxFee > lowestEntry.Transaction.EffectiveMaxFee)
                {
                    // Evict the lowest-fee transaction
                    var evicted = lowestEntry.Transaction;
                    _transactions.Remove(evicted.Hash);
                    _orderedEntries.Remove(lowestEntry);
                    _perSenderCount.TryGetValue(evicted.Sender, out var evictedCount);
                    if (evictedCount > 1)
                        _perSenderCount[evicted.Sender] = evictedCount - 1;
                    else
                        _perSenderCount.Remove(evicted.Sender);
                }
                else
                {
                    return false; // Pool full and new tx is not better
                }
            }

            _transactions[tx.Hash] = tx;
            _orderedEntries.Add(new MempoolEntry(tx));
            _perSenderCount[tx.Sender] = senderCount + 1;
            added = true;
        }

        // Fire event outside the lock to prevent deadlocks
        if (added && raiseEvent)
            OnTransactionAdded?.Invoke(tx);

        return added;
    }

    /// <summary>
    /// Remove a transaction from the mempool.
    /// </summary>
    public bool Remove(Hash256 txHash)
    {
        lock (_lock)
        {
            if (!_transactions.Remove(txHash, out var tx))
                return false;

            _orderedEntries.Remove(new MempoolEntry(tx));
            DecrementSenderCount(tx.Sender);
            return true;
        }
    }

    /// <summary>
    /// Get the top N transactions ordered by priority, filtered to only include
    /// transactions with contiguous nonce sequences per sender.
    /// EXEC-10: Prevents nonce gap issues where a higher-nonce tx would fail execution
    /// because a required lower-nonce tx is missing from the batch.
    /// </summary>
    public List<Transaction> GetPending(int maxCount, IStateDatabase? stateDb = null)
    {
        lock (_lock)
        {
            if (stateDb == null)
                return _orderedEntries.Take(maxCount).Select(e => e.Transaction).ToList();

            // Group by sender, filter to contiguous nonces starting from on-chain nonce
            var bySender = new Dictionary<Address, List<Transaction>>();
            foreach (var entry in _orderedEntries)
            {
                var tx = entry.Transaction;
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(bySender, tx.Sender, out _);
                list ??= [];
                list.Add(tx);
            }

            var result = new List<Transaction>();
            foreach (var (sender, txs) in bySender)
            {
                var account = stateDb.GetAccount(sender);
                var expectedNonce = account?.Nonce ?? 0;

                // txs are already ordered by fee desc then nonce asc from _orderedEntries
                // Sort by nonce ascending to find contiguous sequence
                txs.Sort((a, b) => a.Nonce.CompareTo(b.Nonce));

                foreach (var tx in txs)
                {
                    if (tx.Nonce == expectedNonce)
                    {
                        result.Add(tx);
                        expectedNonce++;
                    }
                    else if (tx.Nonce > expectedNonce)
                    {
                        break; // Gap detected — stop for this sender
                    }
                    // tx.Nonce < expectedNonce: stale, skip
                }
            }

            // M-4: Re-sort by fee between senders but maintain nonce order within each sender.
            // Group by sender, pick the first (lowest nonce) tx from each sender, then interleave.
            var senderQueues = new Dictionary<Address, Queue<Transaction>>();
            foreach (var tx in result)
            {
                if (!senderQueues.TryGetValue(tx.Sender, out var queue))
                {
                    queue = new Queue<Transaction>();
                    senderQueues[tx.Sender] = queue;
                }
                queue.Enqueue(tx); // Already sorted by nonce ascending per sender
            }

            var final = new List<Transaction>();
            while (final.Count < maxCount && senderQueues.Count > 0)
            {
                // Pick the sender whose next tx has the highest fee
                Address bestSender = default;
                UInt256 bestFee = UInt256.Zero;
                bool found = false;
                foreach (var (sender, queue) in senderQueues)
                {
                    var fee = queue.Peek().EffectiveMaxFee;
                    if (!found || fee > bestFee)
                    {
                        bestSender = sender;
                        bestFee = fee;
                        found = true;
                    }
                }

                if (!found) break;

                var nextTx = senderQueues[bestSender].Dequeue();
                final.Add(nextTx);

                if (senderQueues[bestSender].Count == 0)
                    senderQueues.Remove(bestSender);
            }

            return final;
        }
    }

    /// <summary>
    /// Remove all transactions included in a block.
    /// </summary>
    public void RemoveConfirmed(IEnumerable<Transaction> confirmedTxs)
    {
        lock (_lock)
        {
            foreach (var tx in confirmedTxs)
            {
                if (_transactions.Remove(tx.Hash, out var existing))
                {
                    _orderedEntries.Remove(new MempoolEntry(existing));
                    DecrementSenderCount(existing.Sender);
                }
                else if (_dexIntentTransactions.Remove(tx.Hash, out var intentTx))
                {
                    _dexIntentEntries.Remove(new MempoolEntry(intentTx));
                    DecrementSenderCount(intentTx.Sender);
                }
            }
        }
    }

    /// <summary>
    /// Get pending DEX swap intents for batch auction settlement.
    /// Returns intents ordered by fee priority. Within the batch auction, order doesn't affect
    /// the clearing price (uniform pricing), but fee priority determines block inclusion.
    /// </summary>
    /// <param name="maxCount">Maximum number of intents to return.</param>
    /// <param name="stateDb">Optional state database for nonce validation.</param>
    /// <returns>List of swap intent transactions.</returns>
    public List<Transaction> GetPendingDexIntents(int maxCount, IStateDatabase? stateDb = null)
    {
        lock (_lock)
        {
            var result = new List<Transaction>();
            foreach (var entry in _dexIntentEntries)
            {
                if (result.Count >= maxCount) break;

                // Optional nonce check
                if (stateDb != null)
                {
                    var account = stateDb.GetAccount(entry.Transaction.Sender);
                    var expectedNonce = account?.Nonce ?? 0;
                    if (entry.Transaction.Nonce != expectedNonce)
                        continue;
                }

                result.Add(entry.Transaction);
            }
            return result;
        }
    }

    /// <summary>
    /// Get the number of pending DEX swap intents.
    /// </summary>
    public int DexIntentCount
    {
        get { lock (_lock) return _dexIntentTransactions.Count; }
    }

    private bool AddToDexIntentPool(Transaction tx, bool raiseEvent)
    {
        // CR-4: Read base fee under lock to prevent torn reads on multi-word UInt256
        UInt256 baseFee;
        lock (_lock) { baseFee = _currentBaseFee; }
        if (!baseFee.IsZero && tx.EffectiveMaxFee < baseFee)
            return false;
        if (_maxTransactionDataBytes > 0 && tx.Data.Length > _maxTransactionDataBytes)
            return false;

        bool added;
        lock (_lock)
        {
            if (_dexIntentTransactions.ContainsKey(tx.Hash))
                return false;
            if (_transactions.ContainsKey(tx.Hash))
                return false;

            // M-08: DEX intent pool size limit
            if (_dexIntentTransactions.Count >= _maxSize)
                return false;

            // M-1: Per-sender limit applies across both pools
            _perSenderCount.TryGetValue(tx.Sender, out var senderCount);
            if (senderCount >= MaxTransactionsPerSender)
                return false;

            _dexIntentTransactions[tx.Hash] = tx;
            _dexIntentEntries.Add(new MempoolEntry(tx));
            _perSenderCount[tx.Sender] = senderCount + 1;
            added = true;
        }

        if (added && raiseEvent)
            OnTransactionAdded?.Invoke(tx);

        return added;
    }

    /// <summary>
    /// Remove transactions that are no longer executable: stale nonces (already confirmed),
    /// gas price below the current base fee, or insufficient balance for value + gas.
    /// Returns the number of evicted transactions.
    /// </summary>
    public int PruneStale(IStateDatabase stateDb, UInt256 baseFee)
    {
        var toRemove = new List<Hash256>();
        var toRemoveIntents = new List<Hash256>();
        lock (_lock)
        {
            foreach (var tx in _transactions.Values)
            {
                var account = stateDb.GetAccount(tx.Sender);
                var onChainNonce = account?.Nonce ?? 0;

                // Stale: nonce already used (tx was confirmed or replaced)
                if (tx.Nonce < onChainNonce)
                {
                    toRemove.Add(tx.Hash);
                    continue;
                }

                // Underpriced: gas price can't cover the current base fee
                if (!baseFee.IsZero && tx.EffectiveMaxFee < baseFee)
                {
                    toRemove.Add(tx.Hash);
                    continue;
                }

                // Unaffordable: sender cannot cover value + gas at current balance
                var balance = account?.Balance ?? UInt256.Zero;
                var gasCost = tx.EffectiveMaxFee * new UInt256(tx.GasLimit);
                if (UInt256.TryAdd(tx.Value, gasCost, out var totalCost))
                {
                    if (balance < totalCost)
                        toRemove.Add(tx.Hash);
                }
                else
                {
                    // Overflow means cost is impossibly large — evict
                    toRemove.Add(tx.Hash);
                }
            }

            // M-09: Parallel pruning of DEX intent transactions
            foreach (var tx in _dexIntentTransactions.Values)
            {
                var account = stateDb.GetAccount(tx.Sender);
                var onChainNonce = account?.Nonce ?? 0;

                if (tx.Nonce < onChainNonce)
                {
                    toRemoveIntents.Add(tx.Hash);
                    continue;
                }

                if (!baseFee.IsZero && tx.EffectiveMaxFee < baseFee)
                {
                    toRemoveIntents.Add(tx.Hash);
                    continue;
                }

                // Unaffordable: sender cannot cover value + gas at current balance
                var balance = account?.Balance ?? UInt256.Zero;
                var gasCost = tx.EffectiveMaxFee * new UInt256(tx.GasLimit);
                if (UInt256.TryAdd(tx.Value, gasCost, out var totalCost))
                {
                    if (balance < totalCost)
                        toRemoveIntents.Add(tx.Hash);
                }
                else
                {
                    toRemoveIntents.Add(tx.Hash);
                }
            }

            foreach (var hash in toRemove)
            {
                if (_transactions.Remove(hash, out var existing))
                {
                    _orderedEntries.Remove(new MempoolEntry(existing));
                    DecrementSenderCount(existing.Sender);
                }
            }

            foreach (var hash in toRemoveIntents)
            {
                if (_dexIntentTransactions.Remove(hash, out var existing))
                {
                    _dexIntentEntries.Remove(new MempoolEntry(existing));
                    DecrementSenderCount(existing.Sender);
                }
            }
        }
        return toRemove.Count + toRemoveIntents.Count;
    }

    /// <summary>
    /// Update the current base fee used for admission gating.
    /// Called after each block finalization so newly submitted transactions
    /// that can't cover the current base fee are rejected early.
    /// </summary>
    // CR-4: Use lock to prevent torn reads on multi-word UInt256 struct
    public void UpdateBaseFee(UInt256 baseFee)
    {
        lock (_lock) { _currentBaseFee = baseFee; }
    }

    private void DecrementSenderCount(Address sender)
    {
        if (_perSenderCount.TryGetValue(sender, out var count))
        {
            if (count > 1)
                _perSenderCount[sender] = count - 1;
            else
                _perSenderCount.Remove(sender);
        }
    }

    public bool Contains(Hash256 txHash)
    {
        lock (_lock)
            return _transactions.ContainsKey(txHash) || _dexIntentTransactions.ContainsKey(txHash);
    }

    public Transaction? Get(Hash256 txHash)
    {
        lock (_lock)
        {
            if (_transactions.TryGetValue(txHash, out var tx))
                return tx;
            if (_dexIntentTransactions.TryGetValue(txHash, out var intentTx))
                return intentTx;
            return null;
        }
    }
}

internal readonly struct MempoolEntry
{
    public readonly Transaction Transaction;

    public MempoolEntry(Transaction tx)
    {
        Transaction = tx;
    }
}

internal sealed class MempoolEntryComparer : IComparer<MempoolEntry>
{
    public static readonly MempoolEntryComparer Instance = new();

    public int Compare(MempoolEntry x, MempoolEntry y)
    {
        // Higher effective max fee first (EIP-1559 aware)
        var feeCmp = y.Transaction.EffectiveMaxFee.CompareTo(x.Transaction.EffectiveMaxFee);
        if (feeCmp != 0) return feeCmp;

        // Higher priority tip as tiebreaker
        var tipCmp = y.Transaction.MaxPriorityFeePerGas.CompareTo(x.Transaction.MaxPriorityFeePerGas);
        if (tipCmp != 0) return tipCmp;

        // Lower nonce first
        var nonceCmp = x.Transaction.Nonce.CompareTo(y.Transaction.Nonce);
        if (nonceCmp != 0) return nonceCmp;

        // Fall back to hash for deterministic ordering
        return x.Transaction.Hash.CompareTo(y.Transaction.Hash);
    }
}
