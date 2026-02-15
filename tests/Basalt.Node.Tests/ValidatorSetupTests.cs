using System.Security.Cryptography;
using Basalt.Consensus;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using FluentAssertions;
using Xunit;

namespace Basalt.Node.Tests;

public class ValidatorSetupTests
{
    private static (PeerId Id, byte[] Ed25519PrivKey, PublicKey PublicKey, Address Address, byte[] BlsPrivKey) MakeValidator()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var id = PeerId.FromPublicKey(pubKey);
        var addr = Ed25519Signer.DeriveAddress(pubKey);
        // Generate a separate BLS-compatible private key
        var blsPrivKey = new byte[32];
        RandomNumberGenerator.Fill(blsPrivKey);
        blsPrivKey[0] &= 0x3F;
        if (blsPrivKey[0] == 0) blsPrivKey[0] = 1;
        return (id, privKey, pubKey, addr, blsPrivKey);
    }

    [Fact]
    public void ValidatorSet_CreatesCorrectQuorum_For4Validators()
    {
        var validators = Enumerable.Range(0, 4).Select(i =>
        {
            var v = MakeValidator();
            return new ValidatorInfo
            {
                PeerId = v.Id,
                PublicKey = v.PublicKey,
                BlsPublicKey = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(v.BlsPrivKey)),
                Address = v.Address,
                Index = i,
            };
        }).ToList();

        var vs = new ValidatorSet(validators);
        vs.QuorumThreshold.Should().Be(3);
        vs.MaxFaults.Should().Be(1);
        vs.Count.Should().Be(4);
    }

    [Fact]
    public void ValidatorSet_GetLeader_CyclesThroughAll()
    {
        var validators = Enumerable.Range(0, 4).Select(i =>
        {
            var v = MakeValidator();
            return new ValidatorInfo
            {
                PeerId = v.Id,
                PublicKey = v.PublicKey,
                BlsPublicKey = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(v.BlsPrivKey)),
                Address = v.Address,
                Index = i,
            };
        }).ToList();

        var vs = new ValidatorSet(validators);
        var leaders = Enumerable.Range(0, 4)
            .Select(i => vs.GetLeader((ulong)i).PeerId)
            .Distinct()
            .Count();
        leaders.Should().Be(4);
    }

    [Fact]
    public void ValidatorSet_GetByPeerId_ReturnsCorrectValidator()
    {
        var v = MakeValidator();
        var info = new ValidatorInfo
        {
            PeerId = v.Id,
            PublicKey = v.PublicKey,
            BlsPublicKey = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(v.BlsPrivKey)),
            Address = v.Address,
            Index = 0,
        };
        var vs = new ValidatorSet([info]);
        var found = vs.GetByPeerId(v.Id);
        found.Should().NotBeNull();
        found!.Address.Should().Be(v.Address);
    }

    [Fact]
    public void ValidatorSet_GetByPeerId_UnknownPeer_ReturnsNull()
    {
        var v = MakeValidator();
        var info = new ValidatorInfo
        {
            PeerId = v.Id,
            PublicKey = v.PublicKey,
            BlsPublicKey = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(v.BlsPrivKey)),
            Address = v.Address,
            Index = 0,
        };
        var vs = new ValidatorSet([info]);
        var unknown = MakeValidator();
        vs.GetByPeerId(unknown.Id).Should().BeNull();
    }

    [Fact]
    public void NodeConfiguration_ConsensusMode_RequiresBothPeersAndIndex()
    {
        // No peers, has index -> not consensus
        new NodeConfiguration { ValidatorIndex = 0, Peers = [] }
            .IsConsensusMode.Should().BeFalse();

        // Has peers, negative index -> not consensus
        new NodeConfiguration { ValidatorIndex = -1, Peers = ["peer:30303"] }
            .IsConsensusMode.Should().BeFalse();

        // Both -> consensus
        new NodeConfiguration { ValidatorIndex = 0, Peers = ["peer:30303"] }
            .IsConsensusMode.Should().BeTrue();
    }

    [Fact]
    public void PeerId_FromPublicKey_IsDeterministic()
    {
        var (_, _, pubKey, _, _) = MakeValidator();
        var id1 = PeerId.FromPublicKey(pubKey);
        var id2 = PeerId.FromPublicKey(pubKey);
        id1.Should().Be(id2);
    }

    [Fact]
    public void Ed25519_SignAndVerify_RoundTrips()
    {
        var (_, privKey, pubKey, _, _) = MakeValidator();
        var message = new byte[] { 1, 2, 3, 4, 5 };
        var sig = Ed25519Signer.Sign(privKey, message);
        Ed25519Signer.Verify(pubKey, message, sig).Should().BeTrue();
    }

    [Fact]
    public void BlsSigner_SignAndVerify_WorksForValidatorKeys()
    {
        var signer = new BlsSigner();
        var (_, _, _, _, blsPrivKey) = MakeValidator();
        var blsPubKey = BlsSigner.GetPublicKeyStatic(blsPrivKey);
        var message = new byte[] { 10, 20, 30 };
        var sig = signer.Sign(blsPrivKey, message);
        signer.Verify(blsPubKey, message, sig).Should().BeTrue();
    }
}
