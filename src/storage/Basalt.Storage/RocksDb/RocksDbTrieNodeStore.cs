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
