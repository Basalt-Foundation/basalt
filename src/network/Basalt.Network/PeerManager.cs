using System.Collections.Concurrent;
using Basalt.Core;
using Microsoft.Extensions.Logging;

namespace Basalt.Network;

/// <summary>
/// Manages peer connections and peer state.
/// Phase 1: Static peer list, no DHT discovery.
/// </summary>
public sealed class PeerManager
{
    private readonly ConcurrentDictionary<PeerId, PeerInfo> _peers = new();
    private readonly ILogger<PeerManager> _logger;
    private readonly int _maxPeers;

    public PeerManager(ILogger<PeerManager> logger, int maxPeers = 50)
    {
        _logger = logger;
        _maxPeers = maxPeers;
    }

    /// <summary>
    /// Currently connected peers.
    /// </summary>
    public IReadOnlyCollection<PeerInfo> ConnectedPeers =>
        _peers.Values.Where(p => p.State == PeerState.Connected).ToList();

    /// <summary>
    /// All known peers.
    /// </summary>
    public IReadOnlyCollection<PeerInfo> AllPeers => _peers.Values.ToList();

    /// <summary>
    /// Number of connected peers.
    /// </summary>
    public int ConnectedCount => _peers.Values.Count(p => p.State == PeerState.Connected);

    /// <summary>
    /// Add a peer from a static configuration.
    /// </summary>
    public PeerInfo AddStaticPeer(PeerId id, PublicKey publicKey, string host, int port)
    {
        var peer = new PeerInfo
        {
            Id = id,
            PublicKey = publicKey,
            Host = host,
            Port = port,
        };

        _peers.TryAdd(id, peer);
        _logger.LogInformation("Added static peer {PeerId} at {Endpoint}", id, peer.Endpoint);
        return peer;
    }

    /// <summary>
    /// Register a peer after successful handshake.
    /// </summary>
    public bool RegisterPeer(PeerInfo peer)
    {
        if (_peers.Count >= _maxPeers)
        {
            _logger.LogWarning("Max peer limit ({MaxPeers}) reached, rejecting {PeerId}", _maxPeers, peer.Id);
            return false;
        }

        peer.State = PeerState.Connected;
        peer.ConnectedAt = DateTimeOffset.UtcNow;
        peer.LastSeen = DateTimeOffset.UtcNow;

        _peers.AddOrUpdate(peer.Id, peer, (_, _) => peer);
        _logger.LogInformation("Peer {PeerId} connected from {Endpoint}", peer.Id, peer.Endpoint);
        return true;
    }

    /// <summary>
    /// Get a peer by ID.
    /// </summary>
    public PeerInfo? GetPeer(PeerId id)
    {
        return _peers.TryGetValue(id, out var peer) ? peer : null;
    }

    /// <summary>
    /// Mark a peer as disconnected.
    /// </summary>
    public void DisconnectPeer(PeerId id, string reason)
    {
        if (_peers.TryGetValue(id, out var peer))
        {
            peer.State = PeerState.Disconnected;
            _logger.LogInformation("Peer {PeerId} disconnected: {Reason}", id, reason);
        }
    }

    /// <summary>
    /// Ban a peer for misbehavior.
    /// </summary>
    public void BanPeer(PeerId id, string reason)
    {
        if (_peers.TryGetValue(id, out var peer))
        {
            peer.State = PeerState.Banned;
            peer.ReputationScore = 0;
            _logger.LogWarning("Peer {PeerId} banned: {Reason}", id, reason);
        }
    }

    /// <summary>
    /// Update a peer's best block info.
    /// </summary>
    public void UpdatePeerBestBlock(PeerId id, ulong blockNumber, Hash256 blockHash)
    {
        if (_peers.TryGetValue(id, out var peer))
        {
            peer.BestBlockNumber = blockNumber;
            peer.BestBlockHash = blockHash;
            peer.LastSeen = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Remove inactive peers.
    /// </summary>
    public int PruneInactivePeers(TimeSpan timeout)
    {
        var cutoff = DateTimeOffset.UtcNow - timeout;
        int removed = 0;

        foreach (var (id, peer) in _peers)
        {
            if (peer.State == PeerState.Disconnected && peer.LastSeen < cutoff)
            {
                if (_peers.TryRemove(id, out _))
                    removed++;
            }
        }

        if (removed > 0)
            _logger.LogInformation("Pruned {Count} inactive peers", removed);

        return removed;
    }
}
