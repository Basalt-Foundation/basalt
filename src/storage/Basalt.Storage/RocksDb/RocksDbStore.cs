using Basalt.Core;
using RocksDbSharp;

namespace Basalt.Storage.RocksDb;

/// <summary>
/// RocksDB-backed key-value store with multiple column families.
/// Used as the primary persistent storage engine for the Basalt blockchain.
/// </summary>
public sealed class RocksDbStore : IDisposable
{
    private readonly RocksDbSharp.RocksDb _db;
    private readonly Dictionary<string, ColumnFamilyHandle> _columnFamilies;

    /// <summary>
    /// Column family names.
    /// </summary>
    public static class CF
    {
        public const string State = "state";
        public const string Blocks = "blocks";
        public const string Receipts = "receipts";
        public const string Metadata = "metadata";
        public const string TrieNodes = "trie_nodes";
        public const string BlockIndex = "block_index";
    }

    public RocksDbStore(string path)
    {
        var options = new DbOptions()
            .SetCreateIfMissing(true)
            .SetCreateMissingColumnFamilies(true);

        var cfNames = new[]
        {
            "default",
            CF.State,
            CF.Blocks,
            CF.Receipts,
            CF.Metadata,
            CF.TrieNodes,
            CF.BlockIndex,
        };

        var cfOptions = new ColumnFamilyOptions();
        var cfs = new RocksDbSharp.ColumnFamilies();
        foreach (var name in cfNames)
        {
            cfs.Add(name, cfOptions);
        }

        _db = RocksDbSharp.RocksDb.Open(options, path, cfs);
        _columnFamilies = new Dictionary<string, ColumnFamilyHandle>();

        foreach (var name in cfNames)
        {
            _columnFamilies[name] = _db.GetColumnFamily(name);
        }
    }

    public byte[]? Get(string columnFamily, byte[] key)
    {
        return _db.Get(key, _columnFamilies[columnFamily]);
    }

    public byte[]? Get(string columnFamily, ReadOnlySpan<byte> key)
    {
        return _db.Get(key.ToArray(), _columnFamilies[columnFamily]);
    }

    public void Put(string columnFamily, byte[] key, byte[] value)
    {
        _db.Put(key, value, _columnFamilies[columnFamily]);
    }

    public void Put(string columnFamily, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        _db.Put(key.ToArray(), value.ToArray(), _columnFamilies[columnFamily]);
    }

    public void Delete(string columnFamily, byte[] key)
    {
        _db.Remove(key, _columnFamilies[columnFamily]);
    }

    public bool HasKey(string columnFamily, byte[] key)
    {
        return _db.Get(key, _columnFamilies[columnFamily]) != null;
    }

    /// <summary>
    /// Write multiple operations atomically.
    /// </summary>
    public WriteBatchScope CreateWriteBatch() => new(_db, _columnFamilies);

    /// <summary>
    /// Iterate over all keys in a column family.
    /// </summary>
    public IEnumerable<(byte[] Key, byte[] Value)> Iterate(string columnFamily)
    {
        using var iterator = _db.NewIterator(_columnFamilies[columnFamily]);
        iterator.SeekToFirst();
        while (iterator.Valid())
        {
            yield return (iterator.Key(), iterator.Value());
            iterator.Next();
        }
    }

    /// <summary>
    /// Iterate over keys with a given prefix.
    /// </summary>
    public IEnumerable<(byte[] Key, byte[] Value)> IteratePrefix(string columnFamily, byte[] prefix)
    {
        using var iterator = _db.NewIterator(_columnFamilies[columnFamily]);
        iterator.Seek(prefix);
        while (iterator.Valid())
        {
            var key = iterator.Key();
            if (!key.AsSpan().StartsWith(prefix))
                break;
            yield return (key, iterator.Value());
            iterator.Next();
        }
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}

/// <summary>
/// Scoped write batch for atomic multi-key writes.
/// </summary>
public sealed class WriteBatchScope : IDisposable
{
    private readonly RocksDbSharp.RocksDb _db;
    private readonly Dictionary<string, ColumnFamilyHandle> _columnFamilies;
    private readonly WriteBatch _batch;

    internal WriteBatchScope(RocksDbSharp.RocksDb db, Dictionary<string, ColumnFamilyHandle> columnFamilies)
    {
        _db = db;
        _columnFamilies = columnFamilies;
        _batch = new WriteBatch();
    }

    public void Put(string columnFamily, byte[] key, byte[] value)
    {
        _batch.Put(key, value, _columnFamilies[columnFamily]);
    }

    public void Delete(string columnFamily, byte[] key)
    {
        _batch.Delete(key, _columnFamilies[columnFamily]);
    }

    public void Commit()
    {
        _db.Write(_batch);
    }

    public void Dispose()
    {
        _batch.Dispose();
    }
}
