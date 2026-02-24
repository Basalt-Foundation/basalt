using Basalt.Core;
using Basalt.Execution.Dex.Math;

namespace Basalt.Execution.Dex;

/// <summary>
/// On-chain Time-Weighted Average Price oracle.
/// Provides TWAP and volatility queries using the per-pool accumulators stored in DexState.
///
/// TWAP computation:
/// <code>
/// twap = (accumulator[now] - accumulator[start]) / (block[now] - block[start])
/// </code>
///
/// The accumulator stores cumulative (price * blockDelta) at each update,
/// enabling O(1) TWAP queries for any window length without scanning individual blocks.
///
/// Volatility is estimated from the variance of price changes across the window,
/// expressed in basis points for use in dynamic fee calculations.
/// </summary>
public static class TwapOracle
{
    /// <summary>
    /// Compute the TWAP for a pool over a given window of blocks.
    /// Returns the time-weighted average price as token1-per-token0 scaled by <see cref="BatchAuctionSolver.PriceScale"/>.
    /// </summary>
    /// <param name="state">The DEX state containing TWAP accumulators.</param>
    /// <param name="poolId">The pool to query.</param>
    /// <param name="currentBlock">The current block number.</param>
    /// <param name="windowBlocks">The number of blocks to average over.</param>
    /// <returns>The TWAP, or zero if insufficient data.</returns>
    public static UInt256 ComputeTwap(DexState state, ulong poolId, ulong currentBlock, ulong windowBlocks)
    {
        if (windowBlocks == 0) return UInt256.Zero;

        var acc = state.GetTwapAccumulator(poolId);
        if (acc.LastBlock == 0) return UInt256.Zero;

        var endAccum = acc.CumulativePrice;
        var endBlock = acc.LastBlock;

        // M-06: Windowed TWAP using stored per-block accumulator snapshots.
        // twap = (accumulator[end] - accumulator[start]) / (end - start)
        var targetStartBlock = currentBlock > windowBlocks ? currentBlock - windowBlocks : 0;

        // Search backwards from targetStartBlock for the nearest snapshot
        UInt256 startAccum = UInt256.Zero;
        ulong actualStartBlock = 0;

        if (targetStartBlock > 0)
        {
            const ulong maxScan = 2048;
            ulong scanLower = targetStartBlock > maxScan ? targetStartBlock - maxScan : 0;

            for (ulong b = targetStartBlock; b >= scanLower; b--)
            {
                var snapshot = state.GetTwapSnapshot(poolId, b);
                if (snapshot != null)
                {
                    startAccum = snapshot.Value;
                    actualStartBlock = b;
                    break;
                }
                if (b == 0) break; // Prevent ulong underflow
            }
        }

        if (endBlock <= actualStartBlock) return UInt256.Zero;

        var blockSpan = new UInt256(endBlock - actualStartBlock);
        if (endAccum < startAccum) return UInt256.Zero;

        return FullMath.MulDiv(endAccum - startAccum, UInt256.One, blockSpan);
    }

    /// <summary>
    /// Estimate price volatility for a pool over a window, expressed in basis points.
    /// Uses a simplified volatility metric: the ratio of max deviation from the TWAP
    /// to the TWAP itself, scaled to basis points.
    ///
    /// This is an approximation — true volatility would require storing per-block prices.
    /// For dynamic fee purposes, this provides a reasonable signal.
    /// </summary>
    /// <param name="state">The DEX state.</param>
    /// <param name="poolId">The pool to query.</param>
    /// <param name="currentBlock">The current block number.</param>
    /// <param name="windowBlocks">The window size in blocks.</param>
    /// <returns>Estimated volatility in basis points (0-10000).</returns>
    public static uint ComputeVolatilityBps(DexState state, ulong poolId, ulong currentBlock, ulong windowBlocks)
    {
        var twap = ComputeTwap(state, poolId, currentBlock, windowBlocks);
        if (twap.IsZero) return 0;

        // Get current spot price from reserves
        var reserves = state.GetPoolReserves(poolId);
        if (reserves == null || reserves.Value.Reserve0.IsZero)
            return 0;

        var spotPrice = BatchAuctionSolver.ComputeSpotPrice(reserves.Value.Reserve0, reserves.Value.Reserve1);

        // Deviation = |spot - twap| / twap * 10000 (basis points)
        var deviation = spotPrice > twap
            ? spotPrice - twap
            : twap - spotPrice;

        // deviation * 10000 / twap → basis points
        var volatilityBps = FullMath.MulDiv(deviation, new UInt256(10_000), twap);

        // Cap at 10000 bps (100%)
        var maxBps = new UInt256(10_000);
        if (volatilityBps > maxBps)
            return 10_000;

        return (uint)(ulong)volatilityBps.Lo;
    }

    /// <summary>
    /// Carry forward the TWAP accumulator for a pool using its current price.
    /// Called per-block for all active pools to prevent TWAP gaps during low-volume blocks.
    /// </summary>
    public static void CarryForwardAccumulator(DexState state, ulong poolId, UInt256 currentPrice, ulong blockNumber)
    {
        state.UpdateTwapAccumulator(poolId, currentPrice, blockNumber);
    }

    /// <summary>
    /// Serialize TWAP data for inclusion in block header ExtraData.
    /// Format per pool: <c>[8B poolId][32B clearingPrice][32B twap]</c>
    /// Multiple pools concatenated up to MaxExtraDataBytes.
    /// </summary>
    /// <param name="settlements">Batch settlement results from this block.</param>
    /// <param name="state">The DEX state for TWAP lookups.</param>
    /// <param name="currentBlock">Current block number.</param>
    /// <param name="maxBytes">Maximum bytes available.</param>
    /// <returns>Serialized TWAP snapshot for block header ExtraData.</returns>
    public static byte[] SerializeForBlockHeader(
        List<BatchResult> settlements, DexState state,
        ulong currentBlock, uint maxBytes, ulong twapWindowBlocks = 7200)
    {
        const int entrySize = 8 + 32 + 32; // poolId + clearingPrice + twap
        var maxEntries = (int)(maxBytes / entrySize);
        var count = System.Math.Min(settlements.Count, maxEntries);

        if (count == 0) return [];

        var buffer = new byte[count * entrySize];

        for (int i = 0; i < count; i++)
        {
            var settlement = settlements[i];
            var offset = i * entrySize;

            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(
                buffer.AsSpan(offset, 8), settlement.PoolId);
            settlement.ClearingPrice.WriteTo(buffer.AsSpan(offset + 8, 32));

            var twap = ComputeTwap(state, settlement.PoolId, currentBlock, twapWindowBlocks);
            twap.WriteTo(buffer.AsSpan(offset + 40, 32));
        }

        return buffer;
    }

    /// <summary>
    /// Parse TWAP data from block header ExtraData.
    /// </summary>
    /// <param name="extraData">The raw extra data bytes.</param>
    /// <returns>List of (poolId, clearingPrice, twap) tuples.</returns>
    public static List<(ulong PoolId, UInt256 ClearingPrice, UInt256 Twap)> ParseFromBlockHeader(byte[] extraData)
    {
        const int entrySize = 8 + 32 + 32;
        var result = new List<(ulong, UInt256, UInt256)>();

        for (int offset = 0; offset + entrySize <= extraData.Length; offset += entrySize)
        {
            var poolId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
                extraData.AsSpan(offset, 8));
            var clearingPrice = new UInt256(extraData.AsSpan(offset + 8, 32));
            var twap = new UInt256(extraData.AsSpan(offset + 40, 32));

            result.Add((poolId, clearingPrice, twap));
        }

        return result;
    }
}
