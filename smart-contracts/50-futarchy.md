# Futarchy Governance

## Category

Governance / Prediction Markets

## Summary

A futarchy governance contract that implements decision markets where governance proposals are resolved by prediction market outcomes rather than direct voting. For each proposal, two conditional prediction markets are created: one predicting the value of a success metric if the proposal is adopted, and one predicting the same metric if the proposal is rejected. The market with the higher predicted outcome determines whether the proposal is implemented. This mechanism harnesses the information-aggregation power of markets to make governance decisions based on expected consequences rather than popularity or political maneuvering.

## Why It's Useful

- **Information-efficient governance**: Markets aggregate dispersed information more effectively than voting. Participants with relevant knowledge are incentivized to trade on their beliefs, producing more informed decisions.
- **Incentive alignment**: Unlike voting (where incorrect votes have no cost), market participants put capital at risk. This ensures that only participants with genuine conviction and information influence outcomes.
- **Reduced governance theater**: Proposals are evaluated on their expected impact rather than on the proposer's persuasive abilities or political capital. The market does not care about rhetoric.
- **Scalable decision-making**: Futarchy scales better than deliberative governance because it does not require every participant to deeply understand every proposal. Informed specialists can trade on their domain expertise.
- **Manipulation resistance**: Manipulation of prediction markets is expensive and self-correcting. If someone artificially pumps a market, informed traders profit by correcting the price.
- **Complementary to existing governance**: Futarchy can be used alongside traditional voting for different classes of decisions -- high-stakes, measurable-outcome decisions go to futarchy, while value-based decisions remain with direct governance.
- **Experimental governance**: Basalt can pioneer futarchy on a modern blockchain, contributing to governance innovation in the broader ecosystem.

## Key Features

- Proposal creation: anyone can create a futarchy proposal with a description, success metric, measurement method, and resolution time
- Conditional market creation: for each proposal, two prediction markets are created: "metric if adopted" and "metric if rejected"
- Market mechanics: participants buy and sell outcome tokens (YES/NO for each market) using a constant-product automated market maker (CPMM)
- Decision rule: when the decision period ends, the market with the higher time-weighted average price (TWAP) determines the proposal outcome. If "metric if adopted" > "metric if rejected", the proposal passes.
- Resolution: after the proposal outcome is determined, the winning market resolves based on actual metric observation. Losing market positions are refunded at entry price.
- Oracle integration: metric observation uses the Oracle contract for on-chain data feeds
- Liquidity provision: initial liquidity is provided by the proposal creator or the DAO. Additional liquidity providers earn trading fees.
- Position management: traders can buy, sell, and transfer outcome tokens at any time during the trading period
- Anti-manipulation safeguards: TWAP (not spot price) is used for the decision, preventing last-minute manipulation
- Proposal bond: proposal creators post a bond that is returned if the proposal is resolved properly, discouraging spam
- Multiple metric types: support for scalar (price targets), binary (yes/no), and categorical outcomes
- Resolution dispute: if the oracle-provided metric is contested, a governance vote can override

## Basalt-Specific Advantages

- **Oracle contract integration**: Basalt's decentralized Oracle Network provides the on-chain data feeds needed for metric observation and market resolution. This native integration eliminates dependency on external oracle protocols.
- **ZK identity for trader privacy**: Traders in decision markets may not want their positions publicly visible (to avoid social pressure or strategic counter-trading). Basalt's ZK compliance layer allows traders to prove they are authorized participants without revealing their identity or position.
- **Confidential trading via Pedersen commitments**: Trade amounts and positions can use Pedersen commitments, hiding individual trades from on-chain observers. This prevents front-running and strategic position disclosure while maintaining verifiable settlement.
- **Ed25519 signed orders**: Market orders can be signed off-chain with Ed25519 and matched on-chain, enabling an order book style or batch auction mechanism alongside the AMM.
- **BLAKE3 market identifiers**: Market and position identifiers use BLAKE3 hashing, providing efficient and collision-resistant references for the complex multi-market structure.
- **Governance contract fallback**: If futarchy markets are thin or manipulated, the existing quadratic voting Governance contract provides a democratic fallback for dispute resolution.
- **BST-3525 SFT for outcome tokens**: Conditional outcome tokens (YES-if-adopted, NO-if-adopted, YES-if-rejected, NO-if-rejected) are naturally represented as BST-3525 semi-fungible tokens. The slot represents the market (proposal x condition), and the value represents the position size. This makes positions composable and tradeable on secondary markets.
- **BST-4626 vault for market liquidity**: Liquidity provision for decision markets can use the BST-4626 vault pattern, where LPs deposit into a vault that automatically manages liquidity across markets and earns trading fees.
- **UInt256 for price precision**: Market prices and TWAP calculations require high-precision fixed-point arithmetic. Basalt's `UInt256` provides 256 bits of precision, sufficient for accurate price representation.
- **AOT-compiled CPMM**: The constant-product market maker formula (x * y = k) and TWAP computation execute efficiently under AOT compilation, ensuring predictable gas costs for trades.

