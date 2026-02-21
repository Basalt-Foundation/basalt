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

        // M-01: Per-CF options tuned for each access pattern.
        // Point-lookup-heavy CFs get bloom filters to reduce unnecessary disk reads.
        var defaultOptions = new ColumnFamilyOptions();

        var pointLookupOptions = new ColumnFamilyOptions()
            .SetBloomLocality(1);

        var cfs = new RocksDbSharp.ColumnFamilies();
        cfs.Add("default", defaultOptions);
        cfs.Add(CF.State, pointLookupOptions);        // prefix scans (0x01/0x02) + point lookups
        cfs.Add(CF.Blocks, pointLookupOptions);        // point lookups by hash, raw block reads
        cfs.Add(CF.Receipts, pointLookupOptions);      // point lookups by tx hash
        cfs.Add(CF.Metadata, defaultOptions);           // very few keys, no bloom needed
        cfs.Add(CF.TrieNodes, pointLookupOptions);     // write-heavy, point lookups by hash
        cfs.Add(CF.BlockIndex, defaultOptions);         // sequential scans by block number

        _db = RocksDbSharp.RocksDb.Open(options, path, cfs);
        _columnFamilies = new Dictionary<string, ColumnFamilyHandle>();

        var cfNames = new[] { "default", CF.State, CF.Blocks, CF.Receipts, CF.Metadata, CF.TrieNodes, CF.BlockIndex };
        foreach (var name in cfNames)
        {
            _columnFamilies[name] = _db.GetColumnFamily(name);
        }
    }

    public byte[]? Get(string columnFamily, byte[] key)
    {
        return _db.Get(key, _columnFamilies[columnFamily]);
    }

    /// <remarks>
    /// L-09: The span is copied to <c>byte[]</c> via <c>ToArray()</c> because the RocksDbSharp
    /// bindings do not support <c>ReadOnlySpan&lt;byte&gt;</c> keys natively.
    /// </remarks>
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
/// <remarks>
/// <para><b>Important (H-03):</b> This type does <b>not</b> auto-commit on <see cref="Dispose"/>.
/// Callers must explicitly call <see cref="Commit"/> before the scope ends.
/// If <c>Dispose()</c> is called with pending (uncommitted) operations, a warning is logged
/// via <see cref="Console.Error"/> to make the silent drop detectable.</para>
/// </remarks>
public sealed class WriteBatchScope : IDisposable
{
    private readonly RocksDbSharp.RocksDb _db;
    private readonly Dictionary<string, ColumnFamilyHandle> _columnFamilies;
    private readonly WriteBatch _batch;
    private bool _hasOperations;
    private bool _committed;

    internal WriteBatchScope(RocksDbSharp.RocksDb db, Dictionary<string, ColumnFamilyHandle> columnFamilies)
    {
        _db = db;
        _columnFamilies = columnFamilies;
        _batch = new WriteBatch();
    }

    public void Put(string columnFamily, byte[] key, byte[] value)
    {
        _batch.Put(key, value, _columnFamilies[columnFamily]);
        _hasOperations = true;
    }

    public void Delete(string columnFamily, byte[] key)
    {
        _batch.Delete(key, _columnFamilies[columnFamily]);
        _hasOperations = true;
    }

    public void Commit()
    {
        _db.Write(_batch);
        _committed = true;
    }

    public void Dispose()
    {
        if (_hasOperations && !_committed)
        {
            Console.Error.WriteLine(
                "WARNING: WriteBatchScope disposed with uncommitted operations. " +
                "Data was silently dropped. Ensure Commit() is called before Dispose().");
        }
        _batch.Dispose();
    }
}
