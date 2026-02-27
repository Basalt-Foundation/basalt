using System.Numerics;
using Basalt.Core;
using Basalt.Execution.Dex.Math;

namespace Basalt.Execution.Dex;

/// <summary>
/// Computes uniform clearing prices for batch auction settlements.
/// This is the core MEV-elimination mechanism: all swap intents in a block
/// receive the same price, eliminating front-running and sandwich attacks.
///
/// Algorithm overview:
/// <list type="number">
/// <item><description>Collect all critical prices (intent limits, order prices, AMM spot price)</description></item>
/// <item><description>Sweep all prices, selecting the one that maximizes matched volume (C-05: maximum-volume clearing rule)</description></item>
/// <item><description>Ties broken by highest price</description></item>
/// <item><description>At P*: match buyers and sellers peer-to-peer, route residual through AMM</description></item>
/// </list>
///
/// All math uses <see cref="UInt256"/> and <see cref="FullMath.MulDiv"/> for full determinism.
/// Prices are expressed as token1-per-token0, scaled by 2^64 for fixed-point precision.
/// </summary>
public static class BatchAuctionSolver
{
    /// <summary>
    /// Scale factor for fixed-point price representation: 2^64.
    /// All prices in the solver are multiplied by this constant to avoid floating-point.
    /// </summary>
    public static readonly UInt256 PriceScale = new UInt256(1UL << 32) * new UInt256(1UL << 32);

    /// <summary>
    /// Compute the batch settlement for a single trading pair.
    /// Finds the uniform clearing price where supply meets demand, incorporating
    /// AMM reserves as liquidity of last resort.
    /// </summary>
    /// <param name="buyIntents">Intents buying token0 (selling token1), sorted by decreasing limit price.</param>
    /// <param name="sellIntents">Intents selling token0 (buying token1), sorted by increasing limit price.</param>
    /// <param name="buyOrders">Limit buy orders that cross the clearing price.</param>
    /// <param name="sellOrders">Limit sell orders that cross the clearing price.</param>
    /// <param name="reserves">Current AMM reserves.</param>
    /// <param name="feeBps">Pool fee in basis points.</param>
    /// <param name="poolId">The pool ID for this settlement.</param>
    /// <param name="currentBlockNumber">Current block number for deadline enforcement (M-04).</param>
    /// <returns>A <see cref="BatchResult"/> with fills and updated reserves, or null if no settlement.</returns>
    public static BatchResult? ComputeSettlement(
        List<ParsedIntent> buyIntents,
        List<ParsedIntent> sellIntents,
        List<(ulong Id, LimitOrder Order)> buyOrders,
        List<(ulong Id, LimitOrder Order)> sellOrders,
        PoolReserves reserves,
        uint feeBps,
        ulong poolId,
        DexState? dexState = null,
        ulong currentBlockNumber = 0)
    {
        // M-04: Filter expired intents by deadline
        if (currentBlockNumber > 0)
        {
            buyIntents = buyIntents.Where(i => i.Deadline == 0 || i.Deadline >= currentBlockNumber).ToList();
            sellIntents = sellIntents.Where(i => i.Deadline == 0 || i.Deadline >= currentBlockNumber).ToList();
        }

        if (buyIntents.Count == 0 && sellIntents.Count == 0 &&
            buyOrders.Count == 0 && sellOrders.Count == 0)
            return null;

        if (reserves.Reserve0.IsZero || reserves.Reserve1.IsZero)
        {
            // No AMM liquidity — can only do peer-to-peer if both sides exist
            if (buyIntents.Count == 0 && buyOrders.Count == 0)
                return null;
            if (sellIntents.Count == 0 && sellOrders.Count == 0)
                return null;
        }

        // Extract plain order lists for volume/price computation
        var buyOrderPlain = buyOrders.Select(o => o.Order).ToList();
        var sellOrderPlain = sellOrders.Select(o => o.Order).ToList();

        // Step 1: Collect all critical prices
        var criticalPrices = CollectCriticalPrices(
            buyIntents, sellIntents, buyOrderPlain, sellOrderPlain, reserves, dexState, poolId);

        if (criticalPrices.Count == 0)
            return null;

        // Step 2: C-05: Maximum-volume clearing rule — sweep ALL prices,
        // select the one that maximizes min(buyVol, totalSell).
        // Ties broken by highest price.
        UInt256 clearingPrice = UInt256.Zero;
        UInt256 matchedVolume = UInt256.Zero;

        foreach (var price in criticalPrices)
        {
            if (price.IsZero) continue;

            var buyVol = ComputeBuyVolume(price, buyIntents, buyOrderPlain);
            var sellVol = ComputeSellVolume(price, sellIntents, sellOrderPlain);

            // Include AMM as passive liquidity
            var ammSellVol = ComputeAmmSellVolume(price, reserves, feeBps, dexState, poolId);
            var totalSell = UInt256.CheckedAdd(sellVol, ammSellVol);

            if (buyVol.IsZero || totalSell.IsZero) continue;

            var vol = buyVol < totalSell ? buyVol : totalSell;
            if (vol > matchedVolume || (vol == matchedVolume && price > clearingPrice))
            {
                clearingPrice = price;
                matchedVolume = vol;
            }
        }

        if (clearingPrice.IsZero || matchedVolume.IsZero)
            return null;

        // Step 4: Generate fills at the clearing price
        return GenerateFills(
            clearingPrice, matchedVolume,
            buyIntents, sellIntents, buyOrders, sellOrders,
            reserves, feeBps, poolId);
    }

