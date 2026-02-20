using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network.DHT;
using Xunit;

namespace Basalt.Network.Tests;

public class KademliaTests
{
    private static PeerId MakePeerId(int seed)
    {
        var bytes = new byte[32];
        bytes[31] = (byte)seed;
        return new PeerId(new Hash256(bytes));
    }

    private static PeerInfo MakePeer(int seed)
    {
        var id = MakePeerId(seed);
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        return new PeerInfo
        {
            Id = id,
            PublicKey = pubKey,
            // NET-H10: Use diverse /24 subnets so peers aren't rejected by subnet limit
            Host = $"10.{(seed / 256) % 256}.{seed % 256}.{(seed * 7) % 256}",
            Port = 30303,
            State = PeerState.Connected,
        };
    }

    [Fact]
    public void KBucket_Insert_Up_To_K()
    {
        var bucket = new KBucket();
        for (int i = 0; i < KBucket.K; i++)
        {
            Assert.True(bucket.InsertOrUpdate(MakePeer(i)));
        }
        Assert.Equal(KBucket.K, bucket.Count);
    }

    [Fact]
    public void KBucket_Rejects_All_Newcomers_When_Full()
    {
        // NET-H11: Standard Kademlia — bucket full means all newcomers are rejected
        var bucket = new KBucket();
        // Fill bucket
        for (int i = 0; i < KBucket.K; i++)
        {
            var p = MakePeer(i);
            p.ReputationScore = 100;
            bucket.InsertOrUpdate(p);
        }

        // New peer with lower reputation should be rejected
        var lowRep = MakePeer(99);
        lowRep.ReputationScore = 50;
        Assert.False(bucket.InsertOrUpdate(lowRep));

        // NET-H11: New peer with higher reputation should ALSO be rejected
        // (standard Kademlia prefers long-lived nodes over newcomers)
        var highRep = MakePeer(98);
        highRep.ReputationScore = 150;
        Assert.False(bucket.InsertOrUpdate(highRep));
    }

    [Fact]
    public void KBucket_Update_Moves_To_Front()
    {
        var bucket = new KBucket();
        bucket.InsertOrUpdate(MakePeer(1));
        bucket.InsertOrUpdate(MakePeer(2));
        bucket.InsertOrUpdate(MakePeer(3));

        // Update peer 1 — should move to front
        bucket.InsertOrUpdate(MakePeer(1));
        var peers = bucket.GetPeers();
        Assert.Equal(MakePeerId(1), peers[0].Id);
    }

    [Fact]
    public void KademliaTable_AddOrUpdate()
    {
        var localId = MakePeerId(0);
        var table = new KademliaTable(localId);

        for (int i = 1; i <= 50; i++)
            table.AddOrUpdate(MakePeer(i));

        Assert.Equal(50, table.PeerCount);
    }

    [Fact]
    public void KademliaTable_DoesNot_Add_Self()
    {
        var localId = MakePeerId(42);
        var table = new KademliaTable(localId);

        // Create a peer with the same ID as local
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var self = new PeerInfo
        {
            Id = localId,
            PublicKey = pubKey,
            Host = "10.0.0.42",
            Port = 30303,
            State = PeerState.Connected,
        };
        Assert.False(table.AddOrUpdate(self));
        Assert.Equal(0, table.PeerCount);
    }

    [Fact]
    public void KademliaTable_FindClosest_Returns_K_Nearest()
    {
        var localId = MakePeerId(0);
        var table = new KademliaTable(localId);

        for (int i = 1; i <= 100; i++)
            table.AddOrUpdate(MakePeer(i));

        var target = MakePeerId(50);
        var closest = table.FindClosest(target, 10);

        Assert.Equal(10, closest.Count);
        // The closest peers should include peer 50
        Assert.Contains(closest, p => p.Id == MakePeerId(50));
    }

    [Fact]
    public void KademliaTable_Remove()
    {
        var localId = MakePeerId(0);
        var table = new KademliaTable(localId);

        table.AddOrUpdate(MakePeer(1));
        table.AddOrUpdate(MakePeer(2));
        Assert.Equal(2, table.PeerCount);

        table.Remove(MakePeerId(1));
        Assert.Equal(1, table.PeerCount);
    }

    [Fact]
    public void KademliaTable_BucketIndex_Different_For_Different_Distances()
    {
        var localId = MakePeerId(0);
        var table = new KademliaTable(localId);

        // Peer with only last bit different (distance = 1, bucket = 0)
        int b1 = table.GetBucketIndex(MakePeerId(1));
        // Peer with larger distance
        int b2 = table.GetBucketIndex(MakePeerId(128));

        Assert.True(b2 > b1, $"Expected bucket for 128 ({b2}) > bucket for 1 ({b1})");
    }

