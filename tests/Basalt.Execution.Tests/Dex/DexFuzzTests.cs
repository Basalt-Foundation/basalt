using System.Numerics;
using Basalt.Core;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Property-based fuzz tests for DEX math primitives and settlement logic.
/// Uses seeded randomization for reproducibility. Each test runs many iterations
/// with random inputs and verifies algebraic invariants.
/// </summary>
public class DexFuzzTests
{
    private const int Iterations = 500;

    // Seeded RNG for reproducibility — change seed to explore new input space
    private static Random MakeRng(int seed = 42) => new(seed);

    private static UInt256 RandUInt256(Random rng, ulong maxLo = ulong.MaxValue)
    {
        var lo = (ulong)(rng.NextDouble() * maxLo);
        if (lo == 0) lo = 1;
        return new UInt256(lo);
    }

    private static UInt256 RandUInt256InRange(Random rng, ulong min, ulong max)
    {
        var val = min + (ulong)(rng.NextDouble() * (max - min));
        return new UInt256(val);
    }

    // ────────── FullMath.MulDiv ──────────

    [Fact]
    public void MulDiv_MatchesBigInteger_RandomInputs()
    {
        var rng = MakeRng(1);
        for (int i = 0; i < Iterations; i++)
        {
            var a = RandUInt256(rng);
            var b = RandUInt256(rng);
            var d = RandUInt256(rng);

            var expected = FullMath.ToBig(a) * FullMath.ToBig(b) / FullMath.ToBig(d);
            if (expected > BigInteger.Parse("115792089237316195423570985008687907853269984665640564039457584007913129639935"))
                continue; // Skip overflow cases

            var result = FullMath.MulDiv(a, b, d);
            FullMath.ToBig(result).Should().Be(expected, $"iteration {i}: MulDiv({a}, {b}, {d})");
        }
    }

    [Fact]
    public void MulDivRoundingUp_AlwaysGteFloor_RandomInputs()
    {
        var rng = MakeRng(2);
        for (int i = 0; i < Iterations; i++)
        {
            var a = RandUInt256(rng);
            var b = RandUInt256(rng);
            var d = RandUInt256(rng);

            UInt256 floor, ceil;
            try
            {
                floor = FullMath.MulDiv(a, b, d);
                ceil = FullMath.MulDivRoundingUp(a, b, d);
            }
            catch (OverflowException)
            {
                continue;
            }

            ceil.Should().BeGreaterThanOrEqualTo(floor, $"iteration {i}");

            // Ceil should be at most floor + 1
            var diff = ceil - floor;
            diff.Should().BeLessThanOrEqualTo(UInt256.One, $"iteration {i}: ceil - floor > 1");
        }
    }

    [Fact]
    public void MulDivRoundingUp_ExactDivision_EqualsMulDiv()
    {
        var rng = MakeRng(3);
        for (int i = 0; i < Iterations; i++)
        {
            var a = RandUInt256(rng, 1_000_000);
            var d = RandUInt256(rng, 1_000_000);

            // b = d * k for some k → a * b / d = a * k (exact)
            var k = RandUInt256(rng, 1_000_000);
            var b = UInt256.CheckedMul(d, k);

            var floor = FullMath.MulDiv(a, b, d);
            var ceil = FullMath.MulDivRoundingUp(a, b, d);
            ceil.Should().Be(floor, $"iteration {i}: exact division should not round up");
        }
    }

    // ────────── FullMath.Sqrt ──────────

    [Fact]
    public void Sqrt_SquaredResult_NeverExceedsInput()
    {
        var rng = MakeRng(4);
        for (int i = 0; i < Iterations; i++)
        {
            var n = RandUInt256(rng);
            var root = FullMath.Sqrt(n);

            // root^2 <= n
            var rootBig = FullMath.ToBig(root);
            var nBig = FullMath.ToBig(n);
            (rootBig * rootBig).Should().BeLessThanOrEqualTo(nBig, $"iteration {i}");

            // (root+1)^2 > n
            var rootPlus1 = rootBig + BigInteger.One;
            (rootPlus1 * rootPlus1).Should().BeGreaterThan(nBig, $"iteration {i}");
        }
    }

