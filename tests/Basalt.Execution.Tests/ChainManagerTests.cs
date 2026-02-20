using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class ChainManagerTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;
    private long _nextTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private Block CreateBlock(ulong number, Hash256 parentHash, uint chainId = 0)
    {
        if (chainId == 0) chainId = _chainParams.ChainId;

        // Ensure strictly increasing timestamps for monotonicity validation
        var timestamp = _nextTimestamp++;

        return new Block
        {
            Header = new BlockHeader
            {
                Number = number,
                ParentHash = parentHash,
                StateRoot = Hash256.Zero,
                TransactionsRoot = Hash256.Zero,
                ReceiptsRoot = Hash256.Zero,
                Timestamp = timestamp,
                Proposer = Address.Zero,
                ChainId = chainId,
                GasUsed = 0,
                GasLimit = _chainParams.BlockGasLimit,
            },
            Transactions = [],
        };
    }

    [Fact]
    public void LatestBlock_InitiallyNull()
    {
        var chainManager = new ChainManager();

        chainManager.LatestBlock.Should().BeNull();
        chainManager.LatestBlockNumber.Should().Be(0);
    }

    [Fact]
    public void AddBlock_GenesisBlock_Succeeds()
    {
        var chainManager = new ChainManager();
        var genesis = CreateBlock(0, Hash256.Zero);

        var result = chainManager.AddBlock(genesis);

        result.IsSuccess.Should().BeTrue();
        chainManager.LatestBlock.Should().NotBeNull();
        chainManager.LatestBlock!.Number.Should().Be(0);
    }

    [Fact]
    public void AddBlock_IncreasesLatestBlockNumber()
    {
        var chainManager = new ChainManager();
        var genesis = CreateBlock(0, Hash256.Zero);
        chainManager.AddBlock(genesis);

        var block1 = CreateBlock(1, genesis.Hash);
        var result = chainManager.AddBlock(block1);

        result.IsSuccess.Should().BeTrue();
        chainManager.LatestBlockNumber.Should().Be(1);

        var block2 = CreateBlock(2, block1.Hash);
        chainManager.AddBlock(block2);

        chainManager.LatestBlockNumber.Should().Be(2);
    }

    [Fact]
    public void GetBlockByNumber_ReturnsCorrectBlock()
    {
        var chainManager = new ChainManager();
        var genesis = CreateBlock(0, Hash256.Zero);
        chainManager.AddBlock(genesis);

        var block1 = CreateBlock(1, genesis.Hash);
        chainManager.AddBlock(block1);

        var retrieved = chainManager.GetBlockByNumber(0);
        retrieved.Should().NotBeNull();
        retrieved!.Hash.Should().Be(genesis.Hash);

        var retrieved1 = chainManager.GetBlockByNumber(1);
        retrieved1.Should().NotBeNull();
        retrieved1!.Hash.Should().Be(block1.Hash);
    }

    [Fact]
    public void GetBlockByHash_ReturnsCorrectBlock()
    {
        var chainManager = new ChainManager();
        var genesis = CreateBlock(0, Hash256.Zero);
        chainManager.AddBlock(genesis);

        var retrieved = chainManager.GetBlockByHash(genesis.Hash);
        retrieved.Should().NotBeNull();
        retrieved!.Number.Should().Be(0);
    }

    [Fact]
    public void GetBlockByNumber_UnknownNumber_ReturnsNull()
    {
        var chainManager = new ChainManager();
        var genesis = CreateBlock(0, Hash256.Zero);
        chainManager.AddBlock(genesis);

        var retrieved = chainManager.GetBlockByNumber(999);
        retrieved.Should().BeNull();
    }

    [Fact]
    public void GetBlockByHash_UnknownHash_ReturnsNull()
    {
        var chainManager = new ChainManager();

        var retrieved = chainManager.GetBlockByHash(Hash256.Zero);
        retrieved.Should().BeNull();
    }

    [Fact]
    public void AddBlock_FirstBlockMustBeGenesis()
    {
        var chainManager = new ChainManager();
        var block = CreateBlock(1, Hash256.Zero);

        var result = chainManager.AddBlock(block);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidBlockNumber);
    }

    [Fact]
    public void AddBlock_InvalidParentHash_Fails()
    {
        var chainManager = new ChainManager();
        var genesis = CreateBlock(0, Hash256.Zero);
        chainManager.AddBlock(genesis);

        // Create block 1 with a wrong parent hash (not genesis.Hash)
        var wrongParent = new Hash256(new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
            0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20,
        });
        var block1 = CreateBlock(1, wrongParent);

        var result = chainManager.AddBlock(block1);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidParentHash);
    }

    [Fact]
    public void AddBlock_InvalidBlockNumber_Fails()
    {
        var chainManager = new ChainManager();
        var genesis = CreateBlock(0, Hash256.Zero);
        chainManager.AddBlock(genesis);

        // Create block with number 5 (should be 1)
        var block = CreateBlock(5, genesis.Hash);
        var result = chainManager.AddBlock(block);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidBlockNumber);
    }

    [Fact]
    public void CreateGenesisBlock_SetsUpInitialState()
    {
        var chainManager = new ChainManager();
        var stateDb = new InMemoryStateDb();

        var addr1Bytes = new byte[20];
        addr1Bytes[0] = 0xAA;
        var addr1 = new Address(addr1Bytes);

        var addr2Bytes = new byte[20];
        addr2Bytes[0] = 0xBB;
        var addr2 = new Address(addr2Bytes);

        var initialBalances = new Dictionary<Address, UInt256>
        {
            { addr1, new UInt256(1_000_000) },
            { addr2, new UInt256(2_000_000) },
        };

        var genesis = chainManager.CreateGenesisBlock(_chainParams, initialBalances, stateDb);

        genesis.Should().NotBeNull();
        genesis.Number.Should().Be(0);
        chainManager.LatestBlock.Should().NotBeNull();
        chainManager.LatestBlockNumber.Should().Be(0);

        // Verify initial balances were set
        var account1 = stateDb.GetAccount(addr1);
        account1.Should().NotBeNull();
        account1!.Value.Balance.Should().Be(new UInt256(1_000_000));

        var account2 = stateDb.GetAccount(addr2);
        account2.Should().NotBeNull();
        account2!.Value.Balance.Should().Be(new UInt256(2_000_000));
    }

    [Fact]
    public void CreateGenesisBlock_HasEmptyTransactionList()
    {
        var chainManager = new ChainManager();
        var genesis = chainManager.CreateGenesisBlock(_chainParams);

        genesis.Transactions.Should().BeEmpty();
    }

    [Fact]
    public void CreateGenesisBlock_ParentHashIsZero()
    {
        var chainManager = new ChainManager();
        var genesis = chainManager.CreateGenesisBlock(_chainParams);

        genesis.Header.ParentHash.Should().Be(Hash256.Zero);
    }

    [Fact]
    public void ResumeFromBlock_RestoresChainState()
    {
        // Build a chain
        var original = new ChainManager();
        var genesis = original.CreateGenesisBlock(_chainParams);
        var block1 = CreateBlock(1, genesis.Hash);
        original.AddBlock(block1);

        // Resume on a new manager
        var resumed = new ChainManager();
        resumed.ResumeFromBlock(genesis, block1);

        resumed.LatestBlockNumber.Should().Be(1);
        resumed.GetBlockByNumber(0).Should().NotBeNull();
        resumed.GetBlockByNumber(1).Should().NotBeNull();
        resumed.GetBlockByHash(genesis.Hash).Should().NotBeNull();
        resumed.GetBlockByHash(block1.Hash).Should().NotBeNull();
    }

    [Fact]
    public void AddBlock_SequentialChain_AllBlocksRetrievable()
    {
        var chainManager = new ChainManager();
        var genesis = CreateBlock(0, Hash256.Zero);
        chainManager.AddBlock(genesis);

        var prevHash = genesis.Hash;
        for (ulong i = 1; i <= 10; i++)
        {
            var block = CreateBlock(i, prevHash);
            var result = chainManager.AddBlock(block);
            result.IsSuccess.Should().BeTrue();
            prevHash = block.Hash;
        }

        chainManager.LatestBlockNumber.Should().Be(10);

        for (ulong i = 0; i <= 10; i++)
        {
            var block = chainManager.GetBlockByNumber(i);
            block.Should().NotBeNull();
            block!.Number.Should().Be(i);
        }
    }
}
