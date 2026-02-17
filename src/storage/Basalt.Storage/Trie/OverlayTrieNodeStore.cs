using Basalt.Core;

namespace Basalt.Storage.Trie;

/// <summary>
/// In-memory overlay that wraps an <see cref="ITrieNodeStore"/>.
/// Reads fall through to the base store; writes stay in memory only.
/// Used to create throwaway state forks for speculative block building.
/// </summary>
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
}
