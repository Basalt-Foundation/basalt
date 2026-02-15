using Basalt.Api.Rest;
using Basalt.Core;
using Basalt.Execution;
using FluentAssertions;
using Xunit;

namespace Basalt.Api.Tests;

/// <summary>
/// Tests for REST API DTO mapping and property correctness.
/// </summary>
public class RestDtoTests
{
    // Helper: create a deterministic 20-byte address from a seed byte.
    private static Address MakeAddress(byte seed)
    {
        var bytes = new byte[Address.Size];
        bytes[0] = seed;
        return new Address(bytes);
    }

    // Helper: create a deterministic 32-byte hash from a seed byte.
    private static Hash256 MakeHash(byte seed)
    {
        var bytes = new byte[Hash256.Size];
        bytes[0] = seed;
        return new Hash256(bytes);
    }

    // Helper: create a block with known field values.
    private static Block MakeBlock(
        ulong number = 42,
        byte parentHashSeed = 0xAA,
        byte stateRootSeed = 0xBB,
        long timestamp = 1_700_000_000,
        byte proposerSeed = 0xCC,
        ulong gasUsed = 21_000,
        ulong gasLimit = 100_000_000,
        int txCount = 3)
    {
        var txs = new List<Transaction>();
        for (int i = 0; i < txCount; i++)
        {
            txs.Add(new Transaction
            {
                Type = TransactionType.Transfer,
                Nonce = (ulong)i,
                Sender = Address.Zero,
                To = Address.Zero,
                Value = UInt256.Zero,
                GasLimit = 21_000,
                GasPrice = new UInt256(1),
                ChainId = 1,
            });
        }

        var header = new BlockHeader
        {
            Number = number,
            ParentHash = MakeHash(parentHashSeed),
            StateRoot = MakeHash(stateRootSeed),
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = timestamp,
            Proposer = MakeAddress(proposerSeed),
            ChainId = 1,
            GasUsed = gasUsed,
            GasLimit = gasLimit,
        };

        return new Block
        {
            Header = header,
            Transactions = txs,
        };
    }

    [Fact]
    public void BlockResponse_FromBlock_MapsAllFields()
    {
        // Arrange
        var block = MakeBlock(
            number: 42,
            parentHashSeed: 0xAA,
            stateRootSeed: 0xBB,
            timestamp: 1_700_000_000,
            proposerSeed: 0xCC,
            gasUsed: 21_000,
            gasLimit: 100_000_000,
            txCount: 3);

        // Act
        var response = BlockResponse.FromBlock(block);

        // Assert
        response.Number.Should().Be(42);
        response.Hash.Should().Be(block.Hash.ToHexString());
        response.ParentHash.Should().Be(block.Header.ParentHash.ToHexString());
        response.StateRoot.Should().Be(block.Header.StateRoot.ToHexString());
        response.Timestamp.Should().Be(1_700_000_000);
        response.Proposer.Should().Be(block.Header.Proposer.ToHexString());
        response.GasUsed.Should().Be(21_000);
        response.GasLimit.Should().Be(100_000_000);
        response.TransactionCount.Should().Be(3);
    }

    [Fact]
    public void BlockResponse_FromBlock_ZeroBlock_MapsCorrectly()
    {
        // Arrange — genesis-style block with all zeroes
        var block = MakeBlock(number: 0, txCount: 0, gasUsed: 0, gasLimit: 0, timestamp: 0);

        // Act
        var response = BlockResponse.FromBlock(block);

        // Assert
        response.Number.Should().Be(0);
        response.TransactionCount.Should().Be(0);
        response.GasUsed.Should().Be(0);
        response.Timestamp.Should().Be(0);
    }

