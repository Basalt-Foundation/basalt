using Basalt.Core;
using Basalt.Execution.Dex.Math;

namespace Basalt.Execution.Dex;

/// <summary>
/// Order matching logic for the DEX limit order book.
/// Finds crossing orders (orders whose prices overlap) that can be matched
/// at or better than the batch auction clearing price.
///
/// Order book design:
/// <list type="bullet">
/// <item><description>Buy orders: maximum price willing to pay (stored as token1-per-token0)</description></item>
/// <item><description>Sell orders: minimum price willing to accept (stored as token1-per-token0)</description></item>
/// <item><description>Orders cross when buy price >= sell price</description></item>
/// <item><description>Crossed orders execute at the batch clearing price (not their limit)</description></item>
/// </list>
/// </summary>
public static class OrderBook
{
    /// <summary>
    /// Scan the DEX state for limit orders in a pool that cross the given price.
    /// Buy orders with price >= clearingPrice are "crossing" (willing to pay enough).
    /// Sell orders with price <= clearingPrice are "crossing" (willing to accept enough).
    /// </summary>
    /// <param name="dexState">The DEX state to scan.</param>
    /// <param name="poolId">The pool to scan orders for.</param>
    /// <param name="clearingPrice">The reference price (from batch auction).</param>
    /// <param name="currentBlock">Current block number (for expiry filtering).</param>
    /// <param name="maxOrders">Maximum number of orders to return per side.</param>
    /// <returns>Tuple of (crossingBuyOrders, crossingSellOrders).</returns>
    public static (List<(ulong Id, LimitOrder Order)> Buys, List<(ulong Id, LimitOrder Order)> Sells)
        FindCrossingOrders(
            DexState dexState, ulong poolId, UInt256 clearingPrice,
            ulong currentBlock, int maxOrders = 100)
    {
        var buys = new List<(ulong Id, LimitOrder Order)>();
        var sells = new List<(ulong Id, LimitOrder Order)>();

        // L-15: Iterate per-pool linked list instead of scanning all orders globally
        var orderId = dexState.GetPoolOrderHead(poolId);
        while (orderId != ulong.MaxValue && (buys.Count < maxOrders || sells.Count < maxOrders))
        {
            var order = dexState.GetOrder(orderId);
            var nextOrderId = dexState.GetOrderNext(orderId);

            if (order != null && !order.Value.Amount.IsZero)
            {
                // Check expiry
                if (order.Value.ExpiryBlock == 0 || currentBlock <= order.Value.ExpiryBlock)
                {
                    if (order.Value.IsBuy && order.Value.Price >= clearingPrice && buys.Count < maxOrders)
                        buys.Add((orderId, order.Value));
                    else if (!order.Value.IsBuy && order.Value.Price <= clearingPrice && sells.Count < maxOrders)
                        sells.Add((orderId, order.Value));
                }
            }

            orderId = nextOrderId;
        }

        // Sort buys by price descending (most eager first)
        buys.Sort((a, b) => b.Order.Price.CompareTo(a.Order.Price));
        // Sort sells by price ascending (cheapest first)
        sells.Sort((a, b) => a.Order.Price.CompareTo(b.Order.Price));

        return (buys, sells);
    }

