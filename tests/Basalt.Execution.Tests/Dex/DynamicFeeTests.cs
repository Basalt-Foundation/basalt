using Basalt.Core;
using Basalt.Execution.Dex;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for the dynamic fee calculator — verifies that swap fees increase
/// with volatility and are properly clamped to the [MinFeeBps, MaxFeeBps] range.
/// </summary>
public class DynamicFeeTests
{
    // ─── ComputeDynamicFee (pure computation) ───

    [Fact]
    public void ComputeDynamicFee_BelowThreshold_ReturnsBaseFee()
    {
        // 50 bps volatility, threshold is 100 — should use base fee
        var fee = DynamicFeeCalculator.ComputeDynamicFee(30, 50);
        fee.Should().Be(30);
    }

    [Fact]
    public void ComputeDynamicFee_AtThreshold_ReturnsBaseFee()
    {
        // Exactly at threshold — should use base fee
        var fee = DynamicFeeCalculator.ComputeDynamicFee(30, DynamicFeeCalculator.VolatilityThresholdBps);
        fee.Should().Be(30);
    }

    [Fact]
    public void ComputeDynamicFee_AboveThreshold_IncreasesLinearly()
    {
        // 200 bps volatility (100 above threshold)
        // excess = 100, feeIncrease = 100 * 2 * 30 / 100 = 60
        // effective = 30 + 60 = 90
        var fee = DynamicFeeCalculator.ComputeDynamicFee(30, 200);
        fee.Should().Be(90);
    }

    [Fact]
    public void ComputeDynamicFee_HighVolatility_CapsAtMaxFee()
    {
        // Very high volatility — should cap at 500 bps
        var fee = DynamicFeeCalculator.ComputeDynamicFee(30, 10000);
        fee.Should().Be(DynamicFeeCalculator.MaxFeeBps);
    }

    [Fact]
    public void ComputeDynamicFee_ZeroBaseFee_ClampsToMinFee()
    {
        // Zero base fee with no volatility — should return MinFeeBps
        var fee = DynamicFeeCalculator.ComputeDynamicFee(0, 0);
        fee.Should().Be(DynamicFeeCalculator.MinFeeBps);
    }

    [Fact]
    public void ComputeDynamicFee_ZeroVolatility_ReturnsClampedBaseFee()
    {
        var fee = DynamicFeeCalculator.ComputeDynamicFee(30, 0);
        fee.Should().Be(30);
    }

    [Fact]
    public void ComputeDynamicFee_TripleThreshold_SignificantIncrease()
    {
        // 400 bps volatility (300 above threshold)
        // excess = 300, feeIncrease = 300 * 2 * 30 / 100 = 180
        // effective = 30 + 180 = 210
        var fee = DynamicFeeCalculator.ComputeDynamicFee(30, 400);
        fee.Should().Be(210);
    }

    [Fact]
    public void ComputeDynamicFee_HighBaseFee_StillCapped()
    {
        // 100 bps base fee with moderate volatility
        // excess = 200, feeIncrease = 200 * 2 * 100 / 100 = 400
        // effective = 100 + 400 = 500 = MaxFeeBps
        var fee = DynamicFeeCalculator.ComputeDynamicFee(100, 300);
        fee.Should().Be(500);
    }

    [Fact]
    public void ComputeDynamicFee_OverflowProtection_ClampsToMax()
    {
        // Extreme values — should not overflow and should cap at MaxFeeBps
        var fee = DynamicFeeCalculator.ComputeDynamicFee(500, uint.MaxValue);
        fee.Should().Be(DynamicFeeCalculator.MaxFeeBps);
    }

    // ─── ComputeDynamicFeeFromState (integration with TWAP oracle) ───

    [Fact]
    public void ComputeDynamicFeeFromState_NoOracleData_ReturnsBaseFee()
    {
        var stateDb = new InMemoryStateDb();
        var dexState = new DexState(stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);

        // No TWAP data, no reserves → volatility = 0 → base fee clamped
        var fee = DynamicFeeCalculator.ComputeDynamicFeeFromState(dexState, 0, 30, 100);
        fee.Should().Be(30);
    }

    [Fact]
    public void ComputeDynamicFeeFromState_StablePrice_ReturnsBaseFee()
    {
        var stateDb = new InMemoryStateDb();
        var dexState = new DexState(stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);

        // Set equal reserves (spot = 1:1)
        var reserves = new PoolReserves
        {
            Reserve0 = new UInt256(10000),
            Reserve1 = new UInt256(10000),
            TotalSupply = new UInt256(10000),
            KLast = UInt256.Zero,
        };
        dexState.SetPoolReserves(0, reserves);

        // Set TWAP close to spot price
        var price = BatchAuctionSolver.PriceScale;
        dexState.UpdateTwapAccumulator(0, price, 1);
        dexState.UpdateTwapAccumulator(0, price, 100);

        var fee = DynamicFeeCalculator.ComputeDynamicFeeFromState(dexState, 0, 30, 100);

        // With spot ~= TWAP, low volatility, should be close to base fee
        fee.Should().BeInRange(1u, 100u);
    }

    [Fact]
    public void ComputeDynamicFeeFromState_VolatilePrice_IncreasedFee()
    {
        var stateDb = new InMemoryStateDb();
        var dexState = new DexState(stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);

        // Set reserves: 1000 token0, 5000 token1 (spot = 5:1)
        var reserves = new PoolReserves
        {
            Reserve0 = new UInt256(1000),
            Reserve1 = new UInt256(5000),
            TotalSupply = new UInt256(1000),
            KLast = UInt256.Zero,
        };
        dexState.SetPoolReserves(0, reserves);

        // Set TWAP history at 1:1 price — big deviation from current 5:1 spot
        var twapPrice = BatchAuctionSolver.PriceScale;
        dexState.UpdateTwapAccumulator(0, twapPrice, 1);
        dexState.UpdateTwapAccumulator(0, twapPrice, 100);

        var fee = DynamicFeeCalculator.ComputeDynamicFeeFromState(dexState, 0, 30, 100);

        // Large deviation should push fee well above base
        fee.Should().BeGreaterThan(30u);
    }

    // ─── Constants ───

    [Fact]
    public void Constants_AreReasonable()
    {
        DynamicFeeCalculator.VolatilityThresholdBps.Should().Be(100);
        DynamicFeeCalculator.GrowthFactor.Should().Be(2);
        DynamicFeeCalculator.MaxFeeBps.Should().Be(500);
        DynamicFeeCalculator.MinFeeBps.Should().Be(1);
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }
}
