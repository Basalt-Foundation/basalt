using System.Buffers.Binary;
using Basalt.Codec;
using Basalt.Core;

namespace Basalt.Storage.RocksDb;

/// <summary>
/// RocksDB-backed block store with dual indexing (by hash and by number).
/// </summary>
public sealed class BlockStore
{
    private readonly RocksDbStore _store;

    public BlockStore(RocksDbStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Store a block with dual indexing.
    /// </summary>
    public void PutBlock(BlockData block)
    {
        var encoded = block.Encode();
        var hashKey = HashToKey(block.Hash);
        var numberKey = NumberToKey(block.Number);

        using var batch = _store.CreateWriteBatch();
        // Store block data by hash
        batch.Put(RocksDbStore.CF.Blocks, hashKey, encoded);
        // Store hash->number index
        batch.Put(RocksDbStore.CF.BlockIndex, numberKey, hashKey);
        batch.Commit();
    }

    /// <summary>
    /// Get a block by its hash.
    /// </summary>
    public BlockData? GetByHash(Hash256 hash)
    {
        var data = _store.Get(RocksDbStore.CF.Blocks, HashToKey(hash));
        return data == null ? null : BlockData.Decode(data);
    }

    /// <summary>
    /// Get a block by its number.
    /// </summary>
    public BlockData? GetByNumber(ulong number)
    {
        var hashKey = _store.Get(RocksDbStore.CF.BlockIndex, NumberToKey(number));
        if (hashKey == null)
            return null;
        var data = _store.Get(RocksDbStore.CF.Blocks, hashKey);
        return data == null ? null : BlockData.Decode(data);
    }

    /// <summary>
    /// Store a block with dual indexing plus its raw serialized form.
    /// Used by consensus finalization to persist blocks that can be served to syncing peers.
    /// </summary>
    public void PutFullBlock(BlockData block, byte[] serializedBlock, ulong? commitBitmap = null)
    {
        var encoded = block.Encode();
        var hashKey = HashToKey(block.Hash);
        var numberKey = NumberToKey(block.Number);
        var rawKey = RawBlockKey(block.Hash);

        using var batch = _store.CreateWriteBatch();
        batch.Put(RocksDbStore.CF.Blocks, hashKey, encoded);
        batch.Put(RocksDbStore.CF.BlockIndex, numberKey, hashKey);
        batch.Put(RocksDbStore.CF.Blocks, rawKey, serializedBlock);
        if (commitBitmap.HasValue)
        {
            var bmpValue = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(bmpValue, commitBitmap.Value);
            batch.Put(RocksDbStore.CF.Blocks, BitmapKey(block.Number), bmpValue);
        }
        batch.Commit();
    }

    /// <summary>
    /// Get the raw serialized block by hash (for serving to peers).
    /// </summary>
    public byte[]? GetRawBlock(Hash256 hash)
    {
        return _store.Get(RocksDbStore.CF.Blocks, RawBlockKey(hash));
    }

    /// <summary>
    /// Get the raw serialized block by number.
    /// </summary>
    public byte[]? GetRawBlockByNumber(ulong number)
    {
        var hashKey = _store.Get(RocksDbStore.CF.BlockIndex, NumberToKey(number));
        if (hashKey == null)
            return null;
        // hashKey is 32 bytes — convert to raw key
        var rawKey = new byte[4 + hashKey.Length];
        "raw:"u8.CopyTo(rawKey);
        hashKey.CopyTo(rawKey, 4);
        return _store.Get(RocksDbStore.CF.Blocks, rawKey);
    }

    /// <summary>
    /// Check if a block exists by hash.
    /// </summary>
    public bool HasBlock(Hash256 hash)
    {
        return _store.HasKey(RocksDbStore.CF.Blocks, HashToKey(hash));
    }

    /// <summary>
    /// Get the latest block number stored in metadata.
    /// </summary>
    public ulong? GetLatestBlockNumber()
    {
        var data = _store.Get(RocksDbStore.CF.Metadata, "latest_block"u8.ToArray());
        if (data == null || data.Length < 8)
            return null;
        return BinaryPrimitives.ReadUInt64BigEndian(data);
    }

    /// <summary>
    /// Update the latest block number.
    /// </summary>
    public void SetLatestBlockNumber(ulong number)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(data, number);
        _store.Put(RocksDbStore.CF.Metadata, "latest_block"u8.ToArray(), data);
    }

    private static byte[] HashToKey(Hash256 hash)
    {
        var key = new byte[Hash256.Size];
        hash.WriteTo(key);
        return key;
    }

    private static byte[] NumberToKey(ulong number)
    {
        var key = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(key, number);
        return key;
    }