    /// <summary>
    /// Compute the AMM spot price: reserve1 / reserve0, scaled by PriceScale.
    /// This is the marginal price for an infinitesimally small trade.
    /// </summary>
    public static UInt256 ComputeSpotPrice(UInt256 reserve0, UInt256 reserve1)
    {
        if (reserve0.IsZero) return UInt256.Zero;
        return FullMath.MulDiv(reserve1, PriceScale, reserve0);
    }

    // ────────── Critical Price Collection ──────────

    private static List<UInt256> CollectCriticalPrices(
        List<ParsedIntent> buyIntents,
        List<ParsedIntent> sellIntents,
        List<LimitOrder> buyOrders,
        List<LimitOrder> sellOrders,
        PoolReserves reserves,
        DexState? dexState = null,
        ulong poolId = 0)
    {
        var prices = new HashSet<UInt256>();

        foreach (var intent in buyIntents)
        {
            var lp = intent.LimitPrice;
            if (!lp.IsZero && lp != UInt256.MaxValue)
                prices.Add(lp);
        }

        foreach (var intent in sellIntents)
        {
            // H-04: Sell intent limit price = MinAmountOut * PriceScale / AmountIn (token1/token0)
            // ParsedIntent.LimitPrice computes AmountIn/MinAmountOut which is inverted for sell intents.
            if (!intent.AmountIn.IsZero && !intent.MinAmountOut.IsZero)
            {
                var correctPrice = FullMath.MulDiv(intent.MinAmountOut, PriceScale, intent.AmountIn);
                if (!correctPrice.IsZero && correctPrice != UInt256.MaxValue)
                    prices.Add(correctPrice);
            }
        }

        foreach (var order in buyOrders)
            if (!order.Price.IsZero)
                prices.Add(order.Price);

        foreach (var order in sellOrders)
            if (!order.Price.IsZero)
                prices.Add(order.Price);

        // AMM spot price — use concentrated pool price if available
        var concentratedSpot = ComputeConcentratedSpotPrice(dexState, poolId);
        if (!concentratedSpot.IsZero)
        {
            prices.Add(concentratedSpot);
        }
        else if (!reserves.Reserve0.IsZero && !reserves.Reserve1.IsZero)
        {
            prices.Add(ComputeSpotPrice(reserves.Reserve0, reserves.Reserve1));
        }

        return prices.ToList();
    }

    // ────────── Volume Computation ──────────

