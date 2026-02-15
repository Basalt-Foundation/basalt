using Basalt.Core;
using Basalt.Crypto;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Network.Tests;

public class ReputationTests
{
    private static PeerInfo MakePeer(int seed)
    {
        var bytes = new byte[32];
        bytes[31] = (byte)seed;
        var id = new PeerId(new Hash256(bytes));
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        return new PeerInfo
        {
            Id = id,
            PublicKey = pubKey,
            Host = $"10.0.0.{seed}",
            Port = 30303,
            State = PeerState.Connected,
            ReputationScore = 100,
        };
    }

    [Fact]
    public void ValidBlock_Increases_Score()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var scorer = new ReputationScorer(pm, NullLogger<ReputationScorer>.Instance);

        var peer = MakePeer(1);
        pm.RegisterPeer(peer);

        scorer.RecordValidBlock(peer.Id);
        Assert.Equal(100 + ReputationScorer.Deltas.ValidBlock, peer.ReputationScore);
    }

    [Fact]
    public void InvalidBlock_Decreases_Score()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var scorer = new ReputationScorer(pm, NullLogger<ReputationScorer>.Instance);

        var peer = MakePeer(1);
        pm.RegisterPeer(peer);

        scorer.RecordInvalidBlock(peer.Id);
        Assert.Equal(100 + ReputationScorer.Deltas.InvalidBlock, peer.ReputationScore);
    }

    [Fact]
    public void Repeated_Violations_Lead_To_Ban()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var scorer = new ReputationScorer(pm, NullLogger<ReputationScorer>.Instance);

        var peer = MakePeer(1);
        pm.RegisterPeer(peer);

        // Multiple protocol violations
        for (int i = 0; i < 5; i++)
            scorer.RecordProtocolViolation(peer.Id);

        Assert.True(scorer.ShouldDisconnect(peer.Id));
        Assert.Equal(PeerState.Banned, peer.State);
    }

    [Fact]
    public void Score_Clamped_To_Range()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var scorer = new ReputationScorer(pm, NullLogger<ReputationScorer>.Instance);

        var peer = MakePeer(1);
        pm.RegisterPeer(peer);

        // Boost score many times
        for (int i = 0; i < 50; i++)
            scorer.RecordValidBlock(peer.Id);

        Assert.Equal(ReputationScorer.MaxScore, peer.ReputationScore);
    }

    [Fact]
    public void DecayScores_Moves_Toward_Default()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var scorer = new ReputationScorer(pm, NullLogger<ReputationScorer>.Instance);

        var peer = MakePeer(1);
        peer.ReputationScore = 150;
        pm.RegisterPeer(peer);

        scorer.DecayScores();
        Assert.Equal(149, peer.ReputationScore);
    }

    [Fact]
    public void GetPeersByReputation_Returns_Sorted()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var scorer = new ReputationScorer(pm, NullLogger<ReputationScorer>.Instance);

        var p1 = MakePeer(1); p1.ReputationScore = 80;
        var p2 = MakePeer(2); p2.ReputationScore = 150;
        var p3 = MakePeer(3); p3.ReputationScore = 120;
        pm.RegisterPeer(p1);
        pm.RegisterPeer(p2);
        pm.RegisterPeer(p3);

        var sorted = scorer.GetPeersByReputation();
        Assert.Equal(150, sorted[0].ReputationScore);
        Assert.Equal(120, sorted[1].ReputationScore);
        Assert.Equal(80, sorted[2].ReputationScore);
    }
}