    [Fact]
    public void Sqrt_PerfectSquares_ExactResult()
    {
        var rng = MakeRng(5);
        for (int i = 0; i < Iterations; i++)
        {
            var root = RandUInt256(rng, 10_000_000_000);
            var square = UInt256.CheckedMul(root, root);
            var computed = FullMath.Sqrt(square);
            computed.Should().Be(root, $"iteration {i}: sqrt({root}^2) should be {root}");
        }
    }

    // ────────── DexLibrary.GetAmountOut ──────────

    [Fact]
    public void GetAmountOut_PreservesConstantProduct_RandomInputs()
    {
        var rng = MakeRng(6);
        var feeTiers = DexLibrary.AllowedFeeTiers;

        for (int i = 0; i < Iterations; i++)
        {
            var reserveIn = RandUInt256InRange(rng, 10_000, 10_000_000_000);
            var reserveOut = RandUInt256InRange(rng, 10_000, 10_000_000_000);
            var amountIn = RandUInt256InRange(rng, 1, (ulong)reserveOut.Lo / 2);
            var feeBps = feeTiers[rng.Next(feeTiers.Length)];

            var amountOut = DexLibrary.GetAmountOut(amountIn, reserveIn, reserveOut, feeBps);

            // amountOut < reserveOut (can't drain the pool)
            amountOut.Should().BeLessThan(reserveOut, $"iteration {i}");

            // Constant product invariant: newK >= oldK
            var oldK = FullMath.ToBig(reserveIn) * FullMath.ToBig(reserveOut);
            var newK = (FullMath.ToBig(reserveIn) + FullMath.ToBig(amountIn))
                     * (FullMath.ToBig(reserveOut) - FullMath.ToBig(amountOut));
            newK.Should().BeGreaterThanOrEqualTo(oldK, $"iteration {i}: k must not decrease");
        }
    }

    [Fact]
    public void GetAmountOut_MonotonicallyIncreasing_WithLargerInput()
    {
        var rng = MakeRng(7);

        for (int i = 0; i < Iterations; i++)
        {
            var reserveIn = RandUInt256InRange(rng, 100_000, 10_000_000_000);
            var reserveOut = RandUInt256InRange(rng, 100_000, 10_000_000_000);
            var amount1 = RandUInt256InRange(rng, 1, 1_000_000);
            var amount2 = UInt256.CheckedAdd(amount1, RandUInt256InRange(rng, 1, 1_000_000));

            var out1 = DexLibrary.GetAmountOut(amount1, reserveIn, reserveOut, 30);
            var out2 = DexLibrary.GetAmountOut(amount2, reserveIn, reserveOut, 30);

            out2.Should().BeGreaterThanOrEqualTo(out1,
                $"iteration {i}: larger input should produce larger output");
        }
    }

    // ────────── DexLibrary.GetAmountIn ──────────

    [Fact]
    public void GetAmountIn_RoundTrip_OutputAlwaysSufficient()
    {
        var rng = MakeRng(8);

        for (int i = 0; i < Iterations; i++)
        {
            var reserveIn = RandUInt256InRange(rng, 100_000, 10_000_000_000);
            var reserveOut = RandUInt256InRange(rng, 100_000, 10_000_000_000);
            var desiredOut = RandUInt256InRange(rng, 1, (ulong)reserveOut.Lo / 2);
            var feeBps = DexLibrary.AllowedFeeTiers[rng.Next(DexLibrary.AllowedFeeTiers.Length)];

            var requiredIn = DexLibrary.GetAmountIn(desiredOut, reserveIn, reserveOut, feeBps);
            var actualOut = DexLibrary.GetAmountOut(requiredIn, reserveIn, reserveOut, feeBps);

            // After fix C-2: actual output >= desired output (MulDivRoundingUp rounds correctly)
            actualOut.Should().BeGreaterThanOrEqualTo(desiredOut,
                $"iteration {i}: GetAmountIn must produce enough input for desired output");
        }
    }

