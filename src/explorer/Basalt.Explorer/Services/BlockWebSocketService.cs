using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Basalt.Explorer.Services;

public sealed class BlockWebSocketService : IAsyncDisposable
{
    private readonly string _wsUrl;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;

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
            _ = ReceiveLoop(_cts.Token);
        }
        catch
        {
            // Connection failed — will rely on polling fallback
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            try
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
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

        // Auto-reconnect after 3 seconds
        if (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, ct);
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri(_wsUrl), ct);
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
