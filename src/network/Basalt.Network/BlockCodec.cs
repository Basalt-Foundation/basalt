using System;
using System.Collections.Generic;
using Basalt.Codec;
using Basalt.Core;
using Basalt.Execution;

namespace Basalt.Network;

/// <summary>
/// Static codec for serializing and deserializing <see cref="Block"/> and <see cref="Transaction"/>
/// for network transmission using the Basalt binary wire format.
/// </summary>
public static class BlockCodec
{
    // Fixed-size portion of a serialized transaction:
    // Type(1) + Nonce(8) + Sender(20) + To(20) + Value(32) + GasLimit(8) + GasPrice(32)
    // + MaxFeePerGas(32) + MaxPriorityFeePerGas(32)
    // + Priority(1) + ChainId(4) + Signature(64) + SenderPublicKey(32) = 286 bytes
    // Plus variable: varint length prefix + Data bytes
    private const int TxFixedSize = 286;

    // Fixed-size portion of a serialized block header:
    // Number(8) + ParentHash(32) + StateRoot(32) + TransactionsRoot(32) + ReceiptsRoot(32)
    // + Timestamp(8) + Proposer(20) + ChainId(4) + GasUsed(8) + GasLimit(8) + BaseFee(32)
    // + ProtocolVersion(4) = 220 bytes
    // Plus variable: varint length prefix + ExtraData bytes
    private const int HeaderFixedSize = 220;

    /// <summary>
    /// Serialize a <see cref="Transaction"/> into a byte array suitable for network transmission.
    /// </summary>
    public static byte[] SerializeTransaction(Transaction tx)
    {
        int dataLength = tx.Data?.Length ?? 0;
        int estimatedSize = TxFixedSize + dataLength + 16; // 16 bytes overhead for varint

        byte[] result;
        if (estimatedSize > 8192)
        {
            byte[] buffer = new byte[estimatedSize];
            var writer = new BasaltWriter(buffer);
            WriteTransaction(ref writer, tx);
            result = writer.WrittenSpan.ToArray();
        }
        else
        {
            Span<byte> buffer = stackalloc byte[estimatedSize];
            var writer = new BasaltWriter(buffer);
            WriteTransaction(ref writer, tx);
            result = writer.WrittenSpan.ToArray();
        }

        return result;
    }

    /// <summary>
    /// Deserialize a <see cref="Transaction"/> from a binary span.
    /// </summary>
    public static Transaction DeserializeTransaction(ReadOnlySpan<byte> data)
    {
        var reader = new BasaltReader(data);
        return ReadTransaction(ref reader);
    }

    /// <summary>
    /// Serialize a <see cref="Block"/> into a byte array suitable for network transmission.
    /// </summary>
    public static byte[] SerializeBlock(Block block)
    {
        // Pre-serialize all transactions to compute total size
        int txCount = block.Transactions?.Count ?? 0;
        byte[][] serializedTxs = new byte[txCount][];
        int totalTxBytes = 0;

        for (int i = 0; i < txCount; i++)
        {
            serializedTxs[i] = SerializeTransaction(block.Transactions![i]);
            // Each tx is written as WriteBytes (varint length prefix + raw bytes)
            totalTxBytes += serializedTxs[i].Length + VarIntSize((ulong)serializedTxs[i].Length);
        }

        int extraDataLength = block.Header.ExtraData?.Length ?? 0;
        int estimatedSize = HeaderFixedSize + extraDataLength + 16 // header + ExtraData varint overhead
                          + VarIntSize((ulong)txCount)             // transaction count varint
                          + totalTxBytes;                          // all serialized transactions

        byte[] result;
        if (estimatedSize > 8192)
        {
            byte[] buffer = new byte[estimatedSize];
            var writer = new BasaltWriter(buffer);
            WriteBlockHeader(ref writer, block.Header);
            writer.WriteVarInt((ulong)txCount);
            for (int i = 0; i < txCount; i++)
            {
                writer.WriteBytes(serializedTxs[i]);
            }
            result = writer.WrittenSpan.ToArray();
        }
        else
        {
            Span<byte> buffer = stackalloc byte[estimatedSize];
            var writer = new BasaltWriter(buffer);
            WriteBlockHeader(ref writer, block.Header);
            writer.WriteVarInt((ulong)txCount);
            for (int i = 0; i < txCount; i++)
            {
                writer.WriteBytes(serializedTxs[i]);
            }
            result = writer.WrittenSpan.ToArray();
        }

        return result;
    }

    /// <summary>
    /// Deserialize a <see cref="Block"/> from a binary span.
    /// </summary>
    public static Block DeserializeBlock(ReadOnlySpan<byte> data)
    {
        var reader = new BasaltReader(data);

        BlockHeader header = ReadBlockHeader(ref reader);

        int txCount = (int)reader.ReadVarInt();
        var transactions = new List<Transaction>(txCount);

        for (int i = 0; i < txCount; i++)
        {
            ReadOnlySpan<byte> txData = reader.ReadBytes();
            var txReader = new BasaltReader(txData);
            transactions.Add(ReadTransaction(ref txReader));
        }

        return new Block
        {
            Header = header,
            Transactions = transactions,
        };
    }