    [Fact]
    public void GetAmountIn_NoOverchargeOnExactDivision()
    {
        // C-2 regression: verify no spurious +1 when division is exact.
        // Zero fee with carefully chosen amounts that produce exact division.
        var rng = MakeRng(9);

        for (int i = 0; i < Iterations; i++)
        {
            var reserveIn = RandUInt256InRange(rng, 10_000, 1_000_000);
            var reserveOut = RandUInt256InRange(rng, 10_000, 1_000_000);

            // With 0 fee: amountIn = reserveIn * amountOut / (reserveOut - amountOut)
            // Choose amountOut that divides evenly:
            // desiredOut = reserveOut / k for some k
            var k = RandUInt256InRange(rng, 3, 20);
            var desiredOut = reserveOut / k;
            if (desiredOut.IsZero || desiredOut >= reserveOut) continue;

            var requiredIn = DexLibrary.GetAmountIn(desiredOut, reserveIn, reserveOut, 0);

            // Verify the round-trip: the required input should give at least the desired output
            var actualOut = DexLibrary.GetAmountOut(requiredIn, reserveIn, reserveOut, 0);
            actualOut.Should().BeGreaterThanOrEqualTo(desiredOut, $"iteration {i}");

            // Check that GetAmountIn doesn't overcharge by more than 1 unit
            if (requiredIn > UInt256.One)
            {
                var outWithLess = DexLibrary.GetAmountOut(requiredIn - UInt256.One, reserveIn, reserveOut, 0);
                // If reducing input by 1 still gives >= desiredOut, then GetAmountIn overcharged
                if (outWithLess >= desiredOut)
                {
                    // This would be a regression of C-2
                    Assert.Fail($"iteration {i}: GetAmountIn overcharges — reducing input by 1 still sufficient");
                }
            }
        }
    }

    // ────────── DexLibrary.Quote ──────────

    [Fact]
    public void Quote_Proportional_RandomInputs()
    {
        var rng = MakeRng(10);

        for (int i = 0; i < Iterations; i++)
        {
            var reserveA = RandUInt256InRange(rng, 1000, 10_000_000_000);
            var reserveB = RandUInt256InRange(rng, 1000, 10_000_000_000);
            var amountA = RandUInt256InRange(rng, 1, 1_000_000);

            var quotedB = DexLibrary.Quote(amountA, reserveA, reserveB);

            // quote(A) * reserveA should approximately equal amountA * reserveB
            var lhs = FullMath.ToBig(quotedB) * FullMath.ToBig(reserveA);
            var rhs = FullMath.ToBig(amountA) * FullMath.ToBig(reserveB);

            // Floor division means lhs <= rhs, and lhs + reserveA > rhs
            lhs.Should().BeLessThanOrEqualTo(rhs, $"iteration {i}");
            (lhs + FullMath.ToBig(reserveA)).Should().BeGreaterThan(rhs, $"iteration {i}");
        }
    }

    // ────────── DexLibrary.ComputeInitialLiquidity ──────────

    [Fact]
    public void ComputeInitialLiquidity_GeometricMeanInvariant()
    {
        var rng = MakeRng(11);

        for (int i = 0; i < Iterations; i++)
        {
            var amount0 = RandUInt256InRange(rng, 2000, 10_000_000);
            var amount1 = RandUInt256InRange(rng, 2000, 10_000_000);

            var shares = DexLibrary.ComputeInitialLiquidity(amount0, amount1);
            var totalSupply = UInt256.CheckedAdd(shares, DexLibrary.MinimumLiquidity);

            // totalSupply^2 should be close to amount0 * amount1
            var supplyBig = FullMath.ToBig(totalSupply);
            var productBig = FullMath.ToBig(amount0) * FullMath.ToBig(amount1);

            // Floor sqrt means totalSupply^2 <= product < (totalSupply+1)^2
            (supplyBig * supplyBig).Should().BeLessThanOrEqualTo(productBig, $"iteration {i}");
            ((supplyBig + 1) * (supplyBig + 1)).Should().BeGreaterThan(productBig, $"iteration {i}");
        }
    }

    // ────────── SqrtPriceMath ──────────

