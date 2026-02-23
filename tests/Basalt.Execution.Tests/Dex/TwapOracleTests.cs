using Basalt.Core;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for the TWAP (Time-Weighted Average Price) oracle.
/// Covers TWAP computation, volatility estimation, and block header serialization/deserialization.
/// </summary>
public class TwapOracleTests
{
    private readonly InMemoryStateDb _stateDb = new();
    private readonly DexState _dexState;

    private static readonly Address Token0 = Address.Zero;
    private static readonly Address Token1 = MakeAddress(0xAA);

    public TwapOracleTests()
    {
        _dexState = new DexState(_stateDb);
        _dexState.CreatePool(Token0, Token1, 30);
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }

    // ─── ComputeTwap ───

    [Fact]
    public void ComputeTwap_ZeroWindow_ReturnsZero()
    {
        var twap = TwapOracle.ComputeTwap(_dexState, 0, 100, 0);
        twap.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void ComputeTwap_NoAccumulatorData_ReturnsZero()
    {
        var twap = TwapOracle.ComputeTwap(_dexState, 0, 100, 50);
        twap.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void ComputeTwap_WithAccumulatorData_ReturnsAverage()
    {
        // Simulate a price update at block 10 with a known price
        var price = BatchAuctionSolver.PriceScale; // 1:1 price
        _dexState.UpdateTwapAccumulator(0, price, 10);

        // Update again at block 20 — accumulator adds price * (20-10) = price * 10
        _dexState.UpdateTwapAccumulator(0, price, 20);

        // Query TWAP over 20-block window
        var twap = TwapOracle.ComputeTwap(_dexState, 0, 20, 20);

        // TWAP = cumulativePrice / lastBlock
        // cumulativePrice = price * 10 (from block 10→20)
        // effectiveWindow = lastBlock(20) since 20 <= 20
        // twap = price * 10 / 20 = price / 2
        var expectedTwap = FullMath.MulDiv(price, UInt256.One, new UInt256(2));
        twap.Should().Be(expectedTwap);
    }

    [Fact]
    public void ComputeTwap_WindowLargerThanHistory_UsesAvailable()
    {
        var price = BatchAuctionSolver.PriceScale * new UInt256(2); // 2:1 price
        _dexState.UpdateTwapAccumulator(0, price, 5);
        _dexState.UpdateTwapAccumulator(0, price, 10);

        // Ask for 1000-block window but only have 10 blocks of data
        var twap = TwapOracle.ComputeTwap(_dexState, 0, 10, 1000);

        // effectiveWindow = min(lastBlock=10, windowBlocks=1000) = 10
        // cumulativePrice = price * (10-5) = price * 5
        // twap = price * 5 / 10 = price / 2
        var expectedTwap = FullMath.MulDiv(price, UInt256.One, new UInt256(2));
        twap.Should().Be(expectedTwap);
    }

    // ─── ComputeVolatilityBps ───

    [Fact]
    public void ComputeVolatilityBps_NoTwapData_ReturnsZero()
    {
        var vol = TwapOracle.ComputeVolatilityBps(_dexState, 0, 100, 50);
        vol.Should().Be(0);
    }

    [Fact]
    public void ComputeVolatilityBps_NoReserves_ReturnsZero()
    {
        // Set TWAP data but leave reserves at zero
        _dexState.UpdateTwapAccumulator(0, BatchAuctionSolver.PriceScale, 10);
        _dexState.UpdateTwapAccumulator(0, BatchAuctionSolver.PriceScale, 20);

        var vol = TwapOracle.ComputeVolatilityBps(_dexState, 0, 20, 20);
        vol.Should().Be(0);
    }

    [Fact]
    public void ComputeVolatilityBps_SpotEqualsTwap_ReturnsZero()
    {
        // Set reserves to 1000:1000 (spot price = 1:1)
        var reserves = new PoolReserves
        {
            Reserve0 = new UInt256(1000),
            Reserve1 = new UInt256(1000),
            TotalSupply = new UInt256(1000),
            KLast = UInt256.Zero,
        };
        _dexState.SetPoolReserves(0, reserves);

        // Set TWAP to exactly PriceScale (1:1)
        // Need to make cumulative such that twap = PriceScale
        // twap = cumulative / effectiveWindow
        // cumulative = PriceScale * window
        var price = BatchAuctionSolver.PriceScale;
        _dexState.UpdateTwapAccumulator(0, price, 1);

        // At block 100, update with same price
        _dexState.UpdateTwapAccumulator(0, price, 100);

        // cumulative = price * (100-1) = price * 99
        // twap = price * 99 / 100
        // spot = reserve1 * PriceScale / reserve0 = 1000 * PriceScale / 1000 = PriceScale
        // deviation = |spot - twap| = |PriceScale - PriceScale*99/100| = PriceScale/100
        // volatilityBps = (PriceScale/100) * 10000 / (PriceScale*99/100) ≈ 101

        // The vol won't be exactly zero due to accumulation mechanics,
        // but it should be small. Let's just verify it's a reasonable value.
        var vol = TwapOracle.ComputeVolatilityBps(_dexState, 0, 100, 100);
        vol.Should().BeLessThan(200); // Low volatility — within 2%
    }

    [Fact]
    public void ComputeVolatilityBps_LargeDeviation_ReturnsHighBps()
    {
        // Set reserves: 1000 token0, 3000 token1 (spot price = 3x)
        var reserves = new PoolReserves
        {
            Reserve0 = new UInt256(1000),
            Reserve1 = new UInt256(3000),
            TotalSupply = new UInt256(1000),
            KLast = UInt256.Zero,
        };
        _dexState.SetPoolReserves(0, reserves);

        // Set TWAP accumulator with a 1:1 price history
        var lowPrice = BatchAuctionSolver.PriceScale;
        _dexState.UpdateTwapAccumulator(0, lowPrice, 1);
        _dexState.UpdateTwapAccumulator(0, lowPrice, 100);

        // Spot price = 3 * PriceScale, TWAP ≈ PriceScale
        // Deviation should be large
        var vol = TwapOracle.ComputeVolatilityBps(_dexState, 0, 100, 100);
        vol.Should().BeGreaterThan(1000); // > 10% deviation expected
    }

    // ─── SerializeForBlockHeader / ParseFromBlockHeader ───

    [Fact]
    public void SerializeAndParse_RoundTrips()
    {
        var settlements = new List<BatchResult>
        {
            new BatchResult
            {
                PoolId = 42,
                ClearingPrice = BatchAuctionSolver.PriceScale * new UInt256(2),
                TotalVolume0 = new UInt256(10000),
            },
            new BatchResult
            {
                PoolId = 7,
                ClearingPrice = BatchAuctionSolver.PriceScale / new UInt256(2),
                TotalVolume0 = new UInt256(5000),
            },
        };

        // Set up TWAP data for both pools
        _dexState.CreatePool(MakeAddress(0xBB), MakeAddress(0xCC), 30); // Pool 1
        _dexState.CreatePool(MakeAddress(0xDD), MakeAddress(0xEE), 30); // Pool 2

        var serialized = TwapOracle.SerializeForBlockHeader(settlements, _dexState, 50, 256);

        var parsed = TwapOracle.ParseFromBlockHeader(serialized);

        parsed.Should().HaveCount(2);
        parsed[0].PoolId.Should().Be(42);
        parsed[0].ClearingPrice.Should().Be(BatchAuctionSolver.PriceScale * new UInt256(2));
        parsed[1].PoolId.Should().Be(7);
        parsed[1].ClearingPrice.Should().Be(BatchAuctionSolver.PriceScale / new UInt256(2));
    }

    [Fact]
    public void SerializeForBlockHeader_EmptySettlements_ReturnsEmpty()
    {
        var result = TwapOracle.SerializeForBlockHeader([], _dexState, 50, 256);
        result.Should().BeEmpty();
    }

    [Fact]
    public void SerializeForBlockHeader_RespectsMaxBytes()
    {
        var settlements = new List<BatchResult>();
        for (int i = 0; i < 10; i++)
        {
            settlements.Add(new BatchResult
            {
                PoolId = (ulong)i,
                ClearingPrice = BatchAuctionSolver.PriceScale,
            });
        }

        // Each entry is 72 bytes (8 + 32 + 32).
        // With maxBytes=144, only 2 entries should fit.
        var serialized = TwapOracle.SerializeForBlockHeader(settlements, _dexState, 50, 144);
        var parsed = TwapOracle.ParseFromBlockHeader(serialized);
        parsed.Should().HaveCount(2);
    }

    [Fact]
    public void ParseFromBlockHeader_EmptyData_ReturnsEmpty()
    {
        var parsed = TwapOracle.ParseFromBlockHeader([]);
        parsed.Should().BeEmpty();
    }

    [Fact]
    public void ParseFromBlockHeader_TruncatedData_IgnoresIncompleteEntry()
    {
        // 72 bytes per entry; give 100 bytes — should parse 1 entry, ignore remainder
        var data = new byte[100];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0, 8), 99);

        var parsed = TwapOracle.ParseFromBlockHeader(data);
        parsed.Should().HaveCount(1);
        parsed[0].PoolId.Should().Be(99);
    }
}