    [Fact]
    public void TransactionRequest_ToTransaction_MapsAllFields()
    {
        // Arrange — use valid 20-byte hex for addresses, 64-byte hex for sig, 32-byte hex for pubkey
        var senderHex = "0x" + new string('a', 40);   // 20 bytes
        var toHex = "0x" + new string('b', 40);        // 20 bytes
        var sigHex = "0x" + new string('c', 128);      // 64 bytes
        var pubKeyHex = "0x" + new string('d', 64);    // 32 bytes
        var dataHex = "0x" + "deadbeef";               // 4 bytes

        var request = new TransactionRequest
        {
            Type = (byte)TransactionType.Transfer,
            Nonce = 7,
            Sender = senderHex,
            To = toHex,
            Value = "1000",
            GasLimit = 21_000,
            GasPrice = "2",
            Data = dataHex,
            Priority = 1,
            ChainId = 31337,
            Signature = sigHex,
            SenderPublicKey = pubKeyHex,
        };

        // Act
        var tx = request.ToTransaction();

        // Assert
        tx.Type.Should().Be(TransactionType.Transfer);
        tx.Nonce.Should().Be(7);
        tx.Sender.Should().Be(Address.FromHexString(senderHex));
        tx.To.Should().Be(Address.FromHexString(toHex));
        tx.Value.Should().Be(UInt256.Parse("1000"));
        tx.GasLimit.Should().Be(21_000);
        tx.GasPrice.Should().Be(UInt256.Parse("2"));
        tx.Data.Should().BeEquivalentTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        tx.Priority.Should().Be(1);
        tx.ChainId.Should().Be(31337u);
        tx.Signature.Should().NotBe(Signature.Empty);
        tx.SenderPublicKey.Should().NotBe(PublicKey.Empty);
    }

    [Fact]
    public void TransactionRequest_ToTransaction_EmptyData_ProducesEmptyArray()
    {
        var request = new TransactionRequest
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('0', 40),
            To = "0x" + new string('0', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            Data = null,
            Priority = 0,
            ChainId = 1,
            Signature = "0x" + new string('0', 128),
            SenderPublicKey = "0x" + new string('0', 64),
        };

        var tx = request.ToTransaction();

        tx.Data.Should().BeEmpty();
    }

    [Fact]
    public void TransactionRequest_ToTransaction_DataWithout0xPrefix_Works()
    {
        var request = new TransactionRequest
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('0', 40),
            To = "0x" + new string('0', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            Data = "cafebabe",  // no 0x prefix
            Priority = 0,
            ChainId = 1,
            Signature = "0x" + new string('0', 128),
            SenderPublicKey = "0x" + new string('0', 64),
        };

        var tx = request.ToTransaction();

        tx.Data.Should().BeEquivalentTo(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE });
    }

    [Fact]
    public void StatusResponse_HasCorrectProperties()
    {
        var status = new StatusResponse
        {
            BlockHeight = 100,
            LatestBlockHash = "0xabc123",
            MempoolSize = 42,
            ProtocolVersion = 1,
        };

        status.BlockHeight.Should().Be(100);
        status.LatestBlockHash.Should().Be("0xabc123");
        status.MempoolSize.Should().Be(42);
        status.ProtocolVersion.Should().Be(1u);
    }

    [Fact]
    public void AccountResponse_HasCorrectProperties()
    {
        var account = new AccountResponse
        {
            Address = "0x" + new string('a', 40),
            Balance = "1000000000000000000",
            Nonce = 5,
            AccountType = "ExternallyOwned",
        };

        account.Address.Should().Be("0x" + new string('a', 40));
        account.Balance.Should().Be("1000000000000000000");
        account.Nonce.Should().Be(5);
        account.AccountType.Should().Be("ExternallyOwned");
    }

    [Fact]
    public void TransactionResponse_HasCorrectProperties()
    {
        var response = new TransactionResponse
        {
            Hash = "0xdeadbeef",
            Status = "pending",
        };

        response.Hash.Should().Be("0xdeadbeef");
        response.Status.Should().Be("pending");
    }

    [Fact]
    public void ErrorResponse_HasCorrectProperties()
    {
        var error = new ErrorResponse
        {
            Code = 400,
            Message = "Invalid request",
        };

        error.Code.Should().Be(400);
        error.Message.Should().Be("Invalid request");
    }

    [Fact]
    public void FaucetRequest_HasCorrectProperties()
    {
        var request = new FaucetRequest
        {
            Address = "0x" + new string('f', 40),
        };

        request.Address.Should().Be("0x" + new string('f', 40));
    }

    [Fact]
    public void FaucetResponse_HasCorrectProperties()
    {
        var response = new FaucetResponse
        {
            Success = true,
            Message = "Sent 100 BSLT",
            TxHash = "0xabc",
        };

        response.Success.Should().BeTrue();
        response.Message.Should().Be("Sent 100 BSLT");
        response.TxHash.Should().Be("0xabc");
    }
}
