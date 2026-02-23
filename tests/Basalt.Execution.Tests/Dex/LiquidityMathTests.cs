using Basalt.Core;
using Basalt.Execution.Dex.Math;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for LiquidityMath — signed delta arithmetic and liquidity computation.
/// </summary>
public class LiquidityMathTests
{
    // ─── AddDelta ───

    [Fact]
    public void AddDelta_PositiveDelta_Adds()
    {
        var result = LiquidityMath.AddDelta(new UInt256(1000), 500);
        result.Should().Be(new UInt256(1500));
    }

    [Fact]
    public void AddDelta_NegativeDelta_Subtracts()
    {
        var result = LiquidityMath.AddDelta(new UInt256(1000), -500);
        result.Should().Be(new UInt256(500));
    }

    [Fact]
    public void AddDelta_ZeroDelta_NoChange()
    {
        var result = LiquidityMath.AddDelta(new UInt256(1000), 0);
        result.Should().Be(new UInt256(1000));
    }

    [Fact]
    public void AddDelta_SubtractAll_ReturnsZero()
    {
        var result = LiquidityMath.AddDelta(new UInt256(1000), -1000);
        result.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void AddDelta_Underflow_Throws()
    {
        var act = () => LiquidityMath.AddDelta(new UInt256(500), -1000);
        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void AddDelta_FromZero_PositiveDelta()
    {
        var result = LiquidityMath.AddDelta(UInt256.Zero, 1000);
        result.Should().Be(new UInt256(1000));
    }

    [Fact]
    public void AddDelta_FromZero_NegativeDelta_Throws()
    {
        var act = () => LiquidityMath.AddDelta(UInt256.Zero, -1);
        act.Should().Throw<OverflowException>();
    }

    // ─── GetLiquidityForAmounts ───

    [Fact]
    public void GetLiquidityForAmounts_PriceBelowRange_UsesOnlyToken0()
    {
        // Current price is below the position range → only token0 needed
        var sqrtCurrent = TickMath.GetSqrtRatioAtTick(-2000);
        var sqrtA = TickMath.GetSqrtRatioAtTick(-1000);
        var sqrtB = TickMath.GetSqrtRatioAtTick(1000);

        var amount0 = new UInt256(1_000_000);
        var amount1 = new UInt256(1_000_000);

        var liq = LiquidityMath.GetLiquidityForAmounts(sqrtCurrent, sqrtA, sqrtB, amount0, amount1);
        liq.Should().BeGreaterThan(UInt256.Zero);

        // Should only depend on token0 — changing amount1 should not affect liquidity
        var liqDiffAmount1 = LiquidityMath.GetLiquidityForAmounts(
            sqrtCurrent, sqrtA, sqrtB, amount0, new UInt256(999));
        liqDiffAmount1.Should().Be(liq);
    }

    [Fact]
    public void GetLiquidityForAmounts_PriceAboveRange_UsesOnlyToken1()
    {
        // Current price is above the position range → only token1 needed
        var sqrtCurrent = TickMath.GetSqrtRatioAtTick(2000);
        var sqrtA = TickMath.GetSqrtRatioAtTick(-1000);
        var sqrtB = TickMath.GetSqrtRatioAtTick(1000);

        var amount0 = new UInt256(1_000_000);
        var amount1 = new UInt256(1_000_000);

        var liq = LiquidityMath.GetLiquidityForAmounts(sqrtCurrent, sqrtA, sqrtB, amount0, amount1);
        liq.Should().BeGreaterThan(UInt256.Zero);

        // Should only depend on token1 — changing amount0 should not affect liquidity
        var liqDiffAmount0 = LiquidityMath.GetLiquidityForAmounts(
            sqrtCurrent, sqrtA, sqrtB, new UInt256(999), amount1);
        liqDiffAmount0.Should().Be(liq);
    }

    [Fact]
    public void GetLiquidityForAmounts_PriceInRange_UsesBothTokens()
    {
        // Current price is within the position range → both tokens needed
        var sqrtCurrent = TickMath.GetSqrtRatioAtTick(0);
        var sqrtA = TickMath.GetSqrtRatioAtTick(-1000);
        var sqrtB = TickMath.GetSqrtRatioAtTick(1000);

        var liq = LiquidityMath.GetLiquidityForAmounts(
            sqrtCurrent, sqrtA, sqrtB, new UInt256(1_000_000), new UInt256(1_000_000));
        liq.Should().BeGreaterThan(UInt256.Zero);
    }

    [Fact]
    public void GetLiquidityForAmounts_ZeroAmounts_ReturnsZero()
    {
        var sqrtCurrent = TickMath.GetSqrtRatioAtTick(0);
        var sqrtA = TickMath.GetSqrtRatioAtTick(-1000);
        var sqrtB = TickMath.GetSqrtRatioAtTick(1000);

        var liq = LiquidityMath.GetLiquidityForAmounts(
            sqrtCurrent, sqrtA, sqrtB, UInt256.Zero, UInt256.Zero);
        liq.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void GetLiquidityForAmounts_SwappedBounds_SameResult()
    {
        var sqrtCurrent = TickMath.GetSqrtRatioAtTick(0);
        var sqrtA = TickMath.GetSqrtRatioAtTick(-1000);
        var sqrtB = TickMath.GetSqrtRatioAtTick(1000);
        var amount0 = new UInt256(1_000_000);
        var amount1 = new UInt256(1_000_000);

        var liq1 = LiquidityMath.GetLiquidityForAmounts(sqrtCurrent, sqrtA, sqrtB, amount0, amount1);
        var liq2 = LiquidityMath.GetLiquidityForAmounts(sqrtCurrent, sqrtB, sqrtA, amount0, amount1);
        liq1.Should().Be(liq2);
    }

    [Fact]
    public void GetLiquidityForAmounts_MoreTokens_MoreLiquidity()
    {
        var sqrtCurrent = TickMath.GetSqrtRatioAtTick(0);
        var sqrtA = TickMath.GetSqrtRatioAtTick(-1000);
        var sqrtB = TickMath.GetSqrtRatioAtTick(1000);

        var liqSmall = LiquidityMath.GetLiquidityForAmounts(
            sqrtCurrent, sqrtA, sqrtB, new UInt256(1_000), new UInt256(1_000));
        var liqLarge = LiquidityMath.GetLiquidityForAmounts(
            sqrtCurrent, sqrtA, sqrtB, new UInt256(1_000_000), new UInt256(1_000_000));

        liqLarge.Should().BeGreaterThan(liqSmall);
    }
}
