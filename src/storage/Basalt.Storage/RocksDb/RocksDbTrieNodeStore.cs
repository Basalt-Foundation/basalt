using Basalt.Core;
using Basalt.Storage.Trie;

namespace Basalt.Storage.RocksDb;

/// <summary>
/// RocksDB-backed trie node store.
/// </summary>
public sealed class RocksDbTrieNodeStore : ITrieNodeStore
{
    private readonly RocksDbStore _store;

    public RocksDbTrieNodeStore(RocksDbStore store)
    {
        _store = store;
    }

    public TrieNode? Get(Hash256 hash)
    {
        Span<byte> keyBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(keyBytes);

        var data = _store.Get(RocksDbStore.CF.TrieNodes, keyBytes);
        if (data == null)
            return null;

        return TrieNode.Decode(data);
    }

    /// <remarks>
    /// L-01: The <c>stackalloc</c> span is converted to <c>byte[]</c> via <c>ToArray()</c>
    /// because the RocksDbSharp bindings require managed arrays. The span optimization
    /// for key construction is retained for readability even though it allocates on put.
    /// </remarks>
    public void Put(Hash256 hash, TrieNode node)
    {
        Span<byte> keyBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(keyBytes);

        var encoded = node.Encode();
        _store.Put(RocksDbStore.CF.TrieNodes, keyBytes.ToArray(), encoded);
    }

    public void Delete(Hash256 hash)
    {
        Span<byte> keyBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(keyBytes);
        _store.Delete(RocksDbStore.CF.TrieNodes, keyBytes.ToArray());
    }
}
