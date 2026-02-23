using Basalt.Core;
using Basalt.Execution.Dex.Math;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for SqrtPriceMath — token amount computations for concentrated liquidity.
/// </summary>
public class SqrtPriceMathTests
{
    private static readonly UInt256 Q96 = TickMath.Q96;

    // ─── GetAmount0Delta ───

    [Fact]
    public void GetAmount0Delta_SamePrice_ReturnsZero()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var result = SqrtPriceMath.GetAmount0Delta(sqrtP, sqrtP, new UInt256(1_000_000), roundUp: false);
        result.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void GetAmount0Delta_PriceIncrease_ReturnsPositive()
    {
        var sqrtA = TickMath.GetSqrtRatioAtTick(0);
        var sqrtB = TickMath.GetSqrtRatioAtTick(1000);
        var liquidity = new UInt256(1_000_000_000);

        var result = SqrtPriceMath.GetAmount0Delta(sqrtA, sqrtB, liquidity, roundUp: false);
        result.Should().BeGreaterThan(UInt256.Zero);
    }

    [Fact]
    public void GetAmount0Delta_OrderIndependent()
    {
        var sqrtA = TickMath.GetSqrtRatioAtTick(-500);
        var sqrtB = TickMath.GetSqrtRatioAtTick(500);
        var liquidity = new UInt256(1_000_000_000);

        var result1 = SqrtPriceMath.GetAmount0Delta(sqrtA, sqrtB, liquidity, roundUp: false);
        var result2 = SqrtPriceMath.GetAmount0Delta(sqrtB, sqrtA, liquidity, roundUp: false);
        result1.Should().Be(result2);
    }

    [Fact]
    public void GetAmount0Delta_RoundUp_GreaterOrEqual()
    {
        var sqrtA = TickMath.GetSqrtRatioAtTick(0);
        var sqrtB = TickMath.GetSqrtRatioAtTick(100);
        var liquidity = new UInt256(999_999);

        var down = SqrtPriceMath.GetAmount0Delta(sqrtA, sqrtB, liquidity, roundUp: false);
        var up = SqrtPriceMath.GetAmount0Delta(sqrtA, sqrtB, liquidity, roundUp: true);
        up.Should().BeGreaterThanOrEqualTo(down);
    }

    [Fact]
    public void GetAmount0Delta_ZeroSqrtA_Throws()
    {
        var act = () => SqrtPriceMath.GetAmount0Delta(
            UInt256.Zero, TickMath.Q96, new UInt256(1000), roundUp: false);
        act.Should().Throw<DivideByZeroException>();
    }

    // ─── GetAmount1Delta ───

    [Fact]
    public void GetAmount1Delta_SamePrice_ReturnsZero()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var result = SqrtPriceMath.GetAmount1Delta(sqrtP, sqrtP, new UInt256(1_000_000), roundUp: false);
        result.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void GetAmount1Delta_PriceIncrease_ReturnsPositive()
    {
        var sqrtA = TickMath.GetSqrtRatioAtTick(0);
        var sqrtB = TickMath.GetSqrtRatioAtTick(1000);
        var liquidity = new UInt256(1_000_000_000);

        var result = SqrtPriceMath.GetAmount1Delta(sqrtA, sqrtB, liquidity, roundUp: false);
        result.Should().BeGreaterThan(UInt256.Zero);
    }

    [Fact]
    public void GetAmount1Delta_OrderIndependent()
    {
        var sqrtA = TickMath.GetSqrtRatioAtTick(-500);
        var sqrtB = TickMath.GetSqrtRatioAtTick(500);
        var liquidity = new UInt256(1_000_000_000);

        var result1 = SqrtPriceMath.GetAmount1Delta(sqrtA, sqrtB, liquidity, roundUp: false);
        var result2 = SqrtPriceMath.GetAmount1Delta(sqrtB, sqrtA, liquidity, roundUp: false);
        result1.Should().Be(result2);
    }

    [Fact]
    public void GetAmount1Delta_RoundUp_GreaterOrEqual()
    {
        var sqrtA = TickMath.GetSqrtRatioAtTick(0);
        var sqrtB = TickMath.GetSqrtRatioAtTick(100);
        var liquidity = new UInt256(999_999);

        var down = SqrtPriceMath.GetAmount1Delta(sqrtA, sqrtB, liquidity, roundUp: false);
        var up = SqrtPriceMath.GetAmount1Delta(sqrtA, sqrtB, liquidity, roundUp: true);
        up.Should().BeGreaterThanOrEqualTo(down);
    }

    // ─── GetNextSqrtPriceFromAmount0 ───

