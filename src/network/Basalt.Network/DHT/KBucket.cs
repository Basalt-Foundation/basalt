namespace Basalt.Network.DHT;

/// <summary>
/// A k-bucket in the Kademlia routing table.
/// Stores up to K peers with the same distance prefix from the local node.
/// NET-H11: Standard Kademlia eviction — prefer long-lived nodes, never evict
/// responsive peers for newcomers. New peers are rejected when the bucket is full.
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
    /// NET-H11: If the bucket is full and the peer is new, reject the newcomer.
    /// Standard Kademlia never evicts responsive peers for newcomers to prevent Sybil attacks.
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

            // NET-H11: Bucket full — reject the newcomer. Standard Kademlia prefers
            // long-lived nodes to prevent Sybil attacks. Peers are only removed via
            // explicit Remove() when they become unresponsive.
            return false;
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
