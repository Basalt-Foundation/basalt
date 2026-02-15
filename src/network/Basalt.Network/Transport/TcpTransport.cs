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
/// </summary>
public sealed class TcpTransport : IAsyncDisposable
{
    private readonly ILogger<TcpTransport> _logger;
    private readonly ConcurrentDictionary<PeerId, PeerConnection> _connections = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoopTask;

    public TcpTransport(ILogger<TcpTransport> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Fired when a complete message is received from any connected peer.
    /// </summary>
    public Action<PeerId, byte[]>? OnMessageReceived;

    /// <summary>
    /// Fired when a new peer connection has been accepted or established.
    /// </summary>
    public Action<PeerConnection>? OnPeerConnected;

    /// <summary>
    /// Fired when a peer disconnects (detected via read-loop termination).
    /// </summary>
    public Action<PeerId>? OnPeerDisconnected;

    /// <summary>
    /// Returns the set of all currently connected peer IDs.
    /// </summary>
    public IReadOnlyCollection<PeerId> ConnectedPeerIds => _connections.Keys.ToList();

    /// <summary>
    /// Starts listening for inbound TCP connections on the specified port and begins
    /// the accept loop in the background.
    /// </summary>
    public Task StartAsync(int port, CancellationToken cancellationToken)
    {
        if (_listener is not null)
            throw new InvalidOperationException("Transport is already started.");

        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        _logger.LogInformation("TCP transport listening on port {Port}", port);

        _acceptLoopTask = AcceptLoopAsync(_listenerCts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates an outbound TCP connection to the specified host and port.
    /// A temporary <see cref="PeerId"/> is derived from the remote endpoint until
    /// a handshake provides the real identity.
    /// </summary>
    public async Task<PeerConnection> ConnectAsync(string host, int port)
    {
        var client = new TcpClient();

        try
        {
            await client.ConnectAsync(host, port).ConfigureAwait(false);
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
    /// </summary>
    public async Task BroadcastAsync(byte[] data, PeerId? exclude = null)
    {
        foreach (var (peerId, connection) in _connections)
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
    /// </summary>
    public bool UpdatePeerId(PeerId tempId, PeerId realId)
    {
        if (!_connections.TryRemove(tempId, out var connection))
        {
            _logger.LogWarning(
                "Cannot update peer ID: no connection found for temporary ID {TempId}", tempId);
            return false;
        }

        if (!_connections.TryAdd(realId, connection))
        {
            // Duplicate connection — we already have a connection to this peer
            // (likely from simultaneous inbound+outbound). Dispose the duplicate silently.
            _logger.LogDebug(
                "Duplicate connection to {RealId} detected; keeping existing connection", realId);
            connection.Dispose();
            return false;
        }

        connection.PeerId = realId;
        _logger.LogInformation("Peer ID updated from {TempId} to {RealId}", tempId, realId);
        return true;
    }

    /// <summary>
    /// Stops the listener and closes all peer connections.
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

        // Dispose all connections.
        foreach (var (peerId, connection) in _connections)
        {
            connection.Dispose();
            _connections.TryRemove(peerId, out _);
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
    /// </summary>
    public void StartReadLoop(PeerConnection connection)
    {
        _ = RunReadLoopAsync(connection);
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