    /// <summary>
    /// Aggregate buy volume at price P: sum of all buy intents and orders whose limit
    /// price >= P (willing to pay at least P).
    /// </summary>
    private static UInt256 ComputeBuyVolume(
        UInt256 price,
        List<ParsedIntent> buyIntents,
        List<LimitOrder> buyOrders)
    {
        var vol = UInt256.Zero;

        foreach (var intent in buyIntents)
        {
            // Buy intent: limit price is the max they'll pay
            // They're in if their limit price >= clearing price
            if (intent.LimitPrice >= price)
            {
                // Convert their token1 input to token0 output at the clearing price
                // volume = amountIn * PriceScale / price (token0 units)
                var token0Vol = FullMath.MulDiv(intent.AmountIn, PriceScale, price);
                vol = UInt256.CheckedAdd(vol, token0Vol); // L-09: checked add
            }
        }

        foreach (var order in buyOrders)
        {
            if (order.Price >= price)
            {
                // Buy order amount is in token1; convert to token0
                var token0Vol = FullMath.MulDiv(order.Amount, PriceScale, price);
                vol = UInt256.CheckedAdd(vol, token0Vol); // L-09: checked add
            }
        }

        return vol;
    }

    /// <summary>
    /// Aggregate sell volume at price P: sum of all sell intents and orders whose limit
    /// price <= P (willing to accept at least P).
    /// </summary>
    private static UInt256 ComputeSellVolume(
        UInt256 price,
        List<ParsedIntent> sellIntents,
        List<LimitOrder> sellOrders)
    {
        var vol = UInt256.Zero;

        foreach (var intent in sellIntents)
        {
            // Sell intent: they're selling token0, their limit is min price they'll accept
            var minPrice = intent.MinAmountOut.IsZero
                ? UInt256.Zero
                : FullMath.MulDiv(intent.MinAmountOut, PriceScale, intent.AmountIn);

            if (price >= minPrice)
                vol = UInt256.CheckedAdd(vol, intent.AmountIn); // L-09: checked add
        }

        foreach (var order in sellOrders)
        {
            // Sell order: sells token0 at minimum price
            if (price >= order.Price)
                vol = UInt256.CheckedAdd(vol, order.Amount); // L-09: checked add
        }

        return vol;
    }

    /// <summary>
    /// Compute how much token0 the AMM can provide at price P.
    /// Detects whether the pool uses concentrated liquidity or constant-product,
    /// and delegates to the appropriate computation.
    /// </summary>
    private static UInt256 ComputeAmmSellVolume(
        UInt256 price, PoolReserves reserves, uint feeBps,
        DexState? dexState = null, ulong poolId = 0)
    {
        // Check for concentrated liquidity pool
        if (dexState != null)
        {
            var clState = dexState.GetConcentratedPoolState(poolId);
            if (clState != null && !clState.Value.SqrtPriceX96.IsZero)
                return ComputeConcentratedAmmSellVolume(price, clState.Value, dexState, poolId, feeBps);
        }

        // Fall back to constant-product
        return ComputeConstantProductAmmSellVolume(price, reserves, feeBps);
    }

    /// <summary>
    /// L-02: Constant-product AMM sell volume using BigInteger for overflow safety.
    /// </summary>
    private static UInt256 ComputeConstantProductAmmSellVolume(
        UInt256 price, PoolReserves reserves, uint feeBps)
    {
        if (reserves.Reserve0.IsZero || reserves.Reserve1.IsZero)
            return UInt256.Zero;

        var spotPrice = ComputeSpotPrice(reserves.Reserve0, reserves.Reserve1);

        // AMM sells token0 when price goes up (buying pressure pushes price above spot)
        if (price <= spotPrice)
            return UInt256.Zero;

        // L-02: Use BigInteger for k to prevent overflow
        var k = FullMath.ToBig(reserves.Reserve0) * FullMath.ToBig(reserves.Reserve1);
        var newRes0Sq = k * FullMath.ToBig(PriceScale) / FullMath.ToBig(price);
        var newRes0 = BigIntegerSqrt(newRes0Sq);
        var bigRes0 = FullMath.ToBig(reserves.Reserve0);

        if (newRes0 >= bigRes0)
            return UInt256.Zero;

        var ammOutputBig = bigRes0 - newRes0;
        if (ammOutputBig.Sign <= 0)
            return UInt256.Zero;

        // Convert back to UInt256
        var ammOutputBytes = ammOutputBig.ToByteArray(isUnsigned: true);
        if (ammOutputBytes.Length > 32)
            return UInt256.Zero; // Overflow protection

        Span<byte> padded = stackalloc byte[32];
        padded.Clear();
        ammOutputBytes.CopyTo(padded);
        var ammOutput = new UInt256(padded);

        // Apply fee discount: actual output is less due to fees
        var feeComplement = new UInt256(10_000 - feeBps);
        return FullMath.MulDiv(ammOutput, feeComplement, new UInt256(10_000));
    }