## Token Standards Used

- **BST-20**: Base trading token (native BST or stablecoin)
- **BST-3525**: Conditional outcome tokens -- slot = (proposalId, condition), value = position size
- **BST-4626**: Liquidity provision vaults for decision markets
- **BST-721**: Proposal NFTs representing governance proposals with market metadata

## Integration Points

- **Oracle Network (proposed)**: On-chain data feeds for metric observation and market resolution. Critical dependency for futarchy.
- **Governance (0x...1005 area)**: Democratic fallback for disputed resolutions. Governance also controls futarchy parameters (TWAP window, minimum liquidity, bond size).
- **SchemaRegistry (0x...1006)**: ZK identity for trader privacy and authorized participation.
- **IssuerRegistry (0x...1007)**: Credentials for market makers and liquidity providers.
- **StakingPool (0x...1005)**: Proposal bonds can be staked, and liquidity provision can earn additional staking rewards.
- **Escrow (0x...1003)**: Proposal bonds and market collateral held in escrow until resolution.
- **DAO Treasury**: Matching liquidity for decision markets funded from the treasury.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Futarchy Governance -- decision markets where proposals are resolved by
/// prediction market outcomes rather than direct voting.
/// Two conditional markets per proposal: "metric if adopted" vs "metric if rejected".
/// </summary>
[BasaltContract]
public partial class FutarchyGovernance
{
    // Precision for fixed-point price calculations (18 decimals)
    private static readonly UInt256 PricePrecision = new UInt256(1_000_000_000_000_000_000);

    // --- Oracle reference ---
    private readonly byte[] _oracleAddress;
    private readonly byte[] _governanceAddress;

    // --- Admin ---
    private readonly StorageMap<string, string> _admin;

    // --- Proposal state ---
    private readonly StorageValue<ulong> _nextProposalId;
    private readonly StorageMap<string, string> _proposalDescriptions;      // propId -> description
    private readonly StorageMap<string, string> _proposalProposers;         // propId -> proposer hex
    private readonly StorageMap<string, string> _proposalMetricNames;       // propId -> metric name
    private readonly StorageMap<string, ulong> _proposalOracleFeedIds;     // propId -> oracle feed ID for metric
    private readonly StorageMap<string, string> _proposalStatus;            // propId -> "trading"|"decided"|"resolved"|"disputed"
    private readonly StorageMap<string, UInt256> _proposalBonds;            // propId -> bond amount
    private readonly StorageMap<string, ulong> _proposalTradingEndBlocks;   // propId -> end of trading period
    private readonly StorageMap<string, ulong> _proposalResolutionBlocks;   // propId -> when metric is observed
    private readonly StorageMap<string, bool> _proposalDecision;            // propId -> true = adopted, false = rejected

    // --- Market state (two markets per proposal) ---
    // Market "A" = "metric if adopted", Market "R" = "metric if rejected"
    // Each market has YES and NO reserves in a CPMM (x * y = k)
    private readonly StorageMap<string, UInt256> _marketYesReserves;        // "propId:A" or "propId:R" -> YES reserve
    private readonly StorageMap<string, UInt256> _marketNoReserves;         // "propId:A" or "propId:R" -> NO reserve
    private readonly StorageMap<string, UInt256> _marketK;                  // "propId:A" or "propId:R" -> invariant k

