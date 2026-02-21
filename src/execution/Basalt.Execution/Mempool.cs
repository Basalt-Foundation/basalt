using System.Runtime.InteropServices;
using Basalt.Core;
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
    /// Optional transaction validator for pre-admission validation (M-2).
    /// </summary>
    private readonly TransactionValidator? _validator;
    private readonly IStateDatabase? _validationStateDb;

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

    public int Count
    {
        get { lock (_lock) return _transactions.Count; }
    }

    /// <summary>
    /// Add a transaction to the mempool.
    /// </summary>
    /// <param name="tx">The transaction to add.</param>
    /// <param name="raiseEvent">Whether to fire the OnTransactionAdded event. Set to false when
    /// the caller handles gossip separately (e.g., peer-received transactions).</param>
    public bool Add(Transaction tx, bool raiseEvent = true)
    {
        // M-2: Pre-admission validation (signature, nonce, balance, gas)
        if (_validator != null && _validationStateDb != null)
        {
            var validation = _validator.Validate(tx, _validationStateDb);
            if (!validation.IsSuccess)
                return false;
        }

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
            }
        }
    }

    /// <summary>
    /// Remove transactions that are no longer executable: stale nonces (already confirmed)
    /// or gas price below the current base fee.
    /// Returns the number of evicted transactions.
    /// </summary>
    public int PruneStale(IStateDatabase stateDb, UInt256 baseFee)
    {
        var toRemove = new List<Hash256>();
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
        }
        return toRemove.Count;
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
            return _transactions.ContainsKey(txHash);
    }

    public Transaction? Get(Hash256 txHash)
    {
        lock (_lock)
            return _transactions.TryGetValue(txHash, out var tx) ? tx : null;
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
