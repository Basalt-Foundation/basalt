using Microsoft.Extensions.Logging;

namespace Basalt.Network.DHT;

/// <summary>
/// Kademlia iterative node lookup algorithm.
/// Queries alpha closest peers in parallel, converging on the target.
/// </summary>
public sealed class NodeLookup
{
    private readonly KademliaTable _table;
    private readonly ILogger _logger;

    /// <summary>
    /// Called to query a remote peer for their closest nodes to a target.
    /// Returns the peers that the remote peer knows about.
    /// </summary>
    public Func<PeerId, PeerId, List<PeerInfo>>? QueryPeer { get; set; }

    public NodeLookup(KademliaTable table, ILogger logger)
    {
        _table = table;
        _logger = logger;
    }

    /// <summary>
    /// Perform an iterative lookup for the K closest nodes to target.
    /// </summary>
    public List<PeerInfo> Lookup(PeerId target)
    {
        var closest = _table.FindClosest(target, KBucket.K);
        var queried = new HashSet<PeerId> { _table.LocalId };
        var candidates = new Dictionary<PeerId, PeerInfo>();

        foreach (var p in closest)
            candidates[p.Id] = p;

        bool improved = true;
        while (improved)
        {
            improved = false;

            // Pick alpha unqueried closest candidates
            var toQuery = candidates.Values
                .Where(p => !queried.Contains(p.Id))
                .OrderBy(p => XorDistance(p.Id, target))
                .Take(KademliaTable.ConcurrencyAlpha)
                .ToList();

            if (toQuery.Count == 0)
                break;

            foreach (var peer in toQuery)
            {
                queried.Add(peer.Id);

                if (QueryPeer == null)
                    continue;

                try
                {
                    var results = QueryPeer(peer.Id, target);
                    foreach (var result in results)
                    {
                        if (result.Id != _table.LocalId && !candidates.ContainsKey(result.Id))
                        {
                            candidates[result.Id] = result;
                            _table.AddOrUpdate(result);
                            improved = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to query peer {PeerId} during lookup", peer.Id);
                }
            }
        }

        return candidates.Values
            .OrderBy(p => XorDistance(p.Id, target))
            .Take(KBucket.K)
            .ToList();
    }

    private static int XorDistance(PeerId a, PeerId b)
    {
        var aBytes = a.AsHash256().ToArray();
        var bBytes = b.AsHash256().ToArray();

        // Return the position of the highest differing bit (lower = closer)
        for (int i = 0; i < 32; i++)
        {
            byte xor = (byte)(aBytes[i] ^ bBytes[i]);
            if (xor != 0)
                return (31 - i) * 8 + (7 - int.LeadingZeroCount(xor) + 24);
        }
        return 0;
    }
}
