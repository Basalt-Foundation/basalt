using Basalt.Consensus;
using Basalt.Consensus.Staking;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using FluentAssertions;
using Xunit;

namespace Basalt.Consensus.Tests;

public class WeightedLeaderSelectorTests
{
    private static readonly IBlsSigner _blsSigner = new BlsSigner();

    private static (byte[] PrivateKey, PublicKey PublicKey, PeerId PeerId, Address Address) MakeValidator()
    {
        var privateKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(privateKey);
        privateKey[0] &= 0x3F;
        if (privateKey[0] == 0) privateKey[0] = 1;
        var publicKey = Ed25519Signer.GetPublicKey(privateKey);
        var peerId = PeerId.FromPublicKey(publicKey);
        var address = Ed25519Signer.DeriveAddress(publicKey);
        return (privateKey, publicKey, peerId, address);
    }

    private static (ValidatorSet Set, ValidatorInfo[] Infos) MakeValidatorSetWithInfos(int count)
    {
        var validators = Enumerable.Range(0, count).Select(i =>
        {
            var v = MakeValidator();
            return new ValidatorInfo
            {
                PeerId = v.PeerId,
                PublicKey = v.PublicKey,
                BlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(v.PrivateKey)),
                Address = v.Address,
                Index = i,
            };
        }).ToArray();

        return (new ValidatorSet(validators), validators);
    }

    [Fact]
    public void SingleValidator_AlwaysSelected()
    {
        var (set, infos) = MakeValidatorSetWithInfos(1);
        var stakingState = new StakingState { MinValidatorStake = new UInt256(100) };
        stakingState.RegisterValidator(infos[0].Address, new UInt256(1000));

        var selector = new WeightedLeaderSelector(set, stakingState);

        for (ulong view = 0; view < 10; view++)
        {
            selector.SelectLeader(view).Should().Be(infos[0]);
        }
    }

    [Fact]
    public void SelectLeader_IsDeterministic()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);
        var stakingState = new StakingState { MinValidatorStake = new UInt256(100) };
        foreach (var info in infos)
            stakingState.RegisterValidator(info.Address, new UInt256(1000));

        var selector = new WeightedLeaderSelector(set, stakingState);

        var leader1 = selector.SelectLeader(42);
        var leader2 = selector.SelectLeader(42);

        leader1.Should().Be(leader2);
    }

    [Fact]
    public void SelectLeader_DifferentViews_CanSelectDifferentLeaders()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);
        var stakingState = new StakingState { MinValidatorStake = new UInt256(100) };
        foreach (var info in infos)
            stakingState.RegisterValidator(info.Address, new UInt256(1000));

        var selector = new WeightedLeaderSelector(set, stakingState);

        // Over many views, at least 2 distinct leaders should be selected
        var leaders = Enumerable.Range(0, 100)
            .Select(i => selector.SelectLeader((ulong)i).PeerId)
            .Distinct()
            .Count();

        leaders.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void SelectLeader_HigherStake_SelectedMoreOften()
    {
        var (set, infos) = MakeValidatorSetWithInfos(2);
        var stakingState = new StakingState { MinValidatorStake = new UInt256(100) };

        // Validator 0 has 10x the stake of validator 1
        stakingState.RegisterValidator(infos[0].Address, new UInt256(10000));
        stakingState.RegisterValidator(infos[1].Address, new UInt256(1000));

        var selector = new WeightedLeaderSelector(set, stakingState);

        var counts = new int[2];
        for (ulong view = 0; view < 1000; view++)
        {
            var leader = selector.SelectLeader(view);
            if (leader.PeerId == infos[0].PeerId) counts[0]++;
            else counts[1]++;
        }

        // Validator 0 (10x stake) should be selected significantly more often
        counts[0].Should().BeGreaterThan(counts[1],
            "validator with 10x stake should be selected more frequently");
    }

    [Fact]
    public void SelectLeader_EqualStake_BothSelected()
    {
        var (set, infos) = MakeValidatorSetWithInfos(2);
        var stakingState = new StakingState { MinValidatorStake = new UInt256(100) };

        stakingState.RegisterValidator(infos[0].Address, new UInt256(1000));
        stakingState.RegisterValidator(infos[1].Address, new UInt256(1000));

        var selector = new WeightedLeaderSelector(set, stakingState);

        var counts = new int[2];
        for (ulong view = 0; view < 1000; view++)
        {
            var leader = selector.SelectLeader(view);
            if (leader.PeerId == infos[0].PeerId) counts[0]++;
            else counts[1]++;
        }

        // Both should be selected at least some of the time
        counts[0].Should().BeGreaterThan(0);
        counts[1].Should().BeGreaterThan(0);
    }

    [Fact]
    public void SelectLeader_Unregistered_StakeFallsToWeight1()
    {
        var (set, infos) = MakeValidatorSetWithInfos(3);
        var stakingState = new StakingState { MinValidatorStake = new UInt256(100) };

        // Only register one validator
        stakingState.RegisterValidator(infos[0].Address, new UInt256(10000));

        var selector = new WeightedLeaderSelector(set, stakingState);

        // Should not throw
        var leader = selector.SelectLeader(1);
        leader.Should().NotBeNull();
    }

    [Fact]
    public void SelectLeader_LargeViewNumber_DoesNotThrow()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);
        var stakingState = new StakingState { MinValidatorStake = new UInt256(100) };
        foreach (var info in infos)
            stakingState.RegisterValidator(info.Address, new UInt256(1000));

        var selector = new WeightedLeaderSelector(set, stakingState);

        var act = () => selector.SelectLeader(ulong.MaxValue);
        act.Should().NotThrow();
    }

    [Fact]
    public void SelectLeader_IntegratesWithValidatorSetLeaderSelector()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);
        var stakingState = new StakingState { MinValidatorStake = new UInt256(100) };
        foreach (var info in infos)
            stakingState.RegisterValidator(info.Address, new UInt256(1000));

        var selector = new WeightedLeaderSelector(set, stakingState);

        // Wire the weighted selector into ValidatorSet
        set.SetLeaderSelector(view => selector.SelectLeader(view));

        // Now ValidatorSet.GetLeader should use weighted selection
        var leader = set.GetLeader(42);
        var expectedLeader = selector.SelectLeader(42);
        leader.Should().Be(expectedLeader);
    }

    [Fact]
    public void SelectLeader_AllValidatorsAppearOverManyViews()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);
        var stakingState = new StakingState { MinValidatorStake = new UInt256(100) };
        foreach (var info in infos)
            stakingState.RegisterValidator(info.Address, new UInt256(1000));

        var selector = new WeightedLeaderSelector(set, stakingState);

        var selectedPeerIds = new HashSet<PeerId>();
        for (ulong view = 0; view < 1000; view++)
        {
            selectedPeerIds.Add(selector.SelectLeader(view).PeerId);
        }

        // With equal stake and 1000 attempts, all 4 validators should appear
        selectedPeerIds.Count.Should().Be(4);
    }
}