    /// <summary>
    /// Match crossing buy and sell orders at the given clearing price.
    /// Returns fill records and updated order amounts.
    /// </summary>
    /// <param name="buyOrders">Crossing buy orders sorted by price descending.</param>
    /// <param name="sellOrders">Crossing sell orders sorted by price ascending.</param>
    /// <param name="clearingPrice">The uniform clearing price.</param>
    /// <param name="dexState">The DEX state for updating order amounts.</param>
    /// <returns>List of fill records from order matching.</returns>
    public static List<FillRecord> MatchOrders(
        List<(ulong Id, LimitOrder Order)> buyOrders,
        List<(ulong Id, LimitOrder Order)> sellOrders,
        UInt256 clearingPrice,
        DexState dexState)
    {
        var fills = new List<FillRecord>();
        int buyIdx = 0, sellIdx = 0;

        while (buyIdx < buyOrders.Count && sellIdx < sellOrders.Count)
        {
            var (buyId, buyOrder) = buyOrders[buyIdx];
            var (sellId, sellOrder) = sellOrders[sellIdx];

            // Buy order Amount is already in token0 units
            var buyToken0 = buyOrder.Amount;
            var sellToken0 = sellOrder.Amount;

            // Match the smaller side
            var matchToken0 = buyToken0 < sellToken0 ? buyToken0 : sellToken0;
            var matchToken1 = FullMath.MulDiv(matchToken0, clearingPrice, BatchAuctionSolver.PriceScale);

            if (matchToken0.IsZero)
            {
                buyIdx++;
                continue;
            }

            // Fill for the buyer (receives token0, pays token1)
            fills.Add(new FillRecord
            {
                Participant = buyOrder.Owner,
                AmountIn = matchToken1,
                AmountOut = matchToken0,
                IsLimitOrder = true,
                OrderId = buyId,
            });

            // Fill for the seller (receives token1, pays token0)
            fills.Add(new FillRecord
            {
                Participant = sellOrder.Owner,
                AmountIn = matchToken0,
                AmountOut = matchToken1,
                IsLimitOrder = true,
                OrderId = sellId,
            });

            // Update remaining amounts (both in token0 units)
            var remainingBuy = buyOrder.Amount >= matchToken0
                ? UInt256.CheckedSub(buyOrder.Amount, matchToken0)
                : UInt256.Zero;
            var remainingSell = sellOrder.Amount >= matchToken0
                ? UInt256.CheckedSub(sellOrder.Amount, matchToken0)
                : UInt256.Zero;

            if (remainingBuy.IsZero)
            {
                dexState.DeleteOrder(buyId);
                buyIdx++;
            }
            else
            {
                dexState.UpdateOrderAmount(buyId, remainingBuy);
            }

            if (remainingSell.IsZero)
            {
                dexState.DeleteOrder(sellId);
                sellIdx++;
            }
            else
            {
                dexState.UpdateOrderAmount(sellId, remainingSell);
            }
        }

        return fills;
    }

    /// <summary>
    /// Clean up expired orders for a pool, returning escrowed tokens to owners.
    /// </summary>
    /// <param name="dexState">The DEX state.</param>
    /// <param name="stateDb">The state database for token returns.</param>
    /// <param name="poolId">The pool to clean up.</param>
    /// <param name="currentBlock">Current block number.</param>
    /// <returns>Number of expired orders cleaned up.</returns>
    public static int CleanupExpiredOrders(
        DexState dexState, Storage.IStateDatabase stateDb,
        ulong poolId, ulong currentBlock)
    {
        var meta = dexState.GetPoolMetadata(poolId);
        if (meta == null) return 0;

        var count = 0;

        // L-15: Iterate per-pool linked list
        var orderId = dexState.GetPoolOrderHead(poolId);
        var expiredIds = new List<ulong>();
        while (orderId != ulong.MaxValue)
        {
            var nextOrderId = dexState.GetOrderNext(orderId);
            var order = dexState.GetOrder(orderId);
            if (order != null && order.Value.Amount.IsZero)
            {
                // Zero-amount order (dust from rounding) — delete without refund
                expiredIds.Add(orderId);
            }
            else if (order != null && order.Value.ExpiryBlock > 0 && currentBlock > order.Value.ExpiryBlock)
            {
                // Return escrowed tokens (buy orders: convert remaining token0 back to token1 at limit price)
                var escrowToken = order.Value.IsBuy ? meta.Value.Token1 : meta.Value.Token0;
                var refundAmount = order.Value.IsBuy
                    ? FullMath.MulDiv(order.Value.Amount, order.Value.Price, BatchAuctionSolver.PriceScale)
                    : order.Value.Amount;
                var refund = DexEngine.TransferSingleTokenOut(stateDb, order.Value.Owner, escrowToken, refundAmount);
                if (refund.Success)
                    expiredIds.Add(orderId);
            }
            orderId = nextOrderId;
        }

        // Delete expired orders (modifies list, so done after iteration)
        foreach (var expiredId in expiredIds)
        {
            dexState.DeleteOrder(expiredId);
            count++;
        }

        return count;
    }
}
