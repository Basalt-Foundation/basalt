using Basalt.Core;
using Basalt.Execution.Dex.Math;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for the DEX math library (FullMath and DexLibrary).
/// Validates overflow-safe arithmetic, AMM formulas, and LP share calculations.
/// These tests are ported from the Caldera.Core.Tests math suite with added edge cases.
/// </summary>
public class DexMathTests
{
    // ────────── FullMath Tests ──────────

    [Fact]
    public void MulDiv_BasicOperation()
    {
        var result = FullMath.MulDiv(new UInt256(100), new UInt256(200), new UInt256(50));
        result.Should().Be(new UInt256(400));
    }

    [Fact]
    public void MulDiv_LargeNumbers()
    {
        // (2^128 * 2^128) / 2^128 = 2^128
        var large = new UInt256(0, 1); // 2^128
        var result = FullMath.MulDiv(large, large, large);
        result.Should().Be(large);
    }

    [Fact]
    public void MulDiv_ZeroDenominator_Throws()
    {
        var act = () => FullMath.MulDiv(new UInt256(100), new UInt256(200), UInt256.Zero);
        act.Should().Throw<DivideByZeroException>();
    }

    [Fact]
    public void MulDivRoundingUp_RoundsCorrectly()
    {
        // 10 * 3 / 7 = 4.28... → rounds up to 5
        var result = FullMath.MulDivRoundingUp(new UInt256(10), new UInt256(3), new UInt256(7));
        result.Should().Be(new UInt256(5));
    }

    [Fact]
    public void MulDivRoundingUp_ExactDivision_NoRound()
    {
        // 10 * 4 / 5 = 8 exactly
        var result = FullMath.MulDivRoundingUp(new UInt256(10), new UInt256(4), new UInt256(5));
        result.Should().Be(new UInt256(8));
    }

    [Fact]
    public void MulMod_BasicOperation()
    {
        var result = FullMath.MulMod(new UInt256(7), new UInt256(5), new UInt256(6));
        result.Should().Be(new UInt256(5)); // 35 % 6 = 5
    }

    [Fact]
    public void Sqrt_PerfectSquare()
    {
        var result = FullMath.Sqrt(new UInt256(144));
        result.Should().Be(new UInt256(12));
    }

    [Fact]
    public void Sqrt_NonPerfectSquare_Floors()
    {
        // sqrt(10) = 3.16... → floor = 3
        var result = FullMath.Sqrt(new UInt256(10));
        result.Should().Be(new UInt256(3));
    }

