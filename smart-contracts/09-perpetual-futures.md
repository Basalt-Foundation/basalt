# Perpetual Futures Exchange

## Category

Decentralized Finance (DeFi) -- Derivatives

## Summary

A decentralized perpetual futures exchange enabling leveraged long and short trading on any supported asset pair. Positions are maintained indefinitely (no expiry) with a funding rate mechanism that keeps the perpetual price anchored to the spot price. The exchange includes a liquidation engine, insurance fund, and multi-collateral margin support, bringing institutional-grade derivatives trading to the Basalt network.

## Why It's Useful

- **Leverage Trading**: Traders can amplify exposure to asset price movements (e.g., 2x-20x leverage) without owning the full notional value, increasing capital efficiency for directional bets.
- **Short Selling**: Perpetual futures are the primary mechanism for expressing bearish views on-chain. Without them, traders can only go long, creating an incomplete market.
- **Hedging**: Token holders, liquidity providers, and stakers can hedge their exposure using perpetual futures, reducing portfolio risk without selling underlying assets.
- **Price Discovery**: Leveraged markets attract sophisticated traders whose activity produces more efficient price discovery than spot markets alone.
- **Funding Rate Arbitrage**: The funding rate mechanism creates arbitrage opportunities between spot and perp prices, attracting market makers and improving overall market efficiency.
- **Volume and Fees**: Perpetual futures generate significantly more trading volume than spot markets (typically 3-10x), producing substantial fee revenue for the protocol and its stakeholders.

## Key Features

- **Perpetual Contracts (No Expiry)**: Positions remain open indefinitely. No rolling of contracts or settlement at expiry.
- **Funding Rate Mechanism**: An hourly (or per-epoch) funding rate payment between longs and shorts that anchors the perpetual price to the spot/oracle price. When perp > spot, longs pay shorts; when perp < spot, shorts pay longs.
- **Cross-Margin and Isolated Margin**: Users can choose cross-margin (all positions share collateral) or isolated margin (each position has dedicated collateral) to control risk.
- **Multi-Collateral Margin**: BST, USDB, stBSLT, and other approved tokens accepted as margin. Each has its own collateral weight and haircut.
- **Oracle-Based Mark Price**: Position PnL and liquidation are calculated against an oracle mark price (not the last trade price) to prevent manipulation.
- **Liquidation Engine**: Positions below the maintenance margin ratio are liquidated. Partial liquidation reduces position size to restore health. Full liquidation closes the position entirely.
- **Insurance Fund**: A reserve funded by liquidation penalties and a share of trading fees. Covers socialized losses when liquidations result in negative equity (bad debt).
- **Auto-Deleverage (ADL)**: When the insurance fund is depleted, the most profitable opposing positions are automatically deleveraged to cover bad debt, preventing socialized losses.
- **Position Limit and Open Interest Caps**: Per-market and per-user position limits to manage systemic risk.
- **Maker/Taker Fee Model**: Limit orders (makers) pay lower fees than market orders (takers), incentivizing order book depth.

## Basalt-Specific Advantages

- **AOT-Compiled Liquidation Engine**: The liquidation engine runs as native AOT-compiled code, enabling fast processing of multiple liquidations during volatile market conditions. This is critical for perpetual exchanges where delayed liquidations can cause cascading bad debt.
- **ZK Compliance for Regulated Derivatives**: Derivatives trading is heavily regulated in most jurisdictions. Basalt's ZK compliance infrastructure enables traders to prove they are from permitted jurisdictions and have appropriate accreditation (e.g., accredited investor status) without revealing their identity on-chain.
- **Confidential Positions via Pedersen Commitments**: Position sizes, entry prices, and margin amounts can be hidden using Pedersen commitments. Liquidation checks use zero-knowledge proofs to verify margin adequacy without revealing position details, preventing targeted liquidation hunting.
- **BLS Aggregated Oracle Signatures**: Mark price updates from multiple oracle providers can use BLS aggregate signatures for efficient on-chain verification. A single aggregated signature replaces N individual verifications, reducing the cost of frequent price updates needed for accurate mark pricing.
- **BST-3525 SFT Position Tokens**: Open positions can be represented as BST-3525 tokens with metadata (market, side, size, entry price, leverage, margin), enabling position transfer, portfolio management, and potential secondary markets for derivative positions.
- **Ed25519 Order Signing**: Traders can pre-sign orders off-chain for fast submission. Ed25519's speed advantage over ECDSA enables higher-frequency order placement critical for derivatives markets.

