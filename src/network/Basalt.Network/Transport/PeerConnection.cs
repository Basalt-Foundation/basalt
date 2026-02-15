namespace Basalt.Network.Transport;

using System.Buffers.Binary;
using System.Net.Sockets;

/// <summary>
/// Wraps a single TCP connection to a remote peer.
/// Uses length-prefixed framing: [4 bytes big-endian length][N bytes payload].
/// Thread-safe for concurrent sends via an internal semaphore.
/// </summary>
public sealed class PeerConnection : IDisposable
{
    /// <summary>
    /// Maximum allowed message size (16 MB) to prevent denial-of-service via oversized frames.
    /// </summary>
    private const int MaxMessageSize = 16 * 1024 * 1024;

    private const int LengthPrefixSize = 4;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Action<PeerId, byte[]> _onMessageReceived;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private volatile bool _disposed;

    public PeerConnection(TcpClient client, PeerId peerId, Action<PeerId, byte[]> onMessageReceived)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _stream = client.GetStream();
        PeerId = peerId;
        _onMessageReceived = onMessageReceived ?? throw new ArgumentNullException(nameof(onMessageReceived));
    }

    /// <summary>
    /// The identifier of the remote peer on this connection.
    /// </summary>
    public PeerId PeerId { get; set; }

    /// <summary>
    /// Whether the underlying TCP connection is still alive.
    /// </summary>
    public bool IsConnected => !_disposed && _client.Connected;

    /// <summary>
    /// Starts the asynchronous read loop that continuously reads length-prefixed messages
    /// and invokes the message callback for each complete frame.
    /// </summary>
    public async Task StartReadLoopAsync(CancellationToken cancellationToken)
    {
        var headerBuffer = new byte[LengthPrefixSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disposed)
            {
                // Read the 4-byte length header.
                await ReadExactAsync(_stream, headerBuffer, cancellationToken).ConfigureAwait(false);

                int messageLength = (int)BinaryPrimitives.ReadUInt32BigEndian(headerBuffer);

                if (messageLength <= 0 || messageLength > MaxMessageSize)
                {
                    throw new InvalidOperationException(
                        $"Invalid message length {messageLength} from peer {PeerId}. " +
                        $"Must be between 1 and {MaxMessageSize} bytes.");
                }

                // Read the payload.
                var payload = new byte[messageLength];
                await ReadExactAsync(_stream, payload, cancellationToken).ConfigureAwait(false);

                _onMessageReceived(PeerId, payload);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — cancellation was requested.
        }
        catch (IOException)
        {
            // Peer disconnected or network error.
        }
        catch (ObjectDisposedException)
        {
            // Connection was disposed while reading.
        }
        catch (InvalidOperationException)
        {
            // Protocol violation (e.g. oversized message).
        }
    }

    /// <summary>
    /// Sends a length-prefixed message to the remote peer.
    /// Thread-safe: concurrent callers are serialized through an internal semaphore.
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
            throw new ArgumentException("Cannot send an empty message.", nameof(data));

        if (data.Length > MaxMessageSize)
            throw new ArgumentException(
                $"Message size {data.Length} exceeds maximum of {MaxMessageSize} bytes.", nameof(data));

        ObjectDisposedException.ThrowIf(_disposed, this);

        var header = new byte[LengthPrefixSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Receive a single length-prefixed message synchronously (for handshake).
    /// Returns the payload bytes, or null if the connection was closed.
    /// </summary>
    public async Task<byte[]?> ReceiveOneAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var headerBuffer = new byte[LengthPrefixSize];
        try
        {
            await ReadExactAsync(_stream, headerBuffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }

        int messageLength = (int)BinaryPrimitives.ReadUInt32BigEndian(headerBuffer);
        if (messageLength <= 0 || messageLength > MaxMessageSize)
            return null;

        var payload = new byte[messageLength];
        await ReadExactAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream,
    /// looping until the buffer is full or the stream ends.
    /// </summary>
    private static async Task ReadExactAsync(
        NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int bytesRead = await stream
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead == 0)
                throw new IOException("Connection closed by remote peer.");

            offset += bytesRead;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }

        try
        {
            _client.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }

        _sendLock.Dispose();
    }
}
