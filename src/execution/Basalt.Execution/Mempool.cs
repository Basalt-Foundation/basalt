using Basalt.Core;

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

    /// <summary>
    /// Fired when a transaction is successfully added to the mempool.
    /// Used to trigger gossip for locally-submitted transactions.
    /// </summary>
    public event Action<Transaction>? OnTransactionAdded;

    public Mempool(int maxSize = 10_000)
    {
        _maxSize = maxSize;
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
        bool added;
        lock (_lock)
        {
            if (_transactions.ContainsKey(tx.Hash))
                return false;

            if (_transactions.Count >= _maxSize)
                return false;

            _transactions[tx.Hash] = tx;
            _orderedEntries.Add(new MempoolEntry(tx));
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
            return true;
        }
    }

    /// <summary>
    /// Get the top N transactions ordered by priority.
    /// </summary>
    public List<Transaction> GetPending(int maxCount)
    {
        lock (_lock)
        {
            return _orderedEntries.Take(maxCount).Select(e => e.Transaction).ToList();
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
                    _orderedEntries.Remove(new MempoolEntry(existing));
            }
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
        // Higher gas price first
        var gasCmp = y.Transaction.GasPrice.CompareTo(x.Transaction.GasPrice);
        if (gasCmp != 0) return gasCmp;

        // Lower nonce first
        var nonceCmp = x.Transaction.Nonce.CompareTo(y.Transaction.Nonce);
        if (nonceCmp != 0) return nonceCmp;

        // Fall back to hash for deterministic ordering
        return x.Transaction.Hash.CompareTo(y.Transaction.Hash);
    }
}
