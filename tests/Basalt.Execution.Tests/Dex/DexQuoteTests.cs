using Xunit;
using FluentAssertions;
using Basalt.Core;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;

namespace Basalt.Execution.Tests.Dex;

public sealed class DexQuoteTests
{
    private static readonly Address Token0 = new(new byte[20] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 });
    private static readonly Address Token1 = new(new byte[20] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2 });

    [Fact]
    public void GetAmountOut_KnownReserves_ReturnsExpectedOutput()
    {
        // Pool: 1,000,000 / 2,000,000 reserves, 30 bps fee
        var reserveIn = new UInt256(1_000_000);
        var reserveOut = new UInt256(2_000_000);
        var amountIn = new UInt256(10_000);

        var amountOut = DexLibrary.GetAmountOut(amountIn, reserveIn, reserveOut, 30);

        // Expected: amountIn * 9970 * reserveOut / (reserveIn * 10000 + amountIn * 9970)
        // = 10000 * 9970 * 2000000 / (1000000 * 10000 + 10000 * 9970)
        // = 199,400,000,000 / 10,099,700,000 ≈ 19,743
        amountOut.Should().BeGreaterThan(UInt256.Zero);
        amountOut.Should().BeLessThan(new UInt256(20_000)); // Less than ideal due to price impact + fee
        amountOut.Should().BeGreaterThan(new UInt256(19_000)); // But close to 2x (price ratio)
    }

    [Fact]
    public void GetAmountOut_LargeSwap_HasSignificantPriceImpact()
    {
        var reserveIn = new UInt256(1_000_000);
        var reserveOut = new UInt256(1_000_000);

        var smallSwap = DexLibrary.GetAmountOut(new UInt256(1_000), reserveIn, reserveOut, 30);
        var largeSwap = DexLibrary.GetAmountOut(new UInt256(500_000), reserveIn, reserveOut, 30);

        // Small swap should get close to 1:1
        smallSwap.Should().BeGreaterThan(new UInt256(990));

        // Large swap (50% of reserves) should get significantly less than 1:1
        largeSwap.Should().BeLessThan(new UInt256(350_000)); // Far below 500,000
    }

    [Fact]
    public void BestPool_Selection_PicksHighestOutput()
    {
        // Simulate trying multiple fee tiers and picking the best one
        var reserveIn = new UInt256(10_000_000);
        var reserveOut = new UInt256(10_000_000);
        var amountIn = new UInt256(100_000);

        var outputs = new List<(uint fee, UInt256 output)>();

        foreach (var feeBps in DexLibrary.AllowedFeeTiers)
        {
            var output = DexLibrary.GetAmountOut(amountIn, reserveIn, reserveOut, feeBps);
            outputs.Add((feeBps, output));
        }

        // Lower fee tiers should give more output (all else equal)
        var sorted = outputs.OrderByDescending(x => x.output).ToList();
        sorted[0].fee.Should().Be(1); // 1 bps gives the best output
        sorted[^1].fee.Should().Be(100); // 100 bps gives the worst output
    }

    [Fact]
    public void PriceImpact_Calculation_IsCorrect()
    {
        var reserve0 = new UInt256(1_000_000);
        var reserve1 = new UInt256(2_000_000);
        var amountIn = new UInt256(100_000);
        uint feeBps = 30;

        // Spot price = reserve1 / reserve0 * PriceScale
        var spotPrice = BatchAuctionSolver.ComputeSpotPrice(reserve0, reserve1);
        spotPrice.Should().BeGreaterThan(UInt256.Zero);

        // Actual output
        var amountOut = DexLibrary.GetAmountOut(amountIn, reserve0, reserve1, feeBps);

        // Spot-based output (what you'd get at zero price impact, after fee)
        var feeComplement = new UInt256(10_000 - feeBps);
        var feeDenom = new UInt256(10_000);
        var amountInAfterFee = FullMath.MulDiv(amountIn, feeComplement, feeDenom);
        var spotAmountOut = FullMath.MulDiv(amountInAfterFee, spotPrice, BatchAuctionSolver.PriceScale);

        // Price impact = (spotAmountOut - amountOut) / spotAmountOut * 10000
        if (spotAmountOut > amountOut)
        {
            var impactBps = FullMath.MulDiv(spotAmountOut - amountOut, new UInt256(10_000), spotAmountOut);
            var impact = (uint)(ulong)impactBps.Lo;
            impact.Should().BeGreaterThan(0U, "100k into 1M reserves should have measurable price impact");
            impact.Should().BeLessThan(2000U, "10% of reserves shouldn't exceed 20% price impact");
        }
    }

    [Fact]
    public void SpotPrice_Calculation_MatchesReserveRatio()
    {
        var reserve0 = new UInt256(1_000_000);
        var reserve1 = new UInt256(3_000_000);

        var spotPrice = BatchAuctionSolver.ComputeSpotPrice(reserve0, reserve1);

        // spot = reserve1 * PriceScale / reserve0 = 3 * PriceScale
        var expected = FullMath.MulDiv(reserve1, BatchAuctionSolver.PriceScale, reserve0);
        spotPrice.Should().Be(expected);
    }

    [Fact]
    public void DynamicFee_BelowThreshold_ReturnsBaseFee()
    {
        var fee = DynamicFeeCalculator.ComputeDynamicFee(baseFeeBps: 30, volatilityBps: 50);
        fee.Should().Be(30U);
    }

    [Fact]
    public void DynamicFee_AboveThreshold_IncreasesFee()
    {
        var fee = DynamicFeeCalculator.ComputeDynamicFee(baseFeeBps: 30, volatilityBps: 300);
        fee.Should().BeGreaterThan(30U);
        fee.Should().BeLessOrEqualTo(DynamicFeeCalculator.MaxFeeBps);
    }
}
