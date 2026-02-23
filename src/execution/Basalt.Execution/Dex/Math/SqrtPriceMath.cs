using Basalt.Core;

namespace Basalt.Execution.Dex.Math;

/// <summary>
/// Math for computing token amounts from sqrt price changes in concentrated liquidity pools.
/// All sqrt prices are Q64.96 fixed-point values. Liquidity is a raw UInt256.
/// </summary>
/// <remarks>
/// Core formulas (from Uniswap v3 whitepaper):
/// <list type="bullet">
/// <item>amount0 = liquidity * (1/sqrtA - 1/sqrtB) = liquidity * (sqrtB - sqrtA) / (sqrtA * sqrtB)</item>
/// <item>amount1 = liquidity * (sqrtB - sqrtA)</item>
/// </list>
/// Where sqrtA &lt; sqrtB. Token0 is the numeraire; token1 is the quote token.
/// </remarks>
public static class SqrtPriceMath
{
    private static readonly UInt256 Q96 = TickMath.Q96;

    /// <summary>
    /// Computes the amount of token0 received for a price move from sqrtA to sqrtB,
    /// given a liquidity amount. sqrtA and sqrtB can be in any order.
    /// </summary>
    /// <param name="sqrtRatioAX96">One sqrt price boundary (Q64.96).</param>
    /// <param name="sqrtRatioBX96">Other sqrt price boundary (Q64.96).</param>
    /// <param name="liquidity">The liquidity amount.</param>
    /// <param name="roundUp">Whether to round up (for debiting) or down (for crediting).</param>
    /// <returns>The amount of token0.</returns>
    public static UInt256 GetAmount0Delta(
        UInt256 sqrtRatioAX96, UInt256 sqrtRatioBX96, UInt256 liquidity, bool roundUp)
    {
        if (sqrtRatioAX96 > sqrtRatioBX96)
            (sqrtRatioAX96, sqrtRatioBX96) = (sqrtRatioBX96, sqrtRatioAX96);

        if (sqrtRatioAX96.IsZero)
            throw new DivideByZeroException("SqrtPriceMath: sqrtRatioA is zero");

        // amount0 = liquidity * Q96 * (sqrtB - sqrtA) / (sqrtA * sqrtB)
        var numerator = FullMath.MulDiv(liquidity, sqrtRatioBX96 - sqrtRatioAX96, Q96);

        return roundUp
            ? FullMath.MulDivRoundingUp(numerator, Q96, sqrtRatioBX96) // Must use inner sqrtB
            : FullMath.MulDiv(numerator, Q96, sqrtRatioBX96);

        // Note: This is algebraically equivalent to:
        // liquidity * (sqrtB - sqrtA) / (sqrtA * sqrtB / Q96)
        // but avoids overflow in the intermediate sqrtA * sqrtB product.
    }

    /// <summary>
    /// Computes the amount of token1 received for a price move from sqrtA to sqrtB,
    /// given a liquidity amount. sqrtA and sqrtB can be in any order.
    /// </summary>
    /// <param name="sqrtRatioAX96">One sqrt price boundary (Q64.96).</param>
    /// <param name="sqrtRatioBX96">Other sqrt price boundary (Q64.96).</param>
    /// <param name="liquidity">The liquidity amount.</param>
    /// <param name="roundUp">Whether to round up (for debiting) or down (for crediting).</param>
    /// <returns>The amount of token1.</returns>
    public static UInt256 GetAmount1Delta(
        UInt256 sqrtRatioAX96, UInt256 sqrtRatioBX96, UInt256 liquidity, bool roundUp)
    {
        if (sqrtRatioAX96 > sqrtRatioBX96)
            (sqrtRatioAX96, sqrtRatioBX96) = (sqrtRatioBX96, sqrtRatioAX96);

        // amount1 = liquidity * (sqrtB - sqrtA) / Q96
        return roundUp
            ? FullMath.MulDivRoundingUp(liquidity, sqrtRatioBX96 - sqrtRatioAX96, Q96)
            : FullMath.MulDiv(liquidity, sqrtRatioBX96 - sqrtRatioAX96, Q96);
    }

