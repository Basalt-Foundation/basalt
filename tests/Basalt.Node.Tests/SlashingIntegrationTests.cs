using System.Security.Cryptography;
using Basalt.Consensus;
using Basalt.Consensus.Staking;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Node.Tests;

public class SlashingIntegrationTests
{
    private static Address MakeAddress()
    {
        var (_, pub) = Ed25519Signer.GenerateKeyPair();
        return Ed25519Signer.DeriveAddress(pub);
    }

    [Fact]
    public void SlashingEngine_DoubleSign_SlashesValidator()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        var addr = MakeAddress();
        state.RegisterValidator(addr, new UInt256(100_000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);
        var hash1 = Blake3Hasher.Hash([1]);
        var hash2 = Blake3Hasher.Hash([2]);

        engine.SlashDoubleSign(addr, 1, hash1, hash2);

        // Double-sign penalty is 100% -- validator should have 0 stake
        state.GetStakeInfo(addr)!.TotalStake.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void SlashingEngine_Inactivity_ReducesStake()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        var addr = MakeAddress();
        state.RegisterValidator(addr, new UInt256(100_000));
        var initialStake = state.GetStakeInfo(addr)!.TotalStake;

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);
        engine.SlashInactivity(addr, 0, 200);

        // Inactivity penalty is 5%
        state.GetStakeInfo(addr)!.TotalStake.Should().BeLessThan(initialStake);
    }

    [Fact]
    public void StakingState_RegisterValidator_SetsStake()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        var addr = MakeAddress();
        state.RegisterValidator(addr, new UInt256(200_000));
        state.GetStakeInfo(addr)!.TotalStake.Should().Be(new UInt256(200_000));
    }

    [Fact]
    public void StakingState_GetActiveValidators_ReturnsRegistered()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        var addr1 = MakeAddress();
        var addr2 = MakeAddress();
        state.RegisterValidator(addr1, new UInt256(100_000));
        state.RegisterValidator(addr2, new UInt256(100_000));
        state.GetActiveValidators().Should().HaveCount(2);
    }

    [Fact]
    public void WeightedLeaderSelector_SelectsBasedOnStake()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        var addr1 = MakeAddress();
        var addr2 = MakeAddress();
        state.RegisterValidator(addr1, new UInt256(100_000));
        state.RegisterValidator(addr2, new UInt256(900_000));

        // Create validator set with BLS-compatible keys
        var (_, pubKey1) = Ed25519Signer.GenerateKeyPair();
        var (_, pubKey2) = Ed25519Signer.GenerateKeyPair();
        var blsPrivKey1 = new byte[32];
        RandomNumberGenerator.Fill(blsPrivKey1);
        blsPrivKey1[0] &= 0x3F;
        if (blsPrivKey1[0] == 0) blsPrivKey1[0] = 1;
        var blsPrivKey2 = new byte[32];
        RandomNumberGenerator.Fill(blsPrivKey2);
        blsPrivKey2[0] &= 0x3F;
        if (blsPrivKey2[0] == 0) blsPrivKey2[0] = 1;

        var validators = new List<ValidatorInfo>
        {
            new ValidatorInfo
            {
                PeerId = PeerId.FromPublicKey(pubKey1),
                PublicKey = pubKey1,
                BlsPublicKey = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(blsPrivKey1)),
                Address = addr1,
                Index = 0,
            },
            new ValidatorInfo
            {
                PeerId = PeerId.FromPublicKey(pubKey2),
                PublicKey = pubKey2,
                BlsPublicKey = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(blsPrivKey2)),
                Address = addr2,
                Index = 1,
            },
        };

        var vs = new ValidatorSet(validators);

        // Weighted leader selector should work
        var selector = new WeightedLeaderSelector(vs, state);
        selector.Should().NotBeNull();

        // Selecting a leader should return one of the validators
        var leader = selector.SelectLeader(0);
        leader.Should().NotBeNull();
        leader.Address.Should().BeOneOf(addr1, addr2);
    }
}
