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
    private const int MaxForkSearchDepth = 1000;
    private int _consecutiveForkFailures;

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
                    _consecutiveForkFailures = 0;
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
                    _consecutiveForkFailures = 0;
                    var newLocalTip = _chainManager.LatestBlockNumber;
                    Volatile.Write(ref _syncLag, (int)Math.Min(remoteTip - newLocalTip, int.MaxValue));

                    _logger.LogInformation(
                        "Synced {Count} blocks from source, now at #{Height} (lag: {Lag})",
                        applied, newLocalTip, SyncLag);
                }
                else
                {
                    // Batch failed — likely a fork (parent hash mismatch). Attempt recovery.
                    _consecutiveForkFailures++;
                    if (_consecutiveForkFailures >= 3)
                    {
                        var recovered = await TryForkRecoveryAsync(ct);
                        if (recovered)
                        {
                            _consecutiveForkFailures = 0;
                            _backoffMs = 1000;
                            continue; // Immediately retry sync from the new tip
                        }
                    }
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

    /// <summary>
    /// Detect and recover from a chain fork by comparing local blocks with the sync source.
    /// Walks back from the current tip to find the last common ancestor, then rolls back
    /// the chain so re-sync can proceed from the fork point.
    /// </summary>
    private async Task<bool> TryForkRecoveryAsync(CancellationToken ct)
    {
        var localTip = _chainManager.LatestBlockNumber;
        var localBlock = _chainManager.LatestBlock;
        if (localBlock == null || localTip == 0)
            return false;

        // Fetch the block at our current tip from the sync source to confirm fork
        var tipEntry = await FetchRemoteBlockEntryAsync(localTip, ct);
        if (tipEntry == null)
            return false;

        if (tipEntry.Hash == localBlock.Hash.ToHexString())
        {
            // Hashes match — not a fork. The apply failure has a different cause.
            _logger.LogDebug("No fork detected: local and remote hashes match at #{Block}", localTip);
            return false;
        }

        _logger.LogWarning(
            "Fork detected at block #{Block}: local={LocalHash}, remote={RemoteHash}",
            localTip, localBlock.Hash.ToHexString()[..18] + "...", tipEntry.Hash[..Math.Min(18, tipEntry.Hash.Length)] + "...");

        // Walk back to find the fork point (last common ancestor).
        // Use a simple linear walk — forks are typically shallow.
        Block? forkPointBlock = null;
        var searchDepth = (int)Math.Min(localTip, MaxForkSearchDepth);

        for (int i = 1; i <= searchDepth; i++)
        {
            var height = localTip - (ulong)i;
            var local = _chainManager.GetBlockByNumber(height);
            if (local == null)
            {
                _logger.LogWarning("Cannot find local block #{Block} — reached in-memory limit", height);
                break;
            }

            var remote = await FetchRemoteBlockEntryAsync(height, ct);
            if (remote == null)
                continue;

            if (remote.Hash == local.Hash.ToHexString())
            {
                forkPointBlock = local;
                _logger.LogInformation(
                    "Fork point found at block #{Block} (depth={Depth}), rolling back from #{Tip}",
                    height, i, localTip);
                break;
            }
        }

        if (forkPointBlock == null)
        {
            _logger.LogCritical(
                "Could not find common ancestor within {Depth} blocks. " +
                "The RPC node may need a restart with a clean database to recover.",
                searchDepth);
            return false;
        }

        // Rollback chain to the fork point
        _chainManager.RollbackTo(forkPointBlock);
        _logger.LogInformation(
            "Chain rolled back to block #{Block}. Re-sync will resume from here.",
            forkPointBlock.Number);

        return true;
    }

    /// <summary>
    /// Fetch a single block's metadata from the sync source by height.
    /// </summary>
    private async Task<SyncBlockEntry?> FetchRemoteBlockEntryAsync(ulong height, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync(
                $"/v1/sync/blocks?from={height}&count=1",
                BasaltApiJsonContext.Default.SyncBlocksResponse,
                ct);

            if (response?.Blocks != null && response.Blocks.Length > 0)
                return response.Blocks[0];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch remote block #{Block}", height);
        }
        return null;
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