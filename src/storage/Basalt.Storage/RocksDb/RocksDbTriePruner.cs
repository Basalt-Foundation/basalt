using Basalt.Core;
using Basalt.Storage.Trie;

namespace Basalt.Storage.RocksDb;

/// <summary>
/// Mark-and-sweep garbage collector for the <c>trie_nodes</c> column family. The state trie is
/// append-only copy-on-write and never overwrites a node, so every historical version accumulates on
/// disk forever unless swept. This pruner deletes nodes not reachable from a caller-supplied set of
/// retained state roots (typically genesis + a sliding window ≥ MaxRollbackDepth + the current tip).
/// </summary>
/// <remarks>
/// <para><b>Safety guards</b> (a wrong sweep corrupts state irrecoverably):</para>
/// <list type="number">
/// <item><b>Storage-aware mark.</b> Reachability is computed by <see cref="TrieReachability"/>, which
/// descends into every account's storage sub-trie. The world-only walk misses storage nodes.</item>
/// <item><b>Single pinned snapshot.</b> Mark and enumerate both read one <see cref="SnapshotView"/>, so
/// they see an identical database version — a node added after the snapshot is never enumerated, and a
/// node reachable as of the snapshot is never missed.</item>
/// <item><b>Abort on missing.</b> If a retained root references a node absent from the snapshot, the
/// mark throws and this sweep deletes nothing (a corrupt/incomplete store must never be swept).</item>
/// <item><b>Deletion tripwire.</b> If the candidate-delete fraction exceeds
/// <see cref="TriePrunerOptions.MaxDeleteFraction"/>, the sweep aborts without deleting — a guard against
/// a marking bug that under-counts reachable nodes.</item>
/// <item><b>Bounded batches.</b> Deletes commit in <see cref="TriePrunerOptions.DeleteBatchSize"/> chunks
/// so a single write batch never grows unbounded.</item>
/// </list>
/// <para><b>Concurrency contract (caller's responsibility, guard 6).</b> The caller MUST prevent
/// concurrent state mutation (block-commit <c>Swap</c>, rollback, sync re-root) for the duration of a
/// <see cref="Prune"/> call. Content-addressing means a node stale as of the snapshot could be
/// resurrected by a later block with identical content and the same hash; deleting it would then corrupt
/// the new tip. Serializing the sweep against state mutation closes that race. Compaction
/// (<see cref="RocksDbStore.CompactColumnFamily"/>) is slow and MUST run outside that lock.</para>
/// </remarks>
public sealed class RocksDbTriePruner
{
    private readonly RocksDbStore _store;
    private readonly TriePrunerOptions _options;

    public RocksDbTriePruner(RocksDbStore store, TriePrunerOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? new TriePrunerOptions();
    }

    /// <summary>
    /// Sweeps <c>trie_nodes</c>, deleting every node not reachable from <paramref name="retainedRoots"/>.
    /// Does not compact — the caller compacts out-of-lock afterwards (see class remarks).
    /// </summary>
    /// <exception cref="InvalidOperationException">A retained root references a missing node (guard 3).</exception>
    /// <exception cref="TriePruneAbortedException">The deletion tripwire fired (guard 4).</exception>
    public TriePruneStats Prune(IReadOnlyCollection<Hash256> retainedRoots)
    {
        ArgumentNullException.ThrowIfNull(retainedRoots);

        using var snapshot = _store.CreateSnapshotView();

        // Guard 1 + 2 + 3: storage-aware mark over the same pinned snapshot; throws on any missing node.
        var reader = new SnapshotTrieNodeStore(snapshot);
        HashSet<Hash256> reachable;
        try
        {
            reachable = TrieReachability.CollectReachable(reader, retainedRoots);
        }
        catch (MissingTrieNodeException ex)
        {
            // Two very different faults produce the same symptom here, and the fix differs completely, so
            // establish which one it is instead of guessing. If the node is readable from the live store
            // it exists and the pinned snapshot simply cannot see it, which is a visibility problem. If it
            // is absent from both, it was deleted or never written, which is a durability problem.
            throw new MissingTrieNodeDiagnosisException(ex.Hash, ex.Root, LiveStoreHas(ex.Hash), ex);
        }

        // Enumerate the snapshot and partition into keep/delete.
        long totalScanned = 0;
        var toDelete = new List<byte[]>();
        foreach (var (key, _) in snapshot.Iterate(RocksDbStore.CF.TrieNodes))
        {
            totalScanned++;
            // Keys are raw 32-byte node hashes. Anything else is not ours — leave it untouched.
            if (key.Length != Hash256.Size)
                continue;
            if (!reachable.Contains(new Hash256(key)))
                toDelete.Add(key);
        }

        // Guard 4: refuse to delete an implausibly large fraction — signals an under-counted mark.
        if (totalScanned > 0 && toDelete.Count > totalScanned * _options.MaxDeleteFraction)
        {
            throw new TriePruneAbortedException(
                $"Trie prune aborted: would delete {toDelete.Count}/{totalScanned} nodes " +
                $"({(double)toDelete.Count / totalScanned:P1} > {_options.MaxDeleteFraction:P0} tripwire). " +
                "Reachability mark is suspiciously small; refusing to sweep.");
        }

        // Guard 5: bounded batched deletes.
        int deleted = 0;
        for (int offset = 0; offset < toDelete.Count; offset += _options.DeleteBatchSize)
        {
            int count = Math.Min(_options.DeleteBatchSize, toDelete.Count - offset);
            using var batch = _store.CreateWriteBatch();
            for (int i = 0; i < count; i++)
                batch.Delete(RocksDbStore.CF.TrieNodes, toDelete[offset + i]);
            batch.Commit();
            deleted += count;
        }

        return new TriePruneStats(totalScanned, deleted, reachable.Count);
    }

