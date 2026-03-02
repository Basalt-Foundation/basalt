using System.Numerics;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex.Math;

namespace Basalt.Execution.Dex;

/// <summary>
/// Concentrated liquidity pool engine — manages positions, tick crossings, and swaps
/// within a Uniswap v3-style concentrated liquidity pool.
///
/// Each pool has a current sqrt price and active liquidity. Positions contribute liquidity
/// only when the price is within their [tickLower, tickUpper) range.
/// </summary>
public sealed class ConcentratedPool
{
    private readonly DexState _state;

    /// <summary>Q128 = 2^128, used as the fixed-point denominator for fee growth values.</summary>
    private static readonly UInt256 Q128 = UInt256.One << 128;

    public ConcentratedPool(DexState state)
    {
        _state = state;
    }

    /// <summary>
    /// Initialize a concentrated liquidity pool at the given sqrt price.
    /// The pool must already exist (created via CreatePool) and must not already be initialized.
    /// </summary>
    public DexResult InitializePool(ulong poolId, UInt256 sqrtPriceX96)
    {
        if (sqrtPriceX96 < TickMath.MinSqrtRatio || sqrtPriceX96 > TickMath.MaxSqrtRatio)
            return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Initial sqrt price out of range");

        var existing = _state.GetConcentratedPoolState(poolId);
        if (existing != null && !existing.Value.SqrtPriceX96.IsZero)
            return DexResult.Error(BasaltErrorCode.DexPoolAlreadyExists, "Concentrated pool already initialized");

        var tick = TickMath.GetTickAtSqrtRatio(sqrtPriceX96);
        _state.SetConcentratedPoolState(poolId, new ConcentratedPoolState
        {
            SqrtPriceX96 = sqrtPriceX96,
            CurrentTick = tick,
            TotalLiquidity = UInt256.Zero,
        });

        return DexResult.PoolCreated(poolId, []);
    }

    /// <summary>
    /// Mint a new concentrated liquidity position within [tickLower, tickUpper).
    /// Returns the position ID and the actual token amounts deposited.
    /// </summary>
    public DexResult MintPosition(
        Address sender, ulong poolId, int tickLower, int tickUpper,
        UInt256 amount0Desired, UInt256 amount1Desired)
    {
        // Validate tick range
        if (tickLower >= tickUpper)
            return DexResult.Error(BasaltErrorCode.DexInvalidTickRange, "tickLower must be < tickUpper");
        if (tickLower < TickMath.MinTick || tickUpper > TickMath.MaxTick)
            return DexResult.Error(BasaltErrorCode.DexInvalidTick, "Tick out of range");

        var poolMeta = _state.GetPoolMetadata(poolId);
        if (poolMeta == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool does not exist");

        var poolState = _state.GetConcentratedPoolState(poolId);
        if (poolState == null || poolState.Value.SqrtPriceX96.IsZero)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Concentrated pool not initialized");

        var state = poolState.Value;

        // Compute liquidity from the desired amounts
        var sqrtRatioA = TickMath.GetSqrtRatioAtTick(tickLower);
        var sqrtRatioB = TickMath.GetSqrtRatioAtTick(tickUpper);
        var liquidity = LiquidityMath.GetLiquidityForAmounts(
            state.SqrtPriceX96, sqrtRatioA, sqrtRatioB, amount0Desired, amount1Desired);

        if (liquidity.IsZero)
            return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Zero liquidity from provided amounts");

        // H-06: Validate liquidity fits in long for LiquidityNet tracking
        if (liquidity > new UInt256((ulong)long.MaxValue))
            return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Liquidity exceeds max safe value for tick tracking");

        // Compute actual token amounts required
        var amount0 = SqrtPriceMath.GetAmount0Delta(state.SqrtPriceX96, sqrtRatioB, liquidity, roundUp: true);
        var amount1 = SqrtPriceMath.GetAmount1Delta(sqrtRatioA, state.SqrtPriceX96, liquidity, roundUp: true);

        // Adjust amounts based on where current price is relative to the range
        if (state.CurrentTick < tickLower)
        {
            // Price below range: only token0 needed
            amount0 = SqrtPriceMath.GetAmount0Delta(sqrtRatioA, sqrtRatioB, liquidity, roundUp: true);
            amount1 = UInt256.Zero;
        }
        else if (state.CurrentTick >= tickUpper)
        {
            // Price above range: only token1 needed
            amount0 = UInt256.Zero;
            amount1 = SqrtPriceMath.GetAmount1Delta(sqrtRatioA, sqrtRatioB, liquidity, roundUp: true);
        }

        // Allocate position ID
        var positionId = _state.GetPositionCount();
        _state.SetPositionCount(positionId + 1);

        // Snapshot current fee growth inside the position range
        var (fg0, fg1) = GetFeeGrowthInside(poolId, tickLower, tickUpper);

        // Save position with fee snapshot
        _state.SetPosition(positionId, new Position
        {
            Owner = sender,
            PoolId = poolId,
            TickLower = tickLower,
            TickUpper = tickUpper,
            Liquidity = liquidity,
            FeeGrowthInside0LastX128 = fg0,
            FeeGrowthInside1LastX128 = fg1,
            TokensOwed0 = UInt256.Zero,
            TokensOwed1 = UInt256.Zero,
        });

        // Update tick state
        UpdateTick(poolId, tickLower, liquidity, isLower: true);
        UpdateTick(poolId, tickUpper, liquidity, isLower: false);

        // Update pool active liquidity if current price is in range
        if (state.CurrentTick >= tickLower && state.CurrentTick < tickUpper)
        {
            state.TotalLiquidity = UInt256.CheckedAdd(state.TotalLiquidity, liquidity);
            _state.SetConcentratedPoolState(poolId, state);
        }

        var logs = new List<EventLog> { MakeLog("MintPosition", positionId) };
        return DexResult.ConcentratedResult(poolId, amount0, amount1, logs);
    }

    /// <summary>
    /// Burn liquidity from a position, returning the position ID's tokens.
    /// If the entire liquidity is burned, the position is deleted.
    /// Accumulated fees are added to the owed amounts; on full burn they are returned directly.
    /// </summary>
    public DexResult BurnPosition(Address sender, ulong positionId, UInt256 liquidityToBurn)
    {
        var pos = _state.GetPosition(positionId);
        if (pos == null)
            return DexResult.Error(BasaltErrorCode.DexPositionNotFound, "Position does not exist");

        var position = pos.Value;
        if (position.Owner != sender)
            return DexResult.Error(BasaltErrorCode.DexPositionNotOwner, "Not the position owner");

        if (liquidityToBurn.IsZero)
            return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Burn amount is zero");

        if (liquidityToBurn > position.Liquidity)
            return DexResult.Error(BasaltErrorCode.DexInsufficientLiquidity, "Burn amount exceeds position liquidity");

        var poolState = _state.GetConcentratedPoolState(position.PoolId);
        if (poolState == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Pool not found");

        var state = poolState.Value;

        // Compute accumulated fees before reducing liquidity
        var (fg0, fg1) = GetFeeGrowthInside(position.PoolId, position.TickLower, position.TickUpper);
        var owed0 = position.TokensOwed0 + FullMath.MulDiv(
            WrappingSub(fg0, position.FeeGrowthInside0LastX128), position.Liquidity, Q128);
        var owed1 = position.TokensOwed1 + FullMath.MulDiv(
            WrappingSub(fg1, position.FeeGrowthInside1LastX128), position.Liquidity, Q128);

        // Compute token amounts to return (from liquidity)
        var sqrtRatioA = TickMath.GetSqrtRatioAtTick(position.TickLower);
        var sqrtRatioB = TickMath.GetSqrtRatioAtTick(position.TickUpper);

        UInt256 amount0, amount1;
        if (state.CurrentTick < position.TickLower)
        {
            amount0 = SqrtPriceMath.GetAmount0Delta(sqrtRatioA, sqrtRatioB, liquidityToBurn, roundUp: false);
            amount1 = UInt256.Zero;
        }
        else if (state.CurrentTick >= position.TickUpper)
        {
            amount0 = UInt256.Zero;
            amount1 = SqrtPriceMath.GetAmount1Delta(sqrtRatioA, sqrtRatioB, liquidityToBurn, roundUp: false);
        }
        else
        {
            amount0 = SqrtPriceMath.GetAmount0Delta(state.SqrtPriceX96, sqrtRatioB, liquidityToBurn, roundUp: false);
            amount1 = SqrtPriceMath.GetAmount1Delta(sqrtRatioA, state.SqrtPriceX96, liquidityToBurn, roundUp: false);
        }

        // Update ticks
        UpdateTick(position.PoolId, position.TickLower, liquidityToBurn, isLower: true, remove: true);
        UpdateTick(position.PoolId, position.TickUpper, liquidityToBurn, isLower: false, remove: true);

        // Update pool active liquidity if in range
        if (state.CurrentTick >= position.TickLower && state.CurrentTick < position.TickUpper)
        {
            state.TotalLiquidity = UInt256.CheckedSub(state.TotalLiquidity, liquidityToBurn);
            _state.SetConcentratedPoolState(position.PoolId, state);
        }

        // Update or delete position
        if (liquidityToBurn == position.Liquidity)
        {
            // Full burn — include owed fees in the returned amounts and delete position
            amount0 = UInt256.CheckedAdd(amount0, owed0);
            amount1 = UInt256.CheckedAdd(amount1, owed1);
            _state.DeletePosition(positionId);
        }
        else
        {
            // Partial burn — update position with new snapshot and owed amounts
            position.Liquidity = UInt256.CheckedSub(position.Liquidity, liquidityToBurn);
            position.FeeGrowthInside0LastX128 = fg0;
            position.FeeGrowthInside1LastX128 = fg1;
            position.TokensOwed0 = owed0;
            position.TokensOwed1 = owed1;
            _state.SetPosition(positionId, position);
        }

        var logs = new List<EventLog> { MakeLog("BurnPosition", positionId) };
        return DexResult.ConcentratedResult(position.PoolId, amount0, amount1, logs);
    }

    /// <summary>
    /// Collect accumulated fees from a position without removing liquidity.
    /// </summary>
    /// <param name="positionId">The position ID to collect fees from.</param>
    /// <returns>Owed token amounts and the updated position.</returns>
    public (UInt256 Amount0, UInt256 Amount1, Position UpdatedPosition)? CollectFees(ulong positionId)
    {
        var pos = _state.GetPosition(positionId);
        if (pos == null) return null;

        var position = pos.Value;
        var (fg0, fg1) = GetFeeGrowthInside(position.PoolId, position.TickLower, position.TickUpper);

        var owed0 = position.TokensOwed0 + FullMath.MulDiv(
            WrappingSub(fg0, position.FeeGrowthInside0LastX128), position.Liquidity, Q128);
        var owed1 = position.TokensOwed1 + FullMath.MulDiv(
            WrappingSub(fg1, position.FeeGrowthInside1LastX128), position.Liquidity, Q128);

        // Update position snapshot, zero out owed
        position.FeeGrowthInside0LastX128 = fg0;
        position.FeeGrowthInside1LastX128 = fg1;
        position.TokensOwed0 = UInt256.Zero;
        position.TokensOwed1 = UInt256.Zero;
        _state.SetPosition(positionId, position);

        return (owed0, owed1, position);
    }

    /// <summary>
    /// Compute the fee growth inside a tick range [tickLower, tickUpper) for a pool.
    /// Uses Uniswap v3 convention: inside = global - below(lower) - above(upper).
    /// All arithmetic uses wrapping subtraction (modulo 2^256).
    /// </summary>
    public (UInt256 FeeGrowthInside0X128, UInt256 FeeGrowthInside1X128) GetFeeGrowthInside(
        ulong poolId, int tickLower, int tickUpper)
    {
        var poolState = _state.GetConcentratedPoolState(poolId);
        if (poolState == null)
            return (UInt256.Zero, UInt256.Zero);

        var state = poolState.Value;
        var lowerInfo = _state.GetTickInfo(poolId, tickLower);
        var upperInfo = _state.GetTickInfo(poolId, tickUpper);

        // Compute fee growth below tickLower
        UInt256 feeGrowthBelow0, feeGrowthBelow1;
        if (state.CurrentTick >= tickLower)
        {
            feeGrowthBelow0 = lowerInfo.FeeGrowthOutside0X128;
            feeGrowthBelow1 = lowerInfo.FeeGrowthOutside1X128;
        }
        else
        {
            feeGrowthBelow0 = WrappingSub(state.FeeGrowthGlobal0X128, lowerInfo.FeeGrowthOutside0X128);
            feeGrowthBelow1 = WrappingSub(state.FeeGrowthGlobal1X128, lowerInfo.FeeGrowthOutside1X128);
        }

        // Compute fee growth above tickUpper
        UInt256 feeGrowthAbove0, feeGrowthAbove1;
        if (state.CurrentTick < tickUpper)
        {
            feeGrowthAbove0 = upperInfo.FeeGrowthOutside0X128;
            feeGrowthAbove1 = upperInfo.FeeGrowthOutside1X128;
        }
        else
        {
            feeGrowthAbove0 = WrappingSub(state.FeeGrowthGlobal0X128, upperInfo.FeeGrowthOutside0X128);
            feeGrowthAbove1 = WrappingSub(state.FeeGrowthGlobal1X128, upperInfo.FeeGrowthOutside1X128);
        }

        // inside = global - below - above (wrapping)
        var inside0 = WrappingSub(WrappingSub(state.FeeGrowthGlobal0X128, feeGrowthBelow0), feeGrowthAbove0);
        var inside1 = WrappingSub(WrappingSub(state.FeeGrowthGlobal1X128, feeGrowthBelow1), feeGrowthAbove1);

        return (inside0, inside1);
    }

    /// <summary>
    /// Execute a swap through a concentrated liquidity pool.
    /// Iterates through ticks, consuming liquidity at each price level.
    /// </summary>
    public DexResult Swap(ulong poolId, bool zeroForOne, UInt256 amountIn,
        UInt256 sqrtPriceLimitX96, uint feeBps, ulong currentBlock = 0, ulong deadline = 0)
    {
        if (amountIn.IsZero)
            return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Swap amount is zero");

        if (deadline > 0 && currentBlock > deadline)
            return DexResult.Error(BasaltErrorCode.DexDeadlineExpired, "Swap deadline has passed");

        var poolState = _state.GetConcentratedPoolState(poolId);
        if (poolState == null)
            return DexResult.Error(BasaltErrorCode.DexPoolNotFound, "Concentrated pool not found");

        var state = poolState.Value;

        // Validate price limit
        if (zeroForOne)
        {
            if (sqrtPriceLimitX96 >= state.SqrtPriceX96 || sqrtPriceLimitX96 < TickMath.MinSqrtRatio)
                return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Invalid price limit for zeroForOne swap");
        }
        else
        {
            if (sqrtPriceLimitX96 <= state.SqrtPriceX96 || sqrtPriceLimitX96 > TickMath.MaxSqrtRatio)
                return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Invalid price limit for oneForZero swap");
        }

        var result = ExecuteSwapInternal(poolId, zeroForOne, amountIn, sqrtPriceLimitX96, feeBps, state, mutateState: true);

        if (result.Consumed.IsZero)
            return DexResult.Error(BasaltErrorCode.DexInsufficientLiquidity,
                "Swap consumed nothing — insufficient liquidity or iteration limit");

        var swapLogs = new List<EventLog> { MakeLog("ConcentratedSwap", poolId) };

        return DexResult.ConcentratedResult(
            poolId,
            zeroForOne ? result.Consumed : result.Output,
            zeroForOne ? result.Output : result.Consumed,
            swapLogs);
    }

    /// <summary>
    /// Read-only simulation of a concentrated liquidity swap.
    /// Returns the amounts consumed/output WITHOUT modifying any state.
    /// Used by the batch auction solver to estimate AMM output at different price levels.
    /// </summary>
    public (UInt256 AmountConsumed, UInt256 AmountOut)? SimulateSwap(
        ulong poolId, bool zeroForOne, UInt256 amountIn, UInt256 sqrtPriceLimitX96, uint feeBps)
    {
        if (amountIn.IsZero) return null;

        var poolState = _state.GetConcentratedPoolState(poolId);
        if (poolState == null) return null;

        var state = poolState.Value;

        if (zeroForOne)
        {
            if (sqrtPriceLimitX96 >= state.SqrtPriceX96 || sqrtPriceLimitX96 < TickMath.MinSqrtRatio)
                return null;
        }
        else
        {
            if (sqrtPriceLimitX96 <= state.SqrtPriceX96 || sqrtPriceLimitX96 > TickMath.MaxSqrtRatio)
                return null;
        }

        var result = ExecuteSwapInternal(poolId, zeroForOne, amountIn, sqrtPriceLimitX96, feeBps, state, mutateState: false);
        return (result.Consumed, result.Output);
    }

    // ─── Shared Swap Logic (L-06: eliminates Swap/SimulateSwap duplication) ───

    private (UInt256 Consumed, UInt256 Output, UInt256 SqrtPrice, int Tick, UInt256 Liquidity) ExecuteSwapInternal(
        ulong poolId, bool zeroForOne, UInt256 amountIn, UInt256 sqrtPriceLimitX96, uint feeBps,
        ConcentratedPoolState state, bool mutateState)
    {
        var amountRemaining = amountIn;
        var totalAmountOut = UInt256.Zero;
        var currentSqrtPrice = state.SqrtPriceX96;
        var currentTick = state.CurrentTick;
        var currentLiquidity = state.TotalLiquidity;

        // Fee growth accumulators (initialized from pool state)
        var feeGrowthGlobal0X128 = state.FeeGrowthGlobal0X128;
        var feeGrowthGlobal1X128 = state.FeeGrowthGlobal1X128;

        int iterations = 0;
        const int maxIterations = 100_000; // H-08: increased from 1000

        while (!amountRemaining.IsZero && iterations < maxIterations)
        {
            iterations++;

            // Check price limit
            if (zeroForOne && currentSqrtPrice <= sqrtPriceLimitX96) break;
            if (!zeroForOne && currentSqrtPrice >= sqrtPriceLimitX96) break;

            if (currentLiquidity.IsZero)
            {
                // No liquidity at current tick — advance to next initialized tick
                var nextTick = FindNextInitializedTick(poolId, currentTick, zeroForOne);
                if (nextTick == null) break;

                currentTick = nextTick.Value;
                currentSqrtPrice = TickMath.GetSqrtRatioAtTick(currentTick);

                // Cross the tick to pick up liquidity
                var tickInfo = _state.GetTickInfo(poolId, currentTick);
                currentLiquidity = LiquidityMath.AddDelta(currentLiquidity,
                    zeroForOne ? -tickInfo.LiquidityNet : tickInfo.LiquidityNet);
                continue;
            }

            // H-07: Find next initialized tick boundary (not just currentTick ± 1)
            var nextInitTick = FindNextInitializedTick(poolId, currentTick, zeroForOne);
            int targetTick = nextInitTick ?? (zeroForOne ? TickMath.MinTick : TickMath.MaxTick);
            var targetSqrtPrice = TickMath.GetSqrtRatioAtTick(targetTick);

            // Clamp to price limit
            if (zeroForOne && targetSqrtPrice < sqrtPriceLimitX96)
                targetSqrtPrice = sqrtPriceLimitX96;
            if (!zeroForOne && targetSqrtPrice > sqrtPriceLimitX96)
                targetSqrtPrice = sqrtPriceLimitX96;

            // L-05: Deduct fee from input before computing swap step
            var effectiveRemaining = amountRemaining;
            UInt256 feeAmount = UInt256.Zero;
            if (feeBps > 0)
            {
                feeAmount = FullMath.MulDiv(amountRemaining, new UInt256(feeBps), new UInt256(10_000));
                effectiveRemaining = amountRemaining - feeAmount;
            }

            // Compute how much input to consume at this price level
            UInt256 nextSqrtPrice;
            bool crossTick;
            try
            {
                nextSqrtPrice = SqrtPriceMath.GetNextSqrtPriceFromInput(
                    currentSqrtPrice, currentLiquidity, effectiveRemaining, zeroForOne);

                if (zeroForOne)
                    crossTick = nextSqrtPrice <= targetSqrtPrice;
                else
                    crossTick = nextSqrtPrice >= targetSqrtPrice;
            }
            catch (OverflowException)
            {
                // Amount is so large it overflows — we'll definitely cross the tick
                nextSqrtPrice = targetSqrtPrice;
                crossTick = true;
            }

            UInt256 amountInStep, amountOutStep;

            if (crossTick)
            {
                // We'll reach the tick boundary — compute exact amounts for this step
                amountInStep = zeroForOne
                    ? SqrtPriceMath.GetAmount0Delta(targetSqrtPrice, currentSqrtPrice, currentLiquidity, roundUp: true)
                    : SqrtPriceMath.GetAmount1Delta(currentSqrtPrice, targetSqrtPrice, currentLiquidity, roundUp: true);

                amountOutStep = zeroForOne
                    ? SqrtPriceMath.GetAmount1Delta(targetSqrtPrice, currentSqrtPrice, currentLiquidity, roundUp: false)
                    : SqrtPriceMath.GetAmount0Delta(currentSqrtPrice, targetSqrtPrice, currentLiquidity, roundUp: false);

                // Ensure we don't consume more than effective remaining
                if (amountInStep > effectiveRemaining)
                {
                    amountInStep = effectiveRemaining;
                    nextSqrtPrice = SqrtPriceMath.GetNextSqrtPriceFromInput(
                        currentSqrtPrice, currentLiquidity, effectiveRemaining, zeroForOne);
                    amountOutStep = zeroForOne
                        ? SqrtPriceMath.GetAmount1Delta(nextSqrtPrice, currentSqrtPrice, currentLiquidity, roundUp: false)
                        : SqrtPriceMath.GetAmount0Delta(currentSqrtPrice, nextSqrtPrice, currentLiquidity, roundUp: false);
                    crossTick = false;
                }

                currentSqrtPrice = crossTick ? targetSqrtPrice : nextSqrtPrice;
            }
            else
            {
                // All effective remaining input consumed within this tick range
                amountInStep = effectiveRemaining;
                amountOutStep = zeroForOne
                    ? SqrtPriceMath.GetAmount1Delta(nextSqrtPrice, currentSqrtPrice, currentLiquidity, roundUp: false)
                    : SqrtPriceMath.GetAmount0Delta(currentSqrtPrice, nextSqrtPrice, currentLiquidity, roundUp: false);
                currentSqrtPrice = nextSqrtPrice;
            }

            // Total consumed this step = amountInStep + proportional fee
            var stepFee = feeBps > 0
                ? FullMath.MulDivRoundingUp(amountInStep, new UInt256(feeBps), new UInt256(10_000 - feeBps))
                : UInt256.Zero;
            var totalStepConsumed = UInt256.CheckedAdd(amountInStep, stepFee);
            if (totalStepConsumed > amountRemaining)
                totalStepConsumed = amountRemaining;

            // Accumulate fee growth per unit of liquidity (Q128)
            if (!stepFee.IsZero && !currentLiquidity.IsZero)
            {
                if (zeroForOne)
                    feeGrowthGlobal0X128 += FullMath.MulDiv(stepFee, Q128, currentLiquidity);
                else
                    feeGrowthGlobal1X128 += FullMath.MulDiv(stepFee, Q128, currentLiquidity);
            }

            amountRemaining = amountRemaining >= totalStepConsumed
                ? UInt256.CheckedSub(amountRemaining, totalStepConsumed)
                : UInt256.Zero;
            totalAmountOut = UInt256.CheckedAdd(totalAmountOut, amountOutStep);

            // M-05: Cross the targeted initialized tick (not currentTick ± 1)
            if (crossTick && nextInitTick.HasValue)
            {
                var crossTickInfo = _state.GetTickInfo(poolId, nextInitTick.Value);
                if (!crossTickInfo.LiquidityGross.IsZero)
                {
                    // Flip fee growth outside on tick crossing (before applying liquidity delta)
                    crossTickInfo.FeeGrowthOutside0X128 = WrappingSub(feeGrowthGlobal0X128, crossTickInfo.FeeGrowthOutside0X128);
                    crossTickInfo.FeeGrowthOutside1X128 = WrappingSub(feeGrowthGlobal1X128, crossTickInfo.FeeGrowthOutside1X128);
                    if (mutateState)
                        _state.SetTickInfo(poolId, nextInitTick.Value, crossTickInfo);

                    currentLiquidity = LiquidityMath.AddDelta(currentLiquidity,
                        zeroForOne ? -crossTickInfo.LiquidityNet : crossTickInfo.LiquidityNet);
                }
                currentTick = zeroForOne ? nextInitTick.Value - 1 : nextInitTick.Value;
            }
            else
            {
                currentTick = TickMath.GetTickAtSqrtRatio(currentSqrtPrice);
            }
        }

        if (mutateState)
        {
            state.SqrtPriceX96 = currentSqrtPrice;
            state.CurrentTick = currentTick;
            state.TotalLiquidity = currentLiquidity;
            state.FeeGrowthGlobal0X128 = feeGrowthGlobal0X128;
            state.FeeGrowthGlobal1X128 = feeGrowthGlobal1X128;
            _state.SetConcentratedPoolState(poolId, state);
        }

        var amountConsumed = UInt256.CheckedSub(amountIn, amountRemaining);
        return (amountConsumed, totalAmountOut, currentSqrtPrice, currentTick, currentLiquidity);
    }

    // ─── Private Helpers ───

    /// <summary>
    /// Wrapping subtraction for fee math (handles underflow modulo 2^256).
    /// </summary>
    internal static UInt256 WrappingSub(UInt256 a, UInt256 b)
    {
        if (a >= b) return a - b;
        return FullMath.FromBig(
            (FullMath.ToBig(a) - FullMath.ToBig(b) + (BigInteger.One << 256))
            % (BigInteger.One << 256));
    }

    private void UpdateTick(ulong poolId, int tick, UInt256 liquidityDelta, bool isLower, bool remove = false)
    {
        var info = _state.GetTickInfo(poolId, tick);
        var wasInitialized = !info.LiquidityGross.IsZero;
        long delta = checked((long)(ulong)liquidityDelta);

        if (remove)
        {
            info.LiquidityGross = UInt256.CheckedSub(info.LiquidityGross, liquidityDelta);
            if (isLower)
                info.LiquidityNet = checked(info.LiquidityNet - delta);
            else
                info.LiquidityNet = checked(info.LiquidityNet + delta);
        }
        else
        {
            info.LiquidityGross = UInt256.CheckedAdd(info.LiquidityGross, liquidityDelta);
            if (isLower)
                info.LiquidityNet = checked(info.LiquidityNet + delta);
            else
                info.LiquidityNet = checked(info.LiquidityNet - delta);
        }

        var isInitialized = !info.LiquidityGross.IsZero;

        // Initialize fee growth outside when tick transitions from uninitialized to initialized
        if (!wasInitialized && isInitialized && !remove)
        {
            var poolState = _state.GetConcentratedPoolState(poolId);
            if (poolState != null && poolState.Value.CurrentTick >= tick)
            {
                // Tick is below or at current price — set outside to global
                info.FeeGrowthOutside0X128 = poolState.Value.FeeGrowthGlobal0X128;
                info.FeeGrowthOutside1X128 = poolState.Value.FeeGrowthGlobal1X128;
            }
            // else: tick above current price — outside starts at 0 (default)
        }

        // Flip tick bitmap bit on initialization/de-initialization transitions
        if (wasInitialized != isInitialized)
            _state.FlipTickBit(poolId, tick);

        if (info.LiquidityGross.IsZero)
            _state.DeleteTickInfo(poolId, tick);
        else
            _state.SetTickInfo(poolId, tick, info);
    }

    private static EventLog MakeLog(string eventName, ulong id)
    {
        var sigBytes = System.Text.Encoding.UTF8.GetBytes("Dex." + eventName);
        var eventSig = Blake3Hasher.Hash(sigBytes);
        var data = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data, id);
        return new EventLog
        {
            Contract = DexState.DexAddress,
            EventSignature = eventSig,
            Topics = [],
            Data = data,
        };
    }

    /// <summary>
    /// Find the next initialized tick using the tick bitmap.
    /// Each bitmap word covers 256 ticks; bit operations find the nearest set bit
    /// in O(words scanned) instead of O(ticks scanned).
    /// </summary>
    private int? FindNextInitializedTick(ulong poolId, int currentTick, bool searchDown)
    {
        if (searchDown)
        {
            // Find highest initialized tick strictly below currentTick
            int searchTick = currentTick - 1;
            if (searchTick < TickMath.MinTick) return null;

            int wordPos = searchTick >> 8;
            int bitPos = searchTick & 0xFF;

            // Mask: keep only bits 0..bitPos (inclusive) in the first word
            var mask = bitPos == 255 ? ~UInt256.Zero : (UInt256.One << (bitPos + 1)) - UInt256.One;

            // Scan up to 400 words (~102,400 ticks)
            int minWordPos = TickMath.MinTick >> 8;
            for (int w = wordPos; w >= minWordPos; w--)
            {
                var word = _state.GetTickBitmapWord(poolId, w);
                var masked = w == wordPos ? (word & mask) : word;

                if (!masked.IsZero)
                {
                    int highBit = MostSignificantBit(masked);
                    return w * 256 + highBit;
                }
            }
            return null;
        }
        else
        {
            // Find lowest initialized tick strictly above currentTick
            int searchTick = currentTick + 1;
            if (searchTick > TickMath.MaxTick) return null;

            int wordPos = searchTick >> 8;
            int bitPos = searchTick & 0xFF;

            // Mask: keep only bits bitPos..255 (inclusive) in the first word
            var mask = ~((UInt256.One << bitPos) - UInt256.One);

            // Scan up to 400 words (~102,400 ticks)
            int maxWordPos = TickMath.MaxTick >> 8;
            for (int w = wordPos; w <= maxWordPos; w++)
            {
                var word = _state.GetTickBitmapWord(poolId, w);
                var masked = w == wordPos ? (word & mask) : word;

                if (!masked.IsZero)
                {
                    int lowBit = LeastSignificantBit(masked);
                    return w * 256 + lowBit;
                }
            }
            return null;
        }
    }

    /// <summary>Find position of the highest set bit in a UInt256 (0-indexed).</summary>
    private static int MostSignificantBit(UInt256 x)
    {
        // Check 64-bit limbs from most significant to least
        if ((ulong)(x.Hi >> 64) != 0) return 192 + BitHighest((ulong)(x.Hi >> 64));
        if ((ulong)x.Hi != 0) return 128 + BitHighest((ulong)x.Hi);
        if ((ulong)(x.Lo >> 64) != 0) return 64 + BitHighest((ulong)(x.Lo >> 64));
        return BitHighest((ulong)x.Lo);
    }

    /// <summary>Find position of the lowest set bit in a UInt256 (0-indexed).</summary>
    private static int LeastSignificantBit(UInt256 x)
    {
        if ((ulong)x.Lo != 0) return BitLowest((ulong)x.Lo);
        if ((ulong)(x.Lo >> 64) != 0) return 64 + BitLowest((ulong)(x.Lo >> 64));
        if ((ulong)x.Hi != 0) return 128 + BitLowest((ulong)x.Hi);
        return 192 + BitLowest((ulong)(x.Hi >> 64));
    }

    private static int BitHighest(ulong v) => 63 - System.Numerics.BitOperations.LeadingZeroCount(v);
    private static int BitLowest(ulong v) => System.Numerics.BitOperations.TrailingZeroCount(v);
}
