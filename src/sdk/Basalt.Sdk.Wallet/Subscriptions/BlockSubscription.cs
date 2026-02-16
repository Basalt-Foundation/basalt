using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Subscriptions;

/// <summary>
/// WebSocket-based block subscription that connects to a Basalt node's
/// <c>/ws/blocks</c> endpoint and streams block events in real time.
/// Supports automatic reconnection with exponential backoff.
/// </summary>
public sealed class BlockSubscription : IBlockSubscription
{
    private readonly Uri _wsUri;
    private readonly SubscriptionOptions _options;
    private ClientWebSocket? _ws;

    /// <inheritdoc />
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>
    /// Creates a new block subscription targeting the specified node.
    /// </summary>
    /// <param name="baseUrl">
    /// The base URL of the Basalt node (e.g. "http://localhost:5100").
    /// The WebSocket endpoint path <c>/ws/blocks</c> is appended automatically.
    /// </param>
    /// <param name="options">Subscription configuration options, or null for defaults.</param>
    public BlockSubscription(string baseUrl, SubscriptionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        _options = options ?? new SubscriptionOptions();

        var trimmed = baseUrl.TrimEnd('/');
        var scheme = trimmed.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
        var hostPart = trimmed
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase);

        _wsUri = new Uri($"{scheme}://{hostPart}/ws/blocks");
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BlockEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var retryCount = 0;
        var delay = _options.InitialDelayMs;

        while (!ct.IsCancellationRequested)
        {
            _ws?.Dispose();
            _ws = new ClientWebSocket();

            bool connected;
            try
            {
                await _ws.ConnectAsync(_wsUri, ct).ConfigureAwait(false);
                connected = true;
                retryCount = 0;
                delay = _options.InitialDelayMs;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                connected = false;
            }

            if (connected)
            {
                var buffer = new byte[_options.ReceiveBufferSize];
                var messageBuffer = new MemoryStream();

                while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    BlockEvent? blockEvent = null;
                    var receiveError = false;

                    try
                    {
                        messageBuffer.SetLength(0);
                        WebSocketReceiveResult result;

                        do
                        {
                            result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                receiveError = true;
                                break;
                            }

                            messageBuffer.Write(buffer, 0, result.Count);
                        } while (!result.EndOfMessage);

                        if (!receiveError && messageBuffer.Length > 0)
                        {
                            blockEvent = JsonSerializer.Deserialize(
                                messageBuffer.ToArray(),
                                BlockSubscriptionJsonContext.Default.BlockEvent);
                        }
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        receiveError = true;
                    }

                    if (receiveError)
                        break;

                    if (blockEvent is not null)
                        yield return blockEvent;
                }
            }

            // Reconnection logic
            if (ct.IsCancellationRequested)
                yield break;

            if (!_options.AutoReconnect)
                yield break;

            retryCount++;
            if (_options.MaxRetries > 0 && retryCount > _options.MaxRetries)
                yield break;

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            delay = Math.Min(delay * 2, _options.MaxDelayMs);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ws is not null)
        {
            if (_ws.State == WebSocketState.Open)
            {
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposed", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best effort close
                }
            }

            _ws.Dispose();
            _ws = null;
        }
    }
}

/// <summary>
/// Source-generated JSON context for WebSocket block event deserialization.
/// </summary>
[JsonSerializable(typeof(BlockEvent))]
internal partial class BlockSubscriptionJsonContext : JsonSerializerContext;
