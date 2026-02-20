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
    /// NET-C04: Rejects banned peers.
    /// NET-M19: Uses ConnectedCount instead of total peers count.
    /// </summary>
    public bool RegisterPeer(PeerInfo peer)
    {
        // NET-C04: Check if the peer is currently banned
        if (_peers.TryGetValue(peer.Id, out var existing))
        {
            if (existing.State == PeerState.Banned)
            {
                // NET-H13: Check if ban has expired
                if (existing.BannedUntil.HasValue && existing.BannedUntil.Value > DateTimeOffset.UtcNow)
                {
                    _logger.LogWarning("Peer {PeerId} is banned until {BannedUntil}; rejecting",
                        peer.Id, existing.BannedUntil.Value);
                    return false;
                }

                // Ban has expired — allow reconnection
                _logger.LogInformation("Peer {PeerId} ban expired, allowing reconnection", peer.Id);
            }
        }

        // NET-M19: Check connected count, not total count (includes disconnected/banned)
        if (ConnectedCount >= _maxPeers)
        {
            _logger.LogWarning("Max connected peer limit ({MaxPeers}) reached, rejecting {PeerId}", _maxPeers, peer.Id);
            return false;
        }

        peer.State = PeerState.Connected;
        peer.ConnectedAt = DateTimeOffset.UtcNow;
        peer.LastSeen = DateTimeOffset.UtcNow;
        peer.BannedUntil = null; // Clear any prior ban

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
    /// NET-L10: Callback invoked when a peer is disconnected, for Episub cleanup.
    /// </summary>
    public event Action<PeerId>? OnPeerDisconnected;

    /// <summary>
    /// Mark a peer as disconnected.
    /// NET-L10: Invokes OnPeerDisconnected so Episub can remove the peer from eager/lazy sets.
    /// </summary>
    public void DisconnectPeer(PeerId id, string reason)
    {
        if (_peers.TryGetValue(id, out var peer))
        {
            peer.State = PeerState.Disconnected;
            _logger.LogInformation("Peer {PeerId} disconnected: {Reason}", id, reason);

            // NET-L10: Notify listeners (e.g. EpisubService) to clean up peer sets
            OnPeerDisconnected?.Invoke(id);
        }
    }

    /// <summary>
    /// NET-C04/NET-H13: Callback invoked when a peer is banned, to trigger transport-level disconnect.
    /// NET-L02: Converted from public field to event to prevent accidental overwrite.
    /// </summary>
    public event Action<PeerId>? OnPeerBanned;

    /// <summary>
    /// Ban a peer for misbehavior.
    /// NET-C04: Triggers disconnect callback to close the TCP connection.
    /// NET-H13: Sets BannedUntil timestamp (default 1 hour).
    /// </summary>
    public void BanPeer(PeerId id, string reason, TimeSpan? duration = null)
    {
        if (_peers.TryGetValue(id, out var peer))
        {
            peer.State = PeerState.Banned;
            peer.ReputationScore = 0;
            peer.BannedUntil = DateTimeOffset.UtcNow + (duration ?? TimeSpan.FromHours(1));
            _logger.LogWarning("Peer {PeerId} banned until {BannedUntil}: {Reason}",
                id, peer.BannedUntil, reason);

            // NET-C04: Trigger transport disconnect
            OnPeerBanned?.Invoke(id);
        }
    }

    /// <summary>
    /// Update a peer's best block info.
    /// NET-L08: Uses thread-safe UpdateBestBlock for atomic number+hash update.
    /// </summary>
    public void UpdatePeerBestBlock(PeerId id, ulong blockNumber, Hash256 blockHash)
    {
        if (_peers.TryGetValue(id, out var peer))
        {
            // NET-L08: Atomic update of BestBlockNumber + BestBlockHash
            peer.UpdateBestBlock(blockNumber, blockHash);
            peer.LastSeen = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Remove inactive peers.
    /// NET-L09: Also prunes banned peers whose ban has expired.
    /// </summary>
    public int PruneInactivePeers(TimeSpan timeout)
    {
        var cutoff = DateTimeOffset.UtcNow - timeout;
        var now = DateTimeOffset.UtcNow;
        int removed = 0;

        foreach (var (id, peer) in _peers)
        {
            if (peer.State == PeerState.Disconnected && peer.LastSeen < cutoff)
            {
                if (_peers.TryRemove(id, out _))
                    removed++;
            }

            // NET-L09: Also prune banned peers after ban expiry to prevent unbounded accumulation
            if (peer.State == PeerState.Banned &&
                peer.BannedUntil.HasValue &&
                peer.BannedUntil.Value <= now)
            {
                if (_peers.TryRemove(id, out _))
                    removed++;
            }
        }

        if (removed > 0)
            _logger.LogInformation("Pruned {Count} inactive/expired-ban peers", removed);

        return removed;
    }
}
