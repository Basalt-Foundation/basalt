using System.Net.Http.Json;
using Basalt.Api.Rest;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Network;
using Basalt.Storage;
using Microsoft.Extensions.Logging;

namespace Basalt.Node;

/// <summary>
/// Provides sync status for health endpoint reporting.
/// </summary>
public interface ISyncStatus
{
    /// <summary>How many blocks behind the sync source this node is.</summary>
    int SyncLag { get; }
}

/// <summary>
/// Background service that polls a trusted sync source via HTTP and applies finalized blocks
/// using <see cref="BlockApplier"/>. Used by RPC nodes that follow the chain without
/// participating in consensus.
/// </summary>
public sealed class BlockSyncService : ISyncStatus, IAsyncDisposable
{
    private readonly string _syncSourceUrl;
    private readonly BlockApplier _blockApplier;
    private readonly ChainManager _chainManager;
    private readonly StateDbRef _stateDbRef;
    private readonly ChainParameters _chainParams;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    private int _syncLag;
    private int _backoffMs = 1000;
    private const int MaxBackoffMs = 30_000;

    public int SyncLag => Volatile.Read(ref _syncLag);

    public BlockSyncService(
        string syncSourceUrl,
        BlockApplier blockApplier,
        ChainManager chainManager,
        StateDbRef stateDbRef,
        ChainParameters chainParams,
        ILogger logger)
    {
        _syncSourceUrl = syncSourceUrl.TrimEnd('/');
        _blockApplier = blockApplier;
        _chainManager = chainManager;
        _stateDbRef = stateDbRef;
        _chainParams = chainParams;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_syncSourceUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// Main sync loop. Polls the sync source and applies blocks until cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation("BlockSyncService started. Source: {Source}", _syncSourceUrl);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await _httpClient.GetFromJsonAsync(
                    "/v1/sync/status",
                    BasaltApiJsonContext.Default.SyncStatusResponse,
                    ct);

                if (status == null)
                {
                    _logger.LogWarning("Sync source returned null status");
                    await BackoffAsync(ct);
                    continue;
                }

                var localTip = _chainManager.LatestBlockNumber;
                var remoteTip = status.LatestBlock;
                Volatile.Write(ref _syncLag, (int)Math.Min(remoteTip - localTip, int.MaxValue));

                if (remoteTip <= localTip)
                {
                    // Caught up — sleep for one block time, then poll again
                    _backoffMs = 1000; // Reset backoff
                    await Task.Delay((int)_chainParams.BlockTimeMs, ct);
                    continue;
                }

                // Fetch blocks in batches of 100
                var from = localTip + 1;
                var count = (int)Math.Min(remoteTip - localTip, 100);

                var response = await _httpClient.GetFromJsonAsync(
                    $"/v1/sync/blocks?from={from}&count={count}",
                    BasaltApiJsonContext.Default.SyncBlocksResponse,
                    ct);

                if (response?.Blocks == null || response.Blocks.Length == 0)
                {
                    _logger.LogDebug("No blocks returned from sync source (from={From})", from);
                    await BackoffAsync(ct);
                    continue;
                }

                // Deserialize and prepare blocks for batch apply
                var blocks = new List<(Block Block, byte[] Raw, ulong CommitBitmap)>();
                foreach (var entry in response.Blocks)
                {
                    try
                    {
                        var raw = Convert.FromHexString(entry.RawHex);
                        var block = BlockCodec.DeserializeBlock(raw);
                        blocks.Add((block, raw, entry.CommitBitmap ?? 0));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to deserialize synced block #{Number}", entry.Number);
                        break;
                    }
                }

                if (blocks.Count == 0)
                {
                    await BackoffAsync(ct);
                    continue;
                }

                var applied = _blockApplier.ApplyBatch(blocks, _stateDbRef);
                if (applied > 0)
                {
                    _backoffMs = 1000; // Reset backoff on success
                    var newLocalTip = _chainManager.LatestBlockNumber;
                    Volatile.Write(ref _syncLag, (int)Math.Min(remoteTip - newLocalTip, int.MaxValue));

                    _logger.LogInformation(
                        "Synced {Count} blocks from source, now at #{Height} (lag: {Lag})",
                        applied, newLocalTip, SyncLag);
                }
                else
                {
                    await BackoffAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Sync source unreachable: {Message}", ex.Message);
                await BackoffAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in sync loop");
                await BackoffAsync(ct);
            }
        }

        _logger.LogInformation("BlockSyncService stopped");
    }

    private async Task BackoffAsync(CancellationToken ct)
    {
        await Task.Delay(_backoffMs, ct);
        _backoffMs = Math.Min(_backoffMs * 2, MaxBackoffMs);
    }

    public async ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        await ValueTask.CompletedTask;
    }
}
