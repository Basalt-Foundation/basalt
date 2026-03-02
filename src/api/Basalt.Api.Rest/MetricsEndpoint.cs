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
/// M13: Expanded with peer count, base fee, consensus view, finalization latency, DEX intent count.
/// </summary>
public static class MetricsEndpoint
{
    private static long _totalTransactionsProcessed;
    private static long _lastBlockTimestamp;
    private static long _lastBlockTxCount;
    private static long _currentTpsTicks; // Store as ticks (long) for Interlocked
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    // M13: Additional metrics
    private static long _peerCount;
    private static long _baseFee;
    private static long _consensusView;
    private static long _lastFinalizationLatencyMs;
    private static long _dexIntentCount;

    /// <summary>
    /// Record a produced block for TPS calculation.
    /// MEDIUM-6: All shared fields use Interlocked for thread safety.
    /// </summary>
    public static void RecordBlock(int txCount, long timestampMs)
    {
        Interlocked.Add(ref _totalTransactionsProcessed, txCount);

        var prevTimestamp = Interlocked.Read(ref _lastBlockTimestamp);
        if (prevTimestamp > 0)
        {
            var elapsedMs = timestampMs - prevTimestamp;
            if (elapsedMs > 0)
            {
                var tps = txCount * 1000.0 / elapsedMs;
                Interlocked.Exchange(ref _currentTpsTicks, BitConverter.DoubleToInt64Bits(tps));
            }
        }

        Interlocked.Exchange(ref _lastBlockTimestamp, timestampMs);
        Interlocked.Exchange(ref _lastBlockTxCount, txCount);
    }

    /// <summary>M13: Record connected peer count.</summary>
    public static void RecordPeerCount(int count) => Interlocked.Exchange(ref _peerCount, count);

    /// <summary>M13: Record current base fee (Lo limb for Prometheus u64).</summary>
    public static void RecordBaseFee(long baseFee) => Interlocked.Exchange(ref _baseFee, baseFee);

    /// <summary>M13: Record current consensus view/block number.</summary>
    public static void RecordConsensusView(long view) => Interlocked.Exchange(ref _consensusView, view);

    /// <summary>M13: Record block finalization latency in milliseconds.</summary>
    public static void RecordFinalizationLatency(long latencyMs) => Interlocked.Exchange(ref _lastFinalizationLatencyMs, latencyMs);

    /// <summary>M13: Record DEX intent count in mempool.</summary>
    public static void RecordDexIntentCount(int count) => Interlocked.Exchange(ref _dexIntentCount, count);

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
            var sb = new StringBuilder(4096);
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
            sb.Append("basalt_tps ").AppendLine(BitConverter.Int64BitsToDouble(Interlocked.Read(ref _currentTpsTicks)).ToString("F2", CultureInfo.InvariantCulture));

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

            // M13: Additional metrics
            sb.AppendLine("# HELP basalt_peer_count Number of connected peers.");
            sb.AppendLine("# TYPE basalt_peer_count gauge");
            sb.Append("basalt_peer_count ").AppendLine(Interlocked.Read(ref _peerCount).ToString());

            sb.AppendLine("# HELP basalt_base_fee Current base fee.");
            sb.AppendLine("# TYPE basalt_base_fee gauge");
            sb.Append("basalt_base_fee ").AppendLine(Interlocked.Read(ref _baseFee).ToString());

            sb.AppendLine("# HELP basalt_consensus_view Current consensus view/block.");
            sb.AppendLine("# TYPE basalt_consensus_view gauge");
            sb.Append("basalt_consensus_view ").AppendLine(Interlocked.Read(ref _consensusView).ToString());

            sb.AppendLine("# HELP basalt_finalization_latency_ms Last block finalization latency in milliseconds.");
            sb.AppendLine("# TYPE basalt_finalization_latency_ms gauge");
            sb.Append("basalt_finalization_latency_ms ").AppendLine(Interlocked.Read(ref _lastFinalizationLatencyMs).ToString());

            sb.AppendLine("# HELP basalt_dex_intent_count Number of DEX intents in mempool.");
            sb.AppendLine("# TYPE basalt_dex_intent_count gauge");
            sb.Append("basalt_dex_intent_count ").AppendLine(Interlocked.Read(ref _dexIntentCount).ToString());

            return Results.Text(sb.ToString(), "text/plain; version=0.0.4; charset=utf-8");
        }

        app.MapGet("/metrics", Handler);
        app.MapGet("/v1/metrics", Handler);
    }
}
