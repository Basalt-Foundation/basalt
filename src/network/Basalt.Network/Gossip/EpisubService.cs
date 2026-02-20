using System.Collections.Concurrent;
using Basalt.Core;
using Microsoft.Extensions.Logging;

namespace Basalt.Network.Gossip;

/// <summary>
/// Two-tier Episub gossip protocol.
/// Priority tier: Eager push for high-priority messages (consensus, priority txs) - target &lt;200ms.
/// Standard tier: Lazy IHAVE/IWANT for standard txs and blocks - target &lt;600ms.
/// </summary>
public sealed class EpisubService
{
    private readonly PeerManager _peerManager;
    private readonly ILogger<EpisubService> _logger;

    // Eager peers: receive full messages immediately
    private readonly ConcurrentDictionary<PeerId, byte> _eagerPeers = new();

    // Lazy peers: receive IHAVE announcements, request via IWANT
    private readonly ConcurrentDictionary<PeerId, byte> _lazyPeers = new();

    // Message deduplication cache
    private readonly ConcurrentDictionary<Hash256, long> _seenMessages = new();
    private const int MaxSeenMessages = 200_000;
    private const long SeenMessageTtlMs = 120_000; // 2 minutes

    // IHAVE tracking: messages we've announced but not yet requested
    private readonly ConcurrentDictionary<Hash256, List<PeerId>> _ihaveSources = new();

    // Message content cache: stores serialized messages for IWANT responses
    private readonly ConcurrentDictionary<Hash256, byte[]> _messageCache = new();
    private const int MaxCachedMessages = 50_000;

    // Target counts
    private const int TargetEagerPeers = 6;
    private const int TargetLazyPeers = 12;

    // NET-H09: IWANT rate limit and size bound constants
    private const int MaxIWantIds = 100;
    private const int MaxIWantRequestsPerSecond = 10;
    private readonly ConcurrentDictionary<PeerId, long> _lastIWantTimestamps = new();

    // NET-M16: IHAVE/IWANT correlation — track which IHAVE message IDs we sent to each peer
    private readonly ConcurrentDictionary<PeerId, HashSet<Hash256>> _sentIHavePerPeer = new();
    private const int MaxSentIHavePerPeer = 1000;

    // NET-M17: Hard cap on eager peers to prevent Sybil-driven eager tier saturation
    private const int MaxEagerPeers = TargetEagerPeers * 2; // 12

    // NET-M18: Maximum IHAVE sources tracked per message ID
    private const int MaxIHaveSources = 3;

    public event Action<PeerId, byte[]>? OnSendMessage;
    public event Action<PeerId, NetworkMessage>? OnMessageReceived;

    public EpisubService(PeerManager peerManager, ILogger<EpisubService> logger)
    {
        _peerManager = peerManager;
        _logger = logger;
    }

    /// <summary>
    /// Number of eager peers.
    /// </summary>
    public int EagerPeerCount => _eagerPeers.Count;

    /// <summary>
    /// Number of lazy peers.
    /// </summary>
    public int LazyPeerCount => _lazyPeers.Count;

    /// <summary>
    /// Number of cached messages available for IWANT responses.
    /// </summary>
    public int CachedMessageCount => _messageCache.Count;

    /// <summary>
    /// Initialize peer tiers when a new peer connects.
    /// </summary>
    public void OnPeerConnected(PeerId peerId)
    {
        if (_eagerPeers.Count < TargetEagerPeers)
            _eagerPeers.TryAdd(peerId, 0);
        else
            _lazyPeers.TryAdd(peerId, 0);
    }

    /// <summary>
    /// Remove a peer from all tiers.
    /// </summary>
    public void OnPeerDisconnected(PeerId peerId)
    {
        _eagerPeers.TryRemove(peerId, out _);
        _lazyPeers.TryRemove(peerId, out _);
    }

    /// <summary>
    /// Broadcast a priority message (consensus, urgent) via eager push to all eager peers.
    /// </summary>
    public void BroadcastPriority(Hash256 messageId, NetworkMessage message, PeerId? excludePeer = null)
    {
        if (IsMessageSeen(messageId))
            return;
        MarkMessageSeen(messageId);

        // Cache the serialized message for IWANT responses
        CacheMessage(messageId, SerializeMessage(message));

        // Eager push to all eager peers
        foreach (var (peerId, _) in _eagerPeers)
        {
            if (excludePeer.HasValue && peerId == excludePeer.Value)
                continue;
            SendFullMessage(peerId, message);
        }

        // IHAVE to lazy peers
        foreach (var (peerId, _) in _lazyPeers)
        {
            if (excludePeer.HasValue && peerId == excludePeer.Value)
                continue;
            SendIHave(peerId, messageId);

            // NET-M16: Track IHAVE sent to this peer for IWANT correlation
            RecordSentIHave(peerId, messageId);
        }
    }