    [Fact]
    public void SqrtPriceMath_Amount0Delta_Symmetry()
    {
        var rng = MakeRng(12);

        for (int i = 0; i < Iterations; i++)
        {
            // Pick two random ticks in valid range
            var tickA = rng.Next(-50_000, 50_000);
            var tickB = rng.Next(-50_000, 50_000);
            if (tickA == tickB) continue;

            var sqrtA = TickMath.GetSqrtRatioAtTick(tickA);
            var sqrtB = TickMath.GetSqrtRatioAtTick(tickB);
            var liquidity = RandUInt256InRange(rng, 1_000, 1_000_000_000);

            // GetAmount0Delta(a, b) should equal GetAmount0Delta(b, a) — order invariant
            var delta1 = SqrtPriceMath.GetAmount0Delta(sqrtA, sqrtB, liquidity, roundUp: false);
            var delta2 = SqrtPriceMath.GetAmount0Delta(sqrtB, sqrtA, liquidity, roundUp: false);

            delta1.Should().Be(delta2, $"iteration {i}: order should not matter");
        }
    }

    [Fact]
    public void SqrtPriceMath_Amount1Delta_Symmetry()
    {
        var rng = MakeRng(13);

        for (int i = 0; i < Iterations; i++)
        {
            var tickA = rng.Next(-50_000, 50_000);
            var tickB = rng.Next(-50_000, 50_000);
            if (tickA == tickB) continue;

            var sqrtA = TickMath.GetSqrtRatioAtTick(tickA);
            var sqrtB = TickMath.GetSqrtRatioAtTick(tickB);
            var liquidity = RandUInt256InRange(rng, 1_000, 1_000_000_000);

            var delta1 = SqrtPriceMath.GetAmount1Delta(sqrtA, sqrtB, liquidity, roundUp: false);
            var delta2 = SqrtPriceMath.GetAmount1Delta(sqrtB, sqrtA, liquidity, roundUp: false);

            delta1.Should().Be(delta2, $"iteration {i}: order should not matter");
        }
    }

    [Fact]
    public void SqrtPriceMath_RoundUpAlwaysGteRoundDown()
    {
        var rng = MakeRng(14);

        for (int i = 0; i < Iterations; i++)
        {
            var tickA = rng.Next(-50_000, 50_000);
            var tickB = rng.Next(-50_000, 50_000);
            if (tickA == tickB) continue;

            var sqrtA = TickMath.GetSqrtRatioAtTick(tickA);
            var sqrtB = TickMath.GetSqrtRatioAtTick(tickB);
            var liquidity = RandUInt256InRange(rng, 1_000, 1_000_000_000);

            var d0 = SqrtPriceMath.GetAmount0Delta(sqrtA, sqrtB, liquidity, roundUp: false);
            var u0 = SqrtPriceMath.GetAmount0Delta(sqrtA, sqrtB, liquidity, roundUp: true);
            u0.Should().BeGreaterThanOrEqualTo(d0, $"iteration {i}: roundUp >= roundDown for amount0");

            var d1 = SqrtPriceMath.GetAmount1Delta(sqrtA, sqrtB, liquidity, roundUp: false);
            var u1 = SqrtPriceMath.GetAmount1Delta(sqrtA, sqrtB, liquidity, roundUp: true);
            u1.Should().BeGreaterThanOrEqualTo(d1, $"iteration {i}: roundUp >= roundDown for amount1");
        }
    }

    // ────────── BatchAuctionSolver.ComputeSpotPrice ──────────

    [Fact]
    public void SpotPrice_Proportional_ToReserveRatio()
    {
        var rng = MakeRng(15);

        for (int i = 0; i < Iterations; i++)
        {
            var reserve0 = RandUInt256InRange(rng, 10_000, 10_000_000_000);
            var reserve1 = RandUInt256InRange(rng, 10_000, 10_000_000_000);

            var price = BatchAuctionSolver.ComputeSpotPrice(reserve0, reserve1);

            // price = reserve1 * PriceScale / reserve0
            // Doubling reserve1 should double the price
            var doubled = UInt256.CheckedMul(reserve1, new UInt256(2));
            var doublePrice = BatchAuctionSolver.ComputeSpotPrice(reserve0, doubled);

            // Allow rounding tolerance of 1
            var expectedDouble = UInt256.CheckedMul(price, new UInt256(2));
            var diff = doublePrice > expectedDouble
                ? doublePrice - expectedDouble
                : expectedDouble - doublePrice;
            diff.Should().BeLessThanOrEqualTo(new UInt256(2), $"iteration {i}: doubling reserve1 should double price");
        }
    }

