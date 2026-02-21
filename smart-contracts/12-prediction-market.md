# Prediction Market

## Category

Decentralized Finance (DeFi) -- Information Markets

## Summary

A decentralized prediction market protocol enabling users to trade on the outcomes of future events. Users buy and sell binary outcome shares (YES/NO) represented as BST-20 tokens, with prices reflecting the market's collective probability estimate. Markets are resolved by oracle feeds or governance vote, and the protocol supports both automated market maker (LMSR) liquidity and peer-to-peer order matching for price discovery.

## Why It's Useful

- **Information Discovery**: Prediction markets aggregate dispersed information more efficiently than polls, expert panels, or traditional forecasting methods. Prices directly reflect probability estimates backed by real capital.
- **Hedging Real-World Risk**: Users can hedge against adverse outcomes (election results, protocol upgrades, regulatory decisions) by buying shares in the outcome they want to protect against.
- **Decentralized Forecasting**: Governance DAOs can use prediction markets to inform decision-making -- e.g., "Will this protocol upgrade increase TVL by 20%?" -- providing quantified risk assessment.
- **Speculation on Events**: Traders can speculate on sports, politics, crypto events, protocol metrics, and any verifiable outcome, creating engaging markets that attract users to the ecosystem.
- **DeFi Protocol Insurance Substitute**: Markets like "Will Protocol X be exploited before date Y?" serve as informal insurance, with share prices reflecting perceived risk levels.
- **Permissionless Market Creation**: Anyone can create a market for any verifiable event, enabling niche predictions and long-tail information aggregation.

## Key Features

- **Binary Outcome Markets**: Each market has two outcome tokens (YES and NO). After resolution, winning tokens are redeemable for 1 unit; losing tokens are worth 0.
- **LMSR Automated Market Maker**: The Logarithmic Market Scoring Rule (LMSR) provides always-available liquidity with bounded loss for the market maker, eliminating the cold-start liquidity problem.
- **Peer-to-Peer Order Book**: Optional order matching for price discovery alongside LMSR, enabling limit orders for sophisticated traders.
- **Outcome Tokens as BST-20**: YES and NO tokens are standard BST-20, freely tradeable on AMMs, order books, or OTC.
- **Oracle Resolution**: Markets tied to on-chain data (token prices, TVL, block heights) resolve automatically via oracle feeds.
- **Governance Resolution**: Markets tied to off-chain events (elections, weather, sports) resolve via governance vote with dispute resolution.
- **Market Categories**: Markets organized by category (crypto, politics, sports, science) for discovery and curation.
- **Fee Model**: Trading fees split between market creator, liquidity providers, and protocol treasury.
- **Liquidity Mining**: Market creators and early liquidity providers earn enhanced rewards to incentivize diverse market creation.
- **Conditional Markets**: Markets whose resolution depends on another market's outcome (e.g., "If candidate X wins, will policy Y be enacted?").
- **Dispute Resolution**: Contested resolutions can be challenged with a bond, escalating to a broader governance vote.

## Basalt-Specific Advantages

- **AOT-Compiled LMSR Engine**: The LMSR pricing function involves logarithmic and exponential calculations. AOT-compiled execution provides significantly faster computation than EVM-interpreted math libraries, enabling efficient real-time price updates as trades occur.
- **ZK Compliance for Regulated Markets**: Prediction markets face regulatory scrutiny in many jurisdictions (gambling classifications). ZK compliance proofs enable users to prove they are in a permitted jurisdiction without revealing their identity, enabling the protocol to operate compliantly while preserving privacy.
- **Confidential Positions via Pedersen Commitments**: Position sizes can be hidden using Pedersen commitments, preventing information leakage. In prediction markets, position sizes signal beliefs -- hiding them prevents sophisticated traders from inferring private information from order flow.
- **Governance Integration for Resolution**: Basalt's native Governance contract (0x0102) with quadratic voting provides a Sybil-resistant, plutocracy-resistant mechanism for resolving off-chain event markets, reducing the risk of governance capture for disputed resolutions.
- **BST-3525 SFT Market Positions**: User positions across multiple markets can be represented as BST-3525 tokens with metadata (market, outcome, entry price, quantity), enabling portfolio management and secondary market trading of prediction positions.
- **BLS Aggregated Oracle for Automated Resolution**: Markets with on-chain resolution conditions can use BLS aggregate signatures from multiple oracle providers for high-assurance automated resolution, reducing reliance on single oracle points of failure.
- **BNS for Market Discovery**: Markets registered under BNS names (e.g., `btc-100k-2026.predict.basalt`) provide human-readable URLs for market discovery and sharing.

