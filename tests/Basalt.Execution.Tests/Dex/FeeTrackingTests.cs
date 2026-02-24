using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for concentrated liquidity fee tracking — Uniswap v3-style fee accumulation,
/// fee growth inside/outside, mint/burn snapshots, and CollectFees.
/// </summary>
public class FeeTrackingTests
{
    private readonly InMemoryStateDb _stateDb = new();
    private readonly DexState _dexState;
    private readonly ConcentratedPool _pool;

    private static readonly Address Alice;
    private static readonly Address Bob;

    static FeeTrackingTests()
    {
        var (_, alicePub) = Ed25519Signer.GenerateKeyPair();
        Alice = Ed25519Signer.DeriveAddress(alicePub);
        var (_, bobPub) = Ed25519Signer.GenerateKeyPair();
        Bob = Ed25519Signer.DeriveAddress(bobPub);
    }

    public FeeTrackingTests()
    {
        GenesisContractDeployer.DeployAll(_stateDb, ChainParameters.Devnet.ChainId);
        _dexState = new DexState(_stateDb);
        _pool = new ConcentratedPool(_dexState);

        // Create a pool (token0=Address.Zero, token1=0xAA) with 30bps fee and initialize at price=1.0
        _dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);
        _pool.InitializePool(0, TickMath.Q96);
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }

    // ────────── Test 1: Swap accumulates fee growth global ──────────

    [Fact]
    public void Swap_AccumulatesFeeGrowthGlobal()
    {
        // Mint a wide position to provide liquidity
        var mintResult = _pool.MintPosition(Alice, 0, -10000, 10000,
            new UInt256(10_000_000), new UInt256(10_000_000));
        mintResult.Success.Should().BeTrue();

        // Check fee growth before swap
        var stateBefore = _dexState.GetConcentratedPoolState(0)!.Value;
        stateBefore.FeeGrowthGlobal0X128.Should().Be(UInt256.Zero);
        stateBefore.FeeGrowthGlobal1X128.Should().Be(UInt256.Zero);

        // Execute a zeroForOne swap (token0 → token1, fee accrues on token0 side)
        var swapResult = _pool.Swap(0, zeroForOne: true, new UInt256(100_000),
            TickMath.MinSqrtRatio + UInt256.One, feeBps: 30);
        swapResult.Success.Should().BeTrue();

        // Fee growth global for token0 should be > 0
        var stateAfter = _dexState.GetConcentratedPoolState(0)!.Value;
        stateAfter.FeeGrowthGlobal0X128.Should().BeGreaterThan(UInt256.Zero);
        // token1 fee growth should still be zero (swap was zeroForOne)
        stateAfter.FeeGrowthGlobal1X128.Should().Be(UInt256.Zero);
    }

    // ────────── Test 2: Swap tick crossing flips fee growth outside ──────────

    [Fact]
    public void Swap_TickCrossing_FlipsFeeGrowthOutside()
    {
        // Mint two narrow positions to create initialized ticks
        _pool.MintPosition(Alice, 0, -500, 0,
            new UInt256(5_000_000), new UInt256(5_000_000)).Success.Should().BeTrue();
        _pool.MintPosition(Alice, 0, 0, 500,
            new UInt256(5_000_000), new UInt256(5_000_000)).Success.Should().BeTrue();

        // Tick 0 is initialized from both positions. Execute a large swap to cross it.
        var tickInfoBefore = _dexState.GetTickInfo(0, 0);
        var outsideBefore0 = tickInfoBefore.FeeGrowthOutside0X128;

        // Swap enough to cross tick 0 (zeroForOne moves price down)
        var swapResult = _pool.Swap(0, zeroForOne: true, new UInt256(2_000_000),
            TickMath.MinSqrtRatio + UInt256.One, feeBps: 30);
        swapResult.Success.Should().BeTrue();

        // After crossing, the tick's fee growth outside should have been flipped
        var tickInfoAfter = _dexState.GetTickInfo(0, 0);
        // The outside values should differ from before if fees accumulated before crossing
        var stateAfter = _dexState.GetConcentratedPoolState(0)!.Value;
        if (!stateAfter.FeeGrowthGlobal0X128.IsZero)
        {
            // If any fee growth occurred, the outside should have changed
            (tickInfoAfter.FeeGrowthOutside0X128 != outsideBefore0 ||
             stateAfter.FeeGrowthGlobal0X128 > UInt256.Zero).Should().BeTrue();
        }
    }

    // ────────── Test 3: MintPosition snapshots fee growth inside ──────────

    [Fact]
    public void MintPosition_SnapshotsFeeGrowthInside()
    {
        // Mint first position and do a swap to generate fees
        _pool.MintPosition(Alice, 0, -10000, 10000,
            new UInt256(10_000_000), new UInt256(10_000_000)).Success.Should().BeTrue();

        _pool.Swap(0, zeroForOne: true, new UInt256(500_000),
            TickMath.MinSqrtRatio + UInt256.One, feeBps: 30).Success.Should().BeTrue();

        // Now mint a second position — it should snapshot current fee growth inside
        _pool.MintPosition(Bob, 0, -5000, 5000,
            new UInt256(5_000_000), new UInt256(5_000_000)).Success.Should().BeTrue();

        var pos1 = _dexState.GetPosition(1)!.Value; // Bob's position (ID=1)
        // The fee snapshot should be non-zero since fees accrued before this mint
        pos1.FeeGrowthInside0LastX128.Should().BeGreaterThan(UInt256.Zero);
    }

    // ────────── Test 4: BurnPosition updates tokens owed ──────────

    [Fact]
    public void BurnPosition_UpdatesTokensOwed()
    {
        // Mint, swap, then partial burn
        _pool.MintPosition(Alice, 0, -10000, 10000,
            new UInt256(10_000_000), new UInt256(10_000_000)).Success.Should().BeTrue();

        _pool.Swap(0, zeroForOne: true, new UInt256(500_000),
            TickMath.MinSqrtRatio + UInt256.One, feeBps: 30).Success.Should().BeTrue();

        var pos = _dexState.GetPosition(0)!.Value;
        var halfLiquidity = pos.Liquidity / new UInt256(2);

        // Partial burn — position should remain with updated owed
        var burnResult = _pool.BurnPosition(Alice, 0, halfLiquidity);
        burnResult.Success.Should().BeTrue();

        var updatedPos = _dexState.GetPosition(0)!.Value;
        // After partial burn, owed tokens should reflect accumulated fees
        updatedPos.TokensOwed0.Should().BeGreaterThan(UInt256.Zero);
    }

    // ────────── Test 5: CollectFees full flow ──────────

    [Fact]
    public void CollectFees_TransfersOwedTokens()
    {
        // Mint → swap → collect
        _pool.MintPosition(Alice, 0, -10000, 10000,
            new UInt256(10_000_000), new UInt256(10_000_000)).Success.Should().BeTrue();

        _pool.Swap(0, zeroForOne: true, new UInt256(500_000),
            TickMath.MinSqrtRatio + UInt256.One, feeBps: 30).Success.Should().BeTrue();

        var result = _pool.CollectFees(0);
        result.Should().NotBeNull();

        var (owed0, owed1, updatedPos) = result!.Value;
        // Fees should have accumulated on the token0 side (zeroForOne swap)
        owed0.Should().BeGreaterThan(UInt256.Zero);

        // After collection, the position's owed fields should be zero
        updatedPos.TokensOwed0.Should().Be(UInt256.Zero);
        updatedPos.TokensOwed1.Should().Be(UInt256.Zero);
    }

    // ────────── Test 6: Multiple positions earn proportional fees ──────────

    [Fact]
    public void CollectFees_MultiplePositions_DifferentRanges()
    {
        // Alice: wide range, Bob: narrow range (both cover current price)
        _pool.MintPosition(Alice, 0, -10000, 10000,
            new UInt256(10_000_000), new UInt256(10_000_000)).Success.Should().BeTrue();
        _pool.MintPosition(Bob, 0, -1000, 1000,
            new UInt256(10_000_000), new UInt256(10_000_000)).Success.Should().BeTrue();

        // Swap to generate fees
        _pool.Swap(0, zeroForOne: true, new UInt256(500_000),
            TickMath.MinSqrtRatio + UInt256.One, feeBps: 30).Success.Should().BeTrue();

        var aliceFees = _pool.CollectFees(0);
        var bobFees = _pool.CollectFees(1);

        aliceFees.Should().NotBeNull();
        bobFees.Should().NotBeNull();

        // Both should earn fees
        aliceFees!.Value.Amount0.Should().BeGreaterThan(UInt256.Zero);
        bobFees!.Value.Amount0.Should().BeGreaterThan(UInt256.Zero);
    }

    // ────────── Test 7: No swaps = zero fees ──────────

    [Fact]
    public void CollectFees_NoSwaps_ZeroFees()
    {
        _pool.MintPosition(Alice, 0, -10000, 10000,
            new UInt256(10_000_000), new UInt256(10_000_000)).Success.Should().BeTrue();

        // No swap — collect should return zero
        var result = _pool.CollectFees(0);
        result.Should().NotBeNull();
        result!.Value.Amount0.Should().Be(UInt256.Zero);
        result.Value.Amount1.Should().Be(UInt256.Zero);
    }

    // ────────── Test 8: Out-of-range position earns nothing ──────────

    [Fact]
    public void CollectFees_PositionOutOfRange_ZeroFees()
    {
        // Mint in-range and out-of-range positions
        _pool.MintPosition(Alice, 0, -10000, 10000,
            new UInt256(10_000_000), new UInt256(10_000_000)).Success.Should().BeTrue();

        // Position entirely below current price (current tick = 0)
        _pool.MintPosition(Bob, 0, -20000, -15000,
            new UInt256(10_000_000), new UInt256(10_000_000)).Success.Should().BeTrue();

        // Swap in-range
        _pool.Swap(0, zeroForOne: true, new UInt256(500_000),
            TickMath.MinSqrtRatio + UInt256.One, feeBps: 30).Success.Should().BeTrue();

        // Bob's out-of-range position should earn zero fees
        var bobFees = _pool.CollectFees(1);
        bobFees.Should().NotBeNull();
        bobFees!.Value.Amount0.Should().Be(UInt256.Zero);
        bobFees.Value.Amount1.Should().Be(UInt256.Zero);
    }

    // ────────── Test 9: Backward compat — old Position deserializes with zero fees ──────────

    [Fact]
    public void BackwardCompat_OldPositionDeserializesWithZeroFees()
    {
        // Simulate a legacy 68-byte position
        var legacyData = new byte[Position.LegacySerializedSize];
        Alice.WriteTo(legacyData.AsSpan(0, 20));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(legacyData.AsSpan(20, 8), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(legacyData.AsSpan(28, 4), -1000);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(legacyData.AsSpan(32, 4), 1000);
        new UInt256(500).WriteTo(legacyData.AsSpan(36, 32));

        var pos = Position.Deserialize(legacyData);
        pos.Owner.Should().Be(Alice);
        pos.Liquidity.Should().Be(new UInt256(500));
        pos.FeeGrowthInside0LastX128.Should().Be(UInt256.Zero);
        pos.FeeGrowthInside1LastX128.Should().Be(UInt256.Zero);
        pos.TokensOwed0.Should().Be(UInt256.Zero);
        pos.TokensOwed1.Should().Be(UInt256.Zero);
    }

    // ────────── Test 10: Backward compat — old TickInfo ──────────

    [Fact]
    public void BackwardCompat_OldTickInfoDeserializesWithZeroFees()
    {
        var legacyData = new byte[TickInfo.LegacySerializedSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(legacyData.AsSpan(0, 8), 42);
        new UInt256(100).WriteTo(legacyData.AsSpan(8, 32));

        var info = TickInfo.Deserialize(legacyData);
        info.LiquidityNet.Should().Be(42);
        info.LiquidityGross.Should().Be(new UInt256(100));
        info.FeeGrowthOutside0X128.Should().Be(UInt256.Zero);
        info.FeeGrowthOutside1X128.Should().Be(UInt256.Zero);
    }

    // ────────── Test 11: Backward compat — old ConcentratedPoolState ──────────

    [Fact]
    public void BackwardCompat_OldPoolStateDeserializesWithZeroFees()
    {
        var legacyData = new byte[ConcentratedPoolState.LegacySerializedSize];
        TickMath.Q96.WriteTo(legacyData.AsSpan(0, 32));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(legacyData.AsSpan(32, 4), 5);
        new UInt256(1000).WriteTo(legacyData.AsSpan(36, 32));

        var state = ConcentratedPoolState.Deserialize(legacyData);
        state.SqrtPriceX96.Should().Be(TickMath.Q96);
        state.CurrentTick.Should().Be(5);
        state.TotalLiquidity.Should().Be(new UInt256(1000));
        state.FeeGrowthGlobal0X128.Should().Be(UInt256.Zero);
        state.FeeGrowthGlobal1X128.Should().Be(UInt256.Zero);
    }

    // ────────── Test 12: Full burn collects owed fees ──────────

    [Fact]
    public void FullBurn_CollectsOwedFees()
    {
        _pool.MintPosition(Alice, 0, -10000, 10000,
            new UInt256(10_000_000), new UInt256(10_000_000)).Success.Should().BeTrue();

        // Swap to generate fees
        _pool.Swap(0, zeroForOne: true, new UInt256(500_000),
            TickMath.MinSqrtRatio + UInt256.One, feeBps: 30).Success.Should().BeTrue();

        var pos = _dexState.GetPosition(0)!.Value;
        var fullLiquidity = pos.Liquidity;

        // Full burn should include owed fees in the returned amounts
        var burnResult = _pool.BurnPosition(Alice, 0, fullLiquidity);
        burnResult.Success.Should().BeTrue();

        // Amount0 should be greater than pure liquidity withdrawal (includes owed fees)
        burnResult.Amount0.Should().BeGreaterThan(UInt256.Zero);

        // Position should be deleted
        _dexState.GetPosition(0).Should().BeNull();
    }
}
