using Microsoft.Extensions.Logging;

namespace Basalt.Network;

/// <summary>
/// Peer reputation scoring system.
/// Tracks peer behavior and adjusts scores based on:
/// - Availability (uptime, responsiveness)
/// - Latency (response times)
/// - Block validity (valid vs invalid blocks relayed)
/// - Protocol compliance (following gossip rules)
/// </summary>
public sealed class ReputationScorer
{
    private readonly PeerManager _peerManager;
    private readonly ILogger<ReputationScorer> _logger;

    /// <summary>
    /// Score thresholds.
    /// </summary>
    public const int MaxScore = 200;
    public const int DefaultScore = 100;
    public const int BanThreshold = 10;
    public const int LowRepThreshold = 30;

    /// <summary>
    /// Score deltas for various events.
    /// </summary>
    public static class Deltas
    {
        public const int ValidBlock = 5;
        public const int InvalidBlock = -50;
        public const int ValidTransaction = 1;
        public const int InvalidTransaction = -10;
        public const int ValidConsensusVote = 3;
        public const int InvalidConsensusVote = -30;
        public const int TimelyResponse = 2;
        public const int SlowResponse = -1;
        public const int Timeout = -5;
        public const int ProtocolViolation = -20;
        public const int DuplicateMessage = -1;
        public const int SuccessfulHandshake = 10;
        public const int FailedHandshake = -15;
        public const int HeartbeatSuccess = 1;
        public const int HeartbeatFailure = -3;
    }

    public ReputationScorer(PeerManager peerManager, ILogger<ReputationScorer> logger)
    {
        _peerManager = peerManager;
        _logger = logger;
    }

    /// <summary>
    /// Record a valid block from a peer.
    /// </summary>
    public void RecordValidBlock(PeerId peerId) => AdjustScore(peerId, Deltas.ValidBlock, "valid block");

    /// <summary>
    /// Record an invalid block from a peer.
    /// </summary>
    public void RecordInvalidBlock(PeerId peerId) => AdjustScore(peerId, Deltas.InvalidBlock, "invalid block");

    /// <summary>
    /// Record a valid transaction from a peer.
    /// </summary>
    public void RecordValidTransaction(PeerId peerId) => AdjustScore(peerId, Deltas.ValidTransaction, "valid tx");

    /// <summary>
    /// Record an invalid transaction from a peer.
    /// </summary>
    public void RecordInvalidTransaction(PeerId peerId) => AdjustScore(peerId, Deltas.InvalidTransaction, "invalid tx");

    /// <summary>
    /// Record a valid consensus vote from a peer.
    /// </summary>
    public void RecordValidConsensusVote(PeerId peerId) => AdjustScore(peerId, Deltas.ValidConsensusVote, "valid vote");

    /// <summary>
    /// Record an invalid consensus vote from a peer.
    /// </summary>
    public void RecordInvalidConsensusVote(PeerId peerId) => AdjustScore(peerId, Deltas.InvalidConsensusVote, "invalid vote");

    /// <summary>
    /// Record a timely response to a request.
    /// </summary>
    public void RecordTimelyResponse(PeerId peerId) => AdjustScore(peerId, Deltas.TimelyResponse, "timely response");

    /// <summary>
    /// Record a timeout (no response).
    /// </summary>
    public void RecordTimeout(PeerId peerId) => AdjustScore(peerId, Deltas.Timeout, "timeout");

    /// <summary>
    /// Record a protocol violation (malformed messages, unexpected behavior).
    /// </summary>
    public void RecordProtocolViolation(PeerId peerId) => AdjustScore(peerId, Deltas.ProtocolViolation, "protocol violation");

    /// <summary>
    /// Check if a peer's reputation is too low and should be disconnected.
    /// </summary>
    public bool ShouldDisconnect(PeerId peerId)
    {
        var peer = _peerManager.GetPeer(peerId);
        return peer != null && peer.ReputationScore <= BanThreshold;
    }

    /// <summary>
    /// Check if a peer has low reputation (should be avoided for critical operations).
    /// </summary>
    public bool IsLowReputation(PeerId peerId)
    {
        var peer = _peerManager.GetPeer(peerId);
        return peer != null && peer.ReputationScore <= LowRepThreshold;
    }

    /// <summary>
    /// Apply time-based decay to all peer scores (moves toward default).
    /// Call periodically (e.g., every 30 seconds).
    /// </summary>
    public void DecayScores()
    {
        foreach (var peer in _peerManager.AllPeers)
        {
            if (peer.State == PeerState.Banned)
                continue;

            if (peer.ReputationScore > DefaultScore)
                peer.AdjustReputation(-1); // Slowly decay above-average scores
            else if (peer.ReputationScore < DefaultScore && peer.ReputationScore > BanThreshold)
                peer.AdjustReputation(1); // Slowly recover below-average scores
        }
    }

    /// <summary>
    /// Get sorted list of peers by reputation (best first).
    /// </summary>
    public List<PeerInfo> GetPeersByReputation()
    {
        return _peerManager.ConnectedPeers
            .OrderByDescending(p => p.ReputationScore)
            .ToList();
    }

    private void AdjustScore(PeerId peerId, int delta, string reason)
    {
        var peer = _peerManager.GetPeer(peerId);
        if (peer == null) return;

        var oldScore = peer.ReputationScore;
        peer.AdjustReputation(delta);

        if (peer.ReputationScore <= BanThreshold && oldScore > BanThreshold)
        {
            _logger.LogWarning("Peer {PeerId} reputation dropped to {Score} ({Reason}), banning",
                peerId, peer.ReputationScore, reason);
            _peerManager.BanPeer(peerId, $"Reputation too low: {reason}");
        }
        else if (delta < -5)
        {
            _logger.LogDebug("Peer {PeerId} reputation: {Old} -> {New} ({Reason})",
                peerId, oldScore, peer.ReputationScore, reason);
        }
    }
}
