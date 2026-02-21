using Basalt.Core;
using Basalt.Storage.RocksDb;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Tests for ReceiptData Encode/Decode roundtrips (T-02).
/// </summary>
public class ReceiptDataTests
{
    private static Hash256 TestHash(string input) =>
        Basalt.Crypto.Blake3Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(input));

    [Fact]
    public void Roundtrip_NoLogs()
    {
        var receipt = new ReceiptData
        {
            TransactionHash = TestHash("tx1"),
            BlockHash = TestHash("block1"),
            BlockNumber = 42,
            TransactionIndex = 0,
            From = Address.Zero,
            To = Address.Zero,
            GasUsed = 21000,
            Success = true,
            ErrorCode = 0,
            PostStateRoot = Hash256.Zero,
            EffectiveGasPrice = new UInt256(7),
        };

        var decoded = ReceiptData.Decode(receipt.Encode());

        decoded.TransactionHash.Should().Be(receipt.TransactionHash);
        decoded.BlockHash.Should().Be(receipt.BlockHash);
        decoded.BlockNumber.Should().Be(42);
        decoded.TransactionIndex.Should().Be(0);
        decoded.From.Should().Be(Address.Zero);
        decoded.To.Should().Be(Address.Zero);
        decoded.GasUsed.Should().Be(21000);
        decoded.Success.Should().BeTrue();
        decoded.ErrorCode.Should().Be(0);
        decoded.PostStateRoot.Should().Be(Hash256.Zero);
        decoded.EffectiveGasPrice.Should().Be(new UInt256(7));
        decoded.Logs.Should().BeEmpty();
    }

    [Fact]
    public void Roundtrip_WithLogs()
    {
        var receipt = new ReceiptData
        {
            TransactionHash = TestHash("tx2"),
            BlockHash = TestHash("block2"),
            BlockNumber = 100,
            TransactionIndex = 3,
            From = Address.Zero,
            To = Address.Zero,
            GasUsed = 50000,
            Success = true,
            ErrorCode = 0,
            PostStateRoot = Hash256.Zero,
            Logs =
            [
                new LogData
                {
                    Contract = Address.Zero,
                    EventSignature = TestHash("Transfer"),
                    Topics = [TestHash("from"), TestHash("to")],
                    Data = [1, 2, 3, 4],
                },
                new LogData
                {
                    Contract = Address.Zero,
                    EventSignature = TestHash("Approval"),
                    Topics = [],
                    Data = [],
                },
            ],
        };

        var decoded = ReceiptData.Decode(receipt.Encode());

        decoded.Logs.Should().HaveCount(2);
        decoded.Logs[0].EventSignature.Should().Be(TestHash("Transfer"));
        decoded.Logs[0].Topics.Should().HaveCount(2);
        decoded.Logs[0].Topics[0].Should().Be(TestHash("from"));
        decoded.Logs[0].Topics[1].Should().Be(TestHash("to"));
        decoded.Logs[0].Data.Should().BeEquivalentTo(new byte[] { 1, 2, 3, 4 });
        decoded.Logs[1].EventSignature.Should().Be(TestHash("Approval"));
        decoded.Logs[1].Topics.Should().BeEmpty();
        decoded.Logs[1].Data.Should().BeEmpty();
    }

    [Fact]
    public void Roundtrip_FailedTransaction()
    {
        var receipt = new ReceiptData
        {
            TransactionHash = TestHash("fail"),
            BlockHash = TestHash("block"),
            BlockNumber = 1,
            TransactionIndex = 0,
            From = Address.Zero,
            To = Address.Zero,
            GasUsed = 21000,
            Success = false,
            ErrorCode = 1001,
            PostStateRoot = Hash256.Zero,
        };

        var decoded = ReceiptData.Decode(receipt.Encode());

        decoded.Success.Should().BeFalse();
        decoded.ErrorCode.Should().Be(1001);
    }

    [Fact]
    public void Roundtrip_ManyTopics()
    {
        var topics = Enumerable.Range(0, 10).Select(i => TestHash($"topic_{i}")).ToArray();
        var receipt = new ReceiptData
        {
            TransactionHash = TestHash("many_topics"),
            BlockHash = TestHash("block"),
            BlockNumber = 1,
            TransactionIndex = 0,
            From = Address.Zero,
            To = Address.Zero,
            GasUsed = 21000,
            Success = true,
            ErrorCode = 0,
            PostStateRoot = Hash256.Zero,
            Logs =
            [
                new LogData
                {
                    Contract = Address.Zero,
                    EventSignature = TestHash("Event"),
                    Topics = topics,
                    Data = [0xFF],
                },
            ],
        };

        var decoded = ReceiptData.Decode(receipt.Encode());
        decoded.Logs[0].Topics.Should().HaveCount(10);
        for (int i = 0; i < 10; i++)
            decoded.Logs[0].Topics[i].Should().Be(TestHash($"topic_{i}"));
    }

    [Fact]
    public void Roundtrip_LargeLogData()
    {
        var data = new byte[4096];
        Random.Shared.NextBytes(data);

        var receipt = new ReceiptData
        {
            TransactionHash = TestHash("large_data"),
            BlockHash = TestHash("block"),
            BlockNumber = 1,
            TransactionIndex = 0,
            From = Address.Zero,
            To = Address.Zero,
            GasUsed = 100000,
            Success = true,
            ErrorCode = 0,
            PostStateRoot = Hash256.Zero,
            Logs =
            [
                new LogData
                {
                    Contract = Address.Zero,
                    EventSignature = TestHash("BigEvent"),
                    Topics = [],
                    Data = data,
                },
            ],
        };

        var decoded = ReceiptData.Decode(receipt.Encode());
        decoded.Logs[0].Data.Should().BeEquivalentTo(data);
    }

    [Fact]
    public void Roundtrip_LargeEffectiveGasPrice()
    {
        var receipt = new ReceiptData
        {
            TransactionHash = TestHash("gas"),
            BlockHash = TestHash("block"),
            BlockNumber = 1,
            TransactionIndex = 0,
            From = Address.Zero,
            To = Address.Zero,
            GasUsed = 21000,
            Success = true,
            ErrorCode = 0,
            PostStateRoot = Hash256.Zero,
            EffectiveGasPrice = UInt256.Parse("999999999999999999"),
        };

        var decoded = ReceiptData.Decode(receipt.Encode());
        decoded.EffectiveGasPrice.Should().Be(UInt256.Parse("999999999999999999"));
    }
}