    /// <summary>
    /// Broadcast a standard message (regular txs, blocks) via lazy IHAVE/IWANT.
    /// </summary>
    public void BroadcastStandard(Hash256 messageId, NetworkMessage message, PeerId? excludePeer = null)
    {
        if (IsMessageSeen(messageId))
            return;
        MarkMessageSeen(messageId);

        // Cache the serialized message for IWANT responses
        CacheMessage(messageId, SerializeMessage(message));

        // IHAVE to all peers (lazy protocol)
        foreach (var peer in _peerManager.ConnectedPeers)
        {
            if (excludePeer.HasValue && peer.Id == excludePeer.Value)
                continue;
            SendIHave(peer.Id, messageId);

            // NET-M16: Track IHAVE sent to this peer for IWANT correlation
            RecordSentIHave(peer.Id, messageId);
        }
    }

    /// <summary>
    /// Handle an incoming IHAVE message from a peer.
    /// </summary>
    public void HandleIHave(PeerId sender, Hash256 messageId)
    {
        if (IsMessageSeen(messageId))
            return;

        // Track this source
        var sources = _ihaveSources.GetOrAdd(messageId, _ => new List<PeerId>());
        bool isFirstSource;
        lock (sources)
        {
            // NET-M18: Cap the number of IHAVE sources per message ID.
            // Additional sources beyond the cap are ignored; the list serves as
            // fallback sources if the first IWANT times out (for future retry logic).
            if (sources.Count >= MaxIHaveSources)
                return;

            isFirstSource = sources.Count == 0;
            sources.Add(sender);
        }

        // NET-M18: Only send IWANT to the first source that announced the message.
        // Subsequent sources are tracked as fallbacks but do not trigger additional requests.
        if (isFirstSource)
            SendIWant(sender, messageId);
    }

    /// <summary>
    /// Handle an incoming full message from an eager peer.
    /// </summary>
    public void HandleFullMessage(PeerId sender, Hash256 messageId, NetworkMessage message)
    {
        if (IsMessageSeen(messageId))
            return;
        MarkMessageSeen(messageId);

        // Process the message
        OnMessageReceived?.Invoke(sender, message);

        // If sender is a lazy peer, promote to eager (they sent full message without us asking)
        // NET-M17: Only promote if we haven't reached the eager peer hard cap.
        // This prevents a Sybil cluster from forcing all their nodes into the eager tier.
        if (_lazyPeers.TryRemove(sender, out _))
        {
            if (_eagerPeers.Count < MaxEagerPeers)
            {
                _eagerPeers.TryAdd(sender, 0);
                _logger.LogDebug("Promoted {PeerId} from lazy to eager tier", sender);
            }
            else
            {
                // Re-add to lazy tier — eager tier is full
                _lazyPeers.TryAdd(sender, 0);
                _logger.LogDebug("Eager tier full ({MaxEagerPeers} cap), keeping {PeerId} in lazy tier", MaxEagerPeers, sender);
            }
        }
    }

    /// <summary>
    /// Graft a peer from lazy to eager tier.
    /// </summary>
    public void GraftPeer(PeerId peerId)
    {
        if (_lazyPeers.TryRemove(peerId, out _))
        {
            _eagerPeers.TryAdd(peerId, 0);
            _logger.LogDebug("Grafted {PeerId} to eager tier", peerId);
        }
    }

    /// <summary>
    /// Prune a peer from eager to lazy tier.
    /// </summary>
    public void PrunePeer(PeerId peerId)
    {
        if (_eagerPeers.TryRemove(peerId, out _))
        {
            _lazyPeers.TryAdd(peerId, 0);
            _logger.LogDebug("Pruned {PeerId} to lazy tier", peerId);
        }
    }

    /// <summary>
    /// Rebalance peer tiers to maintain target counts.
    /// </summary>
    public void RebalanceTiers()
    {
        // Promote lazy peers to eager if we're under target
        while (_eagerPeers.Count < TargetEagerPeers && _lazyPeers.Count > 0)
        {
            var bestLazy = _lazyPeers.Keys
                .Select(id => _peerManager.GetPeer(id))
                .Where(p => p != null)
                .OrderByDescending(p => p!.ReputationScore)
                .FirstOrDefault();

            if (bestLazy != null)
                GraftPeer(bestLazy.Id);
            else
                break;
        }

        // Prune eager peers to lazy if we're over target
        while (_eagerPeers.Count > TargetEagerPeers * 2)
        {
            var worstEager = _eagerPeers.Keys
                .Select(id => _peerManager.GetPeer(id))
                .Where(p => p != null)
                .OrderBy(p => p!.ReputationScore)
                .FirstOrDefault();

            if (worstEager != null)
                PrunePeer(worstEager.Id);
            else
                break;
        }
    }

    /// <summary>
    /// Store a message in the cache for IWANT responses.
    /// </summary>
    public void CacheMessage(Hash256 messageId, byte[] serializedMessage)
    {
        _messageCache.TryAdd(messageId, serializedMessage);
        if (_messageCache.Count > MaxCachedMessages)
            CleanupMessageCache();
    }

    /// <summary>
    /// Try to retrieve a cached message by its ID.
    /// </summary>
    public bool TryGetCachedMessage(Hash256 messageId, out byte[]? data)
    {
        return _messageCache.TryGetValue(messageId, out data);
    }

