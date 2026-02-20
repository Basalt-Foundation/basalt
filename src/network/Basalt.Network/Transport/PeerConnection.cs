namespace Basalt.Network.Transport;

using System.Buffers.Binary;
using System.Net.Sockets;

/// <summary>
/// Wraps a single TCP connection to a remote peer.
/// Uses length-prefixed framing: [4 bytes big-endian length][N bytes payload].
/// Thread-safe for concurrent sends via an internal semaphore.
/// NET-M02: Thread-safe dispose with Interlocked.
/// NET-H02: Per-frame read timeout.
/// </summary>
public sealed class PeerConnection : IDisposable
{
    /// <summary>
    /// Maximum allowed message size (16 MB) to prevent denial-of-service via oversized frames.
    /// </summary>
    private const int MaxMessageSize = 16 * 1024 * 1024;

    private const int LengthPrefixSize = 4;

    /// <summary>NET-H02: Per-frame read timeout (120 seconds).</summary>
    private static readonly TimeSpan FrameReadTimeout = TimeSpan.FromSeconds(120);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly Action<PeerId, byte[]> _onMessageReceived;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>NET-C02: Per-connection AES-256-GCM encryption (null during handshake).</summary>
    private TransportEncryption? _encryption;

    /// <summary>NET-M02: Use int + Interlocked for thread-safe dispose.</summary>
    private int _disposed;

    public PeerConnection(TcpClient client, PeerId peerId, Action<PeerId, byte[]> onMessageReceived)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _stream = client.GetStream();
        PeerId = peerId;
        _onMessageReceived = onMessageReceived ?? throw new ArgumentNullException(nameof(onMessageReceived));
    }

    /// <summary>
    /// The identifier of the remote peer on this connection.
    /// NET-L01: Setter is internal to prevent external mutation.
    /// </summary>
    public PeerId PeerId { get; internal set; }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Whether the underlying TCP connection is still alive.
    /// </summary>
    public bool IsConnected => !IsDisposed && _client.Connected;

    /// <summary>
    /// NET-C02: Enable AES-256-GCM encryption on this connection after handshake.
    /// Must be called before StartReadLoopAsync.
    /// </summary>
    public void EnableEncryption(TransportEncryption encryption)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        _encryption = encryption;
    }

    /// <summary>
    /// Starts the asynchronous read loop that continuously reads length-prefixed messages
    /// and invokes the message callback for each complete frame.
    /// NET-H02: Each frame read has a 120s timeout to detect stale connections.
    /// NET-M06: Length is validated as uint before int cast.
    /// </summary>
    public async Task StartReadLoopAsync(CancellationToken cancellationToken)
    {
        var headerBuffer = new byte[LengthPrefixSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested && !IsDisposed)
            {
                // NET-H02: Per-frame read timeout
                using var frameCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                frameCts.CancelAfter(FrameReadTimeout);

                // Read the 4-byte length header.
                await ReadExactAsync(_stream, headerBuffer, frameCts.Token).ConfigureAwait(false);

                // NET-M06: Read as uint first, validate range, then cast to int
                uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer);
                if (rawLength == 0 || rawLength > MaxMessageSize)
                {
                    throw new InvalidOperationException(
                        $"Invalid message length {rawLength} from peer {PeerId}. " +
                        $"Must be between 1 and {MaxMessageSize} bytes.");
                }

                int messageLength = (int)rawLength;

                // Read the payload.
                // NET-L04: Consider ArrayPool for large payloads in future optimization
                var payload = new byte[messageLength];
                await ReadExactAsync(_stream, payload, frameCts.Token).ConfigureAwait(false);

                // NET-C02: Decrypt if encryption is enabled
                var message = _encryption != null ? _encryption.Decrypt(payload) : payload;
                _onMessageReceived(PeerId, message);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // NET-H02: Frame read timeout — treat as stale connection.
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
    /// NET-M03: Header and payload combined into a single write to avoid TCP fragmentation.
    /// </summary>
    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
            throw new ArgumentException("Cannot send an empty message.", nameof(data));

        if (data.Length > MaxMessageSize)
            throw new ArgumentException(
                $"Message size {data.Length} exceeds maximum of {MaxMessageSize} bytes.", nameof(data));

        ObjectDisposedException.ThrowIf(IsDisposed, this);

        // NET-C02: Encrypt if encryption is enabled
        var wireData = _encryption != null ? _encryption.Encrypt(data) : data;

        // NET-M03: Combine header + payload into single buffer to avoid TCP fragmentation
        var frame = new byte[LengthPrefixSize + wireData.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)wireData.Length);
        wireData.CopyTo(frame.AsSpan(LengthPrefixSize));

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
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
    /// NET-M06: Length validated as uint before int cast.
    /// </summary>
    public async Task<byte[]?> ReceiveOneAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var headerBuffer = new byte[LengthPrefixSize];
        try
        {
            await ReadExactAsync(_stream, headerBuffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }

        uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer);
        if (rawLength == 0 || rawLength > MaxMessageSize)
            return null;

        int messageLength = (int)rawLength;
        var payload = new byte[messageLength]; // NET-L04: Consider ArrayPool for large payloads in future optimization
        try
        {
            await ReadExactAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null; // NET-L03: Consistent IOException handling for payload read
        }

        // NET-C02: Decrypt if encryption is enabled
        return _encryption != null ? _encryption.Decrypt(payload) : payload;
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

    /// <summary>
    /// NET-M02: Thread-safe dispose using Interlocked.Exchange.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _encryption?.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }

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

        try
        {
            _sendLock.Dispose();
        }
        catch
        {
            // Best-effort cleanup — may already be disposed.
        }
    }
}