    [Fact]
    public void SpotPrice_InverseRelation()
    {
        var rng = MakeRng(16);

        for (int i = 0; i < Iterations; i++)
        {
            var reserve0 = RandUInt256InRange(rng, 10_000, 1_000_000_000);
            var reserve1 = RandUInt256InRange(rng, 10_000, 1_000_000_000);

            var priceAB = BatchAuctionSolver.ComputeSpotPrice(reserve0, reserve1);
            var priceBA = BatchAuctionSolver.ComputeSpotPrice(reserve1, reserve0);

            if (priceAB.IsZero || priceBA.IsZero) continue;

            // priceAB * priceBA ≈ PriceScale^2
            var product = FullMath.ToBig(priceAB) * FullMath.ToBig(priceBA);
            var expected = FullMath.ToBig(BatchAuctionSolver.PriceScale)
                         * FullMath.ToBig(BatchAuctionSolver.PriceScale);

            // Allow 0.1% tolerance for rounding
            var tolerance = expected / 1000;
            var diff = product > expected ? product - expected : expected - product;
            diff.Should().BeLessThanOrEqualTo(tolerance,
                $"iteration {i}: price(A/B) * price(B/A) should ≈ PriceScale^2");
        }
    }

    // ────────── Settlement Invariants ──────────

    [Fact]
    public void Settlement_ClearingPrice_InValidRange()
    {
        var rng = MakeRng(17);

        for (int i = 0; i < 100; i++)
        {
            var reserve0 = RandUInt256InRange(rng, 1_000_000, 100_000_000);
            var reserve1 = RandUInt256InRange(rng, 1_000_000, 100_000_000);

            var spotPrice = BatchAuctionSolver.ComputeSpotPrice(reserve0, reserve1);
            if (spotPrice.IsZero) continue;

            // Create opposing intents that bracket the spot price
            var buyLimit = UInt256.CheckedAdd(spotPrice, spotPrice / new UInt256(10));
            var sellLimit = spotPrice > spotPrice / new UInt256(10)
                ? spotPrice - spotPrice / new UInt256(10)
                : UInt256.One;

            var buyAmount = RandUInt256InRange(rng, 1000, 100_000);
            var sellAmount = RandUInt256InRange(rng, 1000, 100_000);

            var buyers = new List<ParsedIntent>
            {
                new()
                {
                    Sender = MakeAddress(0x01),
                    TokenIn = MakeAddress(0xBB),
                    TokenOut = MakeAddress(0xAA),
                    AmountIn = buyAmount,
                    MinAmountOut = FullMath.MulDiv(buyAmount, BatchAuctionSolver.PriceScale, buyLimit),
                    TxHash = Hash256.Zero,
                }
            };

            var sellers = new List<ParsedIntent>
            {
                new()
                {
                    Sender = MakeAddress(0x02),
                    TokenIn = MakeAddress(0xAA),
                    TokenOut = MakeAddress(0xBB),
                    AmountIn = sellAmount,
                    MinAmountOut = FullMath.MulDiv(sellAmount, sellLimit, BatchAuctionSolver.PriceScale),
                    TxHash = Hash256.Zero,
                }
            };

            var reserves = new PoolReserves
            {
                Reserve0 = reserve0,
                Reserve1 = reserve1,
                TotalSupply = new UInt256(1_000_000),
                KLast = UInt256.Zero,
            };

            var result = BatchAuctionSolver.ComputeSettlement(
                buyers, sellers, [], [], reserves, 30, 0);

            if (result == null) continue;

            // Clearing price should be non-zero
            result.ClearingPrice.Should().BeGreaterThan(UInt256.Zero, $"iteration {i}");

            // All fills should have non-zero amounts
            foreach (var fill in result.Fills)
            {
                fill.AmountIn.Should().BeGreaterThan(UInt256.Zero, $"iteration {i}: fill input");
                fill.AmountOut.Should().BeGreaterThan(UInt256.Zero, $"iteration {i}: fill output");
            }
        }
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }
}