    /// <summary>
    /// Store the commit voter bitmap for a block.
    /// </summary>
    public void PutCommitBitmap(ulong blockNumber, ulong commitBitmap)
    {
        var key = BitmapKey(blockNumber);
        var value = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(value, commitBitmap);
        _store.Put(RocksDbStore.CF.Blocks, key, value);
    }

    /// <summary>
    /// Get the commit voter bitmap for a block, or null if not stored.
    /// </summary>
    public ulong? GetCommitBitmap(ulong blockNumber)
    {
        var data = _store.Get(RocksDbStore.CF.Blocks, BitmapKey(blockNumber));
        if (data == null || data.Length < 8)
            return null;
        return BinaryPrimitives.ReadUInt64BigEndian(data);
    }

    private static byte[] RawBlockKey(Hash256 hash)
    {
        var key = new byte[4 + Hash256.Size];
        "raw:"u8.CopyTo(key);
        hash.WriteTo(key.AsSpan(4));
        return key;
    }

    private static byte[] BitmapKey(ulong blockNumber)
    {
        var key = new byte[4 + 8];
        "bmp:"u8.CopyTo(key);
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(4), blockNumber);
        return key;
    }
}

/// <summary>
/// Serializable block data for storage.
/// Stores the raw header + transaction hashes (full tx data stored separately).
/// </summary>
public sealed class BlockData
{
    public ulong Number { get; init; }
    public Hash256 Hash { get; init; }
    public Hash256 ParentHash { get; init; }
    public Hash256 StateRoot { get; init; }
    public Hash256 TransactionsRoot { get; init; }
    public Hash256 ReceiptsRoot { get; init; }
    public long Timestamp { get; init; }
    public Address Proposer { get; init; }
    public uint ChainId { get; init; }
    public ulong GasUsed { get; init; }
    public ulong GasLimit { get; init; }
    public UInt256 BaseFee { get; init; } = UInt256.Zero;
    public uint ProtocolVersion { get; init; }
    public byte[] ExtraData { get; init; } = [];
    public Hash256[] TransactionHashes { get; init; } = [];

    public byte[] Encode()
    {
        int size = 8 + Hash256.Size * 6 + 8 + Address.Size + 4 + 8 + 8 + 32 + 4 +
                   4 + ExtraData.Length +
                   4 + TransactionHashes.Length * Hash256.Size;
        var buffer = new byte[size];
        var writer = new BasaltWriter(buffer);

        writer.WriteUInt64(Number);
        writer.WriteHash256(Hash);
        writer.WriteHash256(ParentHash);
        writer.WriteHash256(StateRoot);
        writer.WriteHash256(TransactionsRoot);
        writer.WriteHash256(ReceiptsRoot);
        writer.WriteInt64(Timestamp);
        writer.WriteAddress(Proposer);
        writer.WriteUInt32(ChainId);
        writer.WriteUInt64(GasUsed);
        writer.WriteUInt64(GasLimit);
        writer.WriteUInt256(BaseFee);
        writer.WriteUInt32(ProtocolVersion);
        writer.WriteBytes(ExtraData);
        writer.WriteUInt32((uint)TransactionHashes.Length);
        foreach (var txHash in TransactionHashes)
            writer.WriteHash256(txHash);

        return buffer;
    }

    public static BlockData Decode(byte[] data)
    {
        var reader = new BasaltReader(data);

        var number = reader.ReadUInt64();
        var hash = reader.ReadHash256();
        var parentHash = reader.ReadHash256();
        var stateRoot = reader.ReadHash256();
        var txRoot = reader.ReadHash256();
        var receiptsRoot = reader.ReadHash256();
        var timestamp = reader.ReadInt64();
        var proposer = reader.ReadAddress();
        var chainId = reader.ReadUInt32();
        var gasUsed = reader.ReadUInt64();
        var gasLimit = reader.ReadUInt64();
        var baseFee = reader.ReadUInt256();
        var protocolVersion = reader.ReadUInt32();
        var extraData = reader.ReadBytes();
        var txCount = (int)reader.ReadUInt32();
        var txHashes = new Hash256[txCount];
        for (int i = 0; i < txCount; i++)
            txHashes[i] = reader.ReadHash256();

        return new BlockData
        {
            Number = number,
            Hash = hash,
            ParentHash = parentHash,
            StateRoot = stateRoot,
            TransactionsRoot = txRoot,
            ReceiptsRoot = receiptsRoot,
            Timestamp = timestamp,
            Proposer = proposer,
            ChainId = chainId,
            GasUsed = gasUsed,
            GasLimit = gasLimit,
            BaseFee = baseFee,
            ProtocolVersion = protocolVersion,
            ExtraData = extraData.ToArray(),
            TransactionHashes = txHashes,
        };
    }
}
