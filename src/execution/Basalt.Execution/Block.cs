using Basalt.Codec;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Execution;

/// <summary>
/// Block header in the Basalt blockchain.
/// </summary>
public sealed class BlockHeader
{
    public ulong Number { get; init; }
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
    public uint ProtocolVersion { get; init; } = 1;
    public byte[] ExtraData { get; init; } = [];

    private Hash256? _hash;

    public Hash256 Hash
    {
        get
        {
            _hash ??= ComputeHash();
            return _hash.Value;
        }
    }

    private Hash256 ComputeHash()
    {
        var size = GetSerializedSize();
        // H-6: Use heap allocation for large sizes to prevent stack overflow
        if (size > 4096)
        {
            var heapBuffer = new byte[size];
            var writer = new BasaltWriter(heapBuffer);
            WriteTo(ref writer);
            return Blake3Hasher.Hash(writer.WrittenSpan);
        }
        else
        {
            Span<byte> buffer = stackalloc byte[size];
            var writer = new BasaltWriter(buffer);
            WriteTo(ref writer);
            return Blake3Hasher.Hash(writer.WrittenSpan);
        }
    }

    public int GetSerializedSize()
    {
        return 8 + Hash256.Size + Hash256.Size + Hash256.Size + Hash256.Size +
               8 + Address.Size + 4 + 8 + 8 + 32 + 4 + 4 + ExtraData.Length;
    }

    public void WriteTo(ref BasaltWriter writer)
    {
        writer.WriteUInt64(Number);
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
    }
}

/// <summary>
/// A complete block with header and transactions.
/// </summary>
public sealed class Block
{
    public required BlockHeader Header { get; init; }
    public required List<Transaction> Transactions { get; init; }
    public List<TransactionReceipt>? Receipts { get; set; }

    public Hash256 Hash => Header.Hash;
    public ulong Number => Header.Number;
}