    /// <summary>
    /// Write a transaction to the given writer in wire format.
    /// </summary>
    private static void WriteTransaction(ref BasaltWriter writer, Transaction tx)
    {
        writer.WriteByte((byte)tx.Type);        // 1. Type
        writer.WriteUInt64(tx.Nonce);            // 2. Nonce
        writer.WriteAddress(tx.Sender);          // 3. Sender
        writer.WriteAddress(tx.To);              // 4. To
        writer.WriteUInt256(tx.Value);           // 5. Value
        writer.WriteUInt64(tx.GasLimit);         // 6. GasLimit
        writer.WriteUInt256(tx.GasPrice);        // 7. GasPrice
        writer.WriteUInt256(tx.MaxFeePerGas);    // 8. MaxFeePerGas
        writer.WriteUInt256(tx.MaxPriorityFeePerGas); // 9. MaxPriorityFeePerGas
        writer.WriteBytes(tx.Data ?? []);        // 10. Data (varint length-prefixed)
        writer.WriteByte(tx.Priority);           // 9. Priority
        writer.WriteUInt32(tx.ChainId);          // 10. ChainId
        writer.WriteSignature(tx.Signature);     // 11. Signature
        writer.WritePublicKey(tx.SenderPublicKey); // 12. SenderPublicKey
    }

    /// <summary>
    /// Read a transaction from the given reader.
    /// </summary>
    private static Transaction ReadTransaction(ref BasaltReader reader)
    {
        var type = (TransactionType)reader.ReadByte();
        var nonce = reader.ReadUInt64();
        var sender = reader.ReadAddress();
        var to = reader.ReadAddress();
        var value = reader.ReadUInt256();
        var gasLimit = reader.ReadUInt64();
        var gasPrice = reader.ReadUInt256();
        var maxFeePerGas = reader.ReadUInt256();
        var maxPriorityFeePerGas = reader.ReadUInt256();
        var data = reader.ReadBytes().ToArray();
        var priority = reader.ReadByte();
        var chainId = reader.ReadUInt32();
        var signature = reader.ReadSignature();
        var senderPublicKey = reader.ReadPublicKey();

        return new Transaction
        {
            Type = type,
            Nonce = nonce,
            Sender = sender,
            To = to,
            Value = value,
            GasLimit = gasLimit,
            GasPrice = gasPrice,
            MaxFeePerGas = maxFeePerGas,
            MaxPriorityFeePerGas = maxPriorityFeePerGas,
            Data = data,
            Priority = priority,
            ChainId = chainId,
            Signature = signature,
            SenderPublicKey = senderPublicKey,
        };
    }

    /// <summary>
    /// Write a block header to the given writer in wire format.
    /// </summary>
    private static void WriteBlockHeader(ref BasaltWriter writer, BlockHeader header)
    {
        writer.WriteUInt64(header.Number);
        writer.WriteHash256(header.ParentHash);
        writer.WriteHash256(header.StateRoot);
        writer.WriteHash256(header.TransactionsRoot);
        writer.WriteHash256(header.ReceiptsRoot);
        writer.WriteInt64(header.Timestamp);
        writer.WriteAddress(header.Proposer);
        writer.WriteUInt32(header.ChainId);
        writer.WriteUInt64(header.GasUsed);
        writer.WriteUInt64(header.GasLimit);
        writer.WriteUInt256(header.BaseFee);
        writer.WriteUInt32(header.ProtocolVersion);
        writer.WriteBytes(header.ExtraData ?? []);
    }

    /// <summary>
    /// Read a block header from the given reader.
    /// </summary>
    private static BlockHeader ReadBlockHeader(ref BasaltReader reader)
    {
        var number = reader.ReadUInt64();
        var parentHash = reader.ReadHash256();
        var stateRoot = reader.ReadHash256();
        var transactionsRoot = reader.ReadHash256();
        var receiptsRoot = reader.ReadHash256();
        var timestamp = reader.ReadInt64();
        var proposer = reader.ReadAddress();
        var chainId = reader.ReadUInt32();
        var gasUsed = reader.ReadUInt64();
        var gasLimit = reader.ReadUInt64();
        var baseFee = reader.ReadUInt256();
        var protocolVersion = reader.ReadUInt32();
        var extraData = reader.ReadBytes().ToArray();

        return new BlockHeader
        {
            Number = number,
            ParentHash = parentHash,
            StateRoot = stateRoot,
            TransactionsRoot = transactionsRoot,
            ReceiptsRoot = receiptsRoot,
            Timestamp = timestamp,
            Proposer = proposer,
            ChainId = chainId,
            GasUsed = gasUsed,
            GasLimit = gasLimit,
            BaseFee = baseFee,
            ProtocolVersion = protocolVersion,
            ExtraData = extraData,
        };
    }

    /// <summary>
    /// Calculate the number of bytes required to encode a value as a LEB128 varint.
    /// </summary>
    private static int VarIntSize(ulong value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            size++;
            value >>= 7;
        }
        return size;
    }
}
