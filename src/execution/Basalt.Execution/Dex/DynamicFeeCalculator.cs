using Basalt.Core;
using Basalt.Execution.Dex.Math;

namespace Basalt.Execution.Dex;

/// <summary>
/// Calculates dynamic swap fees based on recent price volatility.
/// Inspired by Ambient Finance's dynamic fee model: fees increase during high volatility
/// to compensate LPs for impermanent loss, and decrease during stable periods to
/// attract more trading volume.
///
/// Formula:
/// <code>
/// effectiveFee = baseFee + (volatilityBps / threshold) * growthFactor
/// effectiveFee = clamp(effectiveFee, minFee, maxFee)
/// </code>
///
/// Default parameters:
/// <list type="bullet">
/// <item><description>Volatility threshold: 100 bps (1% deviation triggers fee increase)</description></item>
/// <item><description>Growth factor: 2 (each threshold multiple adds 2x base fee)</description></item>
/// <item><description>Max fee: 500 bps (5% cap to prevent excessive fees)</description></item>
/// <item><description>Min fee: 1 bps (0.01% minimum to always cover gas costs)</description></item>
/// </list>
/// </summary>
public static class DynamicFeeCalculator
{
    /// <summary>
    /// Volatility threshold in basis points. When volatility exceeds this,
    /// fees start increasing linearly.
    /// </summary>
    public const uint VolatilityThresholdBps = 100;

    /// <summary>
    /// Growth factor: how much the fee increases per threshold multiple.
    /// A growth factor of 2 means fees double for each 100 bps of volatility.
    /// </summary>
    public const uint GrowthFactor = 2;

    /// <summary>Maximum dynamic fee in basis points (5%).</summary>
    public const uint MaxFeeBps = 500;

    /// <summary>Minimum dynamic fee in basis points (0.01%).</summary>
    public const uint MinFeeBps = 1;

    /// <summary>
    /// Compute the dynamic fee for a pool based on current volatility.
    /// </summary>
    /// <param name="baseFeeBps">The pool's base fee in basis points.</param>
    /// <param name="volatilityBps">Current estimated volatility in basis points.</param>
    /// <returns>The effective fee in basis points, clamped to [MinFeeBps, MaxFeeBps].</returns>
    public static uint ComputeDynamicFee(uint baseFeeBps, uint volatilityBps)
    {
        // Below threshold: use base fee
        if (volatilityBps <= VolatilityThresholdBps)
            return Clamp(baseFeeBps);

        // Above threshold: linearly increase fee
        // feeIncrease = (volatilityBps - threshold) * growthFactor * baseFee / threshold
        var excess = volatilityBps - VolatilityThresholdBps;
        var feeIncrease = (ulong)excess * GrowthFactor * baseFeeBps / VolatilityThresholdBps;

        var effectiveFee = baseFeeBps + (uint)System.Math.Min(feeIncrease, MaxFeeBps);
        return Clamp(effectiveFee);
    }

    /// <summary>
    /// Compute the dynamic fee for a pool using on-chain TWAP data.
    /// Reads the pool's TWAP accumulator and compares against current spot price.
    /// </summary>
    /// <param name="dexState">The DEX state for reading TWAP and reserves.</param>
    /// <param name="poolId">The pool to compute the fee for.</param>
    /// <param name="baseFeeBps">The pool's base fee tier.</param>
    /// <param name="currentBlock">The current block number.</param>
    /// <param name="windowBlocks">The volatility measurement window (default: 100 blocks).</param>
    /// <returns>The effective dynamic fee in basis points.</returns>
    public static uint ComputeDynamicFeeFromState(
        DexState dexState, ulong poolId, uint baseFeeBps,
        ulong currentBlock, ulong windowBlocks = 7200)
    {
        var volatilityBps = TwapOracle.ComputeVolatilityBps(
            dexState, poolId, currentBlock, windowBlocks);

        return ComputeDynamicFee(baseFeeBps, volatilityBps);
    }

    private static uint Clamp(uint feeBps)
    {
        if (feeBps < MinFeeBps) return MinFeeBps;
        if (feeBps > MaxFeeBps) return MaxFeeBps;
        return feeBps;
    }
}
