using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network.Gossip;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Network.Tests;

public class EpisubTests
{
    private static PeerId MakePeerId(int seed)
    {
        var bytes = new byte[32];
        bytes[31] = (byte)seed;
        return new PeerId(new Hash256(bytes));
    }

    private static PeerInfo MakePeer(int seed)
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        return new PeerInfo
        {
            Id = MakePeerId(seed),
            PublicKey = pubKey,
            Host = $"10.0.0.{seed}",
            Port = 30303,
            State = PeerState.Connected,
        };
    }

    [Fact]
    public void Episub_Assigns_Peers_To_Tiers()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var episub = new EpisubService(pm, NullLogger<EpisubService>.Instance);

        // Connect 10 peers — first 6 should be eager, rest lazy
        for (int i = 1; i <= 10; i++)
        {
            var peer = MakePeer(i);
            pm.RegisterPeer(peer);
            episub.OnPeerConnected(peer.Id);
        }

        Assert.Equal(6, episub.EagerPeerCount);
        Assert.Equal(4, episub.LazyPeerCount);
    }

    [Fact]
    public void Episub_Peer_Disconnect_Removes_From_Tiers()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var episub = new EpisubService(pm, NullLogger<EpisubService>.Instance);

        var peer = MakePeer(1);
        pm.RegisterPeer(peer);
        episub.OnPeerConnected(peer.Id);

        Assert.Equal(1, episub.EagerPeerCount);

        episub.OnPeerDisconnected(peer.Id);
        Assert.Equal(0, episub.EagerPeerCount);
    }

    [Fact]
    public void Episub_Priority_Broadcast_Sends_To_Eager()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var episub = new EpisubService(pm, NullLogger<EpisubService>.Instance);

        var sentTo = new List<PeerId>();
        episub.OnSendMessage += (peerId, _) => sentTo.Add(peerId);

        // Add 3 eager peers
        for (int i = 1; i <= 3; i++)
        {
            var peer = MakePeer(i);
            pm.RegisterPeer(peer);
            episub.OnPeerConnected(peer.Id);
        }

        var msgId = Blake3Hasher.Hash([1, 2, 3]);
        var msg = new BlockAnnounceMessage
        {
            BlockNumber = 1,
            BlockHash = msgId,
            ParentHash = Hash256.Zero,
        };

        episub.BroadcastPriority(msgId, msg);

        Assert.Equal(3, sentTo.Count); // All 3 eager peers
    }

    [Fact]
    public void Episub_Deduplicates_Messages()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var episub = new EpisubService(pm, NullLogger<EpisubService>.Instance);

        var sendCount = 0;
        episub.OnSendMessage += (_, _) => Interlocked.Increment(ref sendCount);

        var peer = MakePeer(1);
        pm.RegisterPeer(peer);
        episub.OnPeerConnected(peer.Id);

        var msgId = Blake3Hasher.Hash([1, 2, 3]);
        var msg = new BlockAnnounceMessage
        {
            BlockNumber = 1,
            BlockHash = msgId,
            ParentHash = Hash256.Zero,
        };

        episub.BroadcastPriority(msgId, msg);
        int firstSend = sendCount;

        // Second broadcast of same message should be suppressed
        episub.BroadcastPriority(msgId, msg);
        Assert.Equal(firstSend, sendCount);
    }

    [Fact]
    public void Episub_Graft_And_Prune()
    {
        var pm = new PeerManager(NullLogger<PeerManager>.Instance);
        var episub = new EpisubService(pm, NullLogger<EpisubService>.Instance);

        // Add 8 peers (6 eager + 2 lazy)
        for (int i = 1; i <= 8; i++)
        {
            var peer = MakePeer(i);
            pm.RegisterPeer(peer);
            episub.OnPeerConnected(peer.Id);
        }

        Assert.Equal(6, episub.EagerPeerCount);
        Assert.Equal(2, episub.LazyPeerCount);

        // Graft peer 7 (lazy -> eager)
        episub.GraftPeer(MakePeerId(7));
        Assert.Equal(7, episub.EagerPeerCount);
        Assert.Equal(1, episub.LazyPeerCount);

        // Prune peer 1 (eager -> lazy)
        episub.PrunePeer(MakePeerId(1));
        Assert.Equal(6, episub.EagerPeerCount);
        Assert.Equal(2, episub.LazyPeerCount);
    }
}
