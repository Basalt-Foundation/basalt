using System.Threading;
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

    /// <summary>
    /// NET-L08: Lock for atomic BestBlockNumber + BestBlockHash updates.
    /// </summary>
    private readonly object _bestBlockLock = new();

    /// <summary>
    /// NET-L08: Atomically update BestBlockNumber and BestBlockHash to prevent
    /// readers from seeing inconsistent (number, hash) pairs across threads.
    /// </summary>
    public void UpdateBestBlock(ulong number, Hash256 hash)
    {
        lock (_bestBlockLock)
        {
            BestBlockNumber = number;
            BestBlockHash = hash;
        }
    }

    /// <summary>
    /// NET-L08: Atomically read BestBlockNumber and BestBlockHash as a consistent pair.
    /// </summary>
    public (ulong Number, Hash256 Hash) GetBestBlock()
    {
        lock (_bestBlockLock)
        {
            return (BestBlockNumber, BestBlockHash);
        }
    }

    // NET-M23: Atomic reputation updates — backing field with Interlocked access
    private int _reputationScore = 100;

    /// <summary>
    /// Current reputation score. Thread-safe via Volatile.Read.
    /// </summary>
    public int ReputationScore
    {
        get => Volatile.Read(ref _reputationScore);
        set => Volatile.Write(ref _reputationScore, value);
    }

    // L-6: LastSeen and ConnectedAt use Interlocked on ticks for thread-safe access.
    // DateTimeOffset is 12 bytes (not guaranteed atomic on all platforms), so we store
    // as long ticks and convert on read/write.
    private long _lastSeenTicks;
    private long _connectedAtTicks;

    public DateTimeOffset LastSeen
    {
        get => new(Volatile.Read(ref _lastSeenTicks), TimeSpan.Zero);
        set => Volatile.Write(ref _lastSeenTicks, value.UtcTicks);
    }

    public DateTimeOffset ConnectedAt
    {
        get => new(Volatile.Read(ref _connectedAtTicks), TimeSpan.Zero);
        set => Volatile.Write(ref _connectedAtTicks, value.UtcTicks);
    }
    public int FailedAttempts { get; set; }

    /// <summary>
    /// NET-H13: Time until which this peer is banned. Null if not banned.
    /// </summary>
    public DateTimeOffset? BannedUntil { get; set; }

    /// <summary>
    /// Endpoint string (host:port).
    /// </summary>
    public string Endpoint => $"{Host}:{Port}";

    /// <summary>
    /// NET-M23: Adjust reputation score atomically using Interlocked.CompareExchange loop.
    /// </summary>
    public void AdjustReputation(int delta)
    {
        while (true)
        {
            int current = Volatile.Read(ref _reputationScore);
            int desired = Math.Clamp(current + delta, 0, 200);
            if (Interlocked.CompareExchange(ref _reputationScore, desired, current) == current)
                break;
        }
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
