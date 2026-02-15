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
            Host = $"10.0.0.{seed}",
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
    public void KBucket_Rejects_When_Full_And_Lower_Reputation()
    {
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
    }

    [Fact]
    public void KBucket_Evicts_When_New_Has_Higher_Reputation()
    {
        var bucket = new KBucket();
        // Fill bucket with low-rep peers
        for (int i = 0; i < KBucket.K; i++)
        {
            var p = MakePeer(i);
            p.ReputationScore = 50;
            bucket.InsertOrUpdate(p);
        }

        // New peer with higher reputation should replace tail
        var highRep = MakePeer(99);
        highRep.ReputationScore = 150;
        Assert.True(bucket.InsertOrUpdate(highRep));
        Assert.True(bucket.Contains(MakePeerId(99)));
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
}
