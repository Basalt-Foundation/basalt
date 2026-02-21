# Options Protocol

## Category

Decentralized Finance (DeFi) -- Derivatives

## Summary

A decentralized options protocol enabling the creation, trading, and settlement of European-style call and put options on any supported asset. Option writers deposit collateral to mint option tokens (BST-20), which can be freely traded. At expiry, option holders exercise in-the-money options against the writer's collateral. Oracle-fed settlement prices determine option payoff, and the protocol supports multiple strike prices and expiry dates per underlying asset.

## Why It's Useful

- **Non-Linear Risk Management**: Options provide asymmetric risk/reward profiles that perpetual futures cannot. Buyers have limited downside (premium paid) with unlimited upside potential (for calls), making them ideal for hedging and speculation.
- **Volatility Trading**: Options enable trading on volatility itself, not just directional price movement. This attracts sophisticated traders and improves market efficiency.
- **Structured Products**: Options are building blocks for structured products (covered calls, protective puts, spreads, straddles), enabling portfolio strategies previously unavailable on-chain.
- **Insurance Substitute**: Put options serve as price insurance for asset holders. A BST holder can buy a put to guarantee a minimum sell price, regardless of market conditions.
- **Yield Enhancement**: Covered call writing (selling calls against held assets) generates premium income, enhancing yield for long-term holders.
- **DeFi Composability**: Option tokens as BST-20 enable secondary market trading, use as collateral, and integration with other DeFi protocols.

## Key Features

- **European-Style Options**: Exercise only at expiry (not before), simplifying settlement and reducing writer risk compared to American options.
- **Call and Put Options**: Calls give the right to buy at the strike price; puts give the right to sell at the strike price.
- **Fully Collateralized Writing**: Writers must deposit full collateral (underlying for calls, quote token for puts) when minting options, ensuring guaranteed settlement.
- **Option Tokens (BST-20)**: Minted options are standard BST-20 tokens, freely tradeable on AMMs, order books, or OTC.
- **Multiple Series**: Each underlying asset can have options at various strike prices and expiry dates, creating an option chain.
- **Oracle Settlement**: At expiry, the settlement price is fetched from the oracle. In-the-money options are automatically exercisable; out-of-the-money options expire worthless.
- **Partial Exercise**: Option holders can exercise a portion of their option tokens.
- **Writer Reclaim**: After expiry, writers of out-of-the-money options can reclaim their locked collateral.
- **Flash Exercise**: Options can be exercised using flash loans for capital-efficient exercise-and-sell strategies.
- **Option Pricing Oracle**: Optional on-chain Black-Scholes approximation or off-chain pricing feeds for reference pricing.

## Basalt-Specific Advantages

- **AOT-Compiled Settlement Engine**: Batch settlement of hundreds of option series at expiry runs as native AOT-compiled code, enabling efficient processing of expiry events that touch many positions simultaneously.
- **ZK Compliance for Regulated Options**: Options are classified as financial derivatives in most jurisdictions. ZK compliance proofs enable traders to prove regulatory eligibility (accredited investor, permitted jurisdiction) without revealing identity, enabling compliant options trading.
- **BST-3525 SFT Writer Positions**: Writer positions (locked collateral + minted option series) are represented as BST-3525 tokens with metadata (strike, expiry, collateral amount, series), enabling secondary market transfer of writer obligations and portfolio-level management.
- **Confidential Positions**: Pedersen commitments hide option position sizes, preventing information leakage about hedging strategies and preventing targeted attacks on large option writers approaching expiry.
- **BLS Aggregated Oracle for Settlement**: Expiry settlement requires a single trusted price point. BLS aggregate signatures from multiple oracle providers provide a cryptographically strong settlement price with minimal on-chain verification cost.
- **Ed25519 Signed Orders**: Off-chain option trading (RFQ model) uses Ed25519 signed quotes for fast order matching, enabling market makers to provide tight spreads with minimal latency.

## Token Standards Used

- **BST-20**: Option tokens (calls and puts) are BST-20, enabling free trading and DeFi composability. Collateral and settlement tokens are BST-20.
- **BST-3525 (SFT)**: Writer positions with metadata (series, strike, expiry, collateral) for portfolio management and transferability.

## Integration Points

