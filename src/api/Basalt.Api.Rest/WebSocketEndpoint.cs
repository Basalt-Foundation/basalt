using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Basalt.Core;
using Basalt.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Basalt.Api.Rest;

/// <summary>
/// WebSocket endpoint for real-time block and transaction updates.
/// Clients connect to /ws/blocks for live block notifications.
/// </summary>
public sealed class WebSocketHandler : IDisposable
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly ChainManager _chainManager;

    public WebSocketHandler(ChainManager chainManager)
    {
        _chainManager = chainManager;
    }

    /// <summary>
    /// Notify all connected WebSocket clients of a new block.
    /// </summary>
    public async Task BroadcastNewBlock(Block block)
    {
        var message = new WebSocketBlockMessage
        {
            Type = "new_block",
            Block = new WebSocketBlockData
            {
                Number = block.Number,
                Hash = block.Hash.ToHexString(),
                ParentHash = block.Header.ParentHash.ToHexString(),
                StateRoot = block.Header.StateRoot.ToHexString(),
                Timestamp = block.Header.Timestamp,
                Proposer = block.Header.Proposer.ToHexString(),
                GasUsed = block.Header.GasUsed,
                GasLimit = block.Header.GasLimit,
                TransactionCount = block.Transactions.Count,
            },
        };

        var json = JsonSerializer.Serialize(message, WsJsonContext.Default.WebSocketBlockMessage);
        var bytes = Encoding.UTF8.GetBytes(json);

        var disconnected = new List<string>();

        foreach (var (id, ws) in _connections)
        {
            if (ws.State != WebSocketState.Open)
            {
                disconnected.Add(id);
                continue;
            }

            try
            {
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                disconnected.Add(id);
            }
        }

        foreach (var id in disconnected)
        {
            _connections.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Handle a new WebSocket connection.
    /// </summary>
    public async Task HandleConnection(WebSocket webSocket)
    {
        var id = Guid.NewGuid().ToString("N");
        _connections[id] = webSocket;

        try
        {
            // Send current status as initial message
            var latest = _chainManager.LatestBlock;
            if (latest != null)
            {
                await BroadcastToSingle(webSocket, latest);
            }

            // Keep alive — wait for close
            var buffer = new byte[256];
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        finally
        {
            _connections.TryRemove(id, out _);
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
            }
        }
    }

    /// <summary>Number of active WebSocket connections.</summary>
    public int ConnectionCount => _connections.Count;

    private static async Task BroadcastToSingle(WebSocket ws, Block block)
    {
        var message = new WebSocketBlockMessage
        {
            Type = "current_block",
            Block = new WebSocketBlockData
            {
                Number = block.Number,
                Hash = block.Hash.ToHexString(),
                ParentHash = block.Header.ParentHash.ToHexString(),
                StateRoot = block.Header.StateRoot.ToHexString(),
                Timestamp = block.Header.Timestamp,
                Proposer = block.Header.Proposer.ToHexString(),
                GasUsed = block.Header.GasUsed,
                GasLimit = block.Header.GasLimit,
                TransactionCount = block.Transactions.Count,
            },
        };

        var json = JsonSerializer.Serialize(message, WsJsonContext.Default.WebSocketBlockMessage);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public void Dispose()
    {
        foreach (var (_, ws) in _connections)
        {
            ws.Dispose();
        }
        _connections.Clear();
    }
}

/// <summary>
/// Extension methods for mapping WebSocket endpoints.
/// </summary>
public static class WebSocketEndpointExtensions
{
    /// <summary>
    /// Map the /ws/blocks WebSocket endpoint.
    /// </summary>
    public static void MapWebSocketEndpoint(
        this IEndpointRouteBuilder app,
        WebSocketHandler handler)
    {
        app.Map("/ws/blocks", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connection required.");
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await handler.HandleConnection(webSocket);
        });
    }
}

public sealed class WebSocketBlockMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("block")] public WebSocketBlockData? Block { get; set; }
}

public sealed class WebSocketBlockData
{
    [JsonPropertyName("number")] public ulong Number { get; set; }
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("parentHash")] public string ParentHash { get; set; } = "";
    [JsonPropertyName("stateRoot")] public string StateRoot { get; set; } = "";
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("proposer")] public string Proposer { get; set; } = "";
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
    [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
}

[JsonSerializable(typeof(WebSocketBlockMessage))]
[JsonSerializable(typeof(WebSocketBlockData))]
public partial class WsJsonContext : JsonSerializerContext;
