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

        // Save position
        _state.SetPosition(positionId, new Position
        {
            Owner = sender,
            PoolId = poolId,
            TickLower = tickLower,
            TickUpper = tickUpper,
            Liquidity = liquidity,
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

        // Compute token amounts to return
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
            _state.DeletePosition(positionId);
        }
        else
        {
            position.Liquidity = UInt256.CheckedSub(position.Liquidity, liquidityToBurn);
            _state.SetPosition(positionId, position);
        }

        var logs = new List<EventLog> { MakeLog("BurnPosition", positionId) };
        return DexResult.ConcentratedResult(position.PoolId, amount0, amount1, logs);
    }

    /// <summary>
    /// Execute a swap through a concentrated liquidity pool.
    /// Iterates through ticks, consuming liquidity at each price level.
    /// </summary>
    /// <param name="poolId">Pool to swap through.</param>
    /// <param name="zeroForOne">True if swapping token0 → token1 (price decreases).</param>
    /// <param name="amountIn">Amount of input token.</param>
    /// <param name="sqrtPriceLimitX96">Price limit — stop swapping if reached.</param>
    /// <returns>Amounts swapped in/out.</returns>
    public DexResult Swap(ulong poolId, bool zeroForOne, UInt256 amountIn, UInt256 sqrtPriceLimitX96)
    {
        if (amountIn.IsZero)
            return DexResult.Error(BasaltErrorCode.DexInvalidAmount, "Swap amount is zero");

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

        var amountRemaining = amountIn;
        var totalAmountOut = UInt256.Zero;
        var currentSqrtPrice = state.SqrtPriceX96;
        var currentTick = state.CurrentTick;
        var currentLiquidity = state.TotalLiquidity;

        // Iterate through price levels, consuming liquidity
        int iterations = 0;
        const int maxIterations = 1000; // Safety limit

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
                if (nextTick == null) break; // No more liquidity

                currentTick = nextTick.Value;
                currentSqrtPrice = TickMath.GetSqrtRatioAtTick(currentTick);

                // Cross the tick to pick up liquidity
                var tickInfo = _state.GetTickInfo(poolId, currentTick);
                currentLiquidity = LiquidityMath.AddDelta(currentLiquidity,
                    zeroForOne ? -tickInfo.LiquidityNet : tickInfo.LiquidityNet);
                continue;
            }

            // Determine the next tick boundary
            var nextTickBoundary = zeroForOne ? currentTick : currentTick + 1;
            var targetSqrtPrice = TickMath.GetSqrtRatioAtTick(
                zeroForOne ? currentTick : currentTick + 1);

            // Clamp to price limit
            if (zeroForOne && targetSqrtPrice < sqrtPriceLimitX96)
                targetSqrtPrice = sqrtPriceLimitX96;
            if (!zeroForOne && targetSqrtPrice > sqrtPriceLimitX96)
                targetSqrtPrice = sqrtPriceLimitX96;

            // Compute how much input to consume at this price level
            var nextSqrtPrice = SqrtPriceMath.GetNextSqrtPriceFromInput(
                currentSqrtPrice, currentLiquidity, amountRemaining, zeroForOne);

            bool crossTick;
            if (zeroForOne)
                crossTick = nextSqrtPrice <= targetSqrtPrice;
            else
                crossTick = nextSqrtPrice >= targetSqrtPrice;

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

                // Ensure we don't consume more than remaining
                if (amountInStep > amountRemaining)
                {
                    amountInStep = amountRemaining;
                    // Recompute with exact remaining amount
                    nextSqrtPrice = SqrtPriceMath.GetNextSqrtPriceFromInput(
                        currentSqrtPrice, currentLiquidity, amountRemaining, zeroForOne);
                    amountOutStep = zeroForOne
                        ? SqrtPriceMath.GetAmount1Delta(nextSqrtPrice, currentSqrtPrice, currentLiquidity, roundUp: false)
                        : SqrtPriceMath.GetAmount0Delta(currentSqrtPrice, nextSqrtPrice, currentLiquidity, roundUp: false);
                    crossTick = false;
                }

                currentSqrtPrice = crossTick ? targetSqrtPrice : nextSqrtPrice;
            }
            else
            {
                // All remaining input consumed within this tick range
                amountInStep = amountRemaining;
                amountOutStep = zeroForOne
                    ? SqrtPriceMath.GetAmount1Delta(nextSqrtPrice, currentSqrtPrice, currentLiquidity, roundUp: false)
                    : SqrtPriceMath.GetAmount0Delta(currentSqrtPrice, nextSqrtPrice, currentLiquidity, roundUp: false);
                currentSqrtPrice = nextSqrtPrice;
            }

            amountRemaining = amountRemaining >= amountInStep
                ? UInt256.CheckedSub(amountRemaining, amountInStep)
                : UInt256.Zero;
            totalAmountOut = UInt256.CheckedAdd(totalAmountOut, amountOutStep);

            // Cross tick if needed
            if (crossTick)
            {
                var nextInitTick = zeroForOne ? currentTick - 1 : currentTick + 1;
                var tickInfo = _state.GetTickInfo(poolId, nextInitTick);
                if (!tickInfo.LiquidityGross.IsZero)
                {
                    currentLiquidity = LiquidityMath.AddDelta(currentLiquidity,
                        zeroForOne ? -tickInfo.LiquidityNet : tickInfo.LiquidityNet);
                }
                currentTick = zeroForOne ? currentTick - 1 : currentTick + 1;
            }
            else
            {
                currentTick = TickMath.GetTickAtSqrtRatio(currentSqrtPrice);
            }
        }

        // Update pool state
        state.SqrtPriceX96 = currentSqrtPrice;
        state.CurrentTick = currentTick;
        state.TotalLiquidity = currentLiquidity;
        _state.SetConcentratedPoolState(poolId, state);

        var amountConsumed = UInt256.CheckedSub(amountIn, amountRemaining);

        var swapLogs = new List<EventLog> { MakeLog("ConcentratedSwap", poolId) };

        return DexResult.ConcentratedResult(
            poolId,
            zeroForOne ? amountConsumed : totalAmountOut,
            zeroForOne ? totalAmountOut : amountConsumed,
            swapLogs);
    }

    // ─── Private Helpers ───

    private void UpdateTick(ulong poolId, int tick, UInt256 liquidityDelta, bool isLower, bool remove = false)
    {
        var info = _state.GetTickInfo(poolId, tick);

        if (remove)
        {
            info.LiquidityGross = UInt256.CheckedSub(info.LiquidityGross, liquidityDelta);
            // For lower tick: subtract delta (was positive). For upper tick: add delta (was negative).
            if (isLower)
                info.LiquidityNet -= (long)(ulong)liquidityDelta;
            else
                info.LiquidityNet += (long)(ulong)liquidityDelta;
        }
        else
        {
            info.LiquidityGross = UInt256.CheckedAdd(info.LiquidityGross, liquidityDelta);
            // For lower tick: add delta (liquidity enters). For upper tick: subtract delta (liquidity exits).
            if (isLower)
                info.LiquidityNet += (long)(ulong)liquidityDelta;
            else
                info.LiquidityNet -= (long)(ulong)liquidityDelta;
        }

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
    /// Scan for the next initialized tick in the given direction.
    /// Simple linear scan — sufficient for moderate tick density.
    /// </summary>
    private int? FindNextInitializedTick(ulong poolId, int currentTick, bool searchDown)
    {
        int step = searchDown ? -1 : 1;
        int limit = searchDown ? TickMath.MinTick : TickMath.MaxTick;

        // Scan up to 1000 ticks in either direction
        for (int i = 1; i <= 1000; i++)
        {
            int candidate = currentTick + step * i;
            if (searchDown && candidate < limit) return null;
            if (!searchDown && candidate > limit) return null;

            var info = _state.GetTickInfo(poolId, candidate);
            if (!info.LiquidityGross.IsZero)
                return candidate;
        }

        return null;
    }
}
