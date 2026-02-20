namespace Basalt.Network.Transport;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Basalt.Crypto;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manages inbound and outbound TCP connections to peers.
/// Provides message sending, broadcasting, and connection lifecycle events.
/// NET-H01: Per-IP and total connection limits.
/// </summary>
public sealed class TcpTransport : IAsyncDisposable
{
    /// <summary>NET-H01: Maximum total connections.</summary>
    private const int MaxTotalConnections = 200;

    /// <summary>NET-H01: Maximum connections per IP address.</summary>
    private const int MaxConnectionsPerIp = 3;

    /// <summary>NET-I02: Default connect timeout.</summary>
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger<TcpTransport> _logger;
    private readonly ConcurrentDictionary<PeerId, PeerConnection> _connections = new();

    /// <summary>NET-H01: Track connection count per IP for DoS protection.</summary>
    private readonly ConcurrentDictionary<string, int> _connectionsPerIp = new();

    /// <summary>NET-H01: Map PeerId → remote IP so we can decrement on disconnect.</summary>
    private readonly ConcurrentDictionary<PeerId, string> _peerIpMap = new();

    /// <summary>NET-M04: Track read loop tasks so exceptions are observed and loops can be awaited on shutdown.</summary>
    private readonly ConcurrentDictionary<PeerId, Task> _readLoopTasks = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoopTask;