- **AMM DEX (0x0200)**: Option tokens can be listed in AMM pools for secondary market trading. Underlying asset price feeds from AMM TWAP.
- **Lending Protocol (0x0220)**: Option tokens can potentially be used as collateral (with appropriate haircuts). Flash loans for capital-efficient exercise.
- **Stablecoin CDP (0x0230)**: USDB as settlement currency for put option exercise.
- **Flash Loan Pool (0x0260)**: Flash exercise of in-the-money options without upfront capital.
- **Governance (0x0102)**: Controls supported underlying assets, expiry schedules, collateral requirements, and fee parameters.
- **BNS**: Registered as `options.basalt`.
- **SchemaRegistry / IssuerRegistry**: ZK compliance for regulated options trading.

## Technical Sketch

```csharp
// ============================================================
// OptionsProtocol -- European-style options
// ============================================================

public enum OptionType : byte
{
    Call = 0,
    Put = 1
}

public enum SeriesStatus : byte
{
    Active = 0,       // before expiry, can mint/trade
    Expired = 1,      // at expiry, awaiting settlement
    Settled = 2       // settlement price recorded, exercisable
}

[BasaltContract(TypeId = 0x0290)]
public partial class OptionsProtocol : SdkContract
{
    // --- Storage ---

    // seriesId => OptionSeries
    private StorageMap<ulong, OptionSeries> _series;
    private StorageValue<ulong> _nextSeriesId;

    // writerId => WriterPosition
    private StorageMap<ulong, WriterPosition> _writerPositions;
    private StorageValue<ulong> _nextWriterId;

    // seriesId => option token address (BST-20)
    private StorageMap<ulong, Address> _optionTokens;

    // Oracle address
    private StorageValue<Address> _oracle;

    // Fee configuration
    private StorageValue<uint> _mintFeeBps;
    private StorageValue<uint> _exerciseFeeBps;
    private StorageValue<Address> _feeRecipient;

    // --- Structs ---

    public struct OptionSeries
    {
        public ulong SeriesId;
        public Address Underlying;       // e.g., BST address
        public Address QuoteToken;       // e.g., USDB address
        public OptionType Type;
        public UInt256 StrikePrice;      // in quote token per underlying
        public ulong ExpiryBlock;
        public SeriesStatus Status;
        public UInt256 SettlementPrice;  // set at expiry by oracle
        public UInt256 TotalCollateral;
        public UInt256 TotalMinted;
    }

    public struct WriterPosition
    {
        public ulong WriterId;
        public ulong SeriesId;
        public Address Writer;
        public UInt256 CollateralDeposited;
        public UInt256 OptionsMinted;
        public UInt256 CollateralReturned;
        public bool Settled;
    }

    // --- Series Management ---

    /// <summary>
    /// Create a new option series. Governance-approved underlyings only.
    /// </summary>
    public ulong CreateSeries(
        Address underlying,
        Address quoteToken,
        OptionType optionType,
        UInt256 strikePrice,
        ulong expiryBlock)
    {
        Require(expiryBlock > Context.BlockNumber, "EXPIRY_IN_PAST");
        Require(IsApprovedUnderlying(underlying), "UNAPPROVED_UNDERLYING");

        var seriesId = _nextSeriesId.Get();
        _nextSeriesId.Set(seriesId + 1);

        _series.Set(seriesId, new OptionSeries
        {
            SeriesId = seriesId,
            Underlying = underlying,
            QuoteToken = quoteToken,
            Type = optionType,
            StrikePrice = strikePrice,
            ExpiryBlock = expiryBlock,
            Status = SeriesStatus.Active,
            SettlementPrice = UInt256.Zero,
            TotalCollateral = UInt256.Zero,
            TotalMinted = UInt256.Zero
        });

        // Deploy BST-20 option token for this series
        var optionToken = DeployOptionToken(seriesId, underlying, optionType, strikePrice, expiryBlock);
        _optionTokens.Set(seriesId, optionToken);

        EmitEvent("SeriesCreated", seriesId, underlying, optionType,
                  strikePrice, expiryBlock, optionToken);
        return seriesId;
    }

    // --- Writing (Minting) Options ---

    /// <summary>
    /// Write options by depositing collateral. Mints option tokens (BST-20).
    /// For calls: deposit underlying tokens.
    /// For puts: deposit quote tokens equal to strike * amount.
    /// </summary>
    public ulong WriteOptions(ulong seriesId, UInt256 amount)
    {
        var series = _series.Get(seriesId);
        Require(series.Status == SeriesStatus.Active, "NOT_ACTIVE");
        Require(Context.BlockNumber < series.ExpiryBlock, "EXPIRED");

        // ZK compliance check
        RequireZkCompliance(Context.Sender);

        // Calculate and lock collateral
        UInt256 collateralRequired;
        Address collateralToken;

        if (series.Type == OptionType.Call)
        {
            // Call writer deposits underlying asset
            collateralRequired = amount;
            collateralToken = series.Underlying;
        }
        else
        {
            // Put writer deposits quote tokens = strike * amount
            collateralRequired = (amount * series.StrikePrice)
                               / 1_000_000_000_000_000_000UL;
            collateralToken = series.QuoteToken;
        }

        TransferTokenIn(collateralToken, Context.Sender, collateralRequired);

        // Mint fee
        var fee = collateralRequired * _mintFeeBps.Get() / 10000;
        if (fee > UInt256.Zero)
            TransferTokenOut(collateralToken, _feeRecipient.Get(), fee);

        // Create writer position
        var writerId = _nextWriterId.Get();
        _nextWriterId.Set(writerId + 1);

        _writerPositions.Set(writerId, new WriterPosition
        {
            WriterId = writerId,
            SeriesId = seriesId,
            Writer = Context.Sender,
            CollateralDeposited = collateralRequired - fee,
            OptionsMinted = amount,
            CollateralReturned = UInt256.Zero,
            Settled = false
        });

        // Mint option tokens to writer
        MintOptionTokens(_optionTokens.Get(seriesId), Context.Sender, amount);

        // Update series totals
        series.TotalCollateral += collateralRequired - fee;
        series.TotalMinted += amount;
        _series.Set(seriesId, series);

        EmitEvent("OptionsWritten", writerId, seriesId, amount, collateralRequired);
        return writerId;
    }

    // --- Settlement ---

    /// <summary>
    /// Settle a series at expiry. Fetches oracle price and records it.
    /// Anyone can trigger settlement after expiry block.
    /// </summary>
    public void Settle(ulong seriesId)
    {
        var series = _series.Get(seriesId);
        Require(series.Status == SeriesStatus.Active, "NOT_ACTIVE");
        Require(Context.BlockNumber >= series.ExpiryBlock, "NOT_EXPIRED");

        // Fetch settlement price from oracle
        var settlementPrice = GetOraclePrice(series.Underlying, series.QuoteToken);
        Require(settlementPrice > UInt256.Zero, "INVALID_ORACLE_PRICE");

        series.SettlementPrice = settlementPrice;
        series.Status = SeriesStatus.Settled;
        _series.Set(seriesId, series);

        EmitEvent("SeriesSettled", seriesId, settlementPrice);
    }

    // --- Exercise ---

    /// <summary>
    /// Exercise in-the-money option tokens. Burns option tokens and
    /// transfers payoff from the collateral pool.
    /// Call payoff = max(0, settlement - strike) per option.
    /// Put payoff = max(0, strike - settlement) per option.
    /// </summary>
    public UInt256 Exercise(ulong seriesId, UInt256 amount)
    {
        var series = _series.Get(seriesId);
        Require(series.Status == SeriesStatus.Settled, "NOT_SETTLED");

        // Calculate payoff per option
        UInt256 payoff = CalculatePayoff(series, amount);
        Require(payoff > UInt256.Zero, "OUT_OF_MONEY");

        // Burn option tokens
        BurnOptionTokens(_optionTokens.Get(seriesId), Context.Sender, amount);

        // Deduct exercise fee
        var fee = payoff * _exerciseFeeBps.Get() / 10000;
        var netPayoff = payoff - fee;

        // Transfer payoff
        Address payoffToken = series.Type == OptionType.Call
            ? series.Underlying
            : series.QuoteToken;

        TransferTokenOut(payoffToken, Context.Sender, netPayoff);
        if (fee > UInt256.Zero)
            TransferTokenOut(payoffToken, _feeRecipient.Get(), fee);

        // Reduce total collateral
        series.TotalCollateral -= payoff;
        series.TotalMinted -= amount;
        _series.Set(seriesId, series);

        EmitEvent("OptionsExercised", seriesId, Context.Sender, amount, netPayoff);
        return netPayoff;
    }

    // --- Writer Collateral Reclaim ---

    /// <summary>
    /// Writer reclaims unexercised collateral after settlement.
    /// For out-of-the-money options, full collateral is returned.
    /// For partially exercised series, proportional collateral returned.
    /// </summary>
    public UInt256 ReclaimCollateral(ulong writerId)
    {
        var wp = _writerPositions.Get(writerId);
        Require(wp.Writer == Context.Sender, "NOT_WRITER");
        Require(!wp.Settled, "ALREADY_SETTLED");

        var series = _series.Get(wp.SeriesId);
        Require(series.Status == SeriesStatus.Settled, "NOT_SETTLED");

        // Calculate how much collateral was consumed by exercises
        var payoffPerOption = CalculatePayoffPerOption(series);
        var collateralConsumed = wp.OptionsMinted * payoffPerOption
                               / 1_000_000_000_000_000_000UL;
        var collateralReturn = wp.CollateralDeposited - collateralConsumed;

        wp.CollateralReturned = collateralReturn;
        wp.Settled = true;
        _writerPositions.Set(writerId, wp);

        Address collateralToken = series.Type == OptionType.Call
            ? series.Underlying
            : series.QuoteToken;

        if (collateralReturn > UInt256.Zero)
            TransferTokenOut(collateralToken, Context.Sender, collateralReturn);

        EmitEvent("CollateralReclaimed", writerId, collateralReturn);
        return collateralReturn;
    }

    // --- Payoff Calculation ---

    /// <summary>
    /// Calculate the payoff for exercising a given amount of options.
    /// </summary>
    public UInt256 CalculatePayoff(OptionSeries series, UInt256 amount)
    {
        var payoffPerUnit = CalculatePayoffPerOption(series);
        return (amount * payoffPerUnit) / 1_000_000_000_000_000_000UL;
    }

    private UInt256 CalculatePayoffPerOption(OptionSeries series)
    {
        if (series.Type == OptionType.Call)
        {
            // Call: max(0, settlement - strike)
            if (series.SettlementPrice > series.StrikePrice)
                return series.SettlementPrice - series.StrikePrice;
            return UInt256.Zero;
        }
        else
        {
            // Put: max(0, strike - settlement)
            if (series.StrikePrice > series.SettlementPrice)
                return series.StrikePrice - series.SettlementPrice;
            return UInt256.Zero;
        }
    }

    // --- Queries ---

    public OptionSeries GetSeries(ulong seriesId) => _series.Get(seriesId);
    public WriterPosition GetWriterPosition(ulong writerId) => _writerPositions.Get(writerId);
    public Address GetOptionToken(ulong seriesId) => _optionTokens.Get(seriesId);

    public bool IsInTheMoney(ulong seriesId)
    {
        var series = _series.Get(seriesId);
        if (series.Status != SeriesStatus.Settled) return false;
        return CalculatePayoffPerOption(series) > UInt256.Zero;
    }

    // --- Internal Helpers ---

    private bool IsApprovedUnderlying(Address asset) { /* ... */ }
    private Address DeployOptionToken(ulong seriesId, Address underlying,
        OptionType type, UInt256 strike, ulong expiry) { /* ... */ }
    private void MintOptionTokens(Address token, Address to, UInt256 amount) { /* ... */ }
    private void BurnOptionTokens(Address token, Address from, UInt256 amount) { /* ... */ }
    private UInt256 GetOraclePrice(Address base_, Address quote) { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private void RequireZkCompliance(Address user) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**High** -- Options protocols require careful handling of: collateral management for both call and put options with different collateral types, accurate settlement price reporting and validation, payoff calculation with proper fixed-point arithmetic for the max(0, S-K) / max(0, K-S) formulas, proportional collateral distribution when a series is partially exercised, and option token lifecycle management (mint, trade, exercise, expire). Oracle dependence for settlement introduces external risk that must be mitigated.

## Priority

**P2** -- Options provide important risk management tools but depend on mature oracle infrastructure, deep liquidity, and a stablecoin for put collateral. They should be deployed after the core DeFi stack (AMM, lending, stablecoin) and perpetual futures are established. The protocol benefits from existing AMM liquidity for option token secondary markets.
