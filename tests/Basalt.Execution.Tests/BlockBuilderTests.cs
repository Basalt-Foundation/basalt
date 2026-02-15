using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class BlockBuilderTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;

    private (byte[] PrivateKey, Address Address) CreateFundedAccount(InMemoryStateDb stateDb, UInt256 balance)
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var address = Ed25519Signer.DeriveAddress(publicKey);
        stateDb.SetAccount(address, new AccountState
        {
            Balance = balance,
            Nonce = 0,
            AccountType = AccountType.ExternallyOwned,
        });
        return (privateKey, address);
    }

    private Transaction CreateSignedTransfer(
        byte[] privateKey,
        Address sender,
        Address to,
        ulong nonce,
        UInt256 value,
        UInt256 gasPrice)
    {
        return Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = nonce,
            Sender = sender,
            To = to,
            Value = value,
            GasLimit = 21_000,
            GasPrice = gasPrice,
            ChainId = _chainParams.ChainId,
        }, privateKey);
    }

    private BlockHeader CreateParentHeader()
    {
        return new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Proposer = Address.Zero,
            ChainId = _chainParams.ChainId,
            GasUsed = 0,
            GasLimit = _chainParams.BlockGasLimit,
        };
    }

    [Fact]
    public void BuildBlock_CreatesBlockWithTransactions()
    {
        var stateDb = new InMemoryStateDb();
        var (privateKey, sender) = CreateFundedAccount(stateDb, new UInt256(10_000_000));

        var recipientBytes = new byte[20];
        recipientBytes[0] = 0xCC;
        var recipient = new Address(recipientBytes);

        var txs = new List<Transaction>();
        for (int i = 0; i < 3; i++)
        {
            txs.Add(CreateSignedTransfer(privateKey, sender, recipient, (ulong)i, new UInt256(100), new UInt256(1)));
        }

        var builder = new BlockBuilder(_chainParams);
        var block = builder.BuildBlock(txs, stateDb, CreateParentHeader(), Address.Zero);

        block.Should().NotBeNull();
        block.Number.Should().Be(1);
        block.Transactions.Should().HaveCount(3);
        block.Receipts.Should().NotBeNull();
        block.Receipts!.Should().HaveCount(3);
        block.Receipts!.Should().OnlyContain(r => r.Success);
    }

    [Fact]
    public void BuildBlock_ComputesCorrectStateRoot()
    {
        var stateDb = new InMemoryStateDb();
        var (privateKey, sender) = CreateFundedAccount(stateDb, new UInt256(10_000_000));

        var recipientBytes = new byte[20];
        recipientBytes[0] = 0xDD;
        var recipient = new Address(recipientBytes);

        var tx = CreateSignedTransfer(privateKey, sender, recipient, 0, new UInt256(500), new UInt256(1));

        var builder = new BlockBuilder(_chainParams);
        var block = builder.BuildBlock(new List<Transaction> { tx }, stateDb, CreateParentHeader(), Address.Zero);

        // State root should be non-zero after executing a transfer
        block.Header.StateRoot.IsZero.Should().BeFalse();

        // State root in the block header should match the current state db root
        var expectedRoot = stateDb.ComputeStateRoot();
        block.Header.StateRoot.Should().Be(expectedRoot);
    }

    [Fact]
    public void BuildBlock_EmptyMempoolProducesEmptyBlock()
    {
        var stateDb = new InMemoryStateDb();
        var builder = new BlockBuilder(_chainParams);

        var block = builder.BuildBlock(new List<Transaction>(), stateDb, CreateParentHeader(), Address.Zero);

        block.Should().NotBeNull();
        block.Number.Should().Be(1);
        block.Transactions.Should().BeEmpty();
        block.Header.GasUsed.Should().Be(0);
        block.Header.TransactionsRoot.Should().Be(Hash256.Zero);
        block.Header.ReceiptsRoot.Should().Be(Hash256.Zero);
    }

    [Fact]
    public void BuildBlock_SkipsInvalidTransactions()
    {
        var stateDb = new InMemoryStateDb();
        var (privateKey, sender) = CreateFundedAccount(stateDb, new UInt256(10_000_000));

        var recipientBytes = new byte[20];
        recipientBytes[0] = 0xEE;
        var recipient = new Address(recipientBytes);

        var validTx = CreateSignedTransfer(privateKey, sender, recipient, 0, new UInt256(100), new UInt256(1));

        // Create a transaction with wrong nonce (should be 1 after the first tx is validated,
        // but since validation happens in-order, nonce=5 will fail)
        var invalidTx = CreateSignedTransfer(privateKey, sender, recipient, 5, new UInt256(100), new UInt256(1));

        var builder = new BlockBuilder(_chainParams);
        var block = builder.BuildBlock(new List<Transaction> { validTx, invalidTx }, stateDb, CreateParentHeader(), Address.Zero);

        // Only the valid transaction should be included
        block.Transactions.Should().HaveCount(1);
        block.Transactions[0].Hash.Should().Be(validTx.Hash);
    }

    [Fact]
    public void BuildBlock_RespectsBlockGasLimit()
    {
        // Create chain params with a very low block gas limit
        var restrictedParams = new ChainParameters
        {
            ChainId = _chainParams.ChainId,
            NetworkName = "test",
            BlockGasLimit = 42_000, // Only fits 2 transfers at 21,000 gas each
        };

        var stateDb = new InMemoryStateDb();
        var (privateKey, sender) = CreateFundedAccount(stateDb, new UInt256(10_000_000));

        var recipientBytes = new byte[20];
        recipientBytes[0] = 0xFF;
        var recipient = new Address(recipientBytes);

        var txs = new List<Transaction>();
        for (int i = 0; i < 5; i++)
        {
            txs.Add(CreateSignedTransfer(privateKey, sender, recipient, (ulong)i, new UInt256(100), new UInt256(1)));
        }

        var builder = new BlockBuilder(restrictedParams);
        var parentHeader = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            ChainId = restrictedParams.ChainId,
            GasLimit = restrictedParams.BlockGasLimit,
        };

        var block = builder.BuildBlock(txs, stateDb, parentHeader, Address.Zero);

        // With gas limit of 42,000, only 2 transactions of 21,000 gas each can fit
        block.Transactions.Count.Should().BeLessOrEqualTo(2);
        block.Header.GasUsed.Should().BeLessOrEqualTo(42_000UL);
    }

    [Fact]
    public void BuildBlock_SetsCorrectBlockNumber()
    {
        var stateDb = new InMemoryStateDb();
        var builder = new BlockBuilder(_chainParams);

        var parentHeader = new BlockHeader
        {
            Number = 41,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            ChainId = _chainParams.ChainId,
            GasLimit = _chainParams.BlockGasLimit,
        };

        var block = builder.BuildBlock(new List<Transaction>(), stateDb, parentHeader, Address.Zero);
        block.Number.Should().Be(42);
    }

    [Fact]
    public void BuildBlock_SetsParentHash()
    {
        var stateDb = new InMemoryStateDb();
        var builder = new BlockBuilder(_chainParams);
        var parentHeader = CreateParentHeader();

        var block = builder.BuildBlock(new List<Transaction>(), stateDb, parentHeader, Address.Zero);

        block.Header.ParentHash.Should().Be(parentHeader.Hash);
    }

    [Fact]
    public void BuildBlock_SetsProposer()
    {
        var stateDb = new InMemoryStateDb();
        var builder = new BlockBuilder(_chainParams);

        var proposerBytes = new byte[20];
        proposerBytes[0] = 0x42;
        var proposer = new Address(proposerBytes);

        var block = builder.BuildBlock(new List<Transaction>(), stateDb, CreateParentHeader(), proposer);

        block.Header.Proposer.Should().Be(proposer);
    }

    [Fact]
    public void BuildBlock_ComputesTransactionsRoot()
    {
        var stateDb = new InMemoryStateDb();
        var (privateKey, sender) = CreateFundedAccount(stateDb, new UInt256(10_000_000));

        var recipientBytes = new byte[20];
        recipientBytes[0] = 0xAA;
        var recipient = new Address(recipientBytes);

        var txs = new List<Transaction>
        {
            CreateSignedTransfer(privateKey, sender, recipient, 0, new UInt256(100), new UInt256(1)),
            CreateSignedTransfer(privateKey, sender, recipient, 1, new UInt256(200), new UInt256(1)),
        };

        var builder = new BlockBuilder(_chainParams);
        var block = builder.BuildBlock(txs, stateDb, CreateParentHeader(), Address.Zero);

        // With transactions, the root should not be zero
        block.Header.TransactionsRoot.IsZero.Should().BeFalse();
    }

    [Fact]
    public void BuildBlock_GasUsedMatchesSumOfReceipts()
    {
        var stateDb = new InMemoryStateDb();
        var (privateKey, sender) = CreateFundedAccount(stateDb, new UInt256(10_000_000));

        var recipientBytes = new byte[20];
        recipientBytes[0] = 0xBB;
        var recipient = new Address(recipientBytes);

        var txs = new List<Transaction>();
        for (int i = 0; i < 4; i++)
        {
            txs.Add(CreateSignedTransfer(privateKey, sender, recipient, (ulong)i, new UInt256(50), new UInt256(1)));
        }

        var builder = new BlockBuilder(_chainParams);
        var block = builder.BuildBlock(txs, stateDb, CreateParentHeader(), Address.Zero);

        var receiptGasSum = block.Receipts!.Sum(r => (long)r.GasUsed);
        block.Header.GasUsed.Should().Be((ulong)receiptGasSum);
    }

    [Fact]
    public void ComputeTransactionsRoot_EmptyList_ReturnsZero()
    {
        var root = BlockBuilder.ComputeTransactionsRoot(new List<Transaction>());
        root.Should().Be(Hash256.Zero);
    }

    [Fact]
    public void ComputeReceiptsRoot_EmptyList_ReturnsZero()
    {
        var root = BlockBuilder.ComputeReceiptsRoot(new List<TransactionReceipt>());
        root.Should().Be(Hash256.Zero);
    }
}
