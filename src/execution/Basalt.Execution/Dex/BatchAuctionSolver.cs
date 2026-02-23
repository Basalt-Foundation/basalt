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
/// <item><description>Sort critical prices ascending</description></item>
/// <item><description>For each price P, compute buy volume (demand) and sell volume (supply)</description></item>
/// <item><description>Find P* where demand crosses supply (equilibrium)</description></item>
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
    /// <returns>A <see cref="BatchResult"/> with fills and updated reserves, or null if no settlement.</returns>
    public static BatchResult? ComputeSettlement(
        List<ParsedIntent> buyIntents,
        List<ParsedIntent> sellIntents,
        List<LimitOrder> buyOrders,
        List<LimitOrder> sellOrders,
        PoolReserves reserves,
        uint feeBps,
        ulong poolId)
    {
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

        // Step 1: Collect all critical prices
        var criticalPrices = CollectCriticalPrices(
            buyIntents, sellIntents, buyOrders, sellOrders, reserves);

        if (criticalPrices.Count == 0)
            return null;

        // Step 2: Sort prices ascending
        criticalPrices.Sort();

        // Step 3: Find equilibrium price via linear scan
        // At each price point, compute aggregate buy volume and sell volume.
        // The clearing price is the highest price where buyVolume >= sellVolume.
        UInt256 clearingPrice = UInt256.Zero;
        UInt256 matchedVolume = UInt256.Zero;

        foreach (var price in criticalPrices)
        {
            if (price.IsZero) continue;

            var buyVol = ComputeBuyVolume(price, buyIntents, buyOrders);
            var sellVol = ComputeSellVolume(price, sellIntents, sellOrders);

            // Include AMM as passive liquidity
            // AMM sell volume at price P: how much token0 the AMM can output at price P
            var ammSellVol = ComputeAmmSellVolume(price, reserves, feeBps);
            var totalSell = sellVol + ammSellVol;

            if (buyVol.IsZero || totalSell.IsZero)
                continue;

            // The matched volume is the minimum of buy and sell at this price
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
        PoolReserves reserves)
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
            var lp = intent.LimitPrice;
            if (!lp.IsZero && lp != UInt256.MaxValue)
                prices.Add(lp);
        }

        foreach (var order in buyOrders)
            if (!order.Price.IsZero)
                prices.Add(order.Price);

        foreach (var order in sellOrders)
            if (!order.Price.IsZero)
                prices.Add(order.Price);

        // AMM spot price
        if (!reserves.Reserve0.IsZero && !reserves.Reserve1.IsZero)
            prices.Add(ComputeSpotPrice(reserves.Reserve0, reserves.Reserve1));

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
                vol = UInt256.CheckedAdd(vol, token0Vol);
            }
        }

        foreach (var order in buyOrders)
        {
            if (order.Price >= price)
            {
                // Buy order amount is in token1; convert to token0
                var token0Vol = FullMath.MulDiv(order.Amount, PriceScale, price);
                vol = UInt256.CheckedAdd(vol, token0Vol);
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
            // For sell intents, LimitPrice = amountIn * PriceScale / minAmountOut
            // They need price >= their minimum, but since they're selling token0,
            // their limit price is the inverse — check if clearing price >= their min
            // Actually: sell intent has amountIn of token0, minAmountOut of token1
            // Their limit (min acceptable price) = minAmountOut / amountIn
            // They participate if clearingPrice >= their limit
            var minPrice = intent.MinAmountOut.IsZero
                ? UInt256.Zero
                : FullMath.MulDiv(intent.MinAmountOut, PriceScale, intent.AmountIn);

            if (price >= minPrice)
                vol = UInt256.CheckedAdd(vol, intent.AmountIn);
        }

        foreach (var order in sellOrders)
        {
            // Sell order: sells token0 at minimum price
            if (price >= order.Price)
                vol = UInt256.CheckedAdd(vol, order.Amount);
        }

        return vol;
    }

    /// <summary>
    /// Compute how much token0 the AMM can provide at price P.
    /// Uses the constant-product formula to determine the maximum output
    /// if someone were to move the price from spot to P.
    /// </summary>
    private static UInt256 ComputeAmmSellVolume(
        UInt256 price, PoolReserves reserves, uint feeBps)
    {
        if (reserves.Reserve0.IsZero || reserves.Reserve1.IsZero)
            return UInt256.Zero;

        var spotPrice = ComputeSpotPrice(reserves.Reserve0, reserves.Reserve1);

        // AMM sells token0 when price goes up (buying pressure pushes price above spot)
        if (price <= spotPrice)
            return UInt256.Zero;

        // Compute the token1 input needed to move price from spot to P
        // At price P: newReserve1/newReserve0 = P/PriceScale
        // With constant product: newReserve0 * newReserve1 = k
        // newReserve0 = sqrt(k * PriceScale / P)
        var k = FullMath.MulDiv(reserves.Reserve0, reserves.Reserve1, UInt256.One);
        var newRes0Sq = FullMath.MulDiv(k, PriceScale, price);
        var newRes0 = FullMath.Sqrt(newRes0Sq);

        if (newRes0 >= reserves.Reserve0)
            return UInt256.Zero;

        // The AMM can sell (reserve0 - newReserve0) token0
        var ammOutput = reserves.Reserve0 - newRes0;

        // Apply fee discount: actual output is less due to fees
        var feeComplement = new UInt256(10_000 - feeBps);
        return FullMath.MulDiv(ammOutput, feeComplement, new UInt256(10_000));
    }

    // ────────── Fill Generation ──────────

    private static BatchResult GenerateFills(
        UInt256 clearingPrice, UInt256 matchedVolume,
        List<ParsedIntent> buyIntents, List<ParsedIntent> sellIntents,
        List<LimitOrder> buyOrders, List<LimitOrder> sellOrders,
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
            // token1 output = fillAmount0 * clearingPrice / PriceScale
            var fillAmount1 = FullMath.MulDiv(fillAmount0, clearingPrice, PriceScale);

            fills.Add(new FillRecord
            {
                Participant = intent.Sender,
                AmountIn = fillAmount0,
                AmountOut = fillAmount1,
                IsLimitOrder = false,
                TxHash = intent.TxHash,
            });

            remainingSellVolume = remainingSellVolume - fillAmount0;
            peerSellVolume = peerSellVolume + fillAmount0;
        }

        foreach (var order in sellOrders)
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
                OrderId = 0, // Would need order ID tracking
            });

            remainingSellVolume = remainingSellVolume - fillAmount0;
            peerSellVolume = peerSellVolume + fillAmount0;
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
            var fillAmount1 = FullMath.MulDiv(fillAmount0, clearingPrice, PriceScale);

            fills.Add(new FillRecord
            {
                Participant = intent.Sender,
                AmountIn = fillAmount1, // They pay token1
                AmountOut = fillAmount0, // They receive token0
                IsLimitOrder = false,
                TxHash = intent.TxHash,
            });

            remainingBuyVolume = remainingBuyVolume - fillAmount0;
            peerBuyVolume = peerBuyVolume + fillAmount0;
        }

        foreach (var order in buyOrders)
        {
            if (remainingBuyVolume.IsZero) break;
            if (order.Price < clearingPrice) continue;

            var token0Want = FullMath.MulDiv(order.Amount, PriceScale, clearingPrice);
            var fillAmount0 = token0Want < remainingBuyVolume ? token0Want : remainingBuyVolume;
            var fillAmount1 = FullMath.MulDiv(fillAmount0, clearingPrice, PriceScale);

            fills.Add(new FillRecord
            {
                Participant = order.Owner,
                AmountIn = fillAmount1,
                AmountOut = fillAmount0,
                IsLimitOrder = true,
                OrderId = 0,
            });

            remainingBuyVolume = remainingBuyVolume - fillAmount0;
            peerBuyVolume = peerBuyVolume + fillAmount0;
        }

        // Step 3: Route residual through AMM
        // Residual = matched volume that wasn't satisfied by peer-to-peer
        var ammVolume = UInt256.Zero;
        var updatedReserves = reserves;

        // If sell side has leftover (more sellers than buyers matched p2p), route buy through AMM
        if (remainingSellVolume > UInt256.Zero && !reserves.Reserve0.IsZero)
        {
            // There were more buyers than p2p sellers could fill;
            // remaining buy volume needs AMM to sell token0
            ammVolume = remainingBuyVolume;
        }
        else if (remainingBuyVolume > UInt256.Zero && !reserves.Reserve0.IsZero)
        {
            ammVolume = remainingBuyVolume;
        }

        // Update reserves based on net flow
        // Peer-to-peer: net zero to the AMM
        // AMM portion: adjust reserves for the residual routed through the AMM
        if (ammVolume > UInt256.Zero && !reserves.Reserve0.IsZero && !reserves.Reserve1.IsZero)
        {
            // Compute AMM swap for the residual
            var ammOutput0 = DexLibrary.GetAmountOut(
                FullMath.MulDiv(ammVolume, clearingPrice, PriceScale),
                reserves.Reserve1, reserves.Reserve0, feeBps);

            updatedReserves = new PoolReserves
            {
                Reserve0 = reserves.Reserve0 - (ammOutput0 < reserves.Reserve0 ? ammOutput0 : UInt256.Zero),
                Reserve1 = reserves.Reserve1 + FullMath.MulDiv(ammVolume, clearingPrice, PriceScale),
                TotalSupply = reserves.TotalSupply,
                KLast = reserves.KLast,
            };
        }

        var totalVolume1 = FullMath.MulDiv(matchedVolume, clearingPrice, PriceScale);

        return new BatchResult
        {
            PoolId = poolId,
            ClearingPrice = clearingPrice,
            TotalVolume0 = matchedVolume,
            TotalVolume1 = totalVolume1,
            AmmVolume = ammVolume,
            Fills = fills,
            UpdatedReserves = updatedReserves,
        };
    }
}