    public TcpTransport(ILogger<TcpTransport> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// NET-L02: Fired when a complete message is received from any connected peer.
    /// Converted from public field to event to prevent accidental overwrite.
    /// </summary>
    public event Action<PeerId, byte[]>? OnMessageReceived;

    /// <summary>
    /// NET-L02: Fired when a new peer connection has been accepted or established.
    /// Converted from public field to event to prevent accidental overwrite.
    /// </summary>
    public event Action<PeerConnection>? OnPeerConnected;

    /// <summary>
    /// NET-L02: Fired when a peer disconnects (detected via read-loop termination).
    /// Converted from public field to event to prevent accidental overwrite.
    /// </summary>
    public event Action<PeerId>? OnPeerDisconnected;

    /// <summary>
    /// Returns the set of all currently connected peer IDs.
    /// </summary>
    public IReadOnlyCollection<PeerId> ConnectedPeerIds => _connections.Keys.ToList();

    /// <summary>
    /// Starts listening for inbound TCP connections on the specified port and begins
    /// the accept loop in the background.
    /// NET-I01: Accepts an optional bind address (defaults to IPAddress.Any).
    /// </summary>
    public Task StartAsync(int port, CancellationToken cancellationToken, IPAddress? bindAddress = null)
    {
        if (_listener is not null)
            throw new InvalidOperationException("Transport is already started.");

        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // NET-I01: Use provided bind address or default to 0.0.0.0
        _listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
        _listener.Start();

        _logger.LogInformation("TCP transport listening on port {Port}", port);

        _acceptLoopTask = AcceptLoopAsync(_listenerCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates an outbound TCP connection to the specified host and port.
    /// A temporary <see cref="PeerId"/> is derived from the remote endpoint until
    /// a handshake provides the real identity.
    /// NET-I02: Accepts an optional timeout (defaults to 10 seconds).
    /// </summary>
    public async Task<PeerConnection> ConnectAsync(string host, int port, TimeSpan? timeout = null)
    {
        var client = new TcpClient();

        try
        {
            // NET-I02: Apply connect timeout to avoid blocking for OS TCP timeout (75s-2min)
            using var cts = new CancellationTokenSource(timeout ?? DefaultConnectTimeout);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? $"{host}:{port}";
        var tempId = CreateTempPeerId(endpoint);

        var connection = new PeerConnection(client, tempId, HandleMessageReceived);

        if (!_connections.TryAdd(tempId, connection))
        {
            connection.Dispose();
            throw new InvalidOperationException(
                $"A connection with temporary peer ID {tempId} already exists.");
        }

        _logger.LogInformation("Outbound connection established to {Endpoint} as {PeerId}", endpoint, tempId);

        // Note: For outbound connections, the caller is responsible for handshake
        // and starting the read loop. OnPeerConnected is NOT fired here.

        return connection;
    }

    /// <summary>
    /// Sends data to a specific connected peer.
    /// </summary>
    public async Task SendAsync(PeerId peer, byte[] data)
    {
        if (!_connections.TryGetValue(peer, out var connection))
        {
            _logger.LogWarning("Attempted to send to unknown peer {PeerId}", peer);
            return;
        }

        if (!connection.IsConnected)
        {
            _logger.LogWarning("Attempted to send to disconnected peer {PeerId}", peer);
            RemoveConnection(peer);
            return;
        }

        try
        {
            await connection.SendAsync(data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send to peer {PeerId}", peer);
            RemoveConnection(peer);
        }
    }

    /// <summary>
    /// Broadcasts data to all connected peers, optionally excluding one.
    /// NET-M07: Snapshots connections before iteration to avoid concurrent modification.
    /// </summary>
    public async Task BroadcastAsync(byte[] data, PeerId? exclude = null)
    {
        // NET-M07: Snapshot before iteration to prevent concurrent modification issues
        var snapshot = _connections.ToArray();

        foreach (var (peerId, connection) in snapshot)
        {
            if (exclude.HasValue && peerId == exclude.Value)
                continue;

            if (!connection.IsConnected)
            {
                RemoveConnection(peerId);
                continue;
            }

            try
            {
                await connection.SendAsync(data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast to peer {PeerId}", peerId);
                RemoveConnection(peerId);
            }
        }
    }

    /// <summary>
    /// Closes and removes the connection for the specified peer.
    /// </summary>
    public void DisconnectPeer(PeerId peer)
    {
        RemoveConnection(peer);
    }

    /// <summary>
    /// Replaces a temporary peer ID (assigned at accept/connect time) with the real
    /// peer ID obtained after a successful handshake.
    /// NET-M01: Set PeerId on connection before TryAdd to avoid race window.
    /// </summary>
    public bool UpdatePeerId(PeerId tempId, PeerId realId)
    {
        if (!_connections.TryRemove(tempId, out var connection))
        {
            _logger.LogWarning(
                "Cannot update peer ID: no connection found for temporary ID {TempId}", tempId);
            return false;
        }

        // NET-M01: Set PeerId before TryAdd so the read loop uses the correct identity
        connection.PeerId = realId;

        // Transfer IP mapping from temp ID to real ID
        if (_peerIpMap.TryRemove(tempId, out var ip))
            _peerIpMap[realId] = ip;

        if (!_connections.TryAdd(realId, connection))
        {
            // Duplicate connection — we already have a connection to this peer
            // (likely from simultaneous inbound+outbound). Dispose the duplicate silently.
            _logger.LogDebug(
                "Duplicate connection to {RealId} detected; keeping existing connection", realId);
            // Decrement IP count since we're dropping this connection
            if (ip != null)
                _connectionsPerIp.AddOrUpdate(ip, 0, (_, count) => Math.Max(0, count - 1));
            _peerIpMap.TryRemove(realId, out _);
            connection.Dispose();
            return false;
        }

        _logger.LogInformation("Peer ID updated from {TempId} to {RealId}", tempId, realId);
        return true;
    }

    /// <summary>
    /// Stops the listener and closes all peer connections.
    /// NET-M04: Awaits tracked read loop tasks before shutdown.
    /// NET-M07: Snapshots connections before disposal to avoid concurrent modification.
    /// </summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("TCP transport stopping");

        if (_listenerCts is not null)
        {
            await _listenerCts.CancelAsync().ConfigureAwait(false);
        }

        _listener?.Stop();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        // NET-M04: Await all tracked read loop tasks so exceptions are observed
        var readLoopSnapshot = _readLoopTasks.Values.ToArray();
        try
        {
            await Task.WhenAll(readLoopSnapshot).ConfigureAwait(false);
        }
        catch
        {
            // Tasks may already be completed, cancelled, or faulted — safe to ignore
        }
        _readLoopTasks.Clear();

        // NET-M07: Snapshot and clear to avoid concurrent modification during iteration
        var connectionSnapshot = _connections.ToArray();
        _connections.Clear();
        foreach (var (_, connection) in connectionSnapshot)
        {
            connection.Dispose();
        }

        _listenerCts?.Dispose();
        _listenerCts = null;
        _listener = null;
        _acceptLoopTask = null;

        _logger.LogInformation("TCP transport stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Start the read loop for a connection (call after handshake completes).
    /// NET-M04: Stores the task in _readLoopTasks so exceptions are observed
    /// and the task can be awaited during shutdown.
    /// </summary>
    public void StartReadLoop(PeerConnection connection)
    {
        // NET-M04: Track the read loop task instead of fire-and-forget
        var task = RunReadLoopAsync(connection);
        _readLoopTasks[connection.PeerId] = task;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Private helpers
    // ────────────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "Error accepting TCP connection");
                continue;
            }

            // NET-H01: Total connection limit
            if (_connections.Count >= MaxTotalConnections)
            {
                _logger.LogWarning("Total connection limit ({Limit}) reached; rejecting inbound", MaxTotalConnections);
                client.Dispose();
                continue;
            }

            // NET-H01: Per-IP connection limit
            var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
            var currentIpCount = _connectionsPerIp.GetOrAdd(remoteIp, 0);
            if (currentIpCount >= MaxConnectionsPerIp)
            {
                _logger.LogWarning("Per-IP connection limit ({Limit}) reached for {Ip}; rejecting",
                    MaxConnectionsPerIp, remoteIp);
                client.Dispose();
                continue;
            }

            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            var tempId = CreateTempPeerId(endpoint);

            var connection = new PeerConnection(client, tempId, HandleMessageReceived);

            if (!_connections.TryAdd(tempId, connection))
            {
                _logger.LogWarning(
                    "Duplicate temporary peer ID {PeerId} from {Endpoint}; rejecting", tempId, endpoint);
                connection.Dispose();
                continue;
            }

            // NET-H01: Increment per-IP counter and track mapping for decrement on disconnect
            _connectionsPerIp.AddOrUpdate(remoteIp, 1, (_, count) => count + 1);
            _peerIpMap[tempId] = remoteIp;

            _logger.LogInformation("Inbound connection accepted from {Endpoint} as {PeerId}", endpoint, tempId);

            // Notify the handler. The handler is responsible for handshake
            // and starting the read loop via StartReadLoop().
            OnPeerConnected?.Invoke(connection);
        }
    }

    private async Task RunReadLoopAsync(PeerConnection connection)
    {
        try
        {
            using var cts = _listenerCts is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(_listenerCts.Token)
                : new CancellationTokenSource();

            await connection.StartReadLoopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Read loop ended for peer {PeerId}", connection.PeerId);
        }
        finally
        {
            RemoveConnection(connection.PeerId);
        }
    }

    private void HandleMessageReceived(PeerId peerId, byte[] data)
    {
        OnMessageReceived?.Invoke(peerId, data);
    }

    private void RemoveConnection(PeerId peerId)
    {
        if (_connections.TryRemove(peerId, out var connection))
        {
            // NET-M04: Remove tracked read loop task
            _readLoopTasks.TryRemove(peerId, out _);

            // NET-H01: Decrement per-IP counter so the IP isn't permanently blocked
            if (_peerIpMap.TryRemove(peerId, out var ip))
                _connectionsPerIp.AddOrUpdate(ip, 0, (_, count) => Math.Max(0, count - 1));

            connection.Dispose();
            _logger.LogInformation("Peer {PeerId} disconnected and removed", peerId);
            OnPeerDisconnected?.Invoke(peerId);
        }
    }

    private static PeerId CreateTempPeerId(string endpoint)
    {
        return new PeerId(Blake3Hasher.Hash(Encoding.UTF8.GetBytes(endpoint)));
    }
}