    // --- TWAP tracking ---
    private readonly StorageMap<string, UInt256> _twapCumulativePrice;      // "propId:A"|"propId:R" -> cumulative price
    private readonly StorageMap<string, ulong> _twapLastUpdateBlock;        // market key -> last update block
    private readonly StorageMap<string, ulong> _twapStartBlock;             // market key -> start block

    // --- Positions ---
    private readonly StorageMap<string, UInt256> _yesPositions;            // "propId:market:traderHex" -> YES tokens held
    private readonly StorageMap<string, UInt256> _noPositions;             // "propId:market:traderHex" -> NO tokens held

    // --- Resolution ---
    private readonly StorageMap<string, UInt256> _resolvedMetricValue;     // propId -> observed metric
    private readonly StorageMap<string, bool> _traderSettled;              // "propId:market:traderHex" -> settled

    // --- Liquidity ---
    private readonly StorageMap<string, UInt256> _lpShares;                // "propId:market:lpHex" -> LP shares
    private readonly StorageMap<string, UInt256> _marketTotalLpShares;     // market key -> total LP shares

    // --- Config ---
    private readonly StorageValue<UInt256> _minBond;
    private readonly StorageValue<UInt256> _minLiquidity;
    private readonly StorageValue<ulong> _twapWindowBlocks;

    public FutarchyGovernance(byte[] oracleAddress, byte[] governanceAddress,
        UInt256 minBond = default, UInt256 minLiquidity = default, ulong twapWindowBlocks = 7200)
    {
        _oracleAddress = oracleAddress;
        _governanceAddress = governanceAddress;

        _admin = new StorageMap<string, string>("fg_admin");
        _nextProposalId = new StorageValue<ulong>("fg_nprop");
        _proposalDescriptions = new StorageMap<string, string>("fg_pdesc");
        _proposalProposers = new StorageMap<string, string>("fg_pprop");
        _proposalMetricNames = new StorageMap<string, string>("fg_pmetric");
        _proposalOracleFeedIds = new StorageMap<string, ulong>("fg_pfeed");
        _proposalStatus = new StorageMap<string, string>("fg_psts");
        _proposalBonds = new StorageMap<string, UInt256>("fg_pbond");
        _proposalTradingEndBlocks = new StorageMap<string, ulong>("fg_ptend");
        _proposalResolutionBlocks = new StorageMap<string, ulong>("fg_pres");
        _proposalDecision = new StorageMap<string, bool>("fg_pdec");
        _marketYesReserves = new StorageMap<string, UInt256>("fg_myr");
        _marketNoReserves = new StorageMap<string, UInt256>("fg_mnr");
        _marketK = new StorageMap<string, UInt256>("fg_mk");
        _twapCumulativePrice = new StorageMap<string, UInt256>("fg_twcp");
        _twapLastUpdateBlock = new StorageMap<string, ulong>("fg_twlu");
        _twapStartBlock = new StorageMap<string, ulong>("fg_twsb");
        _yesPositions = new StorageMap<string, UInt256>("fg_ypos");
        _noPositions = new StorageMap<string, UInt256>("fg_npos");
        _resolvedMetricValue = new StorageMap<string, UInt256>("fg_rmv");
        _traderSettled = new StorageMap<string, bool>("fg_tsettled");
        _lpShares = new StorageMap<string, UInt256>("fg_lps");
        _marketTotalLpShares = new StorageMap<string, UInt256>("fg_mtls");
        _minBond = new StorageValue<UInt256>("fg_mbond");
        _minLiquidity = new StorageValue<UInt256>("fg_mliq");
        _twapWindowBlocks = new StorageValue<ulong>("fg_twwin");

        _admin.Set("admin", Convert.ToHexString(Context.Caller));
        if (minBond.IsZero) minBond = new UInt256(10000);
        _minBond.Set(minBond);
        if (minLiquidity.IsZero) minLiquidity = new UInt256(100000);
        _minLiquidity.Set(minLiquidity);
        _twapWindowBlocks.Set(twapWindowBlocks);
    }

