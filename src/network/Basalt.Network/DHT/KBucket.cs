namespace Basalt.Network.DHT;

/// <summary>
/// A k-bucket in the Kademlia routing table.
/// Stores up to K peers with the same distance prefix from the local node.
/// Implements LRU eviction: least-recently-seen peers are evicted first.
/// </summary>
public sealed class KBucket
{
    public const int K = 20;
    private readonly LinkedList<PeerInfo> _entries = new();
    private readonly object _lock = new();

    /// <summary>
    /// Number of entries in this bucket.
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>
    /// Get all peers in this bucket (most recently seen first).
    /// </summary>
    public List<PeerInfo> GetPeers()
    {
        lock (_lock)
            return [.. _entries];
    }

    /// <summary>
    /// Insert or update a peer in the bucket.
    /// If the peer exists, move it to the front (most recently seen).
    /// If the bucket is full, the least-recently-seen peer is evicted.
    /// </summary>
    /// <returns>True if the peer was added/updated, false if rejected.</returns>
    public bool InsertOrUpdate(PeerInfo peer)
    {
        lock (_lock)
        {
            // Check if peer already exists
            var node = _entries.First;
            while (node != null)
            {
                if (node.Value.Id == peer.Id)
                {
                    // Move to front (most recently seen)
                    _entries.Remove(node);
                    _entries.AddFirst(peer);
                    return true;
                }
                node = node.Next;
            }

            // New peer
            if (_entries.Count < K)
            {
                _entries.AddFirst(peer);
                return true;
            }

            // Bucket full — evict least-recently-seen (tail) if it has low reputation
            var tail = _entries.Last;
            if (tail != null && tail.Value.ReputationScore < peer.ReputationScore)
            {
                _entries.RemoveLast();
                _entries.AddFirst(peer);
                return true;
            }

            return false; // Bucket full, existing peers have better reputation
        }
    }

    /// <summary>
    /// Remove a peer from the bucket.
    /// </summary>
    public bool Remove(PeerId id)
    {
        lock (_lock)
        {
            var node = _entries.First;
            while (node != null)
            {
                if (node.Value.Id == id)
                {
                    _entries.Remove(node);
                    return true;
                }
                node = node.Next;
            }
            return false;
        }
    }

    /// <summary>
    /// Check if a peer exists in this bucket.
    /// </summary>
    public bool Contains(PeerId id)
    {
        lock (_lock)
        {
            return _entries.Any(p => p.Id == id);
        }
    }
}
