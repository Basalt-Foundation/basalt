using Basalt.Core;

namespace Basalt.Network.DHT;

/// <summary>
/// Kademlia routing table with 256 k-buckets.
/// Each bucket stores up to K (20) peers at a specific XOR distance from the local node.
/// </summary>
public sealed class KademliaTable
{
    private readonly PeerId _localId;
    private readonly KBucket[] _buckets;
    private const int BucketCount = 256;
    private const int Alpha = 3; // Concurrent lookups

    public KademliaTable(PeerId localId)
    {
        _localId = localId;
        _buckets = new KBucket[BucketCount];
        for (int i = 0; i < BucketCount; i++)
            _buckets[i] = new KBucket();
    }

    /// <summary>
    /// Local node ID.
    /// </summary>
    public PeerId LocalId => _localId;

    /// <summary>
    /// Concurrency parameter for lookups.
    /// </summary>
    public static int ConcurrencyAlpha => Alpha;

    /// <summary>
    /// Add or update a peer in the routing table.
    /// </summary>
    public bool AddOrUpdate(PeerInfo peer)
    {
        if (peer.Id == _localId)
            return false; // Don't add ourselves

        int bucket = GetBucketIndex(peer.Id);
        return _buckets[bucket].InsertOrUpdate(peer);
    }

    /// <summary>
    /// Remove a peer from the routing table.
    /// </summary>
    public bool Remove(PeerId id)
    {
        int bucket = GetBucketIndex(id);
        return _buckets[bucket].Remove(id);
    }

    /// <summary>
    /// Find the K closest peers to a target ID.
    /// </summary>
    public List<PeerInfo> FindClosest(PeerId target, int count = KBucket.K)
    {
        var candidates = new SortedList<byte[], PeerInfo>(new XorDistanceComparer(target));

        int targetBucket = GetBucketIndex(target);

        // Start from the target bucket and expand outward
        AddBucketPeers(candidates, targetBucket);

        for (int offset = 1; offset < BucketCount && candidates.Count < count; offset++)
        {
            if (targetBucket - offset >= 0)
                AddBucketPeers(candidates, targetBucket - offset);
            if (targetBucket + offset < BucketCount)
                AddBucketPeers(candidates, targetBucket + offset);
        }

        return candidates.Values.Take(count).ToList();
    }

    /// <summary>
    /// Get all peers in the routing table.
    /// </summary>
    public List<PeerInfo> GetAllPeers()
    {
        var result = new List<PeerInfo>();
        foreach (var bucket in _buckets)
            result.AddRange(bucket.GetPeers());
        return result;
    }

    /// <summary>
    /// Total number of peers in the routing table.
    /// </summary>
    public int PeerCount => _buckets.Sum(b => b.Count);

    /// <summary>
    /// Compute the bucket index for a peer ID (based on XOR distance from local ID).
    /// The bucket index is the position of the highest set bit in XOR(local, remote).
    /// </summary>
    public int GetBucketIndex(PeerId remoteId)
    {
        var localBytes = _localId.AsHash256().ToArray();
        var remoteBytes = remoteId.AsHash256().ToArray();

        for (int i = 0; i < 32; i++)
        {
            byte xor = (byte)(localBytes[i] ^ remoteBytes[i]);
            if (xor != 0)
            {
                // Find highest set bit
                int bit = 7;
                while (bit >= 0 && (xor & (1 << bit)) == 0)
                    bit--;
                return (31 - i) * 8 + bit;
            }
        }

        return 0; // Identical IDs (shouldn't happen)
    }

    private void AddBucketPeers(SortedList<byte[], PeerInfo> candidates, int bucketIndex)
    {
        foreach (var peer in _buckets[bucketIndex].GetPeers())
        {
            var key = peer.Id.AsHash256().ToArray();
            candidates.TryAdd(key, peer);
        }
    }

    /// <summary>
    /// Comparer that sorts peers by XOR distance to a target.
    /// </summary>
    private sealed class XorDistanceComparer : IComparer<byte[]>
    {
        private readonly byte[] _target;

        public XorDistanceComparer(PeerId target)
        {
            _target = target.AsHash256().ToArray();
        }

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            for (int i = 0; i < 32; i++)
            {
                byte dx = (byte)(x[i] ^ _target[i]);
                byte dy = (byte)(y[i] ^ _target[i]);
                if (dx != dy) return dx.CompareTo(dy);
            }

            return 0;
        }
    }
}
