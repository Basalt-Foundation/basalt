using Basalt.Core;
using Basalt.Execution.Dex.Math;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for TickMath — tick-to-sqrt-price and sqrt-price-to-tick conversions.
/// </summary>
public class TickMathTests
{
    // ─── GetSqrtRatioAtTick ───

    [Fact]
    public void GetSqrtRatioAtTick_Zero_ReturnsQ96()
    {
        // tick 0 = price 1.0 → sqrtPrice = 1.0 * 2^96
        var result = TickMath.GetSqrtRatioAtTick(0);
        result.Should().Be(TickMath.Q96);
    }

    [Fact]
    public void GetSqrtRatioAtTick_MinTick_ReturnsMinSqrtRatio()
    {
        var result = TickMath.GetSqrtRatioAtTick(TickMath.MinTick);
        result.Should().Be(TickMath.MinSqrtRatio);
        // Should be very small (close to zero)
        result.Should().BeLessThan(TickMath.Q96);
    }

    [Fact]
    public void GetSqrtRatioAtTick_MaxTick_ReturnsMaxSqrtRatio()
    {
        var result = TickMath.GetSqrtRatioAtTick(TickMath.MaxTick);
        result.Should().Be(TickMath.MaxSqrtRatio);
        // Should be very large
        result.Should().BeGreaterThan(TickMath.Q96);
    }

    [Fact]
    public void GetSqrtRatioAtTick_PositiveTick_GreaterThanQ96()
    {
        // Positive tick = price > 1.0 → sqrtPrice > Q96
        var result = TickMath.GetSqrtRatioAtTick(100);
        result.Should().BeGreaterThan(TickMath.Q96);
    }

    [Fact]
    public void GetSqrtRatioAtTick_NegativeTick_LessThanQ96()
    {
        // Negative tick = price < 1.0 → sqrtPrice < Q96
        var result = TickMath.GetSqrtRatioAtTick(-100);
        result.Should().BeLessThan(TickMath.Q96);
    }

    [Fact]
    public void GetSqrtRatioAtTick_Monotonic()
    {
        // sqrtPrice should increase with tick
        var prev = TickMath.GetSqrtRatioAtTick(-1000);
        for (int tick = -999; tick <= 1000; tick += 100)
        {
            var curr = TickMath.GetSqrtRatioAtTick(tick);
            curr.Should().BeGreaterThan(prev, $"tick {tick} should give higher sqrtPrice than {tick - 100}");
            prev = curr;
        }
    }

    [Fact]
    public void GetSqrtRatioAtTick_SymmetricAroundZero()
    {
        // price(tick) * price(-tick) = 1.0001^tick * 1.0001^(-tick) = 1
        // So sqrt(tick) * sqrt(-tick) = Q96^2 / Q96 = Q96 (approximately)
        var pos = TickMath.GetSqrtRatioAtTick(1000);
        var neg = TickMath.GetSqrtRatioAtTick(-1000);

        // pos * neg / Q96 should be approximately Q96
        var product = FullMath.MulDiv(pos, neg, TickMath.Q96);
        // Allow 0.01% tolerance due to fixed-point rounding
        var diff = product > TickMath.Q96 ? product - TickMath.Q96 : TickMath.Q96 - product;
        var tolerance = TickMath.Q96 / new UInt256(10_000); // 0.01%
        diff.Should().BeLessThanOrEqualTo(tolerance);
    }

    [Fact]
    public void GetSqrtRatioAtTick_OutOfRange_Throws()
    {
        var act1 = () => TickMath.GetSqrtRatioAtTick(TickMath.MinTick - 1);
        act1.Should().Throw<ArgumentOutOfRangeException>();

        var act2 = () => TickMath.GetSqrtRatioAtTick(TickMath.MaxTick + 1);
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── GetTickAtSqrtRatio ───

    [Fact]
    public void GetTickAtSqrtRatio_Q96_ReturnsZero()
    {
        var result = TickMath.GetTickAtSqrtRatio(TickMath.Q96);
        result.Should().Be(0);
    }

    [Fact]
    public void GetTickAtSqrtRatio_MinSqrtRatio_ReturnsMinTick()
    {
        var result = TickMath.GetTickAtSqrtRatio(TickMath.MinSqrtRatio);
        result.Should().Be(TickMath.MinTick);
    }

    [Fact]
    public void GetTickAtSqrtRatio_MaxSqrtRatio_ReturnsMaxTick()
    {
        var result = TickMath.GetTickAtSqrtRatio(TickMath.MaxSqrtRatio);
        result.Should().Be(TickMath.MaxTick);
    }

    [Fact]
    public void GetTickAtSqrtRatio_OutOfRange_Throws()
    {
        var belowMin = TickMath.MinSqrtRatio - UInt256.One;
        var act1 = () => TickMath.GetTickAtSqrtRatio(belowMin);
        act1.Should().Throw<ArgumentOutOfRangeException>();

        var aboveMax = TickMath.MaxSqrtRatio + UInt256.One;
        var act2 = () => TickMath.GetTickAtSqrtRatio(aboveMax);
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ─── Round-trip consistency ───

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(-100)]
    [InlineData(10000)]
    [InlineData(-10000)]
    [InlineData(887272)]
    [InlineData(-887272)]
    public void RoundTrip_TickToSqrtToTick(int tick)
    {
        var sqrtPrice = TickMath.GetSqrtRatioAtTick(tick);
        var recovered = TickMath.GetTickAtSqrtRatio(sqrtPrice);
        recovered.Should().Be(tick);
    }

    [Fact]
    public void GetTickAtSqrtRatio_FloorProperty()
    {
        // For a sqrtPrice between two ticks, GetTickAtSqrtRatio should return the lower tick
        var sqrtAt100 = TickMath.GetSqrtRatioAtTick(100);
        var sqrtAt101 = TickMath.GetSqrtRatioAtTick(101);

        // Midpoint between tick 100 and 101
        var mid = (sqrtAt100 + sqrtAt101) / new UInt256(2);
        var tick = TickMath.GetTickAtSqrtRatio(mid);
        tick.Should().Be(100); // Floor
    }
}