    /// <summary>
    /// Compacts <c>trie_nodes</c> to reclaim the disk space freed by the last <see cref="Prune"/>. Slow;
    /// call outside any consensus/state lock (see class remarks).
    /// </summary>
    public void Compact() => _store.CompactColumnFamily(RocksDbStore.CF.TrieNodes);

    private bool LiveStoreHas(Hash256 hash)
    {
        Span<byte> key = stackalloc byte[Hash256.Size];
        hash.WriteTo(key);
        try
        {
            return _store.Get(RocksDbStore.CF.TrieNodes, key) != null;
        }
        catch
        {
            return false; // a probe must never mask the original fault
        }
    }
}

/// <summary>Tuning for <see cref="RocksDbTriePruner"/>.</summary>
public sealed class TriePrunerOptions
{
    /// <summary>Nodes deleted per write batch (guard 5). Default 10 000.</summary>
    public int DeleteBatchSize { get; init; } = 10_000;

    /// <summary>
    /// Abort the sweep if the fraction of nodes marked for deletion exceeds this (guard 4). Default 0.95
    /// — a healthy state keeps a large live working set, so deleting nearly everything signals a marking
    /// bug, not legitimate garbage.
    /// </summary>
    public double MaxDeleteFraction { get; init; } = 0.95;
}

/// <summary>Result of a <see cref="RocksDbTriePruner.Prune"/> sweep.</summary>
/// <param name="TotalScanned">Nodes present in <c>trie_nodes</c> at snapshot time.</param>
/// <param name="Deleted">Nodes deleted (unreachable from any retained root).</param>
/// <param name="Retained">Nodes reachable from the retained roots (kept).</param>
public readonly record struct TriePruneStats(long TotalScanned, int Deleted, int Retained);

/// <summary>Thrown when a sweep aborts on the deletion tripwire (guard 4); nothing was deleted.</summary>
public sealed class TriePruneAbortedException : Exception
{
    public TriePruneAbortedException(string message) : base(message) { }
}

/// <summary>Read-only <see cref="ITrieNodeStore"/> over a pinned <see cref="SnapshotView"/> (mark phase).</summary>
internal sealed class SnapshotTrieNodeStore : ITrieNodeStore
{
    private readonly SnapshotView _snapshot;

    public SnapshotTrieNodeStore(SnapshotView snapshot) => _snapshot = snapshot;

    public TrieNode? Get(Hash256 hash)
    {
        Span<byte> keyBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(keyBytes);
        var data = _snapshot.Get(RocksDbStore.CF.TrieNodes, keyBytes);
        return data == null ? null : TrieNode.Decode(data);
    }

    public void Put(Hash256 hash, TrieNode node)
        => throw new NotSupportedException("SnapshotTrieNodeStore is read-only.");

    public void Delete(Hash256 hash)
        => throw new NotSupportedException("SnapshotTrieNodeStore is read-only.");
}

/// <summary>
/// A missing retained node, with the answer to the one question that separates a snapshot-visibility bug
/// from a durability bug: was the node readable from the live store at the moment the sweep failed.
/// </summary>
public sealed class MissingTrieNodeDiagnosisException(Hash256 hash, Hash256 root, bool presentInLiveStore, Exception inner)
    : InvalidOperationException(
        $"Trie prune aborted: node {hash.ToHexString()} is referenced by retained root {root.ToHexString()} "
        + $"but missing from the pinned snapshot. Present in the live store: {presentInLiveStore}. "
        + (presentInLiveStore
            ? "The node exists, so the snapshot cannot see it: a visibility fault, not a lost node."
            : "The node is absent from the live store too: it was deleted or never written."),
        inner)
{
    /// <summary>The node that could not be read.</summary>
    public Hash256 Hash { get; } = hash;

    /// <summary>The retained root the walk reached it from.</summary>
    public Hash256 Root { get; } = root;

    /// <summary>Whether the live store could read the node when the sweep failed.</summary>
    public bool PresentInLiveStore { get; } = presentInLiveStore;
}
