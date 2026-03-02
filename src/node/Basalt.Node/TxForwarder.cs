using System.Net.Http.Json;
using Basalt.Api.Rest;
using Basalt.Core;
using Basalt.Execution;
using Microsoft.Extensions.Logging;

namespace Basalt.Node;

/// <summary>
/// No-op forwarding for validators and standalone nodes.
/// Transactions are already in the local mempool and gossipped via P2P.
/// </summary>
public sealed class NoOpTxForwarder : ITxForwarder
{
    public Task ForwardAsync(Transaction tx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Forwards transactions from an RPC node to its sync source validator via HTTP.
/// Fire-and-forget: logs warnings on failure but never throws.
/// </summary>
public sealed class HttpTxForwarder : ITxForwarder, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    public HttpTxForwarder(string syncSourceUrl, ILogger? logger = null)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(syncSourceUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(5),
        };
    }

    public async Task ForwardAsync(Transaction tx, CancellationToken ct)
    {
        try
        {
            var request = new TransactionRequest
            {
                Type = (byte)tx.Type,
                Nonce = tx.Nonce,
                Sender = tx.Sender.ToHexString(),
                To = tx.To.ToHexString(),
                Value = tx.Value.ToString(),
                GasLimit = tx.GasLimit,
                GasPrice = tx.GasPrice.ToString(),
                MaxFeePerGas = tx.IsEip1559 ? tx.MaxFeePerGas.ToString() : null,
                MaxPriorityFeePerGas = tx.IsEip1559 ? tx.MaxPriorityFeePerGas.ToString() : null,
                Data = tx.Data.Length > 0 ? Convert.ToHexString(tx.Data) : null,
                Priority = tx.Priority,
                ChainId = tx.ChainId,
                Signature = Convert.ToHexString(tx.Signature.ToArray()),
                SenderPublicKey = tx.SenderPublicKey.ToArray().Length > 0
                    ? Convert.ToHexString(tx.SenderPublicKey.ToArray())
                    : "",
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await _httpClient.PostAsJsonAsync(
                "/v1/transactions",
                request,
                BasaltApiJsonContext.Default.TransactionRequest,
                cts.Token);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Failed to forward tx {Hash} to sync source: {Message}",
                tx.Hash.ToHexString()[..16], ex.Message);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