    // ===================== Proposal Lifecycle =====================

    /// <summary>
    /// Create a futarchy proposal. Sends bond + initial liquidity for both markets.
    /// The proposal creates two conditional prediction markets.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateProposal(string description, string metricName, ulong oracleFeedId,
        ulong tradingPeriodBlocks, ulong resolutionDelayBlocks)
    {
        Context.Require(!string.IsNullOrEmpty(description), "FUTARCHY: description required");
        Context.Require(!string.IsNullOrEmpty(metricName), "FUTARCHY: metric required");
        Context.Require(tradingPeriodBlocks > 0, "FUTARCHY: invalid trading period");

        var totalRequired = _minBond.Get() + _minLiquidity.Get() * new UInt256(2);
        Context.Require(Context.TxValue >= totalRequired, "FUTARCHY: insufficient deposit");

        var id = _nextProposalId.Get();
        _nextProposalId.Set(id + 1);
        var key = id.ToString();

        _proposalDescriptions.Set(key, description);
        _proposalProposers.Set(key, Convert.ToHexString(Context.Caller));
        _proposalMetricNames.Set(key, metricName);
        _proposalOracleFeedIds.Set(key, oracleFeedId);
        _proposalStatus.Set(key, "trading");
        _proposalBonds.Set(key, _minBond.Get());
        _proposalTradingEndBlocks.Set(key, Context.BlockHeight + tradingPeriodBlocks);
        _proposalResolutionBlocks.Set(key, Context.BlockHeight + tradingPeriodBlocks + resolutionDelayBlocks);

        // Initialize both markets with equal YES/NO reserves
        var initialLiquidity = _minLiquidity.Get();
        InitializeMarket(key + ":A", initialLiquidity);
        InitializeMarket(key + ":R", initialLiquidity);

        Context.Emit(new FutarchyProposalCreatedEvent
        {
            ProposalId = id, Proposer = Context.Caller,
            Description = description, MetricName = metricName,
            TradingEndBlock = Context.BlockHeight + tradingPeriodBlocks
        });
        return id;
    }

    // ===================== Trading =====================

    /// <summary>
    /// Buy YES tokens in a conditional market.
    /// market: "A" (if adopted) or "R" (if rejected)
    /// </summary>
    [BasaltEntrypoint]
    public void BuyYes(ulong proposalId, string market)
    {
        Context.Require(!Context.TxValue.IsZero, "FUTARCHY: must send value");
        var propKey = proposalId.ToString();
        Context.Require(_proposalStatus.Get(propKey) == "trading", "FUTARCHY: not in trading");
        Context.Require(Context.BlockHeight <= _proposalTradingEndBlocks.Get(propKey),
            "FUTARCHY: trading ended");

        var marketKey = propKey + ":" + market;
        Context.Require(market == "A" || market == "R", "FUTARCHY: invalid market");

        // Update TWAP before trade
        UpdateTwap(marketKey);

        // CPMM: trader sends BST, receives YES tokens
        var yesReserve = _marketYesReserves.Get(marketKey);
        var noReserve = _marketNoReserves.Get(marketKey);
        var k = _marketK.Get(marketKey);

        // dy = yesReserve - k / (noReserve + amountIn)
        var newNoReserve = noReserve + Context.TxValue;
        var newYesReserve = k / newNoReserve;
        var yesOut = yesReserve - newYesReserve;

        Context.Require(!yesOut.IsZero, "FUTARCHY: zero output");

        _marketYesReserves.Set(marketKey, newYesReserve);
        _marketNoReserves.Set(marketKey, newNoReserve);

        // Record position
        var traderHex = Convert.ToHexString(Context.Caller);
        var posKey = marketKey + ":" + traderHex;
        _yesPositions.Set(posKey, _yesPositions.Get(posKey) + yesOut);

        Context.Emit(new TradeExecutedEvent
        {
            ProposalId = proposalId, Market = market,
            Trader = Context.Caller, Direction = "buyYes",
            AmountIn = Context.TxValue, AmountOut = yesOut
        });
    }

    /// <summary>
    /// Buy NO tokens in a conditional market.
    /// </summary>
    [BasaltEntrypoint]
    public void BuyNo(ulong proposalId, string market)
    {
        Context.Require(!Context.TxValue.IsZero, "FUTARCHY: must send value");
        var propKey = proposalId.ToString();
        Context.Require(_proposalStatus.Get(propKey) == "trading", "FUTARCHY: not in trading");
        Context.Require(Context.BlockHeight <= _proposalTradingEndBlocks.Get(propKey),
            "FUTARCHY: trading ended");

        var marketKey = propKey + ":" + market;
        Context.Require(market == "A" || market == "R", "FUTARCHY: invalid market");

        UpdateTwap(marketKey);

        var yesReserve = _marketYesReserves.Get(marketKey);
        var noReserve = _marketNoReserves.Get(marketKey);
        var k = _marketK.Get(marketKey);

        var newYesReserve = yesReserve + Context.TxValue;
        var newNoReserve = k / newYesReserve;
        var noOut = noReserve - newNoReserve;

        Context.Require(!noOut.IsZero, "FUTARCHY: zero output");

        _marketYesReserves.Set(marketKey, newYesReserve);
        _marketNoReserves.Set(marketKey, newNoReserve);

        var traderHex = Convert.ToHexString(Context.Caller);
        var posKey = marketKey + ":" + traderHex;
        _noPositions.Set(posKey, _noPositions.Get(posKey) + noOut);

        Context.Emit(new TradeExecutedEvent
        {
            ProposalId = proposalId, Market = market,
            Trader = Context.Caller, Direction = "buyNo",
            AmountIn = Context.TxValue, AmountOut = noOut
        });
    }

    // ===================== Decision & Resolution =====================

    /// <summary>
    /// End the trading period and determine the decision based on TWAP comparison.
    /// If TWAP("metric if adopted") > TWAP("metric if rejected"), proposal is adopted.
    /// </summary>
    [BasaltEntrypoint]
    public void DecideProposal(ulong proposalId)
    {
        var key = proposalId.ToString();
        Context.Require(_proposalStatus.Get(key) == "trading", "FUTARCHY: not in trading");
        Context.Require(Context.BlockHeight > _proposalTradingEndBlocks.Get(key),
            "FUTARCHY: trading not ended");

        // Calculate TWAP for both markets
        var twapA = GetMarketTwap(key + ":A");
        var twapR = GetMarketTwap(key + ":R");

        // Decision: adopt if market A shows higher expected metric
        var adopted = twapA >= twapR;
        _proposalDecision.Set(key, adopted);
        _proposalStatus.Set(key, "decided");

        Context.Emit(new ProposalDecidedEvent
        {
            ProposalId = proposalId, Adopted = adopted,
            TwapAdopted = twapA, TwapRejected = twapR
        });
    }

    /// <summary>
    /// Resolve the winning market after the metric is observed via oracle.
    /// </summary>
    [BasaltEntrypoint]
    public void ResolveProposal(ulong proposalId)
    {
        var key = proposalId.ToString();
        Context.Require(_proposalStatus.Get(key) == "decided", "FUTARCHY: not decided");
        Context.Require(Context.BlockHeight >= _proposalResolutionBlocks.Get(key),
            "FUTARCHY: resolution not ready");

        // Fetch metric from oracle
        var feedId = _proposalOracleFeedIds.Get(key);
        var metricValue = Context.CallContract<UInt256>(_oracleAddress, "GetLatestValue", feedId);

        _resolvedMetricValue.Set(key, metricValue);
        _proposalStatus.Set(key, "resolved");

        // Return proposal bond
        var proposer = Convert.FromHexString(_proposalProposers.Get(key));
        var bond = _proposalBonds.Get(key);
        Context.TransferNative(proposer, bond);

        Context.Emit(new ProposalResolvedEvent
        {
            ProposalId = proposalId, MetricValue = metricValue
        });
    }

    /// <summary>
    /// Settle a trader's position in the resolved market.
    /// Winning market: positions pay out based on actual metric vs position direction.
    /// Losing market: positions are refunded at entry cost.
    /// </summary>
    [BasaltEntrypoint]
    public void Settle(ulong proposalId, string market)
    {
        var key = proposalId.ToString();
        Context.Require(_proposalStatus.Get(key) == "resolved", "FUTARCHY: not resolved");

        var marketKey = key + ":" + market;
        var traderHex = Convert.ToHexString(Context.Caller);
        var posKey = marketKey + ":" + traderHex;

        Context.Require(!_traderSettled.Get(posKey), "FUTARCHY: already settled");
        _traderSettled.Set(posKey, true);

        var yesTokens = _yesPositions.Get(posKey);
        var noTokens = _noPositions.Get(posKey);

        // Payout calculation depends on whether this is the winning or losing market
        var adopted = _proposalDecision.Get(key);
        var isWinningMarket = (market == "A" && adopted) || (market == "R" && !adopted);

        var payout = UInt256.Zero;
        if (isWinningMarket)
        {
            // Winning market: YES tokens pay out 1:1 if metric exceeded expectations
            // Simplified: YES tokens pay face value, NO tokens pay zero
            payout = yesTokens;
        }
        else
        {
            // Losing market: refund at proportional rate
            payout = (yesTokens + noTokens) / new UInt256(2); // simplified refund
        }

        if (!payout.IsZero)
            Context.TransferNative(Context.Caller, payout);

        Context.Emit(new PositionSettledEvent
        {
            ProposalId = proposalId, Market = market,
            Trader = Context.Caller, Payout = payout
        });
    }

    // ===================== Dispute Resolution =====================

    /// <summary>
    /// Dispute the decision via traditional governance vote.
    /// </summary>
    [BasaltEntrypoint]
    public void DisputeDecision(ulong proposalId, ulong governanceProposalId)
    {
        var key = proposalId.ToString();
        Context.Require(_proposalStatus.Get(key) == "decided", "FUTARCHY: not decided");

        var govStatus = Context.CallContract<string>(_governanceAddress, "GetStatus", governanceProposalId);
        Context.Require(govStatus == "executed", "FUTARCHY: governance proposal not executed");

        _proposalStatus.Set(key, "disputed");

        Context.Emit(new DecisionDisputedEvent
        {
            ProposalId = proposalId, GovernanceProposalId = governanceProposalId
        });
    }

    // ===================== Views =====================

    [BasaltView]
    public string GetProposalStatus(ulong proposalId) => _proposalStatus.Get(proposalId.ToString()) ?? "unknown";

    [BasaltView]
    public bool GetDecision(ulong proposalId) => _proposalDecision.Get(proposalId.ToString());

    [BasaltView]
    public UInt256 GetYesReserve(ulong proposalId, string market)
        => _marketYesReserves.Get(proposalId.ToString() + ":" + market);

    [BasaltView]
    public UInt256 GetNoReserve(ulong proposalId, string market)
        => _marketNoReserves.Get(proposalId.ToString() + ":" + market);

    [BasaltView]
    public UInt256 GetYesPosition(ulong proposalId, string market, byte[] trader)
        => _yesPositions.Get(proposalId.ToString() + ":" + market + ":" + Convert.ToHexString(trader));

    [BasaltView]
    public UInt256 GetNoPosition(ulong proposalId, string market, byte[] trader)
        => _noPositions.Get(proposalId.ToString() + ":" + market + ":" + Convert.ToHexString(trader));

    [BasaltView]
    public UInt256 GetResolvedMetric(ulong proposalId) => _resolvedMetricValue.Get(proposalId.ToString());

    [BasaltView]
    public ulong GetTradingEndBlock(ulong proposalId)
        => _proposalTradingEndBlocks.Get(proposalId.ToString());

    [BasaltView]
    public UInt256 GetMarketPrice(ulong proposalId, string market)
    {
        var marketKey = proposalId.ToString() + ":" + market;
        var yesReserve = _marketYesReserves.Get(marketKey);
        var noReserve = _marketNoReserves.Get(marketKey);
        if (yesReserve.IsZero) return UInt256.Zero;
        // Price of YES = noReserve / (yesReserve + noReserve) * PricePrecision
        return noReserve * PricePrecision / (yesReserve + noReserve);
    }

    // ===================== Internal =====================

    private void InitializeMarket(string marketKey, UInt256 initialLiquidity)
    {
        // Start with equal reserves: price = 0.5
        _marketYesReserves.Set(marketKey, initialLiquidity);
        _marketNoReserves.Set(marketKey, initialLiquidity);
        _marketK.Set(marketKey, initialLiquidity * initialLiquidity);
        _twapStartBlock.Set(marketKey, Context.BlockHeight);
        _twapLastUpdateBlock.Set(marketKey, Context.BlockHeight);
        _twapCumulativePrice.Set(marketKey, UInt256.Zero);
    }

    private void UpdateTwap(string marketKey)
    {
        var lastBlock = _twapLastUpdateBlock.Get(marketKey);
        if (Context.BlockHeight <= lastBlock) return;

        var elapsed = Context.BlockHeight - lastBlock;
        var yesReserve = _marketYesReserves.Get(marketKey);
        var noReserve = _marketNoReserves.Get(marketKey);

        // Current YES price = noReserve / (yesReserve + noReserve)
        var total = yesReserve + noReserve;
        var currentPrice = !total.IsZero ? noReserve * PricePrecision / total : UInt256.Zero;

        var cumulative = _twapCumulativePrice.Get(marketKey);
        _twapCumulativePrice.Set(marketKey, cumulative + currentPrice * new UInt256(elapsed));
        _twapLastUpdateBlock.Set(marketKey, Context.BlockHeight);
    }

    private UInt256 GetMarketTwap(string marketKey)
    {
        UpdateTwap(marketKey);
        var cumulative = _twapCumulativePrice.Get(marketKey);
        var startBlock = _twapStartBlock.Get(marketKey);
        var totalBlocks = Context.BlockHeight - startBlock;
        if (totalBlocks == 0) return UInt256.Zero;
        return cumulative / new UInt256(totalBlocks);
    }
}

