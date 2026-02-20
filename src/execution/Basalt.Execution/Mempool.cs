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
                    _orderedEntries.Remove(new MempoolEntry(existing));
            }
        }
        return toRemove.Count;
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
