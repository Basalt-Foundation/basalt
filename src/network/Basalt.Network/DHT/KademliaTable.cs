using Basalt.Core;

namespace Basalt.Network.DHT;

/// <summary>
/// Kademlia routing table with 256 k-buckets.
/// Each bucket stores up to K (20) peers at a specific XOR distance from the local node.
/// NET-M24: Thread-safe via ReaderWriterLockSlim for concurrent read access.
/// NET-C05: Outbound-protected slots prevent eclipse attacks from displacing critical peers.
/// NET-H10: IP diversity check limits peers per /24 subnet per bucket.
/// </summary>
public sealed class KademliaTable : IDisposable
{
    private readonly PeerId _localId;
    private readonly KBucket[] _buckets;
    private const int BucketCount = 256;
    private const int Alpha = 3; // Concurrent lookups

    // NET-H10: Maximum peers per /24 subnet per bucket
    private const int MaxPeersPerSubnetPerBucket = 2;

    // NET-M24: Reader-writer lock for thread-safe access
    private readonly ReaderWriterLockSlim _rwLock = new();

    // NET-C05: Outbound-protected peers cannot be removed via Remove()
    private const int MaxOutboundProtected = 4;
    private readonly HashSet<PeerId> _outboundProtected = new();
    private readonly object _outboundLock = new();

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
    /// NET-H10: Rejects peers that would exceed the /24 subnet limit per bucket.
    /// PeerId verification is handled at handshake time (HandshakeProtocol.cs, NET-C01).
    /// M-7: IP diversity check is performed inside the write lock to eliminate TOCTOU race.
    /// </summary>
    public bool AddOrUpdate(PeerInfo peer)
    {
        if (peer.Id == _localId)
            return false; // Don't add ourselves

        int bucket = GetBucketIndex(peer.Id);

        _rwLock.EnterWriteLock();
        try
        {
            // NET-H10: IP diversity check — max 2 peers per /24 subnet per bucket
            // M-7: Moved inside write lock to prevent concurrent AddOrUpdate from both
            // passing the check before either inserts, exceeding the subnet limit.
            string peerSubnet = GetSubnet24(peer.Host);
            var existingPeers = _buckets[bucket].GetPeers();

            // Only check subnet limit if this is a new peer (not an update)
            bool isExisting = existingPeers.Any(p => p.Id == peer.Id);
            if (!isExisting)
            {
                int subnetCount = existingPeers.Count(p => GetSubnet24(p.Host) == peerSubnet);
                if (subnetCount >= MaxPeersPerSubnetPerBucket)
                    return false; // NET-H10: Too many peers from same /24 subnet
            }

            return _buckets[bucket].InsertOrUpdate(peer);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Remove a peer from the routing table.
    /// NET-C05: Outbound-protected peers cannot be removed unless explicitly unprotected first.
    /// </summary>
    public bool Remove(PeerId id)
    {
        // NET-C05: Prevent removal of outbound-protected peers
        if (IsOutboundProtected(id))
            return false;

        _rwLock.EnterWriteLock();
        try
        {
            int bucket = GetBucketIndex(id);
            return _buckets[bucket].Remove(id);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Find the K closest peers to a target ID.
    /// NET-M24: Acquires read lock for atomic snapshot across buckets.
    /// </summary>
    public List<PeerInfo> FindClosest(PeerId target, int count = KBucket.K)
    {
        _rwLock.EnterReadLock();
        try
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
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Get all peers in the routing table.
    /// NET-M24: Acquires read lock for consistent snapshot.
    /// </summary>
    public List<PeerInfo> GetAllPeers()
    {
        _rwLock.EnterReadLock();
        try
        {
            var result = new List<PeerInfo>();
            foreach (var bucket in _buckets)
                result.AddRange(bucket.GetPeers());
            return result;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Total number of peers in the routing table.
    /// NET-M24: Acquires read lock for consistent count.
    /// </summary>
    public int PeerCount
    {
        get
        {
            _rwLock.EnterReadLock();
            try
            {
                return _buckets.Sum(b => b.Count);
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }
    }

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

    /// <summary>
    /// NET-C05: Mark a peer as outbound-protected. Protected peers cannot be removed
    /// via Remove() unless explicitly unprotected. Max 4 protected slots.
    /// </summary>
    public bool MarkOutboundProtected(PeerId id)
    {
        lock (_outboundLock)
        {
            if (_outboundProtected.Count >= MaxOutboundProtected && !_outboundProtected.Contains(id))
                return false;

            return _outboundProtected.Add(id);
        }
    }

    /// <summary>
    /// NET-C05: Check if a peer is outbound-protected.
    /// </summary>
    public bool IsOutboundProtected(PeerId id)
    {
        lock (_outboundLock)
        {
            return _outboundProtected.Contains(id);
        }
    }

    /// <summary>
    /// NET-C05: Remove outbound-protection from a peer, allowing normal removal.
    /// </summary>
    public bool UnmarkOutboundProtected(PeerId id)
    {
        lock (_outboundLock)
        {
            return _outboundProtected.Remove(id);
        }
    }

    /// <summary>
    /// NET-M24: Dispose the reader-writer lock.
    /// </summary>
    public void Dispose()
    {
        _rwLock.Dispose();
    }

    /// <summary>
    /// NET-H10: Extract the subnet prefix from an IP address.
    /// For IPv4 addresses, returns the /24 prefix (first 3 octets, e.g., "192.168.1").
    /// For IPv6 addresses, returns the /48 prefix (first 3 hextets, e.g., "2001:db8:1").
    /// L-7: IPv6 addresses now extract /48 instead of returning the full address,
    /// which would treat every IPv6 address as a unique "subnet".
    /// </summary>
    internal static string GetSubnet24(string host)
    {
        if (string.IsNullOrEmpty(host))
            return host;

        // Try to parse as IPv4: must have exactly 4 dot-separated octets
        var parts = host.Split('.');
        if (parts.Length == 4)
        {
            bool allNumeric = true;
            for (int i = 0; i < 4; i++)
            {
                if (!byte.TryParse(parts[i], out _))
                {
                    allNumeric = false;
                    break;
                }
            }

            if (allNumeric)
                return string.Concat(parts[0], ".", parts[1], ".", parts[2]);
        }

        // L-7: IPv6 /48 prefix — extract first 3 colon-separated groups.
        // This covers both full and abbreviated IPv6 notation.
        if (host.Contains(':'))
        {
            var colonParts = host.Split(':');
            if (colonParts.Length >= 3)
                return string.Concat(colonParts[0], ":", colonParts[1], ":", colonParts[2]);
        }

        // Hostname — use full string as subnet key
        return host;
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

            // NET-L07: Break tie by comparing raw bytes to prevent SortedList.TryAdd
            // from silently dropping equal-distance peers with different IDs.
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
            {
                if (x[i] != y[i])
                    return x[i].CompareTo(y[i]);
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
