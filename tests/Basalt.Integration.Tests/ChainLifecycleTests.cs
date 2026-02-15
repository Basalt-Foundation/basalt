using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Integration.Tests;

/// <summary>
/// End-to-end tests for the chain lifecycle: genesis, block production, and state evolution.
/// </summary>
public class ChainLifecycleTests
{
    private readonly ChainParameters _params = ChainParameters.Devnet;
    private readonly InMemoryStateDb _stateDb = new();
    private readonly ChainManager _chainManager = new();

    [Fact]
    public void Genesis_Creates_Initial_Balances()
    {
        var addr1 = TestHelper.MakeAddress(1);
        var addr2 = TestHelper.MakeAddress(2);
        var initialBalances = new Dictionary<Address, UInt256>
        {
            [addr1] = new UInt256(1_000_000),
            [addr2] = new UInt256(500_000),
        };

        var genesis = _chainManager.CreateGenesisBlock(_params, initialBalances, _stateDb);

        genesis.Number.Should().Be(0);
        genesis.Header.ParentHash.Should().Be(Hash256.Zero);
        _stateDb.GetAccount(addr1)!.Value.Balance.Should().Be(new UInt256(1_000_000));
        _stateDb.GetAccount(addr2)!.Value.Balance.Should().Be(new UInt256(500_000));
    }

    [Fact]
    public void Genesis_Computes_State_Root()
    {
        var genesis = _chainManager.CreateGenesisBlock(_params, new Dictionary<Address, UInt256>
        {
            [TestHelper.MakeAddress(1)] = new UInt256(100),
        }, _stateDb);

        genesis.Header.StateRoot.Should().NotBe(Hash256.Zero);
    }

    [Fact]
    public void ChainManager_Rejects_Invalid_Parent_Hash()
    {
        _chainManager.CreateGenesisBlock(_params);

        var badBlock = new Block
        {
            Header = new BlockHeader
            {
                Number = 1,
                ParentHash = Hash256.Zero, // Wrong: should be genesis hash
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ChainId = _params.ChainId,
                GasLimit = _params.BlockGasLimit,
            },
            Transactions = [],
        };

        var result = _chainManager.AddBlock(badBlock);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void ChainManager_Rejects_Invalid_Block_Number()
    {
        var genesis = _chainManager.CreateGenesisBlock(_params);

        var badBlock = new Block
        {
            Header = new BlockHeader
            {
                Number = 5, // Should be 1
                ParentHash = genesis.Hash,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ChainId = _params.ChainId,
                GasLimit = _params.BlockGasLimit,
            },
            Transactions = [],
        };

        var result = _chainManager.AddBlock(badBlock);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void ChainManager_Accepts_Valid_Block_Sequence()
    {
        var genesis = _chainManager.CreateGenesisBlock(_params);

        var block1 = new Block
        {
            Header = new BlockHeader
            {
                Number = 1,
                ParentHash = genesis.Hash,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ChainId = _params.ChainId,
                GasLimit = _params.BlockGasLimit,
            },
            Transactions = [],
        };

        _chainManager.AddBlock(block1).IsSuccess.Should().BeTrue();
        _chainManager.LatestBlockNumber.Should().Be(1);

        var block2 = new Block
        {
            Header = new BlockHeader
            {
                Number = 2,
                ParentHash = block1.Hash,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ChainId = _params.ChainId,
                GasLimit = _params.BlockGasLimit,
            },
            Transactions = [],
        };

        _chainManager.AddBlock(block2).IsSuccess.Should().BeTrue();
        _chainManager.LatestBlockNumber.Should().Be(2);
    }

    [Fact]
    public void ChainManager_Block_Lookup_Works()
    {
        var genesis = _chainManager.CreateGenesisBlock(_params);

        _chainManager.GetBlockByNumber(0).Should().NotBeNull();
        _chainManager.GetBlockByHash(genesis.Hash).Should().NotBeNull();
        _chainManager.GetBlockByNumber(1).Should().BeNull();
    }
}