    /// <summary>
    /// Compute how much token0 a concentrated liquidity pool can sell at price P.
    /// Uses read-only tick-walking simulation via <see cref="ConcentratedPool.SimulateSwap"/>.
    /// </summary>
    private static UInt256 ComputeConcentratedAmmSellVolume(
        UInt256 price, ConcentratedPoolState clState, DexState dexState, ulong poolId, uint feeBps)
    {
        var clSpot = ComputeConcentratedSpotPrice(dexState, poolId);

        // AMM sells token0 when price goes up (buyers push price above spot)
        if (price <= clSpot || clSpot.IsZero)
            return UInt256.Zero;

        // M-02: Convert solver price to sqrtPriceX96
        // solverPrice = token1/token0 * 2^64
        // sqrtPriceX96 = sqrt(realPrice) * 2^96 = sqrt(solverPrice / 2^64) * 2^96
        //              = sqrt(solverPrice) * 2^(96-32) = sqrt(solverPrice) * 2^64
        var sqrtSolverPrice = FullMath.Sqrt(price);
        var targetSqrtPriceX96 = FullMath.MulDiv(sqrtSolverPrice, new UInt256(1UL << 32) * new UInt256(1UL << 32), UInt256.One);

        // Clamp to valid range
        if (targetSqrtPriceX96 > Math.TickMath.MaxSqrtRatio)
            targetSqrtPriceX96 = Math.TickMath.MaxSqrtRatio;
        if (targetSqrtPriceX96 <= clState.SqrtPriceX96)
            return UInt256.Zero;

        // Simulate a oneForZero swap (buying token0, selling token1)
        // H-05: Pass feeBps to SimulateSwap (which now handles fees internally)
        var pool = new ConcentratedPool(dexState);
        var simResult = pool.SimulateSwap(poolId, false, UInt256.MaxValue / new UInt256(2), targetSqrtPriceX96, feeBps);
        if (simResult == null)
            return UInt256.Zero;

        // H-05: SimulateSwap now includes fee handling internally — no additional fee discount
        return simResult.Value.AmountOut;
    }

    /// <summary>
    /// Compute the spot price for a concentrated liquidity pool.
    /// Returns price in solver format: token1/token0 * PriceScale.
    /// </summary>
    private static UInt256 ComputeConcentratedSpotPrice(DexState? dexState, ulong poolId)
    {
        if (dexState == null) return UInt256.Zero;
        var clState = dexState.GetConcentratedPoolState(poolId);
        if (clState == null || clState.Value.SqrtPriceX96.IsZero) return UInt256.Zero;

        // sqrtPriceX96 = sqrt(price) * 2^96
        // price = sqrtPriceX96^2 / 2^192
        // solverPrice = price * PriceScale = sqrtPriceX96^2 * 2^64 / 2^192 = sqrtPriceX96^2 / 2^128
        var sqrtP = clState.Value.SqrtPriceX96;
        var scale128 = new UInt256(1UL << 32) * new UInt256(1UL << 32) * new UInt256(1UL << 32) * new UInt256(1UL << 32);
        return FullMath.MulDiv(sqrtP, sqrtP, scale128);
    }

    // ────────── Fill Generation ──────────

