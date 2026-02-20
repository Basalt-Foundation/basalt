using Basalt.Core;

namespace Basalt.Storage.Trie;

/// <summary>
/// Storage backend for trie nodes. Maps hash -> node data.
/// </summary>
/// <remarks>
/// TODO (S-03): The trie currently never prunes stale nodes. When an account or storage
/// value is updated, the old trie path nodes become unreachable but remain in the store.
/// A future garbage-collection or reference-counting pass should reclaim these entries
/// to bound long-term storage growth.
/// </remarks>
public interface ITrieNodeStore
{
    TrieNode? Get(Hash256 hash);
    void Put(Hash256 hash, TrieNode node);
    void Delete(Hash256 hash);
}

/// <summary>
/// In-memory trie node store for testing and proof verification.
/// </summary>
public sealed class InMemoryTrieNodeStore : ITrieNodeStore
{
    private readonly Dictionary<Hash256, TrieNode> _nodes = new();

    public TrieNode? Get(Hash256 hash) =>
        _nodes.TryGetValue(hash, out var node) ? node : null;

    public void Put(Hash256 hash, TrieNode node) =>
        _nodes[hash] = node;

    public void Delete(Hash256 hash) =>
        _nodes.Remove(hash);

    public int Count => _nodes.Count;
}