    /// <summary>
    /// Handle incoming IWANT requests. Returns cached messages for requested IDs.
    /// </summary>
    public IEnumerable<(Hash256 Id, byte[] Data)> HandleIWant(PeerId sender, Hash256[] messageIds)
    {
        // NET-H09: Per-peer rate limiting — max 10 IWANT requests per second per peer.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var lastTimestamp = _lastIWantTimestamps.GetOrAdd(sender, 0L);
        var elapsed = now - lastTimestamp;
        if (elapsed < 1000 / MaxIWantRequestsPerSecond) // 100ms minimum interval
        {
            _logger.LogWarning("IWANT rate limit exceeded for peer {PeerId}, ignoring request", sender);
            return [];
        }
        _lastIWantTimestamps[sender] = now;

        // NET-H09: Truncate oversized IWANT requests to prevent amplification DoS.
        var count = Math.Min(messageIds.Length, MaxIWantIds);

        // NET-M16: Only respond with cached data if we previously sent an IHAVE for that
        // message ID to this specific peer. Prevents cache probing by unauthorized peers.
        _sentIHavePerPeer.TryGetValue(sender, out var sentSet);

        var results = new List<(Hash256 Id, byte[] Data)>();
        for (var i = 0; i < count; i++)
        {
            var id = messageIds[i];

            // NET-M16: Skip message IDs we never announced to this peer
            if (sentSet == null)
                continue;
            bool wasSent;
            lock (sentSet)
                wasSent = sentSet.Contains(id);
            if (!wasSent)
                continue;

            if (_messageCache.TryGetValue(id, out var data))
                results.Add((id, data));
        }

        return results;
    }

    /// <summary>
    /// Clean up expired seen messages.
    /// </summary>
    public void CleanupSeenMessages()
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - SeenMessageTtlMs;
        foreach (var (key, timestamp) in _seenMessages)
        {
            if (timestamp < cutoff)
                _seenMessages.TryRemove(key, out _);
        }

        // Clean IHAVE sources
        foreach (var (key, _) in _ihaveSources)
        {
            if (!_seenMessages.ContainsKey(key))
                _ihaveSources.TryRemove(key, out _);
        }

        // Clean message cache
        foreach (var (key, _) in _messageCache)
        {
            if (!_seenMessages.ContainsKey(key))
                _messageCache.TryRemove(key, out _);
        }

        // NET-M16: Clean up IHAVE correlation tracking.
        // Remove entries for peers no longer in either tier, and cap per-peer sets.
        foreach (var (peerId, set) in _sentIHavePerPeer)
        {
            if (!_eagerPeers.ContainsKey(peerId) && !_lazyPeers.ContainsKey(peerId))
            {
                _sentIHavePerPeer.TryRemove(peerId, out _);
                continue;
            }

            // Cap per-peer set at MaxSentIHavePerPeer by clearing if exceeded
            lock (set)
            {
                if (set.Count > MaxSentIHavePerPeer)
                    set.Clear();
            }
        }

        // NET-H09: Clean up IWANT rate limit timestamps for disconnected peers
        foreach (var (peerId, _) in _lastIWantTimestamps)
        {
            if (!_eagerPeers.ContainsKey(peerId) && !_lazyPeers.ContainsKey(peerId))
                _lastIWantTimestamps.TryRemove(peerId, out _);
        }
    }

    private void CleanupMessageCache()
    {
        // Remove cached messages whose seen-message entry has expired
        foreach (var (key, _) in _messageCache)
        {
            if (!_seenMessages.ContainsKey(key))
                _messageCache.TryRemove(key, out _);
        }
    }

    private bool IsMessageSeen(Hash256 msgId) => _seenMessages.ContainsKey(msgId);

    private void MarkMessageSeen(Hash256 msgId)
    {
        _seenMessages.TryAdd(msgId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (_seenMessages.Count > MaxSeenMessages)
            CleanupSeenMessages();
    }

    private void SendFullMessage(PeerId peerId, NetworkMessage message)
    {
        OnSendMessage?.Invoke(peerId, SerializeMessage(message));
    }

    private void SendIHave(PeerId peerId, Hash256 messageId)
    {
        var ihave = new IHaveMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), MessageIds = [messageId] };
        OnSendMessage?.Invoke(peerId, SerializeMessage(ihave));
    }

    private void SendIWant(PeerId peerId, Hash256 messageId)
    {
        var iwant = new IWantMessage { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), MessageIds = [messageId] };
        OnSendMessage?.Invoke(peerId, SerializeMessage(iwant));
    }

    /// <summary>
    /// NET-M16: Record that we sent an IHAVE for a specific message ID to a specific peer.
    /// </summary>
    private void RecordSentIHave(PeerId peerId, Hash256 messageId)
    {
        var set = _sentIHavePerPeer.GetOrAdd(peerId, _ => new HashSet<Hash256>());
        lock (set)
        {
            if (set.Count < MaxSentIHavePerPeer)
                set.Add(messageId);
        }
    }

    private static byte[] SerializeMessage(NetworkMessage message)
    {
        return MessageCodec.Serialize(message);
    }
}
