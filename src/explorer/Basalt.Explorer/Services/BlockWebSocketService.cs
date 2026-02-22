using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Basalt.Explorer.Services;

public sealed class BlockWebSocketService : IAsyncDisposable
{
    private readonly string _wsUrl;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

    /// <summary>LOW-03: Maximum reconnect attempts before giving up.</summary>
    private const int MaxReconnectAttempts = 10;
    private int _reconnectAttempts;

    public event Action<WebSocketBlockEvent>? OnNewBlock;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public BlockWebSocketService(string nodeUrl)
    {
        var uri = new Uri(nodeUrl);
        var wsScheme = uri.Scheme == "https" ? "wss" : "ws";
        _wsUrl = $"{wsScheme}://{uri.Host}:{uri.Port}/ws/blocks";
    }

    public async Task ConnectAsync()
    {
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();

        try
        {
            await _ws.ConnectAsync(new Uri(_wsUrl), _cts.Token);
            _reconnectAttempts = 0; // LOW-03: Reset on successful connection
            _ = ReceiveLoop(_cts.Token);
        }
        catch
        {
            // Connection failed — will rely on polling fallback
        }
    }

    /// <summary>Maximum assembled message size (256 KB) to prevent memory exhaustion.</summary>
    private const int MaxMessageSize = 256 * 1024;

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                // H-2: Accumulate multi-frame messages until EndOfMessage
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (ms.Length + result.Count > MaxMessageSize) break;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                if (ms.Length > MaxMessageSize) continue; // discard oversized messages

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                    var envelope = JsonSerializer.Deserialize(json, ExplorerJsonContext.Default.WebSocketEnvelopeDto);
                    if (envelope?.Type == "new_block" && envelope.Block != null)
                    {
                        OnNewBlock?.Invoke(envelope.Block);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }

        // LOW-03: Auto-reconnect with bounded attempts
        if (!ct.IsCancellationRequested && _reconnectAttempts < MaxReconnectAttempts)
        {
            _reconnectAttempts++;
            try
            {
                await Task.Delay(3000, ct);
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(_wsUrl), ct);
                _reconnectAttempts = 0; // Reset on successful reconnect
                _ = ReceiveLoop(ct);
            }
            catch { /* give up reconnecting */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* ignore */ }
        }
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
