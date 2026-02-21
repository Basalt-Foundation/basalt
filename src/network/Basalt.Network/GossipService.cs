using System.Collections.Concurrent;
using Basalt.Core;
using Basalt.Crypto;
using Microsoft.Extensions.Logging;

namespace Basalt.Network;

/// <summary>
/// Gossip protocol for propagating transactions and blocks.
/// Phase 1: Simple eager-push gossip.
/// Phase 2: Two-tier Episub with priority and standard channels.
/// </summary>
public sealed class GossipService
{
    private readonly PeerManager _peerManager;
    private readonly ReputationScorer? _reputationScorer;
    private readonly ILogger<GossipService> _logger;

    // NET-M14: Maximum number of peers to fan out to per broadcast
    private const int MaxFanOut = 8;

    // Track recently seen messages to avoid rebroadcast
    private readonly ConcurrentDictionary<Hash256, long> _seenMessages = new();
    private const int MaxSeenMessages = 100_000;
    private const long SeenMessageTtlMs = 60_000; // 1 minute

    // NET-L06: Guard to prevent concurrent cleanup runs
    private int _cleanupRunning;

    public event Action<PeerId, NetworkMessage>? OnMessageReceived;
    public event Action<PeerId, byte[]>? OnSendMessage;

    public GossipService(PeerManager peerManager, ILogger<GossipService> logger)
        : this(peerManager, logger, null)
    {
    }

    public GossipService(PeerManager peerManager, ILogger<GossipService> logger, ReputationScorer? reputationScorer)
    {
        _peerManager = peerManager;
        _logger = logger;
        _reputationScorer = reputationScorer;
    }

    /// <summary>
    /// Broadcast a transaction announcement to all connected peers.
    /// </summary>
    public void BroadcastTransaction(Hash256 txHash, PeerId? excludePeer = null)
    {
        var msgId = txHash;
        if (IsMessageSeen(msgId))
            return;
        MarkMessageSeen(msgId);

        var message = new TxAnnounceMessage
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TransactionHashes = [txHash],
        };