    [Fact]
    public void Sqrt_Zero()
    {
        var result = FullMath.Sqrt(UInt256.Zero);
        result.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void Sqrt_One()
    {
        var result = FullMath.Sqrt(UInt256.One);
        result.Should().Be(UInt256.One);
    }

    [Fact]
    public void Sqrt_LargeNumber()
    {
        // sqrt(10^36) = 10^18
        var n = new UInt256(1_000_000_000_000_000_000) * new UInt256(1_000_000_000_000_000_000);
        var result = FullMath.Sqrt(n);
        result.Should().Be(new UInt256(1_000_000_000_000_000_000));
    }

    // ────────── DexLibrary Tests ──────────

    [Fact]
    public void GetAmountOut_StandardSwap()
    {
        // 1000 in, 10000/10000 reserves, 30 bps fee
        var amountOut = DexLibrary.GetAmountOut(
            new UInt256(1000), new UInt256(10000), new UInt256(10000), 30);

        // Expected: (1000 * 9970 * 10000) / (10000 * 10000 + 1000 * 9970) = 99700000 / 109970000 ≈ 906
        amountOut.Should().BeGreaterThan(UInt256.Zero);
        amountOut.Should().BeLessThan(new UInt256(1000)); // Output < input due to fee + price impact
    }

    [Fact]
    public void GetAmountOut_ZeroInput_Throws()
    {
        var act = () => DexLibrary.GetAmountOut(
            UInt256.Zero, new UInt256(10000), new UInt256(10000), 30);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetAmountOut_ZeroReserve_Throws()
    {
        var act = () => DexLibrary.GetAmountOut(
            new UInt256(1000), UInt256.Zero, new UInt256(10000), 30);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetAmountIn_InverseOfGetAmountOut()
    {
        var reserveIn = new UInt256(100_000);
        var reserveOut = new UInt256(100_000);
        var desiredOut = new UInt256(500);

        var requiredIn = DexLibrary.GetAmountIn(desiredOut, reserveIn, reserveOut, 30);

        // Verify: using the required input gives us at least the desired output
        var actualOut = DexLibrary.GetAmountOut(requiredIn, reserveIn, reserveOut, 30);
        actualOut.Should().BeGreaterThanOrEqualTo(desiredOut);
    }

    [Fact]
    public void GetAmountIn_OutputGteReserve_Throws()
    {
        var act = () => DexLibrary.GetAmountIn(
            new UInt256(10000), new UInt256(10000), new UInt256(10000), 30);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Quote_ProportionalAmount()
    {
        // 100 of token A with 1000:2000 reserves → 200 of token B
        var result = DexLibrary.Quote(
            new UInt256(100), new UInt256(1000), new UInt256(2000));
        result.Should().Be(new UInt256(200));
    }

    [Fact]
    public void ComputeInitialLiquidity_GeometricMean()
    {
        // sqrt(1000 * 1000) - 1000 = 1000 - 1000 = 0 → should throw
        var act = () => DexLibrary.ComputeInitialLiquidity(new UInt256(1000), new UInt256(1000));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ComputeInitialLiquidity_ValidDeposit()
    {
        // sqrt(10000 * 10000) - 1000 = 10000 - 1000 = 9000
        var shares = DexLibrary.ComputeInitialLiquidity(new UInt256(10_000), new UInt256(10_000));
        shares.Should().Be(new UInt256(9000));
    }

    [Fact]
    public void ComputeLiquidity_Proportional()
    {
        var shares = DexLibrary.ComputeLiquidity(
            new UInt256(1000), new UInt256(1000),
            new UInt256(10000), new UInt256(10000),
            new UInt256(9000));

        // min(1000 * 9000 / 10000, 1000 * 9000 / 10000) = 900
        shares.Should().Be(new UInt256(900));
    }

    [Fact]
    public void AllowedFeeTiers_Contains_StandardTiers()
    {
        DexLibrary.AllowedFeeTiers.Should().Contain(1u);
        DexLibrary.AllowedFeeTiers.Should().Contain(5u);
        DexLibrary.AllowedFeeTiers.Should().Contain(30u);
        DexLibrary.AllowedFeeTiers.Should().Contain(100u);
    }

    [Fact]
    public void GetAmountOut_NoFee_ExactConstantProduct()
    {
        // With 0 fee: output should satisfy constant product exactly
        // (reserveIn + amountIn) * (reserveOut - amountOut) >= reserveIn * reserveOut
        var reserveIn = new UInt256(100_000);
        var reserveOut = new UInt256(100_000);
        var amountIn = new UInt256(1_000);

        var amountOut = DexLibrary.GetAmountOut(amountIn, reserveIn, reserveOut, 0);

        var newReserveIn = reserveIn + amountIn;
        var newReserveOut = reserveOut - amountOut;
        var newK = FullMath.MulDiv(newReserveIn, newReserveOut, UInt256.One);
        var oldK = FullMath.MulDiv(reserveIn, reserveOut, UInt256.One);

        newK.Should().BeGreaterThanOrEqualTo(oldK);
    }

    // ────────── C-2 Regression: GetAmountIn rounding fix ──────────

    [Fact]
    public void GetAmountIn_ExactDivision_NoOvercharge()
    {
        // When the division is exact, MulDivRoundingUp should NOT add +1.
        // Use reserves and amounts that produce an exact division:
        // numerator = reserveIn * amountOut * 10000
        // denominator = (reserveOut - amountOut) * (10000 - feeBps)
        // Choose values where numerator % denominator == 0.
        var reserveIn = new UInt256(10_000);
        var reserveOut = new UInt256(10_000);
        var feeBps = 0u; // Zero fee makes the math cleaner

        // With zero fee: amountIn = reserveIn * amountOut * 10000 / ((reserveOut - amountOut) * 10000)
        //              = reserveIn * amountOut / (reserveOut - amountOut)
        // Choose amountOut = 5000: amountIn = 10000 * 5000 / 5000 = 10000 exactly
        var amountOut = new UInt256(5_000);
        var amountIn = DexLibrary.GetAmountIn(amountOut, reserveIn, reserveOut, feeBps);

        // Before fix: would return 10001 (unconditional +1). After fix: returns 10000.
        amountIn.Should().Be(new UInt256(10_000));
    }
}
