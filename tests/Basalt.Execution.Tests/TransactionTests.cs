using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class TransactionTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;

    [Fact]
    public void SignAndVerify_Roundtrip()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(publicKey);

        var tx = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = sender,
            To = Address.Zero,
            Value = new UInt256(1000),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _chainParams.ChainId,
        };

        var signedTx = Transaction.Sign(tx, privateKey);
        signedTx.VerifySignature().Should().BeTrue();
    }

    [Fact]
    public void Validator_RejectsInvalidNonce()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(publicKey);

        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(sender, new AccountState
        {
            Balance = new UInt256(1_000_000),
            Nonce = 5,
        });

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 3, // Wrong nonce, should be 5
            Sender = sender,
            To = Address.Zero,
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _chainParams.ChainId,
        }, privateKey);

        var validator = new TransactionValidator(_chainParams);
        var result = validator.Validate(tx, stateDb);
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidNonce);
    }

    [Fact]
    public void Validator_RejectsInsufficientBalance()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(publicKey);

        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(sender, new AccountState
        {
            Balance = new UInt256(100), // Not enough for transfer + gas
            Nonce = 0,
        });

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = sender,
            To = Address.Zero,
            Value = new UInt256(1000),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _chainParams.ChainId,
        }, privateKey);

        var validator = new TransactionValidator(_chainParams);
        var result = validator.Validate(tx, stateDb);
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InsufficientBalance);
    }

    [Fact]
    public void Executor_Transfer_UpdatesBalances()
    {
        var (senderKey, senderPub) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(senderPub);

        var recipientBytes = new byte[20];
        recipientBytes[0] = 0xBB;
        var recipient = new Address(recipientBytes);

        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(senderAddr, new AccountState
        {
            Balance = new UInt256(1_000_000),
            Nonce = 0,
        });

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = recipient,
            Value = new UInt256(500),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _chainParams.ChainId,
        }, senderKey);

        var executor = new TransactionExecutor(_chainParams);
        var header = new BlockHeader { Number = 1, ChainId = _chainParams.ChainId };
        var receipt = executor.Execute(tx, stateDb, header, 0);

        receipt.Success.Should().BeTrue();
        receipt.GasUsed.Should().Be(21_000UL);

        var senderState = stateDb.GetAccount(senderAddr)!.Value;
        senderState.Nonce.Should().Be(1);

        var recipientState = stateDb.GetAccount(recipient)!.Value;
        recipientState.Balance.Should().Be(new UInt256(500));
    }

    [Fact]
    public void BlockBuilder_ProducesValidBlock()
    {
        var (senderKey, senderPub) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(senderPub);
        var recipientBytes = new byte[20];
        recipientBytes[0] = 0xCC;
        var recipient = new Address(recipientBytes);

        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(senderAddr, new AccountState
        {
            Balance = new UInt256(10_000_000),
            Nonce = 0,
        });

        var txs = new List<Transaction>();
        for (int i = 0; i < 5; i++)
        {
            txs.Add(Transaction.Sign(new Transaction
            {
                Type = TransactionType.Transfer,
                Nonce = (ulong)i,
                Sender = senderAddr,
                To = recipient,
                Value = new UInt256(100),
                GasLimit = 21_000,
                GasPrice = new UInt256(1),
                ChainId = _chainParams.ChainId,
            }, senderKey));
        }

        var parentHeader = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            ChainId = _chainParams.ChainId,
            GasLimit = _chainParams.BlockGasLimit,
        };

        var builder = new BlockBuilder(_chainParams);
        var block = builder.BuildBlock(txs, stateDb, parentHeader, Address.Zero);

        block.Number.Should().Be(1);
        block.Transactions.Should().HaveCount(5);
        block.Header.GasUsed.Should().Be(5UL * 21_000);
        block.Header.StateRoot.IsZero.Should().BeFalse();
    }
}