## Token Standards Used

- **BST-20**: Margin tokens (BST, USDB, stBSLT) and fee tokens are all BST-20.
- **BST-3525 (SFT)**: Open position representation with market and position metadata.

## Integration Points

- **AMM DEX (0x0200)**: Spot price reference for funding rate calculation. Liquidation proceeds may be swapped on the AMM.
- **Stablecoin CDP (0x0230)**: USDB as primary margin currency and settlement token.
- **Lending Protocol (0x0220)**: Margin assets can be sourced from lending protocol deposits; insurance fund can earn yield in lending pools during low-volatility periods.
- **Flash Loan Pool (0x0260)**: Flash loans for liquidation execution and margin position adjustment.
- **Governance (0x0102)**: Controls market listings, leverage limits, fee tiers, insurance fund parameters, and emergency pause.
- **BNS**: Registered as `perps.basalt`.
- **SchemaRegistry / IssuerRegistry**: ZK compliance for jurisdiction and accreditation verification.
- **Liquid Staking (0x0250)**: stBSLT accepted as margin collateral.

## Technical Sketch

```csharp
// ============================================================
// PerpetualExchange -- Perpetual futures trading
// ============================================================

public enum PositionSide : byte
{
    Long = 0,
    Short = 1
}

public enum MarginMode : byte
{
    Cross = 0,
    Isolated = 1
}

[BasaltContract(TypeId = 0x0280)]
public partial class PerpetualExchange : SdkContract
{
    // --- Storage ---

    // marketId => MarketConfig
    private StorageMap<ulong, MarketConfig> _markets;
    private StorageValue<ulong> _nextMarketId;

    // positionId => Position
    private StorageMap<ulong, Position> _positions;
    private StorageValue<ulong> _nextPositionId;

    // user + marketId => positionId (for cross-margin)
    private StorageMap<Hash256, ulong> _userPositions;

    // marketId => MarketState
    private StorageMap<ulong, MarketState> _marketState;

    // Insurance fund per market
    private StorageMap<ulong, UInt256> _insuranceFund;

    // Global collateral balances per user
    private StorageMap<Address, UInt256> _crossMarginBalance;

    // Oracle address
    private StorageValue<Address> _oracle;

    // --- Structs ---

    public struct MarketConfig
    {
        public ulong MarketId;
        public Address BaseAsset;           // e.g., BST
        public Address QuoteAsset;          // e.g., USDB
        public uint MaxLeverage;            // e.g., 20 = 20x
        public uint MaintenanceMarginBps;   // e.g., 500 = 5%
        public uint MakerFeeBps;
        public uint TakerFeeBps;
        public uint FundingIntervalBlocks;  // blocks between funding payments
        public UInt256 MaxOpenInterest;
        public bool Active;
    }

    public struct MarketState
    {
        public UInt256 LongOpenInterest;
        public UInt256 ShortOpenInterest;
        public UInt256 CumulativeFundingRate; // cumulative per-unit funding
        public ulong LastFundingBlock;
        public UInt256 LastMarkPrice;
    }

    public struct Position
    {
        public ulong PositionId;
        public ulong MarketId;
        public Address Trader;
        public PositionSide Side;
        public MarginMode Mode;
        public UInt256 Size;                // notional size in base asset units
        public UInt256 EntryPrice;          // avg entry price
        public UInt256 Margin;              // collateral locked (isolated mode)
        public UInt256 CumulativeFundingAtEntry;
        public bool Active;
    }

    public struct FundingRate
    {
        public UInt256 Rate;
        public bool LongsPay;          // true = longs pay shorts
        public ulong Block;
    }

    // --- Market Management ---

    /// <summary>
    /// Create a new perpetual futures market. Governance-only.
    /// </summary>
    public ulong CreateMarket(
        Address baseAsset,
        Address quoteAsset,
        uint maxLeverage,
        uint maintenanceMarginBps,
        uint makerFeeBps,
        uint takerFeeBps,
        uint fundingIntervalBlocks,
        UInt256 maxOpenInterest)
    {
        RequireGovernance();
        var marketId = _nextMarketId.Get();
        _nextMarketId.Set(marketId + 1);

        _markets.Set(marketId, new MarketConfig
        {
            MarketId = marketId,
            BaseAsset = baseAsset,
            QuoteAsset = quoteAsset,
            MaxLeverage = maxLeverage,
            MaintenanceMarginBps = maintenanceMarginBps,
            MakerFeeBps = makerFeeBps,
            TakerFeeBps = takerFeeBps,
            FundingIntervalBlocks = fundingIntervalBlocks,
            MaxOpenInterest = maxOpenInterest,
            Active = true
        });

        _marketState.Set(marketId, new MarketState
        {
            LastFundingBlock = Context.BlockNumber,
            LastMarkPrice = GetMarkPrice(marketId)
        });

        EmitEvent("MarketCreated", marketId, baseAsset, quoteAsset, maxLeverage);
        return marketId;
    }

    // --- Position Management ---

    /// <summary>
    /// Open or increase a perpetual position with specified leverage.
    /// </summary>
    public ulong OpenPosition(
        ulong marketId,
        PositionSide side,
        UInt256 size,
        UInt256 margin,
        MarginMode mode,
        UInt256 maxSlippage)
    {
        var market = _markets.Get(marketId);
        Require(market.Active, "MARKET_INACTIVE");

        // ZK compliance check for regulated markets
        RequireZkCompliance(Context.Sender);

        var markPrice = GetMarkPrice(marketId);

        // Calculate effective leverage
        var notionalValue = size * markPrice / 1_000_000_000_000_000_000UL;
        var leverage = notionalValue / margin;
        Require(leverage <= market.MaxLeverage, "EXCEEDS_MAX_LEVERAGE");

        // Check open interest cap
        var state = _marketState.Get(marketId);
        if (side == PositionSide.Long)
            Require(state.LongOpenInterest + size <= market.MaxOpenInterest, "OI_CAP");
        else
            Require(state.ShortOpenInterest + size <= market.MaxOpenInterest, "OI_CAP");

        // Lock margin
        TransferMarginIn(Context.Sender, margin);

        // Settle funding for existing position if any
        SettleFunding(marketId);

        var positionId = _nextPositionId.Get();
        _nextPositionId.Set(positionId + 1);

        _positions.Set(positionId, new Position
        {
            PositionId = positionId,
            MarketId = marketId,
            Trader = Context.Sender,
            Side = side,
            Mode = mode,
            Size = size,
            EntryPrice = markPrice,
            Margin = margin,
            CumulativeFundingAtEntry = state.CumulativeFundingRate,
            Active = true
        });

        // Update open interest
        if (side == PositionSide.Long)
            state.LongOpenInterest += size;
        else
            state.ShortOpenInterest += size;
        _marketState.Set(marketId, state);

        // Deduct taker fee
        var fee = notionalValue * market.TakerFeeBps / 10000;
        DeductFee(positionId, fee);

        EmitEvent("PositionOpened", positionId, Context.Sender,
                  marketId, side, size, markPrice, margin);
        return positionId;
    }

    /// <summary>
    /// Close a position (partially or fully) at current mark price.
    /// </summary>
    public UInt256 ClosePosition(ulong positionId, UInt256 closeSize)
    {
        var position = _positions.Get(positionId);
        Require(position.Trader == Context.Sender, "NOT_OWNER");
        Require(position.Active, "NOT_ACTIVE");
        Require(closeSize <= position.Size, "EXCEEDS_SIZE");

        var market = _markets.Get(position.MarketId);
        var markPrice = GetMarkPrice(position.MarketId);

        // Calculate PnL
        var pnl = CalculatePnL(position, markPrice, closeSize);

        // Calculate funding owed
        var fundingOwed = CalculateFundingOwed(position, closeSize);

        // Calculate fee
        var notionalValue = closeSize * markPrice / 1_000_000_000_000_000_000UL;
        var fee = notionalValue * market.TakerFeeBps / 10000;

        // Net settlement
        var marginReturn = (position.Margin * closeSize) / position.Size;
        var netPayout = marginReturn + pnl - fundingOwed - fee;

        // Update or close position
        if (closeSize == position.Size)
        {
            position.Active = false;
            position.Size = UInt256.Zero;
            position.Margin = UInt256.Zero;
        }
        else
        {
            position.Size -= closeSize;
            position.Margin -= marginReturn;
        }
        _positions.Set(positionId, position);

        // Update open interest
        var state = _marketState.Get(position.MarketId);
        if (position.Side == PositionSide.Long)
            state.LongOpenInterest -= closeSize;
        else
            state.ShortOpenInterest -= closeSize;
        _marketState.Set(position.MarketId, state);

        // Transfer payout
        if (netPayout > UInt256.Zero)
            TransferMarginOut(Context.Sender, netPayout);

        EmitEvent("PositionClosed", positionId, closeSize, markPrice, pnl);
        return netPayout;
    }

    /// <summary>
    /// Add margin to an existing isolated position.
    /// </summary>
    public void AddMargin(ulong positionId, UInt256 amount)
    {
        var position = _positions.Get(positionId);
        Require(position.Trader == Context.Sender, "NOT_OWNER");
        Require(position.Active, "NOT_ACTIVE");

        TransferMarginIn(Context.Sender, amount);
        position.Margin += amount;
        _positions.Set(positionId, position);
        EmitEvent("MarginAdded", positionId, amount);
    }

    // --- Funding Rate ---

    /// <summary>
    /// Settle funding rate for a market. Called periodically.
    /// Funding = (perpPrice - spotPrice) / spotPrice * interval
    /// </summary>
    public void SettleFunding(ulong marketId)
    {
        var state = _marketState.Get(marketId);
        var market = _markets.Get(marketId);
        var blocksSinceLast = Context.BlockNumber - state.LastFundingBlock;

        if (blocksSinceLast < market.FundingIntervalBlocks)
            return;

        var markPrice = GetMarkPrice(marketId);
        var spotPrice = GetSpotPrice(market.BaseAsset, market.QuoteAsset);

        // Funding rate = (mark - spot) / spot * timeWeight
        UInt256 fundingRate;
        bool longsPay;

        if (markPrice > spotPrice)
        {
            fundingRate = ((markPrice - spotPrice) * 1_000_000_000_000_000_000UL)
                        / spotPrice;
            longsPay = true;
        }
        else
        {
            fundingRate = ((spotPrice - markPrice) * 1_000_000_000_000_000_000UL)
                        / spotPrice;
            longsPay = false;
        }

        // Clamp funding rate to max (e.g., 1% per interval)
        var maxRate = 10_000_000_000_000_000UL; // 1%
        if (fundingRate > maxRate) fundingRate = maxRate;

        state.CumulativeFundingRate += fundingRate;
        state.LastFundingBlock = Context.BlockNumber;
        state.LastMarkPrice = markPrice;
        _marketState.Set(marketId, state);

        EmitEvent("FundingSettled", marketId, fundingRate, longsPay);
    }

    // --- Liquidation ---

    /// <summary>
    /// Liquidate an undercollateralized position. Liquidator receives
    /// a portion of remaining margin as reward.
    /// </summary>
    public void Liquidate(ulong positionId)
    {
        var position = _positions.Get(positionId);
        Require(position.Active, "NOT_ACTIVE");

        var market = _markets.Get(position.MarketId);
        var markPrice = GetMarkPrice(position.MarketId);

        var marginRatio = CalculateMarginRatio(position, markPrice);
        Require(marginRatio < market.MaintenanceMarginBps, "NOT_LIQUIDATABLE");

        var pnl = CalculatePnL(position, markPrice, position.Size);
        var remainingMargin = position.Margin + pnl;

        // Liquidation penalty to insurance fund
        var penalty = position.Margin * 500 / 10000; // 5% penalty
        var liquidatorReward = penalty / 2;
        var insuranceContribution = penalty - liquidatorReward;

        // Pay liquidator
        if (liquidatorReward > UInt256.Zero)
            TransferMarginOut(Context.Sender, liquidatorReward);

        // Insurance fund contribution
        _insuranceFund.Set(position.MarketId,
            _insuranceFund.Get(position.MarketId) + insuranceContribution);

        // Handle bad debt
        if (remainingMargin < UInt256.Zero)
        {
            var badDebt = UInt256.Zero - remainingMargin;
            CoverBadDebt(position.MarketId, badDebt);
        }

        // Close position
        position.Active = false;
        position.Size = UInt256.Zero;
        position.Margin = UInt256.Zero;
        _positions.Set(positionId, position);

        // Update open interest
        var state = _marketState.Get(position.MarketId);
        if (position.Side == PositionSide.Long)
            state.LongOpenInterest -= position.Size;
        else
            state.ShortOpenInterest -= position.Size;
        _marketState.Set(position.MarketId, state);

        EmitEvent("PositionLiquidated", positionId, Context.Sender, markPrice);
    }

    // --- Queries ---

    public Position GetPosition(ulong positionId) => _positions.Get(positionId);
    public MarketConfig GetMarket(ulong marketId) => _markets.Get(marketId);
    public MarketState GetMarketState(ulong marketId) => _marketState.Get(marketId);
    public UInt256 GetInsuranceFund(ulong marketId) => _insuranceFund.Get(marketId);

    public UInt256 GetUnrealizedPnL(ulong positionId)
    {
        var position = _positions.Get(positionId);
        var markPrice = GetMarkPrice(position.MarketId);
        return CalculatePnL(position, markPrice, position.Size);
    }

    public UInt256 GetMarginRatio(ulong positionId)
    {
        var position = _positions.Get(positionId);
        var markPrice = GetMarkPrice(position.MarketId);
        return CalculateMarginRatio(position, markPrice);
    }

    // --- Internal Helpers ---

    private UInt256 CalculatePnL(Position position, UInt256 markPrice, UInt256 size)
    {
        if (position.Side == PositionSide.Long)
            return size * (markPrice - position.EntryPrice) / 1_000_000_000_000_000_000UL;
        else
            return size * (position.EntryPrice - markPrice) / 1_000_000_000_000_000_000UL;
    }

    private UInt256 CalculateMarginRatio(Position position, UInt256 markPrice)
    {
        var pnl = CalculatePnL(position, markPrice, position.Size);
        var effectiveMargin = position.Margin + pnl;
        var notional = position.Size * markPrice / 1_000_000_000_000_000_000UL;
        if (notional.IsZero) return UInt256.MaxValue;
        return (effectiveMargin * 10000) / notional;
    }

    private UInt256 CalculateFundingOwed(Position position, UInt256 size)
    {
        var state = _marketState.Get(position.MarketId);
        var fundingDelta = state.CumulativeFundingRate - position.CumulativeFundingAtEntry;
        return (size * fundingDelta) / 1_000_000_000_000_000_000UL;
    }

    private void CoverBadDebt(ulong marketId, UInt256 badDebt)
    {
        var fund = _insuranceFund.Get(marketId);
        if (fund >= badDebt)
        {
            _insuranceFund.Set(marketId, fund - badDebt);
        }
        else
        {
            // Trigger ADL for remaining bad debt
            TriggerAutoDeleverage(marketId, badDebt - fund);
            _insuranceFund.Set(marketId, UInt256.Zero);
        }
    }

    private void TriggerAutoDeleverage(ulong marketId, UInt256 amount) { /* ... */ }
    private UInt256 GetMarkPrice(ulong marketId) { /* Oracle query */ }
    private UInt256 GetSpotPrice(Address baseAsset, Address quoteAsset) { /* AMM TWAP */ }
    private void TransferMarginIn(Address from, UInt256 amount) { /* ... */ }
    private void TransferMarginOut(Address to, UInt256 amount) { /* ... */ }
    private void DeductFee(ulong positionId, UInt256 fee) { /* ... */ }
    private void RequireZkCompliance(Address user) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**High** -- Perpetual futures exchanges are among the most complex DeFi contracts. They require: precise PnL calculation with signed arithmetic (profits and losses), funding rate computation and settlement across all positions, margin ratio monitoring with multi-collateral support, a robust liquidation engine with partial liquidation and ADL fallback, insurance fund management, and cross-margin accounting. Oracle manipulation resistance and front-running protection are critical security concerns. The interaction between funding rates, mark prices, and liquidation thresholds requires extensive testing.

## Priority

**P2** -- Perpetual futures are a high-value DeFi primitive that generates significant volume and fees, but they depend on robust oracle infrastructure, deep spot liquidity (AMM), and a stablecoin (USDB) for margin. They should be deployed after the core DeFi stack is mature and has proven stability.
