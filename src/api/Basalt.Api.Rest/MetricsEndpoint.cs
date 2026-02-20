using System.Diagnostics;
using System.Globalization;
using System.Text;
using Basalt.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Basalt.Api.Rest;

/// <summary>
/// Prometheus-compatible /metrics endpoint for monitoring.
/// Exposes block height, TPS, mempool size, peer count, and timing metrics.
/// </summary>
public static class MetricsEndpoint
{
    private static long _totalTransactionsProcessed;
    private static long _lastBlockTimestamp;
    private static int _lastBlockTxCount;
    private static double _currentTps;
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    /// <summary>
    /// Record a produced block for TPS calculation.
    /// </summary>
    public static void RecordBlock(int txCount, long timestampMs)
    {
        Interlocked.Add(ref _totalTransactionsProcessed, txCount);

        if (_lastBlockTimestamp > 0)
        {
            var elapsedMs = timestampMs - _lastBlockTimestamp;
            if (elapsedMs > 0)
                _currentTps = txCount * 1000.0 / elapsedMs;
        }

        _lastBlockTimestamp = timestampMs;
        _lastBlockTxCount = txCount;
    }

    /// <summary>
    /// Map the /metrics endpoint.
    /// </summary>
    public static void MapMetricsEndpoint(
        IEndpointRouteBuilder app,
        ChainManager chainManager,
        Mempool mempool)
    {
        IResult Handler()
        {
            var sb = new StringBuilder(2048);
            var latest = chainManager.LatestBlock;
            var blockHeight = latest?.Number ?? 0;
            var mempoolSize = mempool.Count;

            // Block metrics
            sb.AppendLine("# HELP basalt_block_height Current block height.");
            sb.AppendLine("# TYPE basalt_block_height gauge");
            sb.Append("basalt_block_height ").AppendLine(blockHeight.ToString());

            // TPS metrics
            sb.AppendLine("# HELP basalt_tps Current transactions per second.");
            sb.AppendLine("# TYPE basalt_tps gauge");
            sb.Append("basalt_tps ").AppendLine(_currentTps.ToString("F2", CultureInfo.InvariantCulture));

            // Total transactions processed
            sb.AppendLine("# HELP basalt_transactions_total Total transactions processed.");
            sb.AppendLine("# TYPE basalt_transactions_total counter");
            sb.Append("basalt_transactions_total ").AppendLine(
                Interlocked.Read(ref _totalTransactionsProcessed).ToString());

            // Mempool size
            sb.AppendLine("# HELP basalt_mempool_size Number of pending transactions in mempool.");
            sb.AppendLine("# TYPE basalt_mempool_size gauge");
            sb.Append("basalt_mempool_size ").AppendLine(mempoolSize.ToString());

            // Last block gas
            if (latest != null)
            {
                sb.AppendLine("# HELP basalt_block_gas_used Gas used in last block.");
                sb.AppendLine("# TYPE basalt_block_gas_used gauge");
                sb.Append("basalt_block_gas_used ").AppendLine(latest.Header.GasUsed.ToString());

                sb.AppendLine("# HELP basalt_block_gas_limit Gas limit of last block.");
                sb.AppendLine("# TYPE basalt_block_gas_limit gauge");
                sb.Append("basalt_block_gas_limit ").AppendLine(latest.Header.GasLimit.ToString());

                sb.AppendLine("# HELP basalt_block_tx_count Transactions in last block.");
                sb.AppendLine("# TYPE basalt_block_tx_count gauge");
                sb.Append("basalt_block_tx_count ").AppendLine(latest.Transactions.Count.ToString());
            }

            // Uptime
            sb.AppendLine("# HELP basalt_uptime_seconds Node uptime in seconds.");
            sb.AppendLine("# TYPE basalt_uptime_seconds gauge");
            sb.Append("basalt_uptime_seconds ").AppendLine(Uptime.Elapsed.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture));

            return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
        }

        app.MapGet("/metrics", Handler);
        app.MapGet("/v1/metrics", Handler);
    }
}
