using Basalt.Core;
using Basalt.Storage.RocksDb;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Tests for BlockData Encode/Decode roundtrips (T-01).
/// RocksDB-dependent BlockStore operation tests are in a separate integration test class.
/// </summary>
public class BlockDataTests
{
    private static BlockData CreateTestBlock(ulong number = 1, Hash256? hash = null)
    {
        return new BlockData
        {
            Number = number,
            Hash = hash ?? Hash256.Zero,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 1700000000L,
            Proposer = Address.Zero,
            ChainId = 31337,
            GasUsed = 21000,
            GasLimit = 30_000_000,
            BaseFee = new UInt256(7),
            ProtocolVersion = 1,
            ExtraData = [0xCA, 0xFE],
            TransactionHashes = [Hash256.Zero],
        };
    }

    [Fact]
    public void Roundtrip_PreservesAllFields()
    {
        var block = CreateTestBlock();
        var encoded = block.Encode();
        var decoded = BlockData.Decode(encoded);

        decoded.Number.Should().Be(block.Number);
        decoded.Hash.Should().Be(block.Hash);
        decoded.ParentHash.Should().Be(block.ParentHash);
        decoded.StateRoot.Should().Be(block.StateRoot);
        decoded.TransactionsRoot.Should().Be(block.TransactionsRoot);
        decoded.ReceiptsRoot.Should().Be(block.ReceiptsRoot);
        decoded.Timestamp.Should().Be(block.Timestamp);
        decoded.Proposer.Should().Be(block.Proposer);
        decoded.ChainId.Should().Be(block.ChainId);
        decoded.GasUsed.Should().Be(block.GasUsed);
        decoded.GasLimit.Should().Be(block.GasLimit);
        decoded.BaseFee.Should().Be(block.BaseFee);
        decoded.ProtocolVersion.Should().Be(block.ProtocolVersion);
        decoded.ExtraData.Should().BeEquivalentTo(block.ExtraData);
        decoded.TransactionHashes.Should().HaveCount(1);
        decoded.TransactionHashes[0].Should().Be(Hash256.Zero);
    }

    [Fact]
    public void Roundtrip_EmptyExtraDataAndNoTxs()
    {
        var block = new BlockData
        {
            Number = 0,
            Hash = Hash256.Zero,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 0,
            Proposer = Address.Zero,
            ChainId = 1,
            GasUsed = 0,
            GasLimit = 0,
            ProtocolVersion = 0,
        };

        var decoded = BlockData.Decode(block.Encode());
        decoded.ExtraData.Should().BeEmpty();
        decoded.TransactionHashes.Should().BeEmpty();
    }

    [Fact]
    public void Roundtrip_MultipleTxHashes()
    {
        var hashes = new[]
        {
            Basalt.Crypto.Blake3Hasher.Hash("tx1"u8),
            Basalt.Crypto.Blake3Hasher.Hash("tx2"u8),
            Basalt.Crypto.Blake3Hasher.Hash("tx3"u8),
        };

        var block = new BlockData
        {
            Number = 5,
            Hash = Hash256.Zero,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 0,
            Proposer = Address.Zero,
            ChainId = 1,
            GasUsed = 0,
            GasLimit = 0,
            ProtocolVersion = 0,
            TransactionHashes = hashes,
        };

        var decoded = BlockData.Decode(block.Encode());
        decoded.TransactionHashes.Should().HaveCount(3);
        for (int i = 0; i < 3; i++)
            decoded.TransactionHashes[i].Should().Be(hashes[i]);
    }

    [Fact]
    public void Roundtrip_LargeExtraData()
    {
        var extraData = new byte[1024];
        Random.Shared.NextBytes(extraData);

        var block = new BlockData
        {
            Number = 1,
            Hash = Hash256.Zero,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 0,
            Proposer = Address.Zero,
            ChainId = 1,
            GasUsed = 0,
            GasLimit = 0,
            ProtocolVersion = 0,
            ExtraData = extraData,
        };

        var decoded = BlockData.Decode(block.Encode());
        decoded.ExtraData.Should().BeEquivalentTo(extraData);
    }

    [Fact]
    public void Roundtrip_LargeBaseFee()
    {
        var block = new BlockData
        {
            Number = 1,
            Hash = Hash256.Zero,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 0,
            Proposer = Address.Zero,
            ChainId = 1,
            GasUsed = 0,
            GasLimit = 0,
            ProtocolVersion = 0,
            BaseFee = UInt256.Parse("1000000000000000000"),
        };

        var decoded = BlockData.Decode(block.Encode());
        decoded.BaseFee.Should().Be(UInt256.Parse("1000000000000000000"));
    }
}
