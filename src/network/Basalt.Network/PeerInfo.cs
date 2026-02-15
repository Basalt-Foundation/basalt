using Basalt.Core;

namespace Basalt.Network;

/// <summary>
/// Information about a connected peer.
/// </summary>
public sealed class PeerInfo
{
    public required PeerId Id { get; init; }
    public required PublicKey PublicKey { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public ulong BestBlockNumber { get; set; }
    public Hash256 BestBlockHash { get; set; }
    public PeerState State { get; set; } = PeerState.Disconnected;
    public int ReputationScore { get; set; } = 100;
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public int FailedAttempts { get; set; }

    /// <summary>
    /// Endpoint string (host:port).
    /// </summary>
    public string Endpoint => $"{Host}:{Port}";

    /// <summary>
    /// Adjust reputation score.
    /// </summary>
    public void AdjustReputation(int delta)
    {
        ReputationScore = Math.Clamp(ReputationScore + delta, 0, 200);
    }
}

/// <summary>
/// Connection state of a peer.
/// </summary>
public enum PeerState
{
    Disconnected,
    Connecting,
    Handshaking,
    Connected,
    Banned,
}
