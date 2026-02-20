using System.Net;
using System.Net.Sockets;
using Basalt.Core;
using Basalt.Network.Transport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Network.Tests;

public class TcpTransportTests : IAsyncLifetime
{
    private readonly List<TcpTransport> _transports = [];
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var transport in _transports)
        {
            await transport.DisposeAsync();
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────

    private TcpTransport CreateTransport()
    {
        var transport = new TcpTransport(NullLogger<TcpTransport>.Instance);
        _transports.Add(transport);
        return transport;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static PeerId MakePeerId(int seed)
    {
        var bytes = new byte[32];
        bytes[31] = (byte)seed;
        return new PeerId(new Hash256(bytes));
    }

    // ────────────────────────────────────────────────────────────────────
    //  1. Connection tests
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_EstablishesConnection()
    {
        // Arrange
        var server = CreateTransport();
        var client = CreateTransport();
        int port = GetFreePort();

        var peerConnectedTcs = new TaskCompletionSource<PeerConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnPeerConnected += connection =>
        {
            peerConnectedTcs.TrySetResult(connection);
        };

        using var cts = new CancellationTokenSource();
        await server.StartAsync(port, cts.Token);

        // Act
        var outboundConnection = await client.ConnectAsync("127.0.0.1", port);

        // Wait for the server to accept the inbound connection
        var inboundConnection = await peerConnectedTcs.Task.WaitAsync(_timeout);

        // Assert
        outboundConnection.Should().NotBeNull();
        outboundConnection.IsConnected.Should().BeTrue();

        inboundConnection.Should().NotBeNull();
        inboundConnection.IsConnected.Should().BeTrue();

        // The client transport should have one connection (the outbound one)
        client.ConnectedPeerIds.Should().HaveCount(1);

        // The server transport should have one connection (the inbound one)
        server.ConnectedPeerIds.Should().HaveCount(1);
    }

    [Fact]
    public async Task DisconnectPeer_RemovesConnection()
    {
        // Arrange
        var server = CreateTransport();
        var client = CreateTransport();
        int port = GetFreePort();

        var peerConnectedTcs = new TaskCompletionSource<PeerConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnPeerConnected += connection =>
        {
            peerConnectedTcs.TrySetResult(connection);
        };

        using var cts = new CancellationTokenSource();
        await server.StartAsync(port, cts.Token);

        var outboundConnection = await client.ConnectAsync("127.0.0.1", port);
        await peerConnectedTcs.Task.WaitAsync(_timeout);

        client.ConnectedPeerIds.Should().HaveCount(1);
        var outboundPeerId = outboundConnection.PeerId;

        // Act
        client.DisconnectPeer(outboundPeerId);

        // Assert
        client.ConnectedPeerIds.Should().BeEmpty();
    }

    // ────────────────────────────────────────────────────────────────────
    //  2. Framing tests
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SendAndReceive_RoundtripsData()
    {
        // Arrange
        var server = CreateTransport();
        var client = CreateTransport();
        int port = GetFreePort();

        var serverInboundTcs = new TaskCompletionSource<PeerConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnPeerConnected += connection =>
        {
            serverInboundTcs.TrySetResult(connection);
        };

        var messageReceivedTcs = new TaskCompletionSource<(PeerId, byte[])>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnMessageReceived += (peerId, data) =>
        {
            messageReceivedTcs.TrySetResult((peerId, data));
        };

        using var cts = new CancellationTokenSource();
        await server.StartAsync(port, cts.Token);

        var outboundConnection = await client.ConnectAsync("127.0.0.1", port);
        var inboundConnection = await serverInboundTcs.Task.WaitAsync(_timeout);

        // Start read loops on both sides
        server.StartReadLoop(inboundConnection);
        client.StartReadLoop(outboundConnection);

        // Act - send data from client to server
        var payload = "Hello, Basalt!"u8.ToArray();
        await outboundConnection.SendAsync(payload);

        // Assert
        var (receivedPeerId, receivedData) = await messageReceivedTcs.Task.WaitAsync(_timeout);
        receivedData.Should().BeEquivalentTo(payload);
        receivedPeerId.Should().Be(inboundConnection.PeerId);
    }

    [Fact]
    public async Task SendAndReceive_LargeMessage()
    {
        // Arrange
        var server = CreateTransport();
        var client = CreateTransport();
        int port = GetFreePort();

        var serverInboundTcs = new TaskCompletionSource<PeerConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnPeerConnected += connection =>
        {
            serverInboundTcs.TrySetResult(connection);
        };

        var messageReceivedTcs = new TaskCompletionSource<(PeerId, byte[])>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnMessageReceived += (peerId, data) =>
        {
            messageReceivedTcs.TrySetResult((peerId, data));
        };

        using var cts = new CancellationTokenSource();
        await server.StartAsync(port, cts.Token);

        var outboundConnection = await client.ConnectAsync("127.0.0.1", port);
        var inboundConnection = await serverInboundTcs.Task.WaitAsync(_timeout);

        // Start read loops on both sides
        server.StartReadLoop(inboundConnection);
        client.StartReadLoop(outboundConnection);

        // Act - send a 100KB payload
        var largePayload = new byte[100 * 1024];
        Random.Shared.NextBytes(largePayload);
        await outboundConnection.SendAsync(largePayload);

        // Assert
        var (_, receivedData) = await messageReceivedTcs.Task.WaitAsync(_timeout);
        receivedData.Should().HaveCount(largePayload.Length);
        receivedData.Should().BeEquivalentTo(largePayload);
    }

    // ────────────────────────────────────────────────────────────────────
    //  3. Broadcast tests
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BroadcastAsync_SendsToAllPeers()
    {
        // Arrange: one server, three clients all connected
        var server = CreateTransport();
        int port = GetFreePort();

        var inboundConnections = new List<PeerConnection>();
        var inboundSemaphore = new SemaphoreSlim(0);

        server.OnPeerConnected += connection =>
        {
            lock (inboundConnections)
            {
                inboundConnections.Add(connection);
            }
            inboundSemaphore.Release();
        };

        using var cts = new CancellationTokenSource();
        await server.StartAsync(port, cts.Token);

        // Connect 3 clients
        const int clientCount = 3;
        var clients = new TcpTransport[clientCount];
        var outboundConnections = new PeerConnection[clientCount];

        for (int i = 0; i < clientCount; i++)
        {
            clients[i] = CreateTransport();
            outboundConnections[i] = await clients[i].ConnectAsync("127.0.0.1", port);
        }

        // Wait for all 3 inbound connections on the server
        for (int i = 0; i < clientCount; i++)
        {
            var acquired = await inboundSemaphore.WaitAsync(_timeout);
            acquired.Should().BeTrue("server should accept all client connections");
        }

        // Set up message tracking on each client
        var receivedMessages = new int[clientCount];
        var allReceivedTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int totalReceived = 0;

        for (int i = 0; i < clientCount; i++)
        {
            int index = i;
            clients[i].OnMessageReceived += (_, _) =>
            {
                Interlocked.Increment(ref receivedMessages[index]);
                if (Interlocked.Increment(ref totalReceived) == clientCount)
                {
                    allReceivedTcs.TrySetResult();
                }
            };
        }

        // Start read loops for all connections
        lock (inboundConnections)
        {
            foreach (var conn in inboundConnections)
            {
                server.StartReadLoop(conn);
            }
        }

        for (int i = 0; i < clientCount; i++)
        {
            clients[i].StartReadLoop(outboundConnections[i]);
        }

        // Act - server broadcasts a message to all connected peers
        var broadcastPayload = "Broadcast to all!"u8.ToArray();
        await server.BroadcastAsync(broadcastPayload);

        // Assert - all 3 clients should receive the message
        await allReceivedTcs.Task.WaitAsync(_timeout);

        for (int i = 0; i < clientCount; i++)
        {
            receivedMessages[i].Should().Be(1,
                $"client {i} should have received exactly one broadcast message");
        }
    }

    // ────────────────────────────────────────────────────────────────────
    //  4. PeerId management
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdatePeerId_ReplacesTemporaryId()
    {
        // Arrange
        var server = CreateTransport();
        var client = CreateTransport();
        int port = GetFreePort();

        var serverInboundTcs = new TaskCompletionSource<PeerConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnPeerConnected += connection =>
        {
            serverInboundTcs.TrySetResult(connection);
        };

        var messageReceivedTcs = new TaskCompletionSource<(PeerId, byte[])>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        server.OnMessageReceived += (peerId, data) =>
        {
            messageReceivedTcs.TrySetResult((peerId, data));
        };

        using var cts = new CancellationTokenSource();
        await server.StartAsync(port, cts.Token);

        var outboundConnection = await client.ConnectAsync("127.0.0.1", port);
        var inboundConnection = await serverInboundTcs.Task.WaitAsync(_timeout);

        // The inbound connection has a temporary PeerId
        var tempId = inboundConnection.PeerId;

        // Create a "real" PeerId
        var realId = MakePeerId(42);

        // Act - update the temp id to the real id on the server
        bool updated = server.UpdatePeerId(tempId, realId);

        // Assert - update should succeed
        updated.Should().BeTrue();

        // The server should now only know the real id, not the temp id
        server.ConnectedPeerIds.Should().Contain(realId);
        server.ConnectedPeerIds.Should().NotContain(tempId);

        // Start read loops so we can verify messaging works with the new id
        server.StartReadLoop(inboundConnection);
        client.StartReadLoop(outboundConnection);

        // Send a message from the client; it should arrive attributed to the real id
        var payload = "After update"u8.ToArray();
        await outboundConnection.SendAsync(payload);

        var (receivedPeerId, receivedData) = await messageReceivedTcs.Task.WaitAsync(_timeout);
        receivedData.Should().BeEquivalentTo(payload);
        receivedPeerId.Should().Be(realId);

        // Verify server can send to the peer using the new real id
        var serverMessageTcs = new TaskCompletionSource<(PeerId, byte[])>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnMessageReceived += (peerId, data) =>
        {
            serverMessageTcs.TrySetResult((peerId, data));
        };

        var replyPayload = "Reply via real id"u8.ToArray();
        await server.SendAsync(realId, replyPayload);

        var (_, replyData) = await serverMessageTcs.Task.WaitAsync(_timeout);
        replyData.Should().BeEquivalentTo(replyPayload);
    }
}