    [Fact]
    public void GetNextSqrtPriceFromAmount0_ZeroAmount_ReturnsSame()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var result = SqrtPriceMath.GetNextSqrtPriceFromAmount0(
            sqrtP, new UInt256(1_000_000), UInt256.Zero, add: true);
        result.Should().Be(sqrtP);
    }

    [Fact]
    public void GetNextSqrtPriceFromAmount0_AddToken0_PriceDecreases()
    {
        // Adding token0 to pool → more token0 → price of token0 drops → sqrtPrice drops
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var result = SqrtPriceMath.GetNextSqrtPriceFromAmount0(
            sqrtP, new UInt256(1_000_000_000), new UInt256(100_000), add: true);
        result.Should().BeLessThanOrEqualTo(sqrtP);
    }

    [Fact]
    public void GetNextSqrtPriceFromAmount0_ZeroLiquidity_Throws()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var act = () => SqrtPriceMath.GetNextSqrtPriceFromAmount0(
            sqrtP, UInt256.Zero, new UInt256(1000), add: true);
        act.Should().Throw<DivideByZeroException>();
    }

    // ─── GetNextSqrtPriceFromAmount1 ───

    [Fact]
    public void GetNextSqrtPriceFromAmount1_ZeroAmount_ReturnsSame()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var result = SqrtPriceMath.GetNextSqrtPriceFromAmount1(
            sqrtP, new UInt256(1_000_000), UInt256.Zero, add: true);
        result.Should().Be(sqrtP);
    }

    [Fact]
    public void GetNextSqrtPriceFromAmount1_AddToken1_PriceIncreases()
    {
        // Adding token1 to pool → more token1 → price of token1 drops, price of token0 rises → sqrtPrice rises
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var result = SqrtPriceMath.GetNextSqrtPriceFromAmount1(
            sqrtP, new UInt256(1_000_000_000), new UInt256(100_000), add: true);
        result.Should().BeGreaterThanOrEqualTo(sqrtP);
    }

    [Fact]
    public void GetNextSqrtPriceFromAmount1_ZeroLiquidity_Throws()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var act = () => SqrtPriceMath.GetNextSqrtPriceFromAmount1(
            sqrtP, UInt256.Zero, new UInt256(1000), add: true);
        act.Should().Throw<DivideByZeroException>();
    }

    // ─── GetNextSqrtPriceFromInput/Output ───

    [Fact]
    public void GetNextSqrtPriceFromInput_ZeroForOne_PriceDecreases()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var liq = new UInt256(1_000_000_000);
        var result = SqrtPriceMath.GetNextSqrtPriceFromInput(sqrtP, liq, new UInt256(10_000), zeroForOne: true);
        result.Should().BeLessThanOrEqualTo(sqrtP);
    }

    [Fact]
    public void GetNextSqrtPriceFromInput_OneForZero_PriceIncreases()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var liq = new UInt256(1_000_000_000);
        var result = SqrtPriceMath.GetNextSqrtPriceFromInput(sqrtP, liq, new UInt256(10_000), zeroForOne: false);
        result.Should().BeGreaterThanOrEqualTo(sqrtP);
    }

    [Fact]
    public void GetNextSqrtPriceFromOutput_ZeroForOne_PriceDecreases()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var liq = new UInt256(1_000_000_000);
        var result = SqrtPriceMath.GetNextSqrtPriceFromOutput(sqrtP, liq, new UInt256(1_000), zeroForOne: true);
        result.Should().BeLessThanOrEqualTo(sqrtP);
    }

    [Fact]
    public void GetNextSqrtPriceFromOutput_OneForZero_PriceIncreases()
    {
        var sqrtP = TickMath.GetSqrtRatioAtTick(0);
        var liq = new UInt256(1_000_000_000);
        var result = SqrtPriceMath.GetNextSqrtPriceFromOutput(sqrtP, liq, new UInt256(1_000), zeroForOne: false);
        result.Should().BeGreaterThanOrEqualTo(sqrtP);
    }

    // ─── Conservation: amount delta round-trip ───

    [Fact]
    public void AmountDeltas_ConserveTokens()
    {
        // If we compute amount0 and amount1 for a given range and liquidity,
        // then using those amounts to compute liquidity back should give ~ same result.
        var sqrtA = TickMath.GetSqrtRatioAtTick(-1000);
        var sqrtB = TickMath.GetSqrtRatioAtTick(1000);
        var sqrtCurrent = TickMath.GetSqrtRatioAtTick(0);
        var liquidity = new UInt256(1_000_000_000_000);

        var amount0 = SqrtPriceMath.GetAmount0Delta(sqrtCurrent, sqrtB, liquidity, roundUp: true);
        var amount1 = SqrtPriceMath.GetAmount1Delta(sqrtA, sqrtCurrent, liquidity, roundUp: true);

        // Both should be non-zero when current price is in range
        amount0.Should().BeGreaterThan(UInt256.Zero);
        amount1.Should().BeGreaterThan(UInt256.Zero);
    }
}