    private static BatchResult GenerateFills(
        UInt256 clearingPrice, UInt256 matchedVolume,
        List<ParsedIntent> buyIntents, List<ParsedIntent> sellIntents,
        List<(ulong Id, LimitOrder Order)> buyOrders, List<(ulong Id, LimitOrder Order)> sellOrders,
        PoolReserves reserves, uint feeBps, ulong poolId)
    {
        var fills = new List<FillRecord>();
        var remainingBuyVolume = matchedVolume; // in token0 units
        var remainingSellVolume = matchedVolume; // in token0 units

        // Step 1: Fill sell-side (intents and orders providing token0)
        var peerSellVolume = UInt256.Zero;

        foreach (var intent in sellIntents)
        {
            if (remainingSellVolume.IsZero) break;

            var minPrice = intent.MinAmountOut.IsZero
                ? UInt256.Zero
                : FullMath.MulDiv(intent.MinAmountOut, PriceScale, intent.AmountIn);

            if (clearingPrice < minPrice) continue;

            var fillAmount0 = intent.AmountIn < remainingSellVolume ? intent.AmountIn : remainingSellVolume;

            // M-03: Enforce AllowPartialFill
            if (!intent.AllowPartialFill && fillAmount0 < intent.AmountIn)
                continue;

            // token1 output = fillAmount0 * clearingPrice / PriceScale
            var fillAmount1 = FullMath.MulDiv(fillAmount0, clearingPrice, PriceScale);

            fills.Add(new FillRecord
            {
                Participant = intent.Sender,
                AmountIn = fillAmount0,
                AmountOut = fillAmount1,
                IsLimitOrder = false,
                IsBuy = false,
                TxHash = intent.TxHash,
            });

            remainingSellVolume = UInt256.CheckedSub(remainingSellVolume, fillAmount0); // L-09
            peerSellVolume = UInt256.CheckedAdd(peerSellVolume, fillAmount0); // L-09
        }

        foreach (var (orderId, order) in sellOrders)
        {
            if (remainingSellVolume.IsZero) break;
            if (clearingPrice < order.Price) continue;

            var fillAmount0 = order.Amount < remainingSellVolume ? order.Amount : remainingSellVolume;
            var fillAmount1 = FullMath.MulDiv(fillAmount0, clearingPrice, PriceScale);

            fills.Add(new FillRecord
            {
                Participant = order.Owner,
                AmountIn = fillAmount0,
                AmountOut = fillAmount1,
                IsLimitOrder = true,
                IsBuy = false,
                OrderId = orderId,
            });

            remainingSellVolume = UInt256.CheckedSub(remainingSellVolume, fillAmount0); // L-09
            peerSellVolume = UInt256.CheckedAdd(peerSellVolume, fillAmount0); // L-09
        }

        // Step 2: Fill buy-side (intents and orders wanting token0)
        var peerBuyVolume = UInt256.Zero;

        foreach (var intent in buyIntents)
        {
            if (remainingBuyVolume.IsZero) break;
            if (intent.LimitPrice < clearingPrice) continue;

            // Convert intent's token1 input to token0 at clearing price
            var token0Want = FullMath.MulDiv(intent.AmountIn, PriceScale, clearingPrice);
            var fillAmount0 = token0Want < remainingBuyVolume ? token0Want : remainingBuyVolume;

            // M-03: Enforce AllowPartialFill
            if (!intent.AllowPartialFill && fillAmount0 < token0Want)
                continue;

            var fillAmount1 = FullMath.MulDiv(fillAmount0, clearingPrice, PriceScale);

            fills.Add(new FillRecord
            {
                Participant = intent.Sender,
                AmountIn = fillAmount1, // They pay token1
                AmountOut = fillAmount0, // They receive token0
                IsLimitOrder = false,
                IsBuy = true,
                TxHash = intent.TxHash,
            });

            remainingBuyVolume = UInt256.CheckedSub(remainingBuyVolume, fillAmount0); // L-09
            peerBuyVolume = UInt256.CheckedAdd(peerBuyVolume, fillAmount0); // L-09
        }

        foreach (var (orderId, order) in buyOrders)
        {
            if (remainingBuyVolume.IsZero) break;
            if (order.Price < clearingPrice) continue;

            var token0Want = FullMath.MulDiv(order.Amount, PriceScale, clearingPrice);
            var isFullFill = token0Want <= remainingBuyVolume;
            var fillAmount0 = isFullFill ? token0Want : remainingBuyVolume;
            var fillAmount1 = isFullFill ? order.Amount : FullMath.MulDiv(fillAmount0, clearingPrice, PriceScale);

            fills.Add(new FillRecord
            {
                Participant = order.Owner,
                AmountIn = fillAmount1,
                AmountOut = fillAmount0,
                IsLimitOrder = true,
                IsBuy = true,
                OrderId = orderId,
            });

            remainingBuyVolume = UInt256.CheckedSub(remainingBuyVolume, fillAmount0); // L-09
            peerBuyVolume = UInt256.CheckedAdd(peerBuyVolume, fillAmount0); // L-09
        }

        // C-06: Route residual through AMM based on net imbalance
        var ammVolume = UInt256.Zero;
        var updatedReserves = reserves;

        if (!reserves.Reserve0.IsZero && !reserves.Reserve1.IsZero)
        {
            if (remainingBuyVolume > remainingSellVolume)
            {
                // Net buy pressure: AMM sells token0, receives token1
                var netBuy = UInt256.CheckedSub(remainingBuyVolume, remainingSellVolume);
                var token1Cost = FullMath.MulDiv(netBuy, clearingPrice, PriceScale);
                if (!token1Cost.IsZero)
                {
                    var ammOutput0 = DexLibrary.GetAmountOut(token1Cost, reserves.Reserve1, reserves.Reserve0, feeBps);
                    ammVolume = netBuy;
                    updatedReserves = new PoolReserves
                    {
                        Reserve0 = UInt256.CheckedSub(reserves.Reserve0, ammOutput0),
                        Reserve1 = UInt256.CheckedAdd(reserves.Reserve1, token1Cost),
                        TotalSupply = reserves.TotalSupply,
                        KLast = reserves.KLast,
                    };
                }
            }
            else if (remainingSellVolume > remainingBuyVolume)
            {
                // Net sell pressure: AMM buys token0, gives token1
                var netSell = UInt256.CheckedSub(remainingSellVolume, remainingBuyVolume);
                if (!netSell.IsZero)
                {
                    var ammOutput1 = DexLibrary.GetAmountOut(netSell, reserves.Reserve0, reserves.Reserve1, feeBps);
                    ammVolume = netSell;
                    updatedReserves = new PoolReserves
                    {
                        Reserve0 = UInt256.CheckedAdd(reserves.Reserve0, netSell),
                        Reserve1 = UInt256.CheckedSub(reserves.Reserve1, ammOutput1),
                        TotalSupply = reserves.TotalSupply,
                        KLast = reserves.KLast,
                    };
                }
            }
        }

        var totalVolume1 = FullMath.MulDiv(matchedVolume, clearingPrice, PriceScale);

        // L-01: Track AMM direction for solver reward computation.
        // Sell pressure (remainingSellVolume > remainingBuyVolume) means AMM bought token0.
        var ammBoughtToken0 = remainingSellVolume > remainingBuyVolume;

        return new BatchResult
        {
            PoolId = poolId,
            ClearingPrice = clearingPrice,
            TotalVolume0 = matchedVolume,
            TotalVolume1 = totalVolume1,
            AmmVolume = ammVolume,
            AmmBoughtToken0 = ammBoughtToken0,
            Fills = fills,
            UpdatedReserves = updatedReserves,
        };
    }

    /// <summary>
    /// Integer square root for BigInteger via Newton's method.
    /// </summary>
    private static BigInteger BigIntegerSqrt(BigInteger n)
    {
        if (n.IsZero) return BigInteger.Zero;
        if (n.Sign < 0) throw new ArgumentException("Cannot compute sqrt of negative");

        var x = n;
        var y = (x + BigInteger.One) / 2;
        while (y < x)
        {
            x = y;
            y = (x + n / x) / 2;
        }
        return x;
    }
}
