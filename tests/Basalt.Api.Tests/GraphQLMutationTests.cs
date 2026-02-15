using Basalt.Api.GraphQL;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Api.Tests;

/// <summary>
/// Tests for the GraphQL Mutation resolvers called directly (bypassing the HotChocolate middleware).
/// </summary>
public class GraphQLMutationTests
{
    private static readonly ChainParameters TestChainParams = new()
    {
        ChainId = 31337,
        NetworkName = "test",
        BlockGasLimit = 100_000_000,
    };

    [Fact]
    public void SubmitTransaction_InvalidInput_ReturnsError()
    {
        // Arrange — the TransactionInput.ToTransaction() will throw because
        // the address strings are empty/invalid, so the mutation should catch
        // the exception and return a failure result.
        var chainManager = new ChainManager();
        var mempool = new Mempool(100);
        var validator = new TransactionValidator(TestChainParams);
        var stateDb = new InMemoryStateDb();
        var mutation = new Mutation();

        var input = new TransactionInput
        {
            Type = 0,
            Nonce = 0,
            Sender = "invalid_address",
            To = "invalid_address",
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            Data = null,
            Priority = 0,
            ChainId = TestChainParams.ChainId,
            Signature = "bad",
            SenderPublicKey = "bad",
        };

        // Act
        var result = mutation.SubmitTransaction(input, chainManager, mempool, validator, stateDb);

        // Assert — should be a failure because ToTransaction() will throw on invalid addresses
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
        result.Hash.Should().BeNull();
    }

    [Fact]
    public void SubmitTransaction_EmptySender_ReturnsError()
    {
        var chainManager = new ChainManager();
        var mempool = new Mempool(100);
        var validator = new TransactionValidator(TestChainParams);
        var stateDb = new InMemoryStateDb();
        var mutation = new Mutation();

        var input = new TransactionInput
        {
            Type = 0,
            Nonce = 0,
            Sender = "",
            To = "",
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            ChainId = TestChainParams.ChainId,
            Signature = "",
            SenderPublicKey = "",
        };

        var result = mutation.SubmitTransaction(input, chainManager, mempool, validator, stateDb);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SubmitTransaction_ValidFormatButInvalidSignature_ReturnsValidationError()
    {
        // Arrange — well-formed hex addresses/signature/pubkey, but the signature
        // won't verify because it's all zeroes.
        var chainManager = new ChainManager();
        var mempool = new Mempool(100);
        var validator = new TransactionValidator(TestChainParams);
        var stateDb = new InMemoryStateDb();
        var mutation = new Mutation();

        var senderHex = "0x" + new string('a', 40);
        var toHex = "0x" + new string('b', 40);
        var sigHex = "0x" + new string('1', 128);      // 64 bytes, invalid signature
        var pubKeyHex = "0x" + new string('2', 64);    // 32 bytes, invalid pubkey

        // Give the sender a balance so the only failure is the signature
        var senderAddr = Address.FromHexString(senderHex);
        stateDb.SetAccount(senderAddr, new AccountState
        {
            Balance = new UInt256(10_000_000),
            Nonce = 0,
            AccountType = AccountType.ExternallyOwned,
        });

        var input = new TransactionInput
        {
            Type = 0,
            Nonce = 0,
            Sender = senderHex,
            To = toHex,
            Value = "100",
            GasLimit = 21_000,
            GasPrice = "1",
            Data = null,
            Priority = 0,
            ChainId = TestChainParams.ChainId,
            Signature = sigHex,
            SenderPublicKey = pubKeyHex,
        };

        // Act
        var result = mutation.SubmitTransaction(input, chainManager, mempool, validator, stateDb);

        // Assert — should fail at signature validation step
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TransactionInput_ToTransaction_MapsAllFields()
    {
        // Arrange
        var senderHex = "0x" + new string('a', 40);
        var toHex = "0x" + new string('b', 40);
        var sigHex = "0x" + new string('c', 128);
        var pubKeyHex = "0x" + new string('d', 64);

        var input = new TransactionInput
        {
            Type = (byte)TransactionType.ContractCall,
            Nonce = 42,
            Sender = senderHex,
            To = toHex,
            Value = "9999",
            GasLimit = 50_000,
            GasPrice = "5",
            Data = "0xdeadbeef",
            Priority = 2,
            ChainId = 31337,
            Signature = sigHex,
            SenderPublicKey = pubKeyHex,
        };

        // Act
        var tx = input.ToTransaction();

        // Assert
        tx.Type.Should().Be(TransactionType.ContractCall);
        tx.Nonce.Should().Be(42);
        tx.Sender.Should().Be(Address.FromHexString(senderHex));
        tx.To.Should().Be(Address.FromHexString(toHex));
        tx.Value.Should().Be(UInt256.Parse("9999"));
        tx.GasLimit.Should().Be(50_000);
        tx.GasPrice.Should().Be(UInt256.Parse("5"));
        tx.Data.Should().BeEquivalentTo(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        tx.Priority.Should().Be(2);
        tx.ChainId.Should().Be(31337u);
    }

    [Fact]
    public void TransactionInput_ToTransaction_EmptyData_ProducesEmptyArray()
    {
        var input = new TransactionInput
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('0', 40),
            To = "0x" + new string('0', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            Data = null,
            ChainId = 1,
            Signature = "0x" + new string('0', 128),
            SenderPublicKey = "0x" + new string('0', 64),
        };

        var tx = input.ToTransaction();

        tx.Data.Should().BeEmpty();
    }

    [Fact]
    public void TransactionResult_SuccessResult_HasExpectedShape()
    {
        var result = new TransactionResult
        {
            Success = true,
            Hash = "0xabc",
            Status = "pending",
        };

        result.Success.Should().BeTrue();
        result.Hash.Should().Be("0xabc");
        result.Status.Should().Be("pending");
        result.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void TransactionResult_ErrorResult_HasExpectedShape()
    {
        var result = new TransactionResult
        {
            Success = false,
            ErrorMessage = "Something went wrong",
        };

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Something went wrong");
        result.Hash.Should().BeNull();
        result.Status.Should().BeNull();
    }
}
