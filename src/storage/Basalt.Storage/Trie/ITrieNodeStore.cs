using Basalt.Core;

namespace Basalt.Storage.Trie;

/// <summary>
/// Storage backend for trie nodes. Maps hash -> node data.
/// </summary>
/// <remarks>
/// <para><b>H-01:</b> The trie creates new nodes on every mutation but never removes stale
/// (unreachable) nodes automatically. Use <see cref="MerklePatriciaTrie.CollectReachableNodes"/>
/// to identify live nodes, then sweep unreachable entries from the store periodically
/// (e.g., every N blocks). For <see cref="InMemoryTrieNodeStore"/>, call
/// <see cref="InMemoryTrieNodeStore.Prune"/>. For RocksDB, iterate the trie_nodes CF
/// and delete keys not in the reachable set.</para>
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

    /// <summary>
    /// Remove all nodes whose hashes are not in the reachable set (sweep phase of GC).
    /// Call with the result of <see cref="MerklePatriciaTrie.CollectReachableNodes"/>.
    /// </summary>
    /// <returns>The number of stale nodes removed.</returns>
    public int Prune(ISet<Hash256> reachableHashes)
    {
        var stale = new List<Hash256>();
        foreach (var hash in _nodes.Keys)
        {
            if (!reachableHashes.Contains(hash))
                stale.Add(hash);
        }
        foreach (var hash in stale)
            _nodes.Remove(hash);
        return stale.Count;
    }
}
