using Basalt.Core;

namespace Basalt.Storage.Trie;

/// <summary>
/// In-memory overlay that wraps an <see cref="ITrieNodeStore"/>.
/// Reads fall through to the base store; writes stay in memory only.
/// Used to create throwaway state forks for speculative block building.
/// </summary>
/// <remarks>
/// <para><b>L-04:</b> <see cref="Delete"/> stores a <c>null</c> tombstone rather than
/// removing the key, so the overlay dictionary grows with deletes. This is acceptable
/// because overlays are intended to be short-lived (one block proposal cycle).
/// Do not use for long-lived state that accumulates many deletes.</para>
/// </remarks>
internal sealed class OverlayTrieNodeStore : ITrieNodeStore
{
    private readonly ITrieNodeStore _base;
    private readonly Dictionary<Hash256, TrieNode?> _overlay = new();

    public OverlayTrieNodeStore(ITrieNodeStore baseStore) => _base = baseStore;

    public TrieNode? Get(Hash256 hash)
    {
        if (_overlay.TryGetValue(hash, out var node))
            return node;
        return _base.Get(hash);
    }

    public void Put(Hash256 hash, TrieNode node) => _overlay[hash] = node;

    public void Delete(Hash256 hash) => _overlay[hash] = null;

    /// <summary>
    /// Write every live (non-tombstoned) overlay node through to <paramref name="target"/>.
    /// This is how synced state is made durable (H4): after a sync batch the batch's new trie nodes
    /// live only in this in-memory overlay, so they must be persisted before the forked state becomes
    /// canonical — otherwise a restart cannot rebuild state and aborts on the state-root check.
    /// Tombstones are intentionally not propagated: the trie is content-addressed and append-only, so an
    /// unreferenced old node is simply left for the pruner, and the new root references only nodes that
    /// are present here (new) or already in the base store (unchanged).
    /// </summary>
    public void FlushTo(ITrieNodeStore target)
    {
        foreach (var (hash, node) in _overlay)
        {
            if (node != null)
                target.Put(hash, node);
        }
    }
}
