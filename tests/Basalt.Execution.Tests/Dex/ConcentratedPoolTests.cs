using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for ConcentratedPool — position management, tick bookkeeping, and concentrated swaps.
/// </summary>
public class ConcentratedPoolTests
{
    private readonly InMemoryStateDb _stateDb = new();
    private readonly DexState _dexState;
    private readonly ConcentratedPool _pool;

    private static readonly Address Alice;
    private static readonly Address Bob;

    static ConcentratedPoolTests()
    {
        var (_, alicePub) = Ed25519Signer.GenerateKeyPair();
        Alice = Ed25519Signer.DeriveAddress(alicePub);
        var (_, bobPub) = Ed25519Signer.GenerateKeyPair();
        Bob = Ed25519Signer.DeriveAddress(bobPub);
    }

    public ConcentratedPoolTests()
    {
        GenesisContractDeployer.DeployAll(_stateDb, ChainParameters.Devnet.ChainId);
        _dexState = new DexState(_stateDb);
        _pool = new ConcentratedPool(_dexState);

        // Create a v2-style pool (metadata only) and initialize it as concentrated
        _dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);
        _pool.InitializePool(0, TickMath.Q96); // Price = 1.0
    }

    // ─── InitializePool ───

    [Fact]
    public void InitializePool_Success()
    {
        // Already initialized in constructor — verify state
        var state = _dexState.GetConcentratedPoolState(0);
        state.Should().NotBeNull();
        state!.Value.SqrtPriceX96.Should().Be(TickMath.Q96);
        state.Value.CurrentTick.Should().Be(0);
        state.Value.TotalLiquidity.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void InitializePool_AlreadyInitialized_Fails()
    {
        var result = _pool.InitializePool(0, TickMath.Q96);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexPoolAlreadyExists);
    }

    [Fact]
    public void InitializePool_InvalidSqrtPrice_Fails()
    {
        _dexState.CreatePool(Address.Zero, MakeAddress(0xBB), 30);
        var result = _pool.InitializePool(1, UInt256.Zero);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidAmount);
    }

    // ─── MintPosition ───

    [Fact]
    public void MintPosition_InRange_Success()
    {
        var result = _pool.MintPosition(
            Alice, 0,
            tickLower: -1000, tickUpper: 1000,
            amount0Desired: new UInt256(1_000_000),
            amount1Desired: new UInt256(1_000_000));

        result.Success.Should().BeTrue();
        result.Amount0.Should().BeGreaterThan(UInt256.Zero);
        result.Amount1.Should().BeGreaterThan(UInt256.Zero);
        result.Logs.Should().HaveCount(1);

        // Position should be stored
        var pos = _dexState.GetPosition(0);
        pos.Should().NotBeNull();
        pos!.Value.Owner.Should().Be(Alice);
        pos.Value.PoolId.Should().Be(0ul);
        pos.Value.TickLower.Should().Be(-1000);
        pos.Value.TickUpper.Should().Be(1000);
        pos.Value.Liquidity.Should().BeGreaterThan(UInt256.Zero);

        // Pool active liquidity should be updated (price is in range)
        var state = _dexState.GetConcentratedPoolState(0);
        state!.Value.TotalLiquidity.Should().BeGreaterThan(UInt256.Zero);
    }

    [Fact]
    public void MintPosition_BelowRange_OnlyToken0()
    {
        // Set price at tick 2000, position is at [-1000, 1000)
        // Price is above range → only token1 needed
        var stateDb = new InMemoryStateDb();
        GenesisContractDeployer.DeployAll(stateDb, ChainParameters.Devnet.ChainId);
        var dexState = new DexState(stateDb);
        var pool = new ConcentratedPool(dexState);

        dexState.CreatePool(Address.Zero, MakeAddress(0xCC), 30);
        var sqrtP = TickMath.GetSqrtRatioAtTick(-2000);
        pool.InitializePool(0, sqrtP);

        var result = pool.MintPosition(
            Alice, 0,
            tickLower: -1000, tickUpper: 1000,
            amount0Desired: new UInt256(1_000_000),
            amount1Desired: new UInt256(1_000_000));

        result.Success.Should().BeTrue();
        // Price below range: only token0 needed
        result.Amount0.Should().BeGreaterThan(UInt256.Zero);
        result.Amount1.Should().Be(UInt256.Zero);

        // Since price is below range, no active liquidity added
        var state = dexState.GetConcentratedPoolState(0);
        state!.Value.TotalLiquidity.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void MintPosition_AboveRange_OnlyToken1()
    {
        var stateDb = new InMemoryStateDb();
        GenesisContractDeployer.DeployAll(stateDb, ChainParameters.Devnet.ChainId);
        var dexState = new DexState(stateDb);
        var pool = new ConcentratedPool(dexState);

        dexState.CreatePool(Address.Zero, MakeAddress(0xDD), 30);
        var sqrtP = TickMath.GetSqrtRatioAtTick(2000);
        pool.InitializePool(0, sqrtP);

        var result = pool.MintPosition(
            Alice, 0,
            tickLower: -1000, tickUpper: 1000,
            amount0Desired: new UInt256(1_000_000),
            amount1Desired: new UInt256(1_000_000));

        result.Success.Should().BeTrue();
        result.Amount0.Should().Be(UInt256.Zero);
        result.Amount1.Should().BeGreaterThan(UInt256.Zero);
    }

    [Fact]
    public void MintPosition_InvalidTickRange_Fails()
    {
        var result = _pool.MintPosition(
            Alice, 0,
            tickLower: 1000, tickUpper: -1000,
            amount0Desired: new UInt256(1_000_000),
            amount1Desired: new UInt256(1_000_000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidTickRange);
    }

    [Fact]
    public void MintPosition_EqualTicks_Fails()
    {
        var result = _pool.MintPosition(
            Alice, 0,
            tickLower: 0, tickUpper: 0,
            amount0Desired: new UInt256(1_000_000),
            amount1Desired: new UInt256(1_000_000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidTickRange);
    }

    [Fact]
    public void MintPosition_ZeroAmounts_Fails()
    {
        var result = _pool.MintPosition(
            Alice, 0,
            tickLower: -1000, tickUpper: 1000,
            amount0Desired: UInt256.Zero,
            amount1Desired: UInt256.Zero);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidAmount);
    }

    [Fact]
    public void MintPosition_NonexistentPool_Fails()
    {
        var result = _pool.MintPosition(
            Alice, 999,
            tickLower: -1000, tickUpper: 1000,
            amount0Desired: new UInt256(1_000_000),
            amount1Desired: new UInt256(1_000_000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexPoolNotFound);
    }

    [Fact]
    public void MintPosition_MultiplePositions_IndependentIds()
    {
        _pool.MintPosition(Alice, 0, -1000, 1000, new UInt256(1_000_000), new UInt256(1_000_000));
        _pool.MintPosition(Bob, 0, -500, 500, new UInt256(500_000), new UInt256(500_000));

        var pos0 = _dexState.GetPosition(0);
        var pos1 = _dexState.GetPosition(1);

        pos0.Should().NotBeNull();
        pos1.Should().NotBeNull();
        pos0!.Value.Owner.Should().Be(Alice);
        pos1!.Value.Owner.Should().Be(Bob);
        _dexState.GetPositionCount().Should().Be(2ul);
    }

    // ─── BurnPosition ───

    [Fact]
    public void BurnPosition_Full_DeletesPosition()
    {
        _pool.MintPosition(Alice, 0, -1000, 1000, new UInt256(1_000_000), new UInt256(1_000_000));
        var pos = _dexState.GetPosition(0)!.Value;

        var result = _pool.BurnPosition(Alice, 0, pos.Liquidity);

        result.Success.Should().BeTrue();
        result.Amount0.Should().BeGreaterThan(UInt256.Zero);
        result.Amount1.Should().BeGreaterThan(UInt256.Zero);

        // Position should be deleted
        _dexState.GetPosition(0).Should().BeNull();

        // Pool liquidity should return to zero
        var state = _dexState.GetConcentratedPoolState(0);
        state!.Value.TotalLiquidity.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void BurnPosition_Partial_ReducesLiquidity()
    {
        _pool.MintPosition(Alice, 0, -1000, 1000, new UInt256(1_000_000), new UInt256(1_000_000));
        var pos = _dexState.GetPosition(0)!.Value;
        var halfLiquidity = pos.Liquidity / new UInt256(2);

        var result = _pool.BurnPosition(Alice, 0, halfLiquidity);

        result.Success.Should().BeTrue();
        var remaining = _dexState.GetPosition(0);
        remaining.Should().NotBeNull();
        remaining!.Value.Liquidity.Should().BeLessThan(pos.Liquidity);
    }

    [Fact]
    public void BurnPosition_NotOwner_Fails()
    {
        _pool.MintPosition(Alice, 0, -1000, 1000, new UInt256(1_000_000), new UInt256(1_000_000));

        var result = _pool.BurnPosition(Bob, 0, new UInt256(1000));
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexPositionNotOwner);
    }

    [Fact]
    public void BurnPosition_ExceedsLiquidity_Fails()
    {
        _pool.MintPosition(Alice, 0, -1000, 1000, new UInt256(1_000_000), new UInt256(1_000_000));
        var pos = _dexState.GetPosition(0)!.Value;

        var result = _pool.BurnPosition(Alice, 0, UInt256.CheckedAdd(pos.Liquidity, UInt256.One));
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInsufficientLiquidity);
    }

    [Fact]
    public void BurnPosition_ZeroAmount_Fails()
    {
        _pool.MintPosition(Alice, 0, -1000, 1000, new UInt256(1_000_000), new UInt256(1_000_000));

        var result = _pool.BurnPosition(Alice, 0, UInt256.Zero);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidAmount);
    }

    [Fact]
    public void BurnPosition_NonexistentPosition_Fails()
    {
        var result = _pool.BurnPosition(Alice, 999, new UInt256(1000));
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexPositionNotFound);
    }

    // ─── Swap ───

    [Fact]
    public void Swap_ZeroForOne_Success()
    {
        // Add liquidity first
        _pool.MintPosition(Alice, 0, -10000, 10000, new UInt256(10_000_000), new UInt256(10_000_000));

        // Swap token0 → token1 (price should decrease)
        var stateBefore = _dexState.GetConcentratedPoolState(0)!.Value;

        var result = _pool.Swap(0, zeroForOne: true, new UInt256(1_000),
            sqrtPriceLimitX96: TickMath.MinSqrtRatio + UInt256.One);

        result.Success.Should().BeTrue();
        result.Amount0.Should().BeGreaterThan(UInt256.Zero); // Input consumed
        result.Amount1.Should().BeGreaterThan(UInt256.Zero); // Output received

        var stateAfter = _dexState.GetConcentratedPoolState(0)!.Value;
        stateAfter.SqrtPriceX96.Should().BeLessThanOrEqualTo(stateBefore.SqrtPriceX96);
    }

    [Fact]
    public void Swap_OneForZero_Success()
    {
        _pool.MintPosition(Alice, 0, -10000, 10000, new UInt256(10_000_000), new UInt256(10_000_000));

        var stateBefore = _dexState.GetConcentratedPoolState(0)!.Value;

        var result = _pool.Swap(0, zeroForOne: false, new UInt256(1_000),
            sqrtPriceLimitX96: TickMath.MaxSqrtRatio - UInt256.One);

        result.Success.Should().BeTrue();
        result.Amount0.Should().BeGreaterThan(UInt256.Zero);
        result.Amount1.Should().BeGreaterThan(UInt256.Zero);

        var stateAfter = _dexState.GetConcentratedPoolState(0)!.Value;
        stateAfter.SqrtPriceX96.Should().BeGreaterThanOrEqualTo(stateBefore.SqrtPriceX96);
    }

    [Fact]
    public void Swap_ZeroAmount_Fails()
    {
        var result = _pool.Swap(0, true, UInt256.Zero, TickMath.MinSqrtRatio + UInt256.One);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidAmount);
    }

    [Fact]
    public void Swap_InvalidPriceLimit_Fails()
    {
        _pool.MintPosition(Alice, 0, -10000, 10000, new UInt256(10_000_000), new UInt256(10_000_000));

        // For zeroForOne: limit must be < current price
        var result = _pool.Swap(0, zeroForOne: true, new UInt256(1_000),
            sqrtPriceLimitX96: TickMath.MaxSqrtRatio);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidAmount);
    }

    [Fact]
    public void Swap_NoLiquidity_NoOutput()
    {
        // Pool is initialized but has no positions → no liquidity
        // The swap should succeed but produce zero output
        var result = _pool.Swap(0, zeroForOne: true, new UInt256(1_000),
            sqrtPriceLimitX96: TickMath.MinSqrtRatio + UInt256.One);

        result.Success.Should().BeTrue();
        // No liquidity to trade against
        result.Amount1.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void Swap_NonexistentPool_Fails()
    {
        var result = _pool.Swap(999, true, new UInt256(1000), TickMath.MinSqrtRatio + UInt256.One);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexPoolNotFound);
    }

    // ─── Tick State ───

    [Fact]
    public void TickState_UpdatedOnMint()
    {
        _pool.MintPosition(Alice, 0, -1000, 1000, new UInt256(1_000_000), new UInt256(1_000_000));

        var lowerTick = _dexState.GetTickInfo(0, -1000);
        var upperTick = _dexState.GetTickInfo(0, 1000);

        lowerTick.LiquidityGross.Should().BeGreaterThan(UInt256.Zero);
        lowerTick.LiquidityNet.Should().BeGreaterThan(0); // Positive at lower bound
        upperTick.LiquidityGross.Should().BeGreaterThan(UInt256.Zero);
        upperTick.LiquidityNet.Should().BeLessThan(0); // Negative at upper bound
    }

    [Fact]
    public void TickState_ClearedOnFullBurn()
    {
        _pool.MintPosition(Alice, 0, -1000, 1000, new UInt256(1_000_000), new UInt256(1_000_000));
        var pos = _dexState.GetPosition(0)!.Value;

        _pool.BurnPosition(Alice, 0, pos.Liquidity);

        var lowerTick = _dexState.GetTickInfo(0, -1000);
        var upperTick = _dexState.GetTickInfo(0, 1000);

        lowerTick.LiquidityGross.Should().Be(UInt256.Zero);
        upperTick.LiquidityGross.Should().Be(UInt256.Zero);
    }

    // ─── DexState Storage ───

    [Fact]
    public void DexState_Position_RoundTrip()
    {
        var position = new Position
        {
            Owner = Alice,
            PoolId = 42,
            TickLower = -500,
            TickUpper = 500,
            Liquidity = new UInt256(999_999),
        };

        _dexState.SetPosition(100, position);
        var loaded = _dexState.GetPosition(100);

        loaded.Should().NotBeNull();
        loaded!.Value.Owner.Should().Be(Alice);
        loaded.Value.PoolId.Should().Be(42ul);
        loaded.Value.TickLower.Should().Be(-500);
        loaded.Value.TickUpper.Should().Be(500);
        loaded.Value.Liquidity.Should().Be(new UInt256(999_999));
    }

    [Fact]
    public void DexState_TickInfo_RoundTrip()
    {
        var info = new TickInfo
        {
            LiquidityNet = -12345,
            LiquidityGross = new UInt256(67890),
        };

        _dexState.SetTickInfo(0, -100, info);
        var loaded = _dexState.GetTickInfo(0, -100);

        loaded.LiquidityNet.Should().Be(-12345);
        loaded.LiquidityGross.Should().Be(new UInt256(67890));
    }

    [Fact]
    public void DexState_ConcentratedPoolState_RoundTrip()
    {
        var state = new ConcentratedPoolState
        {
            SqrtPriceX96 = TickMath.Q96,
            CurrentTick = 42,
            TotalLiquidity = new UInt256(1_000_000),
        };

        _dexState.SetConcentratedPoolState(5, state);
        var loaded = _dexState.GetConcentratedPoolState(5);

        loaded.Should().NotBeNull();
        loaded!.Value.SqrtPriceX96.Should().Be(TickMath.Q96);
        loaded.Value.CurrentTick.Should().Be(42);
        loaded.Value.TotalLiquidity.Should().Be(new UInt256(1_000_000));
    }

    [Fact]
    public void DexState_PositionCount_Increments()
    {
        _dexState.GetPositionCount().Should().Be(0ul);
        _dexState.SetPositionCount(5);
        _dexState.GetPositionCount().Should().Be(5ul);
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }
}
