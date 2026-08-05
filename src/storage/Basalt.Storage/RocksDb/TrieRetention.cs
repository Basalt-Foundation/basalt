using Basalt.Core;

namespace Basalt.Storage.RocksDb;

/// <summary>
/// Builds the set of state roots a trie prune sweep must retain so that no rollback or replay can land on
/// a root whose nodes were swept away.
/// </summary>
/// <remarks>
/// <para>A fork recovery can rewind up to <c>MaxRollbackDepth</c> blocks. Every rollback target is a
/// canonical ancestor, so retaining the state root of every canonical block in a window of at least
/// <c>MaxRollbackDepth</c> below the tip guarantees any legal rollback re-roots onto a surviving trie.
/// Genesis (block 0) is always retained — deep rollback and full replay anchor there — and the current
/// tip is always retained. The window is reconstructed from <see cref="BlockStore"/>, so it survives a
/// restart without any separate persisted checkpoint file.</para>
/// <para>Callers pass <c>windowSize ≥ MaxRollbackDepth</c> (the node default is
/// <c>MaxRollbackDepth = 1000</c>). A window larger than the chain simply retains everything, which is
/// always safe.</para>
/// </remarks>
public static class TrieRetention
{
    /// <summary>
    /// Pure form. Retains the state root of every block in <c>[tip - windowSize + 1, tip]</c> (clamped at
    /// block 0) plus genesis. <paramref name="stateRootAt"/> maps a block number to its state root, or
    /// returns null if that block is absent.
    /// </summary>
    public static HashSet<Hash256> CollectRetainedRoots(
        ulong? latestBlockNumber, ulong windowSize, Func<ulong, Hash256?> stateRootAt)
    {
        ArgumentNullException.ThrowIfNull(stateRootAt);

        var roots = new HashSet<Hash256>();
        AddNonZero(stateRootAt(0), roots); // genesis anchor, always retained

        if (latestBlockNumber is not ulong tip)
            return roots; // no canonical tip yet (pre-genesis / empty store)

        // Window [tip - windowSize + 1, tip], clamped at 0. windowSize 0 still keeps the tip itself.
        ulong span = windowSize == 0 ? 1 : windowSize;
        ulong from = tip + 1 >= span ? tip + 1 - span : 0;
        for (ulong n = from; n <= tip; n++)
            AddNonZero(stateRootAt(n), roots);

        return roots;
    }

    /// <summary>
    /// <see cref="BlockStore"/>-backed form: reads the canonical tip and each block's state root directly
    /// from persisted blocks.
    /// </summary>
    public static HashSet<Hash256> CollectRetainedRoots(BlockStore blockStore, ulong windowSize)
    {
        ArgumentNullException.ThrowIfNull(blockStore);
        return CollectRetainedRoots(
            blockStore.GetLatestBlockNumber(),
            windowSize,
            n => blockStore.GetByNumber(n)?.StateRoot);
    }

    private static void AddNonZero(Hash256? root, HashSet<Hash256> roots)
    {
        if (root is Hash256 r && r != Hash256.Zero)
            roots.Add(r);
    }
}
