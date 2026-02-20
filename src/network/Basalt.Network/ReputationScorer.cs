using System.Collections.Concurrent;
using System.Diagnostics;
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

    // NET-M22: Instant ban penalty for severe protocol violations
    public const int InstantBanPenalty = -100;

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

    // NET-M20: Diminishing returns — per-peer reward window tracking
    private const int RewardWindowSeconds = 60;
    private const int MaxTxRewardsPerWindow = 10;
    private const int MaxBlockRewardsPerWindow = 5;

    private readonly ConcurrentDictionary<PeerId, (int Count, long WindowStart)> _txRewardWindows = new();
    private readonly ConcurrentDictionary<PeerId, (int Count, long WindowStart)> _blockRewardWindows = new();

    // NET-M21: Active recovery only — tracks positive interactions since last decay
    private readonly ConcurrentDictionary<PeerId, bool> _hasPositiveInteraction = new();

    // NET-M22: Minor penalty accumulator to prevent false positive bans
    private readonly ConcurrentDictionary<PeerId, int> _minorPenaltyAccumulator = new();

    public ReputationScorer(PeerManager peerManager, ILogger<ReputationScorer> logger)
    {
        _peerManager = peerManager;
        _logger = logger;
    }

    /// <summary>
    /// Record a valid block from a peer.
    /// </summary>
    public void RecordValidBlock(PeerId peerId)
    {
        // NET-M21: Track positive interaction for active recovery
        _hasPositiveInteraction[peerId] = true;

        // NET-M20: Diminishing returns — cap block rewards per window
        if (IsRewardCapped(peerId, _blockRewardWindows, MaxBlockRewardsPerWindow))
            return;

        AdjustScore(peerId, Deltas.ValidBlock, "valid block");
    }

    /// <summary>
    /// Record an invalid block from a peer.
    /// </summary>
    public void RecordInvalidBlock(PeerId peerId) => AdjustScore(peerId, Deltas.InvalidBlock, "invalid block");

    /// <summary>
    /// Record a valid transaction from a peer.
    /// </summary>
    public void RecordValidTransaction(PeerId peerId)
    {
        // NET-M21: Track positive interaction for active recovery
        _hasPositiveInteraction[peerId] = true;

        // NET-M20: Diminishing returns — cap transaction rewards per window
        if (IsRewardCapped(peerId, _txRewardWindows, MaxTxRewardsPerWindow))
            return;

        AdjustScore(peerId, Deltas.ValidTransaction, "valid tx");
    }

    /// <summary>
    /// Record an invalid transaction from a peer.
    /// </summary>
    public void RecordInvalidTransaction(PeerId peerId) => AdjustScore(peerId, Deltas.InvalidTransaction, "invalid tx");

    /// <summary>
    /// Record a valid consensus vote from a peer.
    /// </summary>
    public void RecordValidConsensusVote(PeerId peerId)
    {
        // NET-M21: Track positive interaction for active recovery
        _hasPositiveInteraction[peerId] = true;
        AdjustScore(peerId, Deltas.ValidConsensusVote, "valid vote");
    }

    /// <summary>
    /// Record an invalid consensus vote from a peer.
    /// </summary>
    public void RecordInvalidConsensusVote(PeerId peerId) => AdjustScore(peerId, Deltas.InvalidConsensusVote, "invalid vote");

    /// <summary>
    /// Record a timely response to a request.
    /// </summary>
    public void RecordTimelyResponse(PeerId peerId)
    {
        // NET-M21: Track positive interaction for active recovery
        _hasPositiveInteraction[peerId] = true;
        AdjustScore(peerId, Deltas.TimelyResponse, "timely response");
    }

    /// <summary>
    /// Record a timeout (no response).
    /// </summary>
    public void RecordTimeout(PeerId peerId) => AdjustScore(peerId, Deltas.Timeout, "timeout");

    /// <summary>
    /// Record a protocol violation (malformed messages, unexpected behavior).
    /// NET-M22: Severe violations (penalty <= -20) trigger instant ban by setting score to 0.
    /// ProtocolViolation delta is -20, which qualifies as severe.
    /// </summary>
    public void RecordProtocolViolation(PeerId peerId)
    {
        // NET-M22: Severe protocol violation — instant ban by setting score to 0
        var peer = _peerManager.GetPeer(peerId);
        if (peer == null) return;

        var oldScore = peer.ReputationScore;
        peer.ReputationScore = 0;

        _logger.LogWarning(
            "Peer {PeerId} instant-banned for severe protocol violation (score {Old} -> 0)",
            peerId, oldScore);
        _peerManager.BanPeer(peerId, "Reputation too low: severe protocol violation");
    }

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
    /// NET-M21: Positive recovery only applies to peers with recent positive interactions.
    /// </summary>
    public void DecayScores()
    {
        foreach (var peer in _peerManager.AllPeers)
        {
            if (peer.State == PeerState.Banned)
                continue;

            if (peer.ReputationScore > DefaultScore)
            {
                // Negative decay (above default) applies unconditionally
                peer.AdjustReputation(-1);
            }
            else if (peer.ReputationScore < DefaultScore && peer.ReputationScore > BanThreshold)
            {
                // NET-M21: Positive recovery only if peer had a positive interaction since last decay
                if (_hasPositiveInteraction.TryGetValue(peer.Id, out var hadPositive) && hadPositive)
                {
                    peer.AdjustReputation(1);
                }
            }
        }

        // NET-M21: Reset positive interaction tracking for next decay cycle
        _hasPositiveInteraction.Clear();
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

    /// <summary>
    /// NET-M20: Check if a peer has exceeded their reward cap in the current window.
    /// Returns true if the reward should be skipped (cap exceeded).
    /// Increments the counter and resets the window if expired.
    /// </summary>
    private bool IsRewardCapped(
        PeerId peerId,
        ConcurrentDictionary<PeerId, (int Count, long WindowStart)> windows,
        int maxPerWindow)
    {
        long now = Stopwatch.GetTimestamp();
        long windowTicks = RewardWindowSeconds * Stopwatch.Frequency;

        var current = windows.GetOrAdd(peerId, _ => (0, now));

        // Reset window if expired
        if (now - current.WindowStart >= windowTicks)
        {
            current = (1, now);
            windows[peerId] = current;
            return false;
        }

        // Check cap
        if (current.Count >= maxPerWindow)
            return true;

        // Increment
        windows[peerId] = (current.Count + 1, current.WindowStart);
        return false;
    }

    /// <summary>
    /// NET-M22: Determine if a penalty delta is a minor penalty.
    /// Minor penalties are small penalties (magnitude <= 5): SlowResponse, Timeout, HeartbeatFailure.
    /// </summary>
    private static bool IsMinorPenalty(int delta)
    {
        return delta < 0 && delta >= -5;
    }

    private void AdjustScore(PeerId peerId, int delta, string reason)
    {
        var peer = _peerManager.GetPeer(peerId);
        if (peer == null) return;

        var oldScore = peer.ReputationScore;

        // NET-M22: Cap minor penalties so they cannot push score below LowRepThreshold
        if (IsMinorPenalty(delta))
        {
            int accumulated = _minorPenaltyAccumulator.GetOrAdd(peerId, 0);
            int newAccumulated = accumulated + Math.Abs(delta);
            _minorPenaltyAccumulator[peerId] = newAccumulated;

            // Check if applying this penalty would bring score below LowRepThreshold
            int projectedScore = oldScore + delta;
            if (projectedScore < LowRepThreshold)
            {
                // Cap the penalty: only reduce to LowRepThreshold at most
                int cappedDelta = LowRepThreshold - oldScore;
                if (cappedDelta >= 0)
                {
                    // Score is already at or below LowRepThreshold, skip minor penalty entirely
                    return;
                }
                delta = cappedDelta;
            }
        }

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
