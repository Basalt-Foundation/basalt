# Order Book DEX

## Category

Decentralized Finance (DeFi) -- Trading Infrastructure

## Summary

A fully on-chain order book exchange implementing price-time priority matching for BST-20 token pairs. Unlike AMM-based exchanges, this contract maintains sorted buy and sell order lists, executing trades at exact limit prices specified by users. This design provides superior price discovery, zero slippage on filled orders, and a familiar trading experience for professional market makers.

## Why It's Useful

- **Superior Price Discovery**: Limit orders reflect traders' true valuation of assets, producing tighter spreads and more accurate prices than AMM curves, especially for low-liquidity or newly launched tokens.
- **Zero Slippage for Limit Orders**: Traders know their exact execution price when placing limit orders, unlike AMMs where large trades suffer significant price impact.
- **Professional Market Making**: Market makers can place and manage orders at specific price levels, earning the bid-ask spread -- a model they understand from traditional finance.
- **Complementary to AMM**: The order book and AMM can coexist. Arbitrageurs keep prices aligned between both venues, improving overall market efficiency.
- **Better for Illiquid Pairs**: AMM pools require substantial capital lockup to provide reasonable spreads. An order book can offer tight spreads with less total capital if market makers are active.

## Key Features

- **Limit Orders**: Place buy or sell orders at a specific price. Orders rest on the book until filled, partially filled, or canceled.
- **Market Orders**: Execute against resting limit orders at the best available prices. Market orders sweep the book until fully filled or the book is exhausted.
- **Price-Time Priority (FIFO)**: Orders at the same price level are filled in the order they were placed, ensuring fairness.
- **Partial Fills**: Large orders can be partially filled, with the unfilled remainder staying on the book.
- **Maker/Taker Fee Model**: Makers (liquidity providers who place resting orders) pay lower fees than takers (who execute against resting orders). Fee tiers configurable via governance.
- **Order Cancellation**: Users can cancel their own unfilled or partially filled orders and reclaim locked funds.
- **Self-Trade Prevention**: Orders from the same address on opposite sides are prevented from matching against each other.
- **Escrow-Based Settlement**: Funds are locked in the Escrow system contract upon order placement, ensuring guaranteed settlement on match.
- **Order Expiry**: Optional time-to-live (TTL) for orders; expired orders are automatically skipped during matching.
- **Batch Auction Mode**: Optional opening auction for new pairs or daily price discovery, where orders accumulate and clear at a single price.

## Basalt-Specific Advantages

- **AOT-Compiled Matching Engine**: The order matching loop runs as AOT-compiled native code, providing significantly faster execution than EVM-interpreted matching. This is critical for order books where matching complexity is O(n) in the number of price levels touched.
- **Escrow System Contract Integration**: Basalt's built-in Escrow contract (0x0103) provides atomic fund locking and release, eliminating the need to build a custom escrow mechanism and reducing smart contract surface area for bugs.
- **ZK Compliance for Security Token Trading**: Pairs involving regulated security tokens can require ZK compliance proofs from both maker and taker, enabling compliant trading of tokenized securities without revealing trader identities on the public chain.
- **Confidential Orders via Pedersen Commitments**: Order amounts and prices can be committed using Pedersen commitments, hiding order book depth from front-runners. The matching engine verifies commitments during execution without revealing the underlying values until settlement.
- **Ed25519 Order Signing**: Traders can pre-sign orders off-chain using Ed25519 (faster signing and verification than ECDSA), enabling high-frequency order placement and cancellation patterns.
- **BLS Aggregated Batch Cancellations**: Market makers who need to cancel many orders at once can aggregate cancellation signatures via BLS, reducing on-chain cost for volatility-driven mass cancellations.

## Token Standards Used

- **BST-20**: All tradeable tokens are BST-20. Quote currency (e.g., WBSLT, stablecoins) and base currency are both BST-20.

## Integration Points

- **Escrow (0x0103)**: Funds are locked in Escrow when an order is placed and released to the counterparty upon match. Canceled orders trigger Escrow refund.
- **BNS**: Trading pairs and the exchange contract are registered under BNS names (e.g., `orderbook.basalt`, `bst-usdb.orderbook.basalt`).
- **Governance (0x0102)**: Fee tier changes, new pair listings (if permissioned), and emergency pause are governed.
- **SchemaRegistry / IssuerRegistry**: ZK compliance verification for regulated trading pairs.
- **BridgeETH (0x...1008)**: Bridged assets can be listed and traded on the order book.

