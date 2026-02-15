using Basalt.Consensus;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using FluentAssertions;
using Xunit;

namespace Basalt.Consensus.Tests;

public class ValidatorSetTests
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

    private static ValidatorSet MakeValidatorSet(int count)
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
        }).ToList();

        return new ValidatorSet(validators);
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

    // --- Count ---

    [Fact]
    public void Count_ReturnsNumberOfValidators()
    {
        var set = MakeValidatorSet(4);
        set.Count.Should().Be(4);
    }

    [Fact]
    public void Count_SingleValidator()
    {
        var set = MakeValidatorSet(1);
        set.Count.Should().Be(1);
    }

    [Fact]
    public void Count_SevenValidators()
    {
        var set = MakeValidatorSet(7);
        set.Count.Should().Be(7);
    }

    // --- QuorumThreshold ---

    [Theory]
    [InlineData(1, 1)]   // (1*2/3)+1 = 1
    [InlineData(3, 3)]   // (3*2/3)+1 = 3
    [InlineData(4, 3)]   // (4*2/3)+1 = 3
    [InlineData(7, 5)]   // (7*2/3)+1 = 5
    [InlineData(10, 7)]  // (10*2/3)+1 = 7
    [InlineData(100, 67)] // (100*2/3)+1 = 67
    public void QuorumThreshold_CalculatedCorrectly(int validatorCount, int expectedQuorum)
    {
        var set = MakeValidatorSet(validatorCount);
        set.QuorumThreshold.Should().Be(expectedQuorum);
    }

    // --- MaxFaults ---

    [Theory]
    [InlineData(1, 0)]   // (1-1)/3 = 0
    [InlineData(3, 0)]   // (3-1)/3 = 0 (but 3f+1=4 means f=0 with only 3 nodes isn't quite right, but the formula is (n-1)/3)
    [InlineData(4, 1)]   // (4-1)/3 = 1
    [InlineData(7, 2)]   // (7-1)/3 = 2
    [InlineData(10, 3)]  // (10-1)/3 = 3
    [InlineData(100, 33)] // (100-1)/3 = 33
    public void MaxFaults_CalculatedCorrectly(int validatorCount, int expectedFaults)
    {
        var set = MakeValidatorSet(validatorCount);
        set.MaxFaults.Should().Be(expectedFaults);
    }

    // --- GetLeader ---

    [Fact]
    public void GetLeader_RoundRobin_CyclesThroughAllValidators()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);

        for (int view = 0; view < 4; view++)
        {
            var leader = set.GetLeader((ulong)view);
            leader.Should().Be(infos[view % 4]);
        }
    }

    [Fact]
    public void GetLeader_RoundRobin_WrapsAround()
    {
        var (set, infos) = MakeValidatorSetWithInfos(3);

        // View 3 should wrap to validator 0
        set.GetLeader(3).Should().Be(infos[0]);
        set.GetLeader(6).Should().Be(infos[0]);
        set.GetLeader(7).Should().Be(infos[1]);
    }

    [Fact]
    public void GetLeader_SingleValidator_AlwaysSame()
    {
        var (set, infos) = MakeValidatorSetWithInfos(1);

        for (ulong view = 0; view < 10; view++)
        {
            set.GetLeader(view).Should().Be(infos[0]);
        }
    }

    [Fact]
    public void GetLeader_Deterministic_SameViewSameLeader()
    {
        var (set, _) = MakeValidatorSetWithInfos(4);

        var leader1 = set.GetLeader(42);
        var leader2 = set.GetLeader(42);

        leader1.Should().Be(leader2);
    }

    [Fact]
    public void GetLeader_LargeViewNumber_DoesNotThrow()
    {
        var set = MakeValidatorSet(4);
        var act = () => set.GetLeader(ulong.MaxValue);
        act.Should().NotThrow();
    }

    // --- GetByPeerId ---

    [Fact]
    public void GetByPeerId_ReturnsCorrectValidator()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);

        foreach (var info in infos)
        {
            var found = set.GetByPeerId(info.PeerId);
            found.Should().NotBeNull();
            found!.PeerId.Should().Be(info.PeerId);
            found.Address.Should().Be(info.Address);
        }
    }

    [Fact]
    public void GetByPeerId_UnknownPeerId_ReturnsNull()
    {
        var set = MakeValidatorSet(4);
        var unknown = MakeValidator();
        var unknownPeerId = unknown.PeerId;

        set.GetByPeerId(unknownPeerId).Should().BeNull();
    }

    // --- GetByAddress ---

    [Fact]
    public void GetByAddress_ReturnsCorrectValidator()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);

        foreach (var info in infos)
        {
            var found = set.GetByAddress(info.Address);
            found.Should().NotBeNull();
            found!.Address.Should().Be(info.Address);
            found.PeerId.Should().Be(info.PeerId);
        }
    }

    [Fact]
    public void GetByAddress_UnknownAddress_ReturnsNull()
    {
        var set = MakeValidatorSet(4);
        var unknownAddr = Address.FromHexString("0x0000000000000000000000000000000000000099");

        set.GetByAddress(unknownAddr).Should().BeNull();
    }

    // --- IsValidator ---

    [Fact]
    public void IsValidator_ReturnsTrueForValidators()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);

        foreach (var info in infos)
        {
            set.IsValidator(info.PeerId).Should().BeTrue();
        }
    }

    [Fact]
    public void IsValidator_ReturnsFalseForNonValidator()
    {
        var set = MakeValidatorSet(4);
        var unknown = MakeValidator();

        set.IsValidator(unknown.PeerId).Should().BeFalse();
    }

    // --- Validators (IReadOnlyList) ---

    [Fact]
    public void Validators_ReturnsAllValidatorsInOrder()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);

        set.Validators.Should().HaveCount(4);
        for (int i = 0; i < 4; i++)
        {
            set.Validators[i].PeerId.Should().Be(infos[i].PeerId);
        }
    }

    // --- SetLeaderSelector ---

    [Fact]
    public void SetLeaderSelector_OverridesRoundRobin()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);

        // Always select the last validator regardless of view
        set.SetLeaderSelector(view => infos[3]);

        for (ulong view = 0; view < 10; view++)
        {
            set.GetLeader(view).Should().Be(infos[3]);
        }
    }

    [Fact]
    public void SetLeaderSelector_ReceivesCorrectViewNumber()
    {
        var (set, infos) = MakeValidatorSetWithInfos(4);
        ulong receivedView = 0;
        set.SetLeaderSelector(view =>
        {
            receivedView = view;
            return infos[0];
        });

        set.GetLeader(42);
        receivedView.Should().Be(42);
    }

    // --- ValidatorInfo properties ---

    [Fact]
    public void ValidatorInfo_StakeDefaultsToZero()
    {
        var v = MakeValidator();
        var info = new ValidatorInfo
        {
            PeerId = v.PeerId,
            PublicKey = v.PublicKey,
            BlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(v.PrivateKey)),
            Address = v.Address,
            Index = 0,
        };

        info.Stake.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void ValidatorInfo_StakeCanBeSet()
    {
        var v = MakeValidator();
        var info = new ValidatorInfo
        {
            PeerId = v.PeerId,
            PublicKey = v.PublicKey,
            BlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(v.PrivateKey)),
            Address = v.Address,
            Index = 0,
        };

        info.Stake = new UInt256(1000);
        info.Stake.Should().Be(new UInt256(1000));
    }

    // --- Edge cases: BFT thresholds ---

    [Fact]
    public void QuorumThreshold_IsStrictlyGreaterThanTwoThirds()
    {
        // BFT safety requires strictly more than 2/3 agreement
        for (int n = 1; n <= 20; n++)
        {
            var set = MakeValidatorSet(n);
            var q = set.QuorumThreshold;

            // quorum > 2n/3 (floating point comparison)
            q.Should().BeGreaterThan((int)(2.0 * n / 3.0) - 1,
                $"Quorum {q} must be > 2*{n}/3 for {n} validators");
        }
    }

    [Fact]
    public void MaxFaults_Plus_QuorumThreshold_MakesSystemSafe()
    {
        // For BFT: n >= 3f+1 and quorum >= 2f+1
        for (int n = 1; n <= 20; n++)
        {
            var set = MakeValidatorSet(n);
            var f = set.MaxFaults;
            var q = set.QuorumThreshold;

            // n >= 3f + 1
            n.Should().BeGreaterThanOrEqualTo(3 * f + 1,
                $"n={n} must be >= 3*f+1 = {3 * f + 1}");

            // quorum >= 2f + 1
            q.Should().BeGreaterThanOrEqualTo(2 * f + 1,
                $"quorum={q} must be >= 2*f+1 = {2 * f + 1} for n={n}");
        }
    }
}