// ===================== Events =====================

[BasaltEvent]
public class FutarchyProposalCreatedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Proposer { get; set; } = null!;
    public string Description { get; set; } = "";
    public string MetricName { get; set; } = "";
    public ulong TradingEndBlock { get; set; }
}

[BasaltEvent]
public class TradeExecutedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public string Market { get; set; } = "";
    [Indexed] public byte[] Trader { get; set; } = null!;
    public string Direction { get; set; } = "";
    public UInt256 AmountIn { get; set; }
    public UInt256 AmountOut { get; set; }
}

[BasaltEvent]
public class ProposalDecidedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public bool Adopted { get; set; }
    public UInt256 TwapAdopted { get; set; }
    public UInt256 TwapRejected { get; set; }
}

[BasaltEvent]
public class ProposalResolvedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public UInt256 MetricValue { get; set; }
}

[BasaltEvent]
public class PositionSettledEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public string Market { get; set; } = "";
    [Indexed] public byte[] Trader { get; set; } = null!;
    public UInt256 Payout { get; set; }
}

[BasaltEvent]
public class DecisionDisputedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public ulong GovernanceProposalId { get; set; }
}
```

## Complexity

**High** -- Futarchy is the most complex governance mechanism in this collection. It combines prediction market mechanics (CPMM, TWAP calculations, position management) with governance lifecycle management (proposal creation, decision, resolution, settlement) and oracle integration. The TWAP calculation must be gas-efficient and manipulation-resistant. The settlement logic for conditional markets (winning vs. losing market, YES vs. NO payout) involves nuanced game-theoretic reasoning. The dispute resolution mechanism must handle edge cases where markets are thin, manipulated, or where the oracle fails. Fixed-point arithmetic for price calculations requires careful precision management. This contract would benefit from extensive formal verification.

## Priority

**P3** -- Futarchy is an innovative and theoretically appealing governance mechanism, but it is experimental and has limited real-world deployment history. It has significant prerequisites (Oracle Network must be deployed and reliable, sufficient market liquidity, educated participant base). It should be deployed as an experimental governance module after the core governance system (quadratic voting) and oracle infrastructure are battle-tested. Suitable for Basalt's second year of mainnet operations or as a testnet experiment.
