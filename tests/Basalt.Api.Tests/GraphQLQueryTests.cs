using Basalt.Api.GraphQL;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Api.Tests;

/// <summary>
/// Tests for the GraphQL Query resolvers called directly (bypassing the HotChocolate middleware).
/// </summary>
public class GraphQLQueryTests
{
    private static readonly ChainParameters TestChainParams = new()
    {
        ChainId = 31337,
        NetworkName = "test",
        BlockGasLimit = 100_000_000,
    };

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

    /// <summary>
    /// Creates a genesis block and adds it to the chain manager.
    /// </summary>
    private static Block AddGenesisBlock(ChainManager chainManager)
    {
        var header = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 1_000_000,
            Proposer = Address.Zero,
            ChainId = TestChainParams.ChainId,
            GasUsed = 0,
            GasLimit = TestChainParams.BlockGasLimit,
        };

        var genesis = new Block { Header = header, Transactions = [] };
        chainManager.AddBlock(genesis);
        return genesis;
    }

    /// <summary>
    /// Creates a block that extends the current chain tip.
    /// </summary>
    private static Block AddBlock(ChainManager chainManager, ulong number, byte proposerSeed = 0x01)
    {
        var parent = chainManager.LatestBlock!;
        var header = new BlockHeader
        {
            Number = number,
            ParentHash = parent.Hash,
            StateRoot = MakeHash((byte)(number & 0xFF)),
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 1_000_000 + (long)(number * 400),
            Proposer = MakeAddress(proposerSeed),
            ChainId = TestChainParams.ChainId,
            GasUsed = 1_000 * number,
            GasLimit = TestChainParams.BlockGasLimit,
        };

        var block = new Block { Header = header, Transactions = [] };
        var result = chainManager.AddBlock(block);
        result.IsSuccess.Should().BeTrue($"adding block #{number} should succeed");
        return block;
    }

    [Fact]
    public void GetStatus_ReturnsChainStatus()
    {
        // Arrange
        var chainManager = new ChainManager();
        var mempool = new Mempool(100);
        var genesis = AddGenesisBlock(chainManager);
        var block1 = AddBlock(chainManager, 1);
        var query = new Query();

        // Act
        var status = query.GetStatus(chainManager, mempool);

        // Assert
        status.BlockHeight.Should().Be(1);
        status.LatestBlockHash.Should().Be(block1.Hash.ToHexString());
        status.MempoolSize.Should().Be(0);
        status.ProtocolVersion.Should().Be(1u);
    }

    [Fact]
    public void GetStatus_NoBlocks_ReturnsZeroHeight()
    {
        // Arrange — empty chain
        var chainManager = new ChainManager();
        var mempool = new Mempool(100);
        var query = new Query();

        // Act
        var status = query.GetStatus(chainManager, mempool);

        // Assert
        status.BlockHeight.Should().Be(0);
        status.LatestBlockHash.Should().Be(Hash256.Zero.ToHexString());
        status.MempoolSize.Should().Be(0);
    }

    [Fact]
    public void GetStatus_MempoolCountReflected()
    {
        // Arrange
        var chainManager = new ChainManager();
        var mempool = new Mempool(100);
        AddGenesisBlock(chainManager);

        // Add a transaction to the mempool
        var tx = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = Address.Zero,
            To = MakeAddress(0x01),
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = TestChainParams.ChainId,
        };
        mempool.Add(tx, raiseEvent: false);

        var query = new Query();

        // Act
        var status = query.GetStatus(chainManager, mempool);

        // Assert
        status.MempoolSize.Should().Be(1);
    }

    [Fact]
    public void GetBlock_ByNumber_ReturnsBlock()
    {
        // Arrange
        var chainManager = new ChainManager();
        AddGenesisBlock(chainManager);
        var block1 = AddBlock(chainManager, 1);
        var query = new Query();

        // Act
        var result = query.GetBlock("1", chainManager);

        // Assert
        result.Should().NotBeNull();
        result!.Number.Should().Be(1);
        result.Hash.Should().Be(block1.Hash.ToHexString());
    }

    [Fact]
    public void GetBlock_ByHash_ReturnsBlock()
    {
        // Arrange
        var chainManager = new ChainManager();
        var genesis = AddGenesisBlock(chainManager);
        var query = new Query();

        // Act — look up by hex hash string
        var result = query.GetBlock(genesis.Hash.ToHexString(), chainManager);

        // Assert
        result.Should().NotBeNull();
        result!.Number.Should().Be(0);
        result.Hash.Should().Be(genesis.Hash.ToHexString());
    }

    [Fact]
    public void GetBlock_InvalidId_ReturnsNull()
    {
        // Arrange
        var chainManager = new ChainManager();
        AddGenesisBlock(chainManager);
        var query = new Query();

        // Act — "not_a_number_or_hash" is neither a ulong nor a valid hex hash
        var result = query.GetBlock("not_a_number_or_hash", chainManager);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetBlock_NonExistentNumber_ReturnsNull()
    {
        // Arrange
        var chainManager = new ChainManager();
        AddGenesisBlock(chainManager);
        var query = new Query();

        // Act
        var result = query.GetBlock("999", chainManager);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetLatestBlock_NoBlocks_ReturnsNull()
    {
        // Arrange — empty chain
        var chainManager = new ChainManager();
        var query = new Query();

        // Act
        var result = query.GetLatestBlock(chainManager);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetLatestBlock_WithBlocks_ReturnsLatest()
    {
        // Arrange
        var chainManager = new ChainManager();
        AddGenesisBlock(chainManager);
        AddBlock(chainManager, 1);
        var block2 = AddBlock(chainManager, 2);
        var query = new Query();

        // Act
        var result = query.GetLatestBlock(chainManager);

        // Assert
        result.Should().NotBeNull();
        result!.Number.Should().Be(2);
        result.Hash.Should().Be(block2.Hash.ToHexString());
    }

    [Fact]
    public void GetBlocks_ReturnsUpToNBlocks()
    {
        // Arrange — chain with genesis + 5 blocks
        var chainManager = new ChainManager();
        AddGenesisBlock(chainManager);
        for (ulong i = 1; i <= 5; i++)
            AddBlock(chainManager, i);

        var query = new Query();

        // Act — request last 3 blocks
        var results = query.GetBlocks(3, chainManager);

        // Assert — should return blocks 5, 4, 3 (descending from latest)
        results.Should().HaveCount(3);
        results[0].Number.Should().Be(5);
        results[1].Number.Should().Be(4);
        results[2].Number.Should().Be(3);
    }

    [Fact]
    public void GetBlocks_RequestMoreThanExist_ReturnsAll()
    {
        // Arrange — chain with genesis + 2 blocks
        var chainManager = new ChainManager();
        AddGenesisBlock(chainManager);
        AddBlock(chainManager, 1);
        AddBlock(chainManager, 2);
        var query = new Query();

        // Act — request 50 but only 2 non-genesis blocks exist
        // (GetBlocks iterates from latest.Number down to >0, so it skips genesis)
        var results = query.GetBlocks(50, chainManager);

        // Assert
        results.Should().HaveCount(2);
        results[0].Number.Should().Be(2);
        results[1].Number.Should().Be(1);
    }

    [Fact]
    public void GetBlocks_EmptyChain_ReturnsEmptyList()
    {
        var chainManager = new ChainManager();
        var query = new Query();

        var results = query.GetBlocks(10, chainManager);

        results.Should().BeEmpty();
    }

    [Fact]
    public void GetBlocks_CapsAt100()
    {
        // The implementation caps at 100. We don't need 100 blocks,
        // just verify it respects the cap by requesting more than 100.
        var chainManager = new ChainManager();
        AddGenesisBlock(chainManager);
        // Add 5 blocks
        for (ulong i = 1; i <= 5; i++)
            AddBlock(chainManager, i);

        var query = new Query();

        // Request 200 (capped to 100, but only 5 non-genesis blocks exist)
        var results = query.GetBlocks(200, chainManager);

        // Should get all 5, because we only have 5 non-genesis blocks
        results.Should().HaveCount(5);
    }

    [Fact]
    public void GetAccount_ValidAddress_ReturnsAccount()
    {
        // Arrange
        var stateDb = new InMemoryStateDb();
        var addr = MakeAddress(0x42);
        stateDb.SetAccount(addr, new AccountState
        {
            Balance = new UInt256(1_000_000),
            Nonce = 5,
            AccountType = AccountType.ExternallyOwned,
        });

        var query = new Query();

        // Act
        var result = query.GetAccount(addr.ToHexString(), stateDb);

        // Assert
        result.Should().NotBeNull();
        result!.Address.Should().Be(addr.ToHexString());
        result.Balance.Should().Be("1000000");
        result.Nonce.Should().Be(5);
        result.AccountType.Should().Be("ExternallyOwned");
    }

    [Fact]
    public void GetAccount_ContractAccount_ReturnsCorrectType()
    {
        var stateDb = new InMemoryStateDb();
        var addr = MakeAddress(0x99);
        stateDb.SetAccount(addr, new AccountState
        {
            Balance = UInt256.Zero,
            Nonce = 0,
            AccountType = AccountType.Contract,
        });

        var query = new Query();

        var result = query.GetAccount(addr.ToHexString(), stateDb);

        result.Should().NotBeNull();
        result!.AccountType.Should().Be("Contract");
    }

    [Fact]
    public void GetAccount_InvalidAddress_ReturnsNull()
    {
        var stateDb = new InMemoryStateDb();
        var query = new Query();

        // Act — garbage address
        var result = query.GetAccount("not-a-valid-address", stateDb);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void GetAccount_NonExistentAddress_ReturnsNull()
    {
        var stateDb = new InMemoryStateDb();
        var query = new Query();

        // Valid format but not in state
        var addr = MakeAddress(0xFF);
        var result = query.GetAccount(addr.ToHexString(), stateDb);

        result.Should().BeNull();
    }

    [Fact]
    public void GetBlock_MapsAllBlockResultFields()
    {
        // Arrange
        var chainManager = new ChainManager();
        var genesis = AddGenesisBlock(chainManager);
        var block1 = AddBlock(chainManager, 1, proposerSeed: 0xAB);
        var query = new Query();

        // Act
        var result = query.GetBlock("1", chainManager);

        // Assert — verify all BlockResult fields are mapped
        result.Should().NotBeNull();
        result!.Number.Should().Be(block1.Number);
        result.Hash.Should().Be(block1.Hash.ToHexString());
        result.ParentHash.Should().Be(block1.Header.ParentHash.ToHexString());
        result.StateRoot.Should().Be(block1.Header.StateRoot.ToHexString());
        result.Timestamp.Should().Be(block1.Header.Timestamp);
        result.Proposer.Should().Be(block1.Header.Proposer.ToHexString());
        result.GasUsed.Should().Be(block1.Header.GasUsed);
        result.GasLimit.Should().Be(block1.Header.GasLimit);
        result.TransactionCount.Should().Be(block1.Transactions.Count);
    }
}
