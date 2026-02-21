using Basalt.Codec;
using Basalt.Core;

namespace Basalt.Storage.RocksDb;

/// <summary>
/// RocksDB-backed transaction receipt store.
/// </summary>
public sealed class ReceiptStore
{
    private readonly RocksDbStore _store;

    public ReceiptStore(RocksDbStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Store a receipt indexed by transaction hash.
    /// </summary>
    public void PutReceipt(ReceiptData receipt)
    {
        var key = new byte[Hash256.Size];
        receipt.TransactionHash.WriteTo(key);
        _store.Put(RocksDbStore.CF.Receipts, key, receipt.Encode());
    }

    /// <summary>
    /// Store multiple receipts atomically.
    /// </summary>
    public void PutReceipts(IEnumerable<ReceiptData> receipts)
    {
        using var batch = _store.CreateWriteBatch();
        foreach (var receipt in receipts)
        {
            var key = new byte[Hash256.Size];
            receipt.TransactionHash.WriteTo(key);
            batch.Put(RocksDbStore.CF.Receipts, key, receipt.Encode());
        }
        batch.Commit();
    }

    /// <summary>
    /// Get a receipt by transaction hash.
    /// </summary>
    public ReceiptData? GetReceipt(Hash256 txHash)
    {
        var key = new byte[Hash256.Size];
        txHash.WriteTo(key);
        var data = _store.Get(RocksDbStore.CF.Receipts, key);
        return data == null ? null : ReceiptData.Decode(data);
    }
}

/// <summary>
/// Serializable receipt data for storage.
/// </summary>
public sealed class ReceiptData
{
    public Hash256 TransactionHash { get; init; }
    public Hash256 BlockHash { get; init; }
    public ulong BlockNumber { get; init; }
    public int TransactionIndex { get; init; }
    public Address From { get; init; }
    public Address To { get; init; }
    public ulong GasUsed { get; init; }
    public bool Success { get; init; }
    public int ErrorCode { get; init; }
    public Hash256 PostStateRoot { get; init; }
    public UInt256 EffectiveGasPrice { get; init; } = UInt256.Zero;
    public LogData[] Logs { get; init; } = [];

    /// <remarks>
    /// <para><b>L-06:</b> The size calculation assumes <see cref="Basalt.Codec.BasaltWriter.WriteBytes"/>
    /// uses a 4-byte fixed-length prefix. The <c>4 + log.Data.Length</c> term per log accounts
    /// for this. If the codec changes to varint length encoding, update the size formula.</para>
    /// </remarks>
    public byte[] Encode()
    {
        int size = Hash256.Size * 3 + 8 + 4 + Address.Size * 2 + 8 + 1 + 4 + 32 + 4;
        foreach (var log in Logs)
            size += Address.Size + Hash256.Size + 4 + log.Topics.Length * Hash256.Size + 4 + log.Data.Length;

        var buffer = new byte[size];
        var writer = new BasaltWriter(buffer);

        writer.WriteHash256(TransactionHash);
        writer.WriteHash256(BlockHash);
        writer.WriteUInt64(BlockNumber);
        writer.WriteInt32(TransactionIndex);
        writer.WriteAddress(From);
        writer.WriteAddress(To);
        writer.WriteUInt64(GasUsed);
        writer.WriteByte(Success ? (byte)1 : (byte)0);
        writer.WriteInt32(ErrorCode);
        writer.WriteHash256(PostStateRoot);
        writer.WriteUInt256(EffectiveGasPrice);
        writer.WriteUInt32((uint)Logs.Length);

        foreach (var log in Logs)
        {
            writer.WriteAddress(log.Contract);
            writer.WriteHash256(log.EventSignature);
            writer.WriteUInt32((uint)log.Topics.Length);
            foreach (var topic in log.Topics)
                writer.WriteHash256(topic);
            writer.WriteBytes(log.Data);
        }

        return buffer;
    }

    public static ReceiptData Decode(byte[] data)
    {
        var reader = new BasaltReader(data);

        var txHash = reader.ReadHash256();
        var blockHash = reader.ReadHash256();
        var blockNumber = reader.ReadUInt64();
        var txIndex = reader.ReadInt32();
        var from = reader.ReadAddress();
        var to = reader.ReadAddress();
        var gasUsed = reader.ReadUInt64();
        var success = reader.ReadByte() == 1;
        var errorCode = reader.ReadInt32();
        var postStateRoot = reader.ReadHash256();
        var effectiveGasPrice = reader.ReadUInt256();
        var logCount = (int)reader.ReadUInt32();
        var logs = new LogData[logCount];

        for (int i = 0; i < logCount; i++)
        {
            var contract = reader.ReadAddress();
            var eventSig = reader.ReadHash256();
            var topicCount = (int)reader.ReadUInt32();
            var topics = new Hash256[topicCount];
            for (int j = 0; j < topicCount; j++)
                topics[j] = reader.ReadHash256();
            var logData = reader.ReadBytes();

            logs[i] = new LogData
            {
                Contract = contract,
                EventSignature = eventSig,
                Topics = topics,
                Data = logData.ToArray(),
            };
        }

        return new ReceiptData
        {
            TransactionHash = txHash,
            BlockHash = blockHash,
            BlockNumber = blockNumber,
            TransactionIndex = txIndex,
            From = from,
            To = to,
            GasUsed = gasUsed,
            Success = success,
            ErrorCode = errorCode,
            PostStateRoot = postStateRoot,
            EffectiveGasPrice = effectiveGasPrice,
            Logs = logs,
        };
    }
}

/// <summary>
/// Event log data for storage.
/// </summary>
public sealed class LogData
{
    public Address Contract { get; init; }
    public Hash256 EventSignature { get; init; }
    public Hash256[] Topics { get; init; } = [];
    public byte[] Data { get; init; } = [];
}
