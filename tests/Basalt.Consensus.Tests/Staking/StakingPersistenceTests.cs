using Basalt.Consensus.Staking;
using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Consensus.Tests.Staking;

/// <summary>
/// B1: Tests for staking state persistence round-trip via IStakingPersistence.
/// </summary>
public class StakingPersistenceTests
{
    [Fact]
    public void FlushAndLoad_RoundTrips_SingleValidator()
    {
        var persistence = new InMemoryStakingPersistence();
        var state = new StakingState();
        var addr = Address.FromHexString("0x0000000000000000000000000000000000000100");
        state.RegisterValidator(addr, UInt256.Parse("200000000000000000000000"));

        state.FlushToPersistence(persistence);

        var loaded = new StakingState();
        loaded.LoadFromPersistence(persistence);
        var info = loaded.GetStakeInfo(addr);

        info.Should().NotBeNull();
        info!.SelfStake.Should().Be(UInt256.Parse("200000000000000000000000"));
        info.TotalStake.Should().Be(UInt256.Parse("200000000000000000000000"));
        info.IsActive.Should().BeTrue();
        info.Address.Should().Be(addr);
    }

    [Fact]
    public void FlushAndLoad_RoundTrips_MultipleValidators()
    {
        var persistence = new InMemoryStakingPersistence();
        var state = new StakingState();
        var addrs = new[]
        {
            Address.FromHexString("0x0000000000000000000000000000000000000100"),
            Address.FromHexString("0x0000000000000000000000000000000000000101"),
            Address.FromHexString("0x0000000000000000000000000000000000000102"),
        };

        foreach (var addr in addrs)
            state.RegisterValidator(addr, UInt256.Parse("200000000000000000000000"));

        state.FlushToPersistence(persistence);

        var loaded = new StakingState();
        loaded.LoadFromPersistence(persistence);
        var active = loaded.GetActiveValidators();

        active.Should().HaveCount(3);
    }

    [Fact]
    public void FlushAndLoad_PreservesDelegators()
    {
        var persistence = new InMemoryStakingPersistence();
        var state = new StakingState();
        var validator = Address.FromHexString("0x0000000000000000000000000000000000000100");
        var delegator = Address.FromHexString("0x0000000000000000000000000000000000000200");

        state.RegisterValidator(validator, UInt256.Parse("200000000000000000000000"));
        state.Delegate(delegator, validator, UInt256.Parse("50000000000000000000000"));

        state.FlushToPersistence(persistence);

        var loaded = new StakingState();
        loaded.LoadFromPersistence(persistence);
        var info = loaded.GetStakeInfo(validator);

        info.Should().NotBeNull();
        info!.DelegatedStake.Should().Be(UInt256.Parse("50000000000000000000000"));
        info.TotalStake.Should().Be(UInt256.Parse("250000000000000000000000"));
        info.Delegators.Should().ContainKey(delegator);
        info.Delegators[delegator].Should().Be(UInt256.Parse("50000000000000000000000"));
    }

    [Fact]
    public void FlushAndLoad_PreservesUnbondingQueue()
    {
        var persistence = new InMemoryStakingPersistence();
        var state = new StakingState();
        var validator = Address.FromHexString("0x0000000000000000000000000000000000000100");
        state.RegisterValidator(validator, UInt256.Parse("200000000000000000000000"));
        state.InitiateUnstake(validator, UInt256.Parse("50000000000000000000000"), currentBlock: 100);

        state.FlushToPersistence(persistence);

        // Verify unbonding queue was saved
        var loadedQueue = persistence.LoadUnbondingQueue();
        loadedQueue.Should().HaveCount(1);
        loadedQueue[0].Validator.Should().Be(validator);
        loadedQueue[0].Amount.Should().Be(UInt256.Parse("50000000000000000000000"));
    }

    [Fact]
    public void LoadFromEmpty_DoesNotCrash()
    {
        var persistence = new InMemoryStakingPersistence();
        var state = new StakingState();

        // Loading from empty persistence should not throw
        state.LoadFromPersistence(persistence);

        state.GetActiveValidators().Should().BeEmpty();
    }

    [Fact]
    public void FlushAndLoad_PreservesP2PEndpoint()
    {
        var persistence = new InMemoryStakingPersistence();
        var state = new StakingState();
        var addr = Address.FromHexString("0x0000000000000000000000000000000000000100");
        state.RegisterValidator(addr, UInt256.Parse("200000000000000000000000"),
            p2pEndpoint: "192.168.1.1:30303");

        state.FlushToPersistence(persistence);

        var loaded = new StakingState();
        loaded.LoadFromPersistence(persistence);
        var info = loaded.GetStakeInfo(addr);

        info.Should().NotBeNull();
        info!.P2PEndpoint.Should().Be("192.168.1.1:30303");
    }

    [Fact]
    public void FlushAndLoad_PreservesRegisteredAtBlock()
    {
        var persistence = new InMemoryStakingPersistence();
        var state = new StakingState();
        var addr = Address.FromHexString("0x0000000000000000000000000000000000000100");
        state.RegisterValidator(addr, UInt256.Parse("200000000000000000000000"), blockNumber: 42);

        state.FlushToPersistence(persistence);

        var loaded = new StakingState();
        loaded.LoadFromPersistence(persistence);
        var info = loaded.GetStakeInfo(addr);

        info.Should().NotBeNull();
        info!.RegisteredAtBlock.Should().Be(42UL);
    }

    [Fact]
    public void Load_MergesWithExistingState()
    {
        var persistence = new InMemoryStakingPersistence();
        var addr1 = Address.FromHexString("0x0000000000000000000000000000000000000100");
        var addr2 = Address.FromHexString("0x0000000000000000000000000000000000000101");

        // Save one validator
        var state1 = new StakingState();
        state1.RegisterValidator(addr1, UInt256.Parse("200000000000000000000000"));
        state1.FlushToPersistence(persistence);

        // Create state with different validator and load persisted
        var state2 = new StakingState();
        state2.RegisterValidator(addr2, UInt256.Parse("200000000000000000000000"));
        state2.LoadFromPersistence(persistence);

        // Should have both validators
        state2.GetStakeInfo(addr1).Should().NotBeNull();
        state2.GetStakeInfo(addr2).Should().NotBeNull();
    }

    /// <summary>
    /// In-memory implementation of IStakingPersistence for testing.
    /// </summary>
    private sealed class InMemoryStakingPersistence : IStakingPersistence
    {
        private Dictionary<Address, StakeInfo>? _savedStakes;
        private List<UnbondingEntry>? _savedUnbonding;

        public void SaveStakes(IReadOnlyDictionary<Address, StakeInfo> stakes)
        {
            _savedStakes = new Dictionary<Address, StakeInfo>(stakes);
        }

        public Dictionary<Address, StakeInfo> LoadStakes()
        {
            return _savedStakes != null
                ? new Dictionary<Address, StakeInfo>(_savedStakes)
                : new Dictionary<Address, StakeInfo>();
        }

        public void SaveUnbondingQueue(IReadOnlyList<UnbondingEntry> queue)
        {
            _savedUnbonding = new List<UnbondingEntry>(queue);
        }

        public List<UnbondingEntry> LoadUnbondingQueue()
        {
            return _savedUnbonding != null
                ? new List<UnbondingEntry>(_savedUnbonding)
                : new List<UnbondingEntry>();
        }
    }
}
