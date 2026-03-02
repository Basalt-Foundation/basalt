using Basalt.Core;

namespace Basalt.Execution.Dex.Math;

/// <summary>
/// Safe arithmetic for liquidity deltas in concentrated liquidity pools.
/// Liquidity is unsigned (UInt256) but deltas can be negative when crossing ticks
/// or burning positions. This library handles the signed addition safely.
/// </summary>
public static class LiquidityMath
{
    /// <summary>
    /// Adds a signed delta to an unsigned liquidity value.
    /// </summary>
    /// <param name="x">Current liquidity (unsigned).</param>
    /// <param name="y">Delta to apply. Positive for adding, negative for removing.</param>
    /// <returns>The resulting liquidity value.</returns>
    /// <exception cref="OverflowException">
    /// Thrown if the result would underflow (removing more than exists)
    /// or overflow UInt256 range.
    /// </exception>
    public static UInt256 AddDelta(UInt256 x, long y)
    {
        if (y >= 0)
        {
            return UInt256.CheckedAdd(x, new UInt256((ulong)y));
        }
        else
        {
            if (y == long.MinValue)
                throw new OverflowException("LiquidityMath: cannot negate long.MinValue");
            var absY = new UInt256((ulong)(-y));
            if (x < absY)
                throw new OverflowException($"LiquidityMath: underflow — cannot subtract {absY} from {x}");
            return UInt256.CheckedSub(x, absY);
        }
    }

    /// <summary>
    /// Computes the liquidity amount from token amounts for a concentrated position.
    /// Given the current sqrt price and the position's tick range, determines how much
    /// liquidity can be minted from the provided token amounts.
    /// </summary>
    /// <param name="sqrtPriceX96">Current pool sqrt price (Q64.96).</param>
    /// <param name="sqrtRatioAX96">Lower bound sqrt price of the position (Q64.96).</param>
    /// <param name="sqrtRatioBX96">Upper bound sqrt price of the position (Q64.96).</param>
    /// <param name="amount0">Available amount of token0.</param>
    /// <param name="amount1">Available amount of token1.</param>
    /// <returns>The maximum liquidity that can be minted from the provided amounts.</returns>
    public static UInt256 GetLiquidityForAmounts(
        UInt256 sqrtPriceX96,
        UInt256 sqrtRatioAX96,
        UInt256 sqrtRatioBX96,
        UInt256 amount0,
        UInt256 amount1)
    {
        if (sqrtRatioAX96 > sqrtRatioBX96)
            (sqrtRatioAX96, sqrtRatioBX96) = (sqrtRatioBX96, sqrtRatioAX96);

        if (sqrtPriceX96 <= sqrtRatioAX96)
        {
            // Current price below range: only token0 needed
            return GetLiquidityForAmount0(sqrtRatioAX96, sqrtRatioBX96, amount0);
        }
        else if (sqrtPriceX96 < sqrtRatioBX96)
        {
            // Current price within range: both tokens needed, take minimum
            var liq0 = GetLiquidityForAmount0(sqrtPriceX96, sqrtRatioBX96, amount0);
            var liq1 = GetLiquidityForAmount1(sqrtRatioAX96, sqrtPriceX96, amount1);
            return liq0 < liq1 ? liq0 : liq1;
        }
        else
        {
            // Current price above range: only token1 needed
            return GetLiquidityForAmount1(sqrtRatioAX96, sqrtRatioBX96, amount1);
        }
    }

    /// <summary>
    /// Computes liquidity from a token0 amount and a price range.
    /// L = amount0 * sqrtA * sqrtB / (sqrtB - sqrtA) / Q96
    /// </summary>
    private static UInt256 GetLiquidityForAmount0(
        UInt256 sqrtRatioAX96, UInt256 sqrtRatioBX96, UInt256 amount0)
    {
        if (sqrtRatioAX96 > sqrtRatioBX96)
            (sqrtRatioAX96, sqrtRatioBX96) = (sqrtRatioBX96, sqrtRatioAX96);

        var diff = sqrtRatioBX96 - sqrtRatioAX96;
        if (diff.IsZero) return UInt256.Zero;

        var intermediate = FullMath.MulDiv(sqrtRatioAX96, sqrtRatioBX96, TickMath.Q96);
        return FullMath.MulDiv(amount0, intermediate, diff);
    }

    /// <summary>
    /// Computes liquidity from a token1 amount and a price range.
    /// L = amount1 * Q96 / (sqrtB - sqrtA)
    /// </summary>
    private static UInt256 GetLiquidityForAmount1(
        UInt256 sqrtRatioAX96, UInt256 sqrtRatioBX96, UInt256 amount1)
    {
        if (sqrtRatioAX96 > sqrtRatioBX96)
            (sqrtRatioAX96, sqrtRatioBX96) = (sqrtRatioBX96, sqrtRatioAX96);

        var diff = sqrtRatioBX96 - sqrtRatioAX96;
        if (diff.IsZero) return UInt256.Zero;

        return FullMath.MulDiv(amount1, TickMath.Q96, diff);
    }
}