## Technical Sketch

```csharp
// ============================================================
// OrderBookDex -- On-chain order book exchange
// ============================================================

public enum OrderSide : byte
{
    Buy = 0,
    Sell = 1
}

public enum OrderStatus : byte
{
    Open = 0,
    PartiallyFilled = 1,
    Filled = 2,
    Canceled = 3,
    Expired = 4
}

[BasaltContract(TypeId = 0x0210)]
public partial class OrderBookDex : SdkContract
{
    // --- Storage ---

    // pairId => PairConfig
    private StorageMap<ulong, PairConfig> _pairs;

    // orderId => Order
    private StorageMap<ulong, Order> _orders;

    // pairId + side + priceLevel => first orderId in FIFO queue
    private StorageMap<Hash256, ulong> _priceLevel;

    // orderId => next orderId at same price level
    private StorageMap<ulong, ulong> _orderQueue;

    // pairId + side => best price (best bid / best ask)
    private StorageMap<Hash256, UInt256> _bestPrice;

    // Global order counter
    private StorageValue<ulong> _nextOrderId;

    // Fee configuration (basis points)
    private StorageValue<uint> _makerFeeBps;
    private StorageValue<uint> _takerFeeBps;

    // Escrow contract address
    private StorageValue<Address> _escrow;

    // --- Structs ---

    public struct PairConfig
    {
        public Address BaseToken;
        public Address QuoteToken;
        public UInt256 MinOrderSize;
        public UInt256 TickSize;        // minimum price increment
        public bool RequiresCompliance;
        public bool Active;
    }

    public struct Order
    {
        public ulong OrderId;
        public ulong PairId;
        public Address Trader;
        public OrderSide Side;
        public OrderStatus Status;
        public UInt256 Price;           // quote tokens per base token
        public UInt256 OriginalQty;     // base token amount
        public UInt256 FilledQty;
        public ulong PlacedAtBlock;
        public ulong ExpiryBlock;       // 0 = no expiry
    }

    public struct TradeResult
    {
        public ulong MakerOrderId;
        public ulong TakerOrderId;
        public UInt256 Price;
        public UInt256 Quantity;
        public UInt256 MakerFee;
        public UInt256 TakerFee;
    }

    // --- Pair Management ---

    /// <summary>
    /// Register a new trading pair. Governance-gated or permissionless
    /// depending on configuration.
    /// </summary>
    public ulong CreatePair(
        Address baseToken,
        Address quoteToken,
        UInt256 minOrderSize,
        UInt256 tickSize,
        bool requiresCompliance)
    {
        // Validate tokens, ensure pair does not already exist
        // Store PairConfig, return pairId
    }

    // --- Order Placement ---

    /// <summary>
    /// Place a limit order. Funds are locked in Escrow.
    /// If the order crosses the spread, it matches immediately.
    /// Unfilled remainder rests on the book.
    /// </summary>
    public ulong PlaceLimitOrder(
        ulong pairId,
        OrderSide side,
        UInt256 price,
        UInt256 quantity,
        ulong expiryBlock)
    {
        var pair = _pairs.Get(pairId);
        Require(pair.Active, "PAIR_INACTIVE");
        Require(quantity >= pair.MinOrderSize, "BELOW_MIN_SIZE");
        Require(price % pair.TickSize == UInt256.Zero, "INVALID_TICK");

        if (pair.RequiresCompliance)
            RequireZkCompliance(Context.Sender);

        // Lock funds in Escrow
        var lockAmount = side == OrderSide.Buy
            ? price * quantity   // lock quote tokens
            : quantity;          // lock base tokens
        var lockToken = side == OrderSide.Buy ? pair.QuoteToken : pair.BaseToken;
        LockInEscrow(Context.Sender, lockToken, lockAmount);

        // Create order
        var orderId = _nextOrderId.Get();
        _nextOrderId.Set(orderId + 1);

        var order = new Order
        {
            OrderId = orderId,
            PairId = pairId,
            Trader = Context.Sender,
            Side = side,
            Status = OrderStatus.Open,
            Price = price,
            OriginalQty = quantity,
            FilledQty = UInt256.Zero,
            PlacedAtBlock = Context.BlockNumber,
            ExpiryBlock = expiryBlock
        };

        // Try to match against resting orders on opposite side
        var remaining = MatchOrder(ref order, pair);

        if (remaining > UInt256.Zero)
        {
            // Insert unfilled remainder into the book
            InsertIntoBook(order);
        }

        _orders.Set(orderId, order);
        EmitEvent("OrderPlaced", orderId, side, price, quantity);
        return orderId;
    }

    /// <summary>
    /// Place a market order. Executes immediately against resting orders.
    /// Any unfilled quantity is returned (no resting on book).
    /// </summary>
    public UInt256 PlaceMarketOrder(
        ulong pairId,
        OrderSide side,
        UInt256 quantity)
    {
        var pair = _pairs.Get(pairId);
        Require(pair.Active, "PAIR_INACTIVE");

        if (pair.RequiresCompliance)
            RequireZkCompliance(Context.Sender);

        // Lock funds
        var lockAmount = EstimateMarketOrderCost(pairId, side, quantity);
        var lockToken = side == OrderSide.Buy ? pair.QuoteToken : pair.BaseToken;
        LockInEscrow(Context.Sender, lockToken, lockAmount);

        var order = new Order
        {
            OrderId = _nextOrderId.Get(),
            PairId = pairId,
            Trader = Context.Sender,
            Side = side,
            Price = side == OrderSide.Buy ? UInt256.MaxValue : UInt256.Zero,
            OriginalQty = quantity,
        };
        _nextOrderId.Set(order.OrderId + 1);

        var filled = MatchOrder(ref order, pair);

        // Refund any unused locked funds
        if (filled < quantity)
            RefundFromEscrow(Context.Sender, lockToken, lockAmount - ComputeUsedAmount(filled));

        EmitEvent("MarketOrder", order.OrderId, side, quantity, filled);
        return filled;
    }

    // --- Order Cancellation ---

    /// <summary>
    /// Cancel an open or partially filled order. Returns locked funds.
    /// </summary>
    public void CancelOrder(ulong orderId)
    {
        var order = _orders.Get(orderId);
        Require(order.Trader == Context.Sender, "NOT_OWNER");
        Require(order.Status == OrderStatus.Open
             || order.Status == OrderStatus.PartiallyFilled, "NOT_CANCELABLE");

        var remaining = order.OriginalQty - order.FilledQty;
        var pair = _pairs.Get(order.PairId);

        // Remove from book
        RemoveFromBook(order);

        // Refund remaining locked funds from Escrow
        var refundToken = order.Side == OrderSide.Buy ? pair.QuoteToken : pair.BaseToken;
        var refundAmount = order.Side == OrderSide.Buy
            ? order.Price * remaining
            : remaining;
        RefundFromEscrow(order.Trader, refundToken, refundAmount);

        order.Status = OrderStatus.Canceled;
        _orders.Set(orderId, order);
        EmitEvent("OrderCanceled", orderId, remaining);
    }

    // --- Matching Engine ---

    /// <summary>
    /// Match an incoming order against the opposite side of the book.
    /// Returns filled quantity.
    /// </summary>
    private UInt256 MatchOrder(ref Order taker, PairConfig pair)
    {
        var oppositeSide = taker.Side == OrderSide.Buy
            ? OrderSide.Sell : OrderSide.Buy;

        var remaining = taker.OriginalQty - taker.FilledQty;

        while (remaining > UInt256.Zero)
        {
            var bestPriceKey = ComputeBestPriceKey(taker.PairId, oppositeSide);
            var bestPrice = _bestPrice.Get(bestPriceKey);

            // Check if best resting price is acceptable
            if (taker.Side == OrderSide.Buy && bestPrice > taker.Price)
                break;
            if (taker.Side == OrderSide.Sell && bestPrice < taker.Price)
                break;
            if (bestPrice.IsZero)
                break;

            // Walk FIFO queue at this price level
            var levelKey = ComputeLevelKey(taker.PairId, oppositeSide, bestPrice);
            var makerOrderId = _priceLevel.Get(levelKey);

            while (makerOrderId != 0 && remaining > UInt256.Zero)
            {
                var maker = _orders.Get(makerOrderId);

                // Skip expired orders
                if (maker.ExpiryBlock > 0 && Context.BlockNumber > maker.ExpiryBlock)
                {
                    maker.Status = OrderStatus.Expired;
                    _orders.Set(makerOrderId, maker);
                    makerOrderId = _orderQueue.Get(makerOrderId);
                    continue;
                }

                // Self-trade prevention
                if (maker.Trader == taker.Trader)
                {
                    makerOrderId = _orderQueue.Get(makerOrderId);
                    continue;
                }

                var makerRemaining = maker.OriginalQty - maker.FilledQty;
                var fillQty = UInt256.Min(remaining, makerRemaining);

                // Execute trade
                ExecuteTrade(ref taker, ref maker, fillQty, pair);

                remaining -= fillQty;

                if (maker.FilledQty == maker.OriginalQty)
                {
                    maker.Status = OrderStatus.Filled;
                    makerOrderId = _orderQueue.Get(makerOrderId);
                }

                _orders.Set(maker.OrderId, maker);
            }

            // Update price level head
            _priceLevel.Set(levelKey, makerOrderId);

            // If level exhausted, find next best price
            if (makerOrderId == 0)
                UpdateBestPrice(taker.PairId, oppositeSide);
        }

        taker.FilledQty = taker.OriginalQty - remaining;
        taker.Status = remaining.IsZero ? OrderStatus.Filled : OrderStatus.PartiallyFilled;
        return taker.FilledQty;
    }

    /// <summary>
    /// Execute a single trade between maker and taker.
    /// Settles via Escrow: release locked funds to counterparties.
    /// </summary>
    private void ExecuteTrade(
        ref Order taker, ref Order maker,
        UInt256 fillQty, PairConfig pair)
    {
        var makerFee = (fillQty * maker.Price * _makerFeeBps.Get()) / 10000;
        var takerFee = (fillQty * maker.Price * _takerFeeBps.Get()) / 10000;

        // Transfer base tokens: seller -> buyer
        // Transfer quote tokens: buyer -> seller
        // Deduct fees
        // All via Escrow release

        maker.FilledQty += fillQty;
        taker.FilledQty += fillQty;

        EmitEvent("Trade", maker.OrderId, taker.OrderId,
                  maker.Price, fillQty, makerFee, takerFee);
    }

    // --- Book Management Helpers ---
    private void InsertIntoBook(Order order) { /* Insert into price-level FIFO queue */ }
    private void RemoveFromBook(Order order) { /* Remove from FIFO queue */ }
    private void UpdateBestPrice(ulong pairId, OrderSide side) { /* Scan for next best */ }
    private Hash256 ComputeBestPriceKey(ulong pairId, OrderSide side) { /* ... */ }
    private Hash256 ComputeLevelKey(ulong pairId, OrderSide side, UInt256 price) { /* ... */ }

    // --- Escrow Helpers ---
    private void LockInEscrow(Address user, Address token, UInt256 amount) { /* ... */ }
    private void RefundFromEscrow(Address user, Address token, UInt256 amount) { /* ... */ }

    // --- Compliance ---
    private void RequireZkCompliance(Address user) { /* ... */ }

    // --- Queries ---
    public Order GetOrder(ulong orderId) => _orders.Get(orderId);
    public PairConfig GetPair(ulong pairId) => _pairs.Get(pairId);
    public UInt256 GetBestBid(ulong pairId)
        => _bestPrice.Get(ComputeBestPriceKey(pairId, OrderSide.Buy));
    public UInt256 GetBestAsk(ulong pairId)
        => _bestPrice.Get(ComputeBestPriceKey(pairId, OrderSide.Sell));
    private UInt256 EstimateMarketOrderCost(ulong pairId, OrderSide side, UInt256 qty) { /* ... */ }
    private UInt256 ComputeUsedAmount(UInt256 filled) { /* ... */ }
}
```

## Complexity

**High** -- On-chain order books are among the most complex DeFi contracts. The matching engine must correctly handle price-time priority, partial fills, self-trade prevention, order expiry, and efficient book traversal. Gas cost management is critical since matching a single taker order may touch many maker orders. Storage layout must be carefully designed for efficient price level traversal without unbounded loops.

## Priority

**P1** -- While the AMM (P0) provides immediate swap capability, the order book DEX is essential for professional trading, low-liquidity pairs, and security token markets. It should follow shortly after the AMM to provide a complete trading venue.