    /// <summary>
    /// Computes the next sqrt price given a token0 input/output amount.
    /// Used during swap execution to determine price after consuming liquidity.
    /// </summary>
    /// <param name="sqrtPX96">The starting sqrt price (Q64.96).</param>
    /// <param name="liquidity">The available liquidity.</param>
    /// <param name="amount">The token0 amount being swapped.</param>
    /// <param name="add">True if adding token0 (buying token1), false if removing.</param>
    /// <returns>The resulting sqrt price after the swap.</returns>
    public static UInt256 GetNextSqrtPriceFromAmount0(
        UInt256 sqrtPX96, UInt256 liquidity, UInt256 amount, bool add)
    {
        if (amount.IsZero) return sqrtPX96;
        if (liquidity.IsZero) throw new DivideByZeroException("SqrtPriceMath: zero liquidity");

        // When adding token0: price goes down.
        // nextSqrtP = liquidity * sqrtP / (liquidity + amount * sqrtP / Q96)
        // When removing token0: price goes up.
        // nextSqrtP = liquidity * sqrtP / (liquidity - amount * sqrtP / Q96)

        var numerator = FullMath.MulDiv(liquidity, sqrtPX96, Q96);
        var product = FullMath.MulDiv(amount, sqrtPX96, Q96);

        if (add)
        {
            var denominator = UInt256.CheckedAdd(liquidity, product);
            return FullMath.MulDivRoundingUp(numerator, Q96, denominator);
        }
        else
        {
            if (product >= liquidity)
                throw new OverflowException("SqrtPriceMath: amount exceeds available liquidity");
            var denominator = UInt256.CheckedSub(liquidity, product);
            return FullMath.MulDivRoundingUp(numerator, Q96, denominator);
        }
    }

    /// <summary>
    /// Computes the next sqrt price given a token1 input/output amount.
    /// </summary>
    /// <param name="sqrtPX96">The starting sqrt price (Q64.96).</param>
    /// <param name="liquidity">The available liquidity.</param>
    /// <param name="amount">The token1 amount being swapped.</param>
    /// <param name="add">True if adding token1 (buying token0), false if removing.</param>
    /// <returns>The resulting sqrt price after the swap.</returns>
    public static UInt256 GetNextSqrtPriceFromAmount1(
        UInt256 sqrtPX96, UInt256 liquidity, UInt256 amount, bool add)
    {
        if (amount.IsZero) return sqrtPX96;
        if (liquidity.IsZero) throw new DivideByZeroException("SqrtPriceMath: zero liquidity");

        // When adding token1: price goes up.
        // nextSqrtP = sqrtP + amount * Q96 / liquidity
        // When removing token1: price goes down.
        // nextSqrtP = sqrtP - amount * Q96 / liquidity

        if (add)
        {
            var quotient = FullMath.MulDiv(amount, Q96, liquidity);
            return UInt256.CheckedAdd(sqrtPX96, quotient);
        }
        else
        {
            var quotient = FullMath.MulDivRoundingUp(amount, Q96, liquidity);
            if (quotient >= sqrtPX96)
                throw new OverflowException("SqrtPriceMath: amount exceeds available for token1");
            return UInt256.CheckedSub(sqrtPX96, quotient);
        }
    }

    /// <summary>
    /// Determines the next sqrt price from either token0 or token1 input amount,
    /// based on which token is being swapped in (zero-for-one direction).
    /// </summary>
    /// <param name="sqrtPX96">Current sqrt price (Q64.96).</param>
    /// <param name="liquidity">Available liquidity.</param>
    /// <param name="amountIn">Amount of input token.</param>
    /// <param name="zeroForOne">True if swapping token0 for token1 (price decreases).</param>
    /// <returns>The next sqrt price.</returns>
    public static UInt256 GetNextSqrtPriceFromInput(
        UInt256 sqrtPX96, UInt256 liquidity, UInt256 amountIn, bool zeroForOne)
    {
        return zeroForOne
            ? GetNextSqrtPriceFromAmount0(sqrtPX96, liquidity, amountIn, add: true)
            : GetNextSqrtPriceFromAmount1(sqrtPX96, liquidity, amountIn, add: true);
    }

    /// <summary>
    /// Determines the next sqrt price from either token0 or token1 output amount.
    /// </summary>
    /// <param name="sqrtPX96">Current sqrt price (Q64.96).</param>
    /// <param name="liquidity">Available liquidity.</param>
    /// <param name="amountOut">Amount of output token.</param>
    /// <param name="zeroForOne">True if swapping token0 for token1 (price decreases).</param>
    /// <returns>The next sqrt price.</returns>
    public static UInt256 GetNextSqrtPriceFromOutput(
        UInt256 sqrtPX96, UInt256 liquidity, UInt256 amountOut, bool zeroForOne)
    {
        return zeroForOne
            ? GetNextSqrtPriceFromAmount1(sqrtPX96, liquidity, amountOut, add: false)
            : GetNextSqrtPriceFromAmount0(sqrtPX96, liquidity, amountOut, add: false);
    }
}
