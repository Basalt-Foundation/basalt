using System.Collections.Concurrent;
using Basalt.Core;
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
    private readonly ILogger<GossipService> _logger;

    // Track recently seen messages to avoid rebroadcast
    private readonly ConcurrentDictionary<Hash256, long> _seenMessages = new();
    private const int MaxSeenMessages = 100_000;
    private const long SeenMessageTtlMs = 60_000; // 1 minute

    public event Action<PeerId, NetworkMessage>? OnMessageReceived;
    public event Action<PeerId, byte[]>? OnSendMessage;

    public GossipService(PeerManager peerManager, ILogger<GossipService> logger)
    {
        _peerManager = peerManager;
        _logger = logger;
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
            BlockNumber = number,
            BlockHash = hash,
            ParentHash = parentHash,
        };

        BroadcastToAll(message, excludePeer);
    }

    /// <summary>
    /// Broadcast a consensus message to all validators.
    /// </summary>
    public void BroadcastConsensusMessage(NetworkMessage message)
    {
        BroadcastToAll(message, null);
    }

    /// <summary>
    /// Handle an incoming message from a peer.
    /// </summary>
    public void HandleMessage(PeerId sender, NetworkMessage message)
    {
        _peerManager.GetPeer(sender)?.AdjustReputation(1); // Small reward for valid messages
        OnMessageReceived?.Invoke(sender, message);
    }

    /// <summary>
    /// Send a message to a specific peer.
    /// </summary>
    public void SendToPeer(PeerId peerId, NetworkMessage message)
    {
        // Serialize and send (Phase 1: in-process event)
        OnSendMessage?.Invoke(peerId, SerializeMessage(message));
    }

    private void BroadcastToAll(NetworkMessage message, PeerId? excludePeer)
    {
        var peers = _peerManager.ConnectedPeers;
        var data = SerializeMessage(message);

        foreach (var peer in peers)
        {
            if (excludePeer.HasValue && peer.Id == excludePeer.Value)
                continue;

            OnSendMessage?.Invoke(peer.Id, data);
        }
    }

    private bool IsMessageSeen(Hash256 msgId) => _seenMessages.ContainsKey(msgId);

    private void MarkMessageSeen(Hash256 msgId)
    {
        _seenMessages.TryAdd(msgId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        // Periodic cleanup
        if (_seenMessages.Count > MaxSeenMessages)
            CleanupSeenMessages();
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
