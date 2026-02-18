using Basalt.Consensus.Staking;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using Xunit;

namespace Basalt.Consensus.Tests;

public class EpochManagerTests
{
    private static Address MakeAddr(byte seed) => Address.FromHexString($"0x{seed:X40}");

    private static StakingState CreateStakingState(int validatorCount, UInt256? stake = null)
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000) };
        var s = stake ?? new UInt256(10000);
        for (byte i = 1; i <= validatorCount; i++)
            ss.RegisterValidator(MakeAddr(i), s);
        return ss;
    }

    private static ValidatorSet CreatePlaceholderSet(int count)
    {
        IBlsSigner blsSigner = new BlsSigner();
        var validators = new List<ValidatorInfo>();
        for (int i = 0; i < count; i++)
        {
            var key = new byte[32];
            key[0] = (byte)(i + 1);
            key[31] = 1;
            var pk = Ed25519Signer.GetPublicKey(key);
            validators.Add(new ValidatorInfo
            {
                PeerId = PeerId.FromPublicKey(pk),
                PublicKey = pk,
                BlsPublicKey = new BlsPublicKey(blsSigner.GetPublicKey(key)),
                Address = MakeAddr((byte)(i + 1)),
                Index = i,
            });
        }
        return new ValidatorSet(validators);
    }

    private static ChainParameters DevnetParams => new()
    {
        ChainId = 31337,
        NetworkName = "test-devnet",
        EpochLength = 100,
        ValidatorSetSize = 4,
        MinValidatorStake = new UInt256(1000),
    };

    [Fact]
    public void ComputeEpoch_Returns_Correct_Epoch()
    {
        Assert.Equal(0UL, EpochManager.ComputeEpoch(0, 100));
        Assert.Equal(0UL, EpochManager.ComputeEpoch(99, 100));
        Assert.Equal(1UL, EpochManager.ComputeEpoch(100, 100));
        Assert.Equal(1UL, EpochManager.ComputeEpoch(199, 100));
        Assert.Equal(2UL, EpochManager.ComputeEpoch(200, 100));
    }

    [Fact]
    public void ComputeEpoch_Zero_EpochLength_Returns_Zero()
    {
        Assert.Equal(0UL, EpochManager.ComputeEpoch(500, 0));
    }

    [Fact]
    public void IsEpochBoundary_Detects_Boundaries()
    {
        var ss = CreateStakingState(4);
        var set = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, set);

        Assert.False(mgr.IsEpochBoundary(0));
        Assert.False(mgr.IsEpochBoundary(50));
        Assert.False(mgr.IsEpochBoundary(99));
        Assert.True(mgr.IsEpochBoundary(100));
        Assert.True(mgr.IsEpochBoundary(200));
        Assert.False(mgr.IsEpochBoundary(101));
    }

    [Fact]
    public void OnBlockFinalized_NoTransition_Before_Boundary()
    {
        var ss = CreateStakingState(4);
        var set = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, set);

        for (ulong i = 1; i < 100; i++)
        {
            var result = mgr.OnBlockFinalized(i);
            Assert.Null(result);
        }

        Assert.Equal(0UL, mgr.CurrentEpoch);
    }

    [Fact]
    public void OnBlockFinalized_Transitions_At_Boundary()
    {
        var ss = CreateStakingState(4);
        var set = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, set);

        var newSet = mgr.OnBlockFinalized(100);

        Assert.NotNull(newSet);
        Assert.Equal(1UL, mgr.CurrentEpoch);
        Assert.Equal(4, newSet!.Count);
    }

    [Fact]
    public void OnBlockFinalized_Fires_Event()
    {
        var ss = CreateStakingState(4);
        var set = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, set);

        ulong firedEpoch = 0;
        ValidatorSet? firedSet = null;
        mgr.OnEpochTransition += (epoch, vs) =>
        {
            firedEpoch = epoch;
            firedSet = vs;
        };

        mgr.OnBlockFinalized(100);

        Assert.Equal(1UL, firedEpoch);
        Assert.NotNull(firedSet);
    }

    [Fact]
    public void OnBlockFinalized_Does_Not_Double_Trigger()
    {
        var ss = CreateStakingState(4);
        var set = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, set);

        var first = mgr.OnBlockFinalized(100);
        var second = mgr.OnBlockFinalized(100);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1UL, mgr.CurrentEpoch);
    }

    [Fact]
    public void BuildValidatorSetFromStaking_Sorts_By_Address()
    {
        var ss = CreateStakingState(4);
        var set = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, set);

        var built = mgr.BuildValidatorSetFromStaking();

        // Verify deterministic ordering: indices match ascending address order
        for (int i = 0; i < built.Count - 1; i++)
        {
            Assert.True(built.Validators[i].Address.CompareTo(built.Validators[i + 1].Address) < 0);
            Assert.Equal(i, built.Validators[i].Index);
        }
    }

    [Fact]
    public void BuildValidatorSetFromStaking_Caps_At_ValidatorSetSize()
    {
        // Register 6 validators but ValidatorSetSize is 4
        var ss = CreateStakingState(6);
        var set = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, set);

        var built = mgr.BuildValidatorSetFromStaking();

        Assert.Equal(4, built.Count);
    }

    [Fact]
    public void BuildValidatorSetFromStaking_Excludes_Inactive()
    {
        var ss = CreateStakingState(4);
        // Deactivate validator 2 by unstaking everything
        ss.InitiateUnstake(MakeAddr(2), new UInt256(10000), 0);

        var set = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, set);

        var built = mgr.BuildValidatorSetFromStaking();

        Assert.Equal(3, built.Count);
        Assert.Null(built.GetByAddress(MakeAddr(2)));
    }

    [Fact]
    public void TransferIdentities_Preserves_PeerId_Across_Epochs()
    {
        var ss = CreateStakingState(4);
        var originalSet = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, originalSet);

        // Remember original PeerIds by address
        var originalPeerIds = new Dictionary<Address, PeerId>();
        foreach (var v in originalSet.Validators)
            originalPeerIds[v.Address] = v.PeerId;

        // Trigger epoch transition
        var newSet = mgr.OnBlockFinalized(100);
        Assert.NotNull(newSet);

        // Verify PeerIds were transferred
        foreach (var v in newSet!.Validators)
        {
            if (originalPeerIds.TryGetValue(v.Address, out var expectedPeerId))
                Assert.Equal(expectedPeerId, v.PeerId);
        }
    }

    [Fact]
    public void Multiple_Epochs_Work_Correctly()
    {
        var ss = CreateStakingState(4);
        var set = CreatePlaceholderSet(4);
        var mgr = new EpochManager(DevnetParams, ss, set);

        // Epoch 1
        var set1 = mgr.OnBlockFinalized(100);
        Assert.NotNull(set1);
        Assert.Equal(1UL, mgr.CurrentEpoch);

        // Epoch 2
        var set2 = mgr.OnBlockFinalized(200);
        Assert.NotNull(set2);
        Assert.Equal(2UL, mgr.CurrentEpoch);

        // Non-boundary doesn't transition
        Assert.Null(mgr.OnBlockFinalized(250));
        Assert.Equal(2UL, mgr.CurrentEpoch);
    }
}