## Token Standards Used

- **BST-20**: Outcome tokens (YES/NO shares) and collateral tokens (USDB) are BST-20.
- **BST-3525 (SFT)**: Multi-market positions with metadata for portfolio management.
- **BST-4626 (Vault)**: LMSR liquidity pools can implement BST-4626 for composability.

## Integration Points

- **Governance (0x0102)**: Dispute resolution for contested market outcomes. Market creation approval for sensitive categories. Protocol parameter governance.
- **AMM DEX (0x0200)**: Outcome tokens can be traded on AMM pools for additional liquidity. TWAP prices from AMM serve as oracle reference for crypto-related markets.
- **Stablecoin CDP (0x0230)**: USDB as primary collateral for minting outcome tokens.
- **BNS (0x0101)**: Human-readable market names for discovery and sharing.
- **SchemaRegistry / IssuerRegistry**: ZK compliance for jurisdiction-restricted markets.
- **BridgeETH (0x...1008)**: Cross-chain oracle data for markets on Ethereum events.

## Technical Sketch

```csharp
// ============================================================
// PredictionMarket -- Binary outcome prediction markets
// ============================================================

public enum MarketStatus : byte
{
    Active = 0,         // trading open
    Paused = 1,         // trading halted
    ResolutionPending = 2, // awaiting oracle/governance resolution
    Resolved = 3,       // outcome determined
    Disputed = 4        // resolution contested
}

public enum Outcome : byte
{
    Unresolved = 0,
    Yes = 1,
    No = 2,
    Invalid = 3         // market voided, full refund
}

[BasaltContract(TypeId = 0x02B0)]
public partial class PredictionMarket : SdkContract
{
    // --- Storage ---

    // marketId => Market
    private StorageMap<ulong, Market> _markets;
    private StorageValue<ulong> _nextMarketId;

    // marketId => LMSR state
    private StorageMap<ulong, LmsrState> _lmsrState;

    // marketId => YES token address
    private StorageMap<ulong, Address> _yesTokens;

    // marketId => NO token address
    private StorageMap<ulong, Address> _noTokens;

    // marketId => total collateral deposited
    private StorageMap<ulong, UInt256> _totalCollateral;

    // Dispute bond amount
    private StorageValue<UInt256> _disputeBond;

    // Protocol fee
    private StorageValue<uint> _protocolFeeBps;
    private StorageValue<uint> _creatorFeeBps;
    private StorageValue<Address> _feeRecipient;

    // --- Structs ---

    public struct Market
    {
        public ulong MarketId;
        public Address Creator;
        public Address CollateralToken;   // e.g., USDB
        public string Question;           // "Will BST reach $100 by 2026-12-31?"
        public string Category;
        public ulong ResolutionBlock;     // when market resolves
        public MarketStatus Status;
        public Outcome ResolvedOutcome;
        public Address OracleAddress;     // for automated resolution
        public byte[] ResolutionCriteria; // hash of detailed criteria
        public ulong CreatedBlock;
    }

    public struct LmsrState
    {
        public UInt256 YesShares;         // outstanding YES tokens
        public UInt256 NoShares;          // outstanding NO tokens
        public UInt256 Liquidity;         // LMSR b parameter (controls spread)
        public UInt256 SubsidyCost;       // total cost to market maker
    }

    // --- Market Creation ---

    /// <summary>
    /// Create a new binary prediction market.
    /// Creator provides initial LMSR liquidity subsidy.
    /// </summary>
    public ulong CreateMarket(
        Address collateralToken,
        string question,
        string category,
        ulong resolutionBlock,
        Address oracleAddress,
        byte[] resolutionCriteria,
        UInt256 initialLiquidity)
    {
        Require(resolutionBlock > Context.BlockNumber, "PAST_RESOLUTION");
        Require(initialLiquidity > UInt256.Zero, "ZERO_LIQUIDITY");

        // Transfer initial liquidity from creator
        TransferTokenIn(collateralToken, Context.Sender, initialLiquidity);

        var marketId = _nextMarketId.Get();
        _nextMarketId.Set(marketId + 1);

        _markets.Set(marketId, new Market
        {
            MarketId = marketId,
            Creator = Context.Sender,
            CollateralToken = collateralToken,
            Question = question,
            Category = category,
            ResolutionBlock = resolutionBlock,
            Status = MarketStatus.Active,
            ResolvedOutcome = Outcome.Unresolved,
            OracleAddress = oracleAddress,
            ResolutionCriteria = resolutionCriteria,
            CreatedBlock = Context.BlockNumber
        });

        // Initialize LMSR state
        _lmsrState.Set(marketId, new LmsrState
        {
            YesShares = UInt256.Zero,
            NoShares = UInt256.Zero,
            Liquidity = initialLiquidity,
            SubsidyCost = UInt256.Zero
        });

        // Deploy outcome tokens
        var yesToken = DeployOutcomeToken(marketId, "YES");
        var noToken = DeployOutcomeToken(marketId, "NO");
        _yesTokens.Set(marketId, yesToken);
        _noTokens.Set(marketId, noToken);

        EmitEvent("MarketCreated", marketId, Context.Sender, question,
                  resolutionBlock, yesToken, noToken);
        return marketId;
    }

    // --- Trading via LMSR ---

    /// <summary>
    /// Buy outcome shares using the LMSR pricing mechanism.
    /// Price is determined by current share distribution and liquidity parameter.
    /// </summary>
    public UInt256 BuyOutcome(ulong marketId, Outcome outcome, UInt256 shareAmount)
    {
        Require(outcome == Outcome.Yes || outcome == Outcome.No, "INVALID_OUTCOME");
        var market = _markets.Get(marketId);
        Require(market.Status == MarketStatus.Active, "NOT_ACTIVE");
        Require(Context.BlockNumber < market.ResolutionBlock, "MARKET_CLOSED");

        // ZK compliance check for regulated markets
        RequireZkComplianceIfNeeded(marketId);

        var state = _lmsrState.Get(marketId);

        // Calculate LMSR cost
        var cost = CalculateLmsrCost(state, outcome, shareAmount);

        // Apply trading fee
        var protocolFee = cost * _protocolFeeBps.Get() / 10000;
        var creatorFee = cost * _creatorFeeBps.Get() / 10000;
        var totalCost = cost + protocolFee + creatorFee;

        TransferTokenIn(market.CollateralToken, Context.Sender, totalCost);

        // Distribute fees
        if (protocolFee > UInt256.Zero)
            TransferTokenOut(market.CollateralToken, _feeRecipient.Get(), protocolFee);
        if (creatorFee > UInt256.Zero)
            TransferTokenOut(market.CollateralToken, market.Creator, creatorFee);

        // Update LMSR state
        if (outcome == Outcome.Yes)
            state.YesShares += shareAmount;
        else
            state.NoShares += shareAmount;
        _lmsrState.Set(marketId, state);

        // Mint outcome tokens
        Address outcomeToken = outcome == Outcome.Yes
            ? _yesTokens.Get(marketId)
            : _noTokens.Get(marketId);
        MintToken(outcomeToken, Context.Sender, shareAmount);

        _totalCollateral.Set(marketId, _totalCollateral.Get(marketId) + cost);

        EmitEvent("SharesBought", marketId, Context.Sender, outcome, shareAmount, totalCost);
        return totalCost;
    }

    /// <summary>
    /// Sell outcome shares back to the LMSR.
    /// </summary>
    public UInt256 SellOutcome(ulong marketId, Outcome outcome, UInt256 shareAmount)
    {
        Require(outcome == Outcome.Yes || outcome == Outcome.No, "INVALID_OUTCOME");
        var market = _markets.Get(marketId);
        Require(market.Status == MarketStatus.Active, "NOT_ACTIVE");

        var state = _lmsrState.Get(marketId);

        // Calculate LMSR proceeds
        var proceeds = CalculateLmsrProceeds(state, outcome, shareAmount);

        // Burn outcome tokens
        Address outcomeToken = outcome == Outcome.Yes
            ? _yesTokens.Get(marketId)
            : _noTokens.Get(marketId);
        BurnToken(outcomeToken, Context.Sender, shareAmount);

        // Update state
        if (outcome == Outcome.Yes)
            state.YesShares -= shareAmount;
        else
            state.NoShares -= shareAmount;
        _lmsrState.Set(marketId, state);

        _totalCollateral.Set(marketId, _totalCollateral.Get(marketId) - proceeds);
        TransferTokenOut(market.CollateralToken, Context.Sender, proceeds);

        EmitEvent("SharesSold", marketId, Context.Sender, outcome, shareAmount, proceeds);
        return proceeds;
    }

    /// <summary>
    /// Mint a complete set (1 YES + 1 NO) for 1 unit of collateral.
    /// Useful for arbitrage and market making.
    /// </summary>
    public void MintCompleteSet(ulong marketId, UInt256 amount)
    {
        var market = _markets.Get(marketId);
        Require(market.Status == MarketStatus.Active, "NOT_ACTIVE");

        TransferTokenIn(market.CollateralToken, Context.Sender, amount);

        MintToken(_yesTokens.Get(marketId), Context.Sender, amount);
        MintToken(_noTokens.Get(marketId), Context.Sender, amount);

        _totalCollateral.Set(marketId, _totalCollateral.Get(marketId) + amount);
        EmitEvent("CompleteSetMinted", marketId, Context.Sender, amount);
    }

    /// <summary>
    /// Redeem a complete set (1 YES + 1 NO) for 1 unit of collateral.
    /// </summary>
    public void RedeemCompleteSet(ulong marketId, UInt256 amount)
    {
        var market = _markets.Get(marketId);

        BurnToken(_yesTokens.Get(marketId), Context.Sender, amount);
        BurnToken(_noTokens.Get(marketId), Context.Sender, amount);

        TransferTokenOut(market.CollateralToken, Context.Sender, amount);

        _totalCollateral.Set(marketId, _totalCollateral.Get(marketId) - amount);
        EmitEvent("CompleteSetRedeemed", marketId, Context.Sender, amount);
    }

    // --- Resolution ---

    /// <summary>
    /// Resolve a market via oracle. Anyone can trigger after resolution block.
    /// </summary>
    public void ResolveViaOracle(ulong marketId)
    {
        var market = _markets.Get(marketId);
        Require(market.Status == MarketStatus.Active, "NOT_ACTIVE");
        Require(Context.BlockNumber >= market.ResolutionBlock, "TOO_EARLY");
        Require(market.OracleAddress != Address.Zero, "NO_ORACLE");

        // Query oracle for resolution
        var oracleResult = Context.Call<Outcome>(market.OracleAddress,
            "ResolveMarket", marketId, market.ResolutionCriteria);

        Require(oracleResult == Outcome.Yes || oracleResult == Outcome.No
             || oracleResult == Outcome.Invalid, "INVALID_RESULT");

        market.ResolvedOutcome = oracleResult;
        market.Status = MarketStatus.Resolved;
        _markets.Set(marketId, market);

        EmitEvent("MarketResolved", marketId, oracleResult);
    }

    /// <summary>
    /// Resolve a market via governance vote. Governance-only.
    /// Used for off-chain events that cannot be oracle-resolved.
    /// </summary>
    public void ResolveViaGovernance(ulong marketId, Outcome outcome)
    {
        RequireGovernance();

        var market = _markets.Get(marketId);
        Require(market.Status == MarketStatus.Active
             || market.Status == MarketStatus.ResolutionPending
             || market.Status == MarketStatus.Disputed, "INVALID_STATUS");

        market.ResolvedOutcome = outcome;
        market.Status = MarketStatus.Resolved;
        _markets.Set(marketId, market);

        EmitEvent("MarketResolvedByGovernance", marketId, outcome);
    }

    /// <summary>
    /// Dispute a market resolution. Requires posting a bond.
    /// Escalates to governance vote.
    /// </summary>
    public void DisputeResolution(ulong marketId)
    {
        var market = _markets.Get(marketId);
        Require(market.Status == MarketStatus.Resolved, "NOT_RESOLVED");

        var bond = _disputeBond.Get();
        TransferTokenIn(market.CollateralToken, Context.Sender, bond);

        market.Status = MarketStatus.Disputed;
        _markets.Set(marketId, market);

        EmitEvent("ResolutionDisputed", marketId, Context.Sender, bond);
    }

    // --- Redemption ---

    /// <summary>
    /// Redeem winning outcome tokens for collateral after resolution.
    /// Winning tokens = 1 collateral unit each. Losing tokens = 0.
    /// Invalid = full refund for complete sets.
    /// </summary>
    public UInt256 RedeemWinnings(ulong marketId, UInt256 amount)
    {
        var market = _markets.Get(marketId);
        Require(market.Status == MarketStatus.Resolved, "NOT_RESOLVED");

        UInt256 payout = UInt256.Zero;

        if (market.ResolvedOutcome == Outcome.Invalid)
        {
            // Invalid: refund based on complete sets
            var yesBalance = GetTokenBalance(_yesTokens.Get(marketId), Context.Sender);
            var noBalance = GetTokenBalance(_noTokens.Get(marketId), Context.Sender);
            var sets = UInt256.Min(UInt256.Min(yesBalance, noBalance), amount);

            BurnToken(_yesTokens.Get(marketId), Context.Sender, sets);
            BurnToken(_noTokens.Get(marketId), Context.Sender, sets);
            payout = sets;
        }
        else
        {
            Address winningToken = market.ResolvedOutcome == Outcome.Yes
                ? _yesTokens.Get(marketId)
                : _noTokens.Get(marketId);

            BurnToken(winningToken, Context.Sender, amount);
            payout = amount; // 1:1 redemption for winners
        }

        TransferTokenOut(market.CollateralToken, Context.Sender, payout);

        EmitEvent("WinningsRedeemed", marketId, Context.Sender, payout);
        return payout;
    }

    // --- LMSR Pricing ---

    /// <summary>
    /// Calculate the LMSR cost to buy a given amount of outcome shares.
    /// Cost = b * ln(exp(q_yes/b) + exp(q_no/b)) after - before
    /// </summary>
    public UInt256 CalculateLmsrCost(
        LmsrState state, Outcome outcome, UInt256 amount)
    {
        var b = state.Liquidity;

        // Cost function: C(q) = b * ln(e^(q_yes/b) + e^(q_no/b))
        // Cost of trade = C(q_after) - C(q_before)

        var costBefore = LmsrCostFunction(state.YesShares, state.NoShares, b);

        UInt256 newYes = state.YesShares;
        UInt256 newNo = state.NoShares;
        if (outcome == Outcome.Yes)
            newYes += amount;
        else
            newNo += amount;

        var costAfter = LmsrCostFunction(newYes, newNo, b);

        return costAfter - costBefore;
    }

    /// <summary>
    /// Get the current LMSR price for each outcome (0 to 1, scaled to 1e18).
    /// Price_yes = e^(q_yes/b) / (e^(q_yes/b) + e^(q_no/b))
    /// </summary>
    public (UInt256 yesPrice, UInt256 noPrice) GetPrices(ulong marketId)
    {
        var state = _lmsrState.Get(marketId);
        var b = state.Liquidity;

        // Simplified pricing using softmax
        var expYes = ApproxExp(state.YesShares, b);
        var expNo = ApproxExp(state.NoShares, b);
        var total = expYes + expNo;

        var yesPrice = (expYes * 1_000_000_000_000_000_000UL) / total;
        var noPrice = (expNo * 1_000_000_000_000_000_000UL) / total;

        return (yesPrice, noPrice);
    }

    // --- Queries ---

    public Market GetMarket(ulong marketId) => _markets.Get(marketId);
    public LmsrState GetLmsrState(ulong marketId) => _lmsrState.Get(marketId);
    public Address GetYesToken(ulong marketId) => _yesTokens.Get(marketId);
    public Address GetNoToken(ulong marketId) => _noTokens.Get(marketId);
    public UInt256 GetTotalCollateral(ulong marketId) => _totalCollateral.Get(marketId);

    public UInt256 GetShareCost(ulong marketId, Outcome outcome, UInt256 amount)
    {
        var state = _lmsrState.Get(marketId);
        return CalculateLmsrCost(state, outcome, amount);
    }

    // --- Internal Helpers ---

    private UInt256 LmsrCostFunction(UInt256 qYes, UInt256 qNo, UInt256 b)
    {
        // C(q) = b * ln(e^(qYes/b) + e^(qNo/b))
        // Implemented using fixed-point arithmetic with overflow protection
        var expYes = ApproxExp(qYes, b);
        var expNo = ApproxExp(qNo, b);
        return b * ApproxLn(expYes + expNo);
    }

    private UInt256 ApproxExp(UInt256 numerator, UInt256 denominator)
    {
        // Taylor series or lookup table approximation for e^(x)
        // where x = numerator/denominator
        // Implementation uses fixed-point arithmetic
        /* ... */
        return UInt256.Zero; // placeholder
    }

    private UInt256 ApproxLn(UInt256 x)
    {
        // Approximate natural log using fixed-point arithmetic
        /* ... */
        return UInt256.Zero; // placeholder
    }

    private UInt256 CalculateLmsrProceeds(LmsrState state, Outcome outcome, UInt256 amount)
    {
        // Inverse of cost calculation
        /* ... */
        return UInt256.Zero; // placeholder
    }

    private Address DeployOutcomeToken(ulong marketId, string name) { /* ... */ }
    private void MintToken(Address token, Address to, UInt256 amount) { /* ... */ }
    private void BurnToken(Address token, Address from, UInt256 amount) { /* ... */ }
    private UInt256 GetTokenBalance(Address token, Address holder) { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private void RequireZkComplianceIfNeeded(ulong marketId) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**High** -- Prediction markets combine several complex components: LMSR pricing with logarithmic/exponential fixed-point arithmetic (the most mathematically complex part), dual resolution mechanisms (oracle + governance), dispute resolution with bond mechanics, outcome token lifecycle management, and complete set arbitrage logic. The LMSR implementation requires careful numerical analysis to avoid overflow/underflow in fixed-point exp/ln approximations. Resolution edge cases (invalid markets, disputed outcomes, oracle failures) require thorough handling.

## Priority

**P3** -- Prediction markets are a valuable application layer but not a prerequisite for other DeFi protocols. They depend on a stablecoin (USDB for collateral), governance (for resolution), and oracle infrastructure. They represent an important ecosystem differentiator but can be deployed after core DeFi is mature. Regulatory considerations (gambling classification) may require careful launch strategy.