        BroadcastToAll(message, excludePeer);
    }

    /// <summary>
    /// Broadcast a block announcement to all connected peers.
    /// </summary>
    public void BroadcastBlock(ulong number, Hash256 hash, Hash256 parentHash, PeerId? excludePeer = null)
    {
        if (IsMessageSeen(hash))
            return;
        MarkMessageSeen(hash);

        var message = new BlockAnnounceMessage
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            BlockNumber = number,
            BlockHash = hash,
            ParentHash = parentHash,
        };

        BroadcastToAll(message, excludePeer);
    }

    /// <summary>
    /// Broadcast a consensus message to all validators.
    /// NET-L05: Deduplicates consensus messages using BLAKE3 hash of serialized data.
    /// </summary>
    public void BroadcastConsensusMessage(NetworkMessage message)
    {
        // NET-L05: Compute message ID from serialized data for deduplication
        var serialized = SerializeMessage(message);
        var msgId = Blake3Hasher.Hash(serialized);

        if (IsMessageSeen(msgId))
            return;
        MarkMessageSeen(msgId);

        BroadcastToAll(message, null);
    }

    /// <summary>
    /// Handle an incoming message from a peer.
    /// NET-H08: Does NOT relay/broadcast the message. Only marks as seen and invokes
    /// OnMessageReceived for the higher-level handler (NodeCoordinator) which is
    /// responsible for re-broadcasting after validation.
    /// NET-M15: Does NOT award reputation. Caller must invoke RewardPeerForValidMessage
    /// after validating the message content.
    /// <para>
    /// <b>M-1: SenderId verification.</b> The <paramref name="sender"/> parameter is the
    /// authenticated peer identity from the TCP connection (post-handshake PeerId). The
    /// <c>message.SenderId</c> field is self-reported by the peer in the wire format header.
    /// Callers should treat <paramref name="sender"/> as authoritative for routing and
    /// reputation decisions, not <c>message.SenderId</c>.
    /// </para>
    /// </summary>
    public void HandleMessage(PeerId sender, NetworkMessage message)
    {
        // M-4: Compute message ID and deduplicate to prevent processing the same message twice.
        // This is especially important for relayed gossip where multiple peers may forward
        // the same transaction/block announcement.
        var serialized = SerializeMessage(message);
        var msgId = Blake3Hasher.Hash(serialized);
        if (IsMessageSeen(msgId))
            return;
        MarkMessageSeen(msgId);

        // NET-H08: Only invoke callback — no relay. The higher-level handler
        // (NodeCoordinator) is responsible for re-broadcasting after validation.
        // NET-M15: No reputation reward here — caller calls RewardPeerForValidMessage after validation.
        OnMessageReceived?.Invoke(sender, message);
    }

    /// <summary>
    /// NET-M15: Reward a peer for sending a valid, verified message.
    /// Called by the higher-level handler (NodeCoordinator) after message validation succeeds.
    /// </summary>
    public void RewardPeerForValidMessage(PeerId peer)
    {
        if (_reputationScorer != null)
        {
            _reputationScorer.RecordValidTransaction(peer);
        }
        else
        {
            // Fallback: adjust reputation directly via PeerManager
            _peerManager.GetPeer(peer)?.AdjustReputation(1);
        }
    }

    /// <summary>
    /// Send a message to a specific peer.
    /// </summary>
    public void SendToPeer(PeerId peerId, NetworkMessage message)
    {
        // Serialize and send (Phase 1: in-process event)
        OnSendMessage?.Invoke(peerId, SerializeMessage(message));
    }

    /// <summary>
    /// NET-M14: Broadcast to connected peers with fan-out limit.
    /// If more than MaxFanOut peers are available, randomly selects MaxFanOut peers.
    /// </summary>
    private void BroadcastToAll(NetworkMessage message, PeerId? excludePeer)
    {
        var allPeers = _peerManager.ConnectedPeers;
        var data = SerializeMessage(message);

        // Filter out the excluded peer
        var eligible = new List<PeerInfo>();
        foreach (var peer in allPeers)
        {
            if (excludePeer.HasValue && peer.Id == excludePeer.Value)
                continue;
            eligible.Add(peer);
        }

        // NET-M14: Apply fan-out limit
        IList<PeerInfo> targets;
        if (eligible.Count > MaxFanOut)
        {
            // Randomly select MaxFanOut peers using Fisher-Yates partial shuffle
            for (int i = eligible.Count - 1; i > 0 && i >= eligible.Count - MaxFanOut; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (eligible[i], eligible[j]) = (eligible[j], eligible[i]);
            }
            targets = eligible.GetRange(eligible.Count - MaxFanOut, MaxFanOut);
        }
        else
        {
            targets = eligible;
        }

        foreach (var peer in targets)
        {
            OnSendMessage?.Invoke(peer.Id, data);
        }
    }

    private bool IsMessageSeen(Hash256 msgId) => _seenMessages.ContainsKey(msgId);

    private void MarkMessageSeen(Hash256 msgId)
    {
        _seenMessages.TryAdd(msgId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // NET-L06: Periodic cleanup with concurrency guard
        if (_seenMessages.Count > MaxSeenMessages)
        {
            if (Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) == 0)
            {
                try
                {
                    CleanupSeenMessages();
                }
                finally
                {
                    Interlocked.Exchange(ref _cleanupRunning, 0);
                }
            }
        }
    }

    private void CleanupSeenMessages()
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - SeenMessageTtlMs;
        foreach (var (key, timestamp) in _seenMessages)
        {
            if (timestamp < cutoff)
                _seenMessages.TryRemove(key, out _);
        }
    }

    private static byte[] SerializeMessage(NetworkMessage message)
    {
        return MessageCodec.Serialize(message);
    }
}