    [Fact]
    public void NodeLookup_Finds_Target()
    {
        var localId = MakePeerId(0);
        var table = new KademliaTable(localId);

        // Add 20 peers
        for (int i = 1; i <= 20; i++)
            table.AddOrUpdate(MakePeer(i));

        var lookup = new NodeLookup(table, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        // No remote query function — just returns local table results
        var results = lookup.Lookup(MakePeerId(10));

        Assert.NotEmpty(results);
        Assert.Contains(results, p => p.Id == MakePeerId(10));
    }

    [Fact]
    public void KademliaTable_RejectsExcessiveSubnetPeers()
    {
        // NET-H10: Verify IP diversity check — max 2 peers per /24 subnet per bucket
        var localId = MakePeerId(0);
        var table = new KademliaTable(localId);

        // Create 3 peers that map to the same bucket with the same /24 subnet
        var (_, pubKey1) = Ed25519Signer.GenerateKeyPair();
        var (_, pubKey2) = Ed25519Signer.GenerateKeyPair();
        var (_, pubKey3) = Ed25519Signer.GenerateKeyPair();

        // Peers with IDs 1, 3 both go to bucket 0 and 1 respectively,
        // but 1 goes to bucket 0. Use peer IDs that share the same bucket.
        // PeerId(1) -> bucket 0, PeerId(2) -> bucket 1, PeerId(3) -> bucket 1
        // Seeds 2 and 3: XOR with 0 = 2 (bit 1 -> bucket 1) and 3 (bit 1 -> bucket 1)
        var peer1 = new PeerInfo { Id = MakePeerId(2), PublicKey = pubKey1, Host = "192.168.1.10", Port = 30303, State = PeerState.Connected };
        var peer2 = new PeerInfo { Id = MakePeerId(3), PublicKey = pubKey2, Host = "192.168.1.20", Port = 30303, State = PeerState.Connected };
        var peer3 = new PeerInfo { Id = MakePeerId(6), PublicKey = pubKey3, Host = "192.168.1.30", Port = 30303, State = PeerState.Connected };
        // PeerId(6) = 0b110, highest bit = bit 2 -> bucket 2, not the same bucket.
        // Use PeerId that also maps to bucket 1: needs highest bit at position 1.
        // Values 2, 3 have highest bit at position 1 -> bucket 1. That's only 2 values.
        // So with just 2 peers we hit the limit. Add one more that should be rejected.
        // Actually, we need 3 peers in the same bucket. Use IDs where highest bit is at a higher position
        // to get a bucket with more room. Bucket 3 (bit 3): IDs 8-15.
        peer1 = new PeerInfo { Id = MakePeerId(8), PublicKey = pubKey1, Host = "192.168.1.10", Port = 30303, State = PeerState.Connected };
        peer2 = new PeerInfo { Id = MakePeerId(9), PublicKey = pubKey2, Host = "192.168.1.20", Port = 30303, State = PeerState.Connected };
        peer3 = new PeerInfo { Id = MakePeerId(10), PublicKey = pubKey3, Host = "192.168.1.30", Port = 30303, State = PeerState.Connected };

        Assert.True(table.AddOrUpdate(peer1));
        Assert.True(table.AddOrUpdate(peer2));
        // Third peer from same /24 should be rejected
        Assert.False(table.AddOrUpdate(peer3));
        Assert.Equal(2, table.PeerCount);
    }

    [Fact]
    public void KademliaTable_OutboundProtected_CannotBeRemoved()
    {
        // NET-C05: Outbound-protected peers cannot be removed
        var localId = MakePeerId(0);
        var table = new KademliaTable(localId);

        var peer = MakePeer(1);
        table.AddOrUpdate(peer);
        Assert.Equal(1, table.PeerCount);

        // Mark as outbound-protected
        Assert.True(table.MarkOutboundProtected(peer.Id));
        Assert.True(table.IsOutboundProtected(peer.Id));

        // Attempt to remove — should fail
        Assert.False(table.Remove(peer.Id));
        Assert.Equal(1, table.PeerCount);

        // Unmark and remove — should succeed
        Assert.True(table.UnmarkOutboundProtected(peer.Id));
        Assert.False(table.IsOutboundProtected(peer.Id));
        Assert.True(table.Remove(peer.Id));
        Assert.Equal(0, table.PeerCount);
    }

    [Fact]
    public void KademliaTable_OutboundProtected_MaxSlots()
    {
        // NET-C05: Only 4 outbound-protected slots available
        var localId = MakePeerId(0);
        var table = new KademliaTable(localId);

        for (int i = 1; i <= 4; i++)
        {
            Assert.True(table.MarkOutboundProtected(MakePeerId(i)));
        }

        // 5th should be rejected
        Assert.False(table.MarkOutboundProtected(MakePeerId(5)));
    }
}
