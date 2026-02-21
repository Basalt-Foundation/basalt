# Decentralized Oracle Network

## Category

Infrastructure / DeFi

## Summary

A decentralized oracle contract that aggregates price feeds and other off-chain data from a set of staked reporters using a median-of-N aggregation strategy. Reporters who submit values that deviate beyond a configurable threshold from the median are slashed, creating strong economic incentives for accurate reporting. The oracle supports multiple feed types (price, weather, sports scores), heartbeat and deviation-triggered updates, and a pay-per-query model for consumer contracts. This is critical infrastructure for DeFi, prediction markets, and any application that depends on real-world data.

## Why It's Useful

- **DeFi foundation**: Lending protocols, DEXes, stablecoins, and derivatives all require reliable price feeds. Without a decentralized oracle, DeFi on Basalt is impossible.
- **Manipulation resistance**: Median-of-N aggregation with economic slashing makes it extremely expensive to manipulate reported values, unlike centralized oracles with single points of failure.
- **Multi-domain data**: Beyond price feeds, the oracle supports weather data (parametric insurance), sports outcomes (prediction markets), and arbitrary key-value data feeds.
- **Revenue model for reporters**: Staked reporters earn fees from consumer contract queries, creating sustainable economics for data providers.
- **Heartbeat guarantees**: Consumer contracts can rely on data being updated at minimum intervals, even when prices are stable, ensuring freshness guarantees.
- **Composability**: Any Basalt contract can query the oracle via cross-contract calls, enabling a rich ecosystem of data-dependent applications.

## Key Features

- Feed registration: create named feeds with configurable parameters (heartbeat interval, deviation threshold, minimum reporter count, query fee)
- Reporter staking: reporters must stake a minimum amount to participate. Stake serves as collateral for slashing on inaccurate reports.
- Median-of-N aggregation: when a feed update round closes, the median value is selected as the canonical answer. This is robust against up to (N-1)/2 Byzantine reporters.
- Deviation slashing: reporters whose submitted values deviate more than a configurable percentage from the final median are slashed a percentage of their stake
- Heartbeat trigger: if no update has been submitted for a feed within the heartbeat interval (measured in blocks), any reporter can initiate a new round
- Deviation trigger: if a reporter detects that the off-chain value has moved more than the deviation threshold since the last on-chain value, they can initiate an early round
- Round lifecycle: Open (accepting submissions) -> Closed (median computed, slashing applied) -> Finalized
- Consumer query: contracts pay a per-query fee to read the latest value for a feed. Fees are distributed to reporters.
- Feed pausability: feed owners can pause a feed in case of data source compromise
- Historical data: the last N round results are stored on-chain for consumer contracts that need historical price data (e.g., TWAP calculations)

## Basalt-Specific Advantages

- **BLAKE3 commitment scheme**: Reporters can use a commit-reveal scheme where the commit is a BLAKE3 hash of (value || salt). BLAKE3's speed means commits are cheap to compute and verify, and the 256-bit output provides strong hiding.
- **Ed25519 reporter identity**: Reporter registration uses Ed25519 public keys for identity, and signed reports can be verified on-chain using the same `Ed25519Signer.Verify` pattern as BridgeETH.
- **Staking integration**: Reporters stake via the StakingPool system contract, so slashing reduces their delegation directly. This reuses existing infrastructure rather than reimplementing staking.
- **ZK compliance for reporters**: The SchemaRegistry/IssuerRegistry can be used to verify that reporters meet compliance requirements (e.g., registered data providers), adding a trust layer without centralization.
- **Confidential submissions via Pedersen commitments**: Reporters can submit Pedersen commitments during the commit phase, preventing front-running of oracle updates. The commitment scheme leverages Basalt's native Pedersen commitment support.
- **AOT-compiled median computation**: The median calculation over N values is a tight loop that benefits from AOT compilation, ensuring consistent gas costs regardless of the number of reporters.
- **Cross-contract query model**: Basalt's `Context.CallContract<T>()` makes oracle queries a simple function call from consumer contracts, with automatic reentrancy protection.

## Token Standards Used

- **BST-20**: Query fees can be paid in BST-20 tokens (not just native BST), enabling feed-specific payment tokens
- **BST-3525**: Reporter credentials could be represented as BST-3525 SFTs with different quality tiers (slot = feed category, value = reputation score)

## Integration Points

- **StakingPool (0x...1005)**: Reporter staking and slashing are managed via the StakingPool. Slashed funds are redistributed to honest reporters or burned.
- **Governance (0x...1005 area)**: Feed parameters (heartbeat, deviation threshold, minimum reporters) can be updated via governance proposals.
- **SchemaRegistry (0x...1006)**: Reporter compliance verification -- ensures registered reporters meet data provider requirements.
- **IssuerRegistry (0x...1007)**: Verifiable credentials for reporter certification.
- **Escrow (0x...1003)**: Dispute resolution for challenged oracle reports can use Escrow for conditional fund release.

## Technical Sketch

```csharp
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Decentralized Oracle Network -- median-of-N aggregation with staked reporters
/// and economic slashing for manipulation resistance.
/// </summary>
[BasaltContract]
public partial class OracleNetwork
{
    private const ulong MinReporterStake = 10_000;

    // --- Feed configuration ---
    private readonly StorageValue<ulong> _nextFeedId;
    private readonly StorageMap<string, string> _feedNames;              // feedId -> name
    private readonly StorageMap<string, string> _feedOwners;             // feedId -> owner hex
    private readonly StorageMap<string, ulong> _feedHeartbeatBlocks;     // feedId -> max blocks between updates
    private readonly StorageMap<string, uint> _feedDeviationThresholdBps; // feedId -> basis points (e.g., 100 = 1%)
    private readonly StorageMap<string, uint> _feedMinReporters;         // feedId -> minimum reporters per round
    private readonly StorageMap<string, UInt256> _feedQueryFee;          // feedId -> fee per query
    private readonly StorageMap<string, bool> _feedPaused;               // feedId -> paused

    // --- Reporter management ---
    private readonly StorageMap<string, UInt256> _reporterStakes;       // reporterHex -> staked amount
    private readonly StorageMap<string, bool> _reporterActive;          // reporterHex -> active
    private readonly StorageValue<uint> _reporterCount;

    // --- Round management ---
    private readonly StorageMap<string, ulong> _feedCurrentRound;       // feedId -> current round number
    private readonly StorageMap<string, ulong> _roundOpenBlock;          // "feedId:round" -> block opened
    private readonly StorageMap<string, string> _roundStatus;            // "feedId:round" -> "open"|"closed"|"finalized"
    private readonly StorageMap<string, uint> _roundSubmissionCount;     // "feedId:round" -> number of submissions
    private readonly StorageMap<string, string> _roundSubmissions;       // "feedId:round:reporterHex" -> committed value (hex)
    private readonly StorageMap<string, bool> _roundHasSubmitted;        // "feedId:round:reporterHex" -> true

    // --- Results ---
    private readonly StorageMap<string, UInt256> _roundMedianValue;     // "feedId:round" -> median
    private readonly StorageMap<string, ulong> _roundTimestamp;          // "feedId:round" -> block timestamp
    private readonly StorageMap<string, UInt256> _feedLatestValue;       // feedId -> latest value
    private readonly StorageMap<string, ulong> _feedLastUpdateBlock;     // feedId -> last update block

    // --- Fee pool ---
    private readonly StorageMap<string, UInt256> _feedAccumulatedFees;   // feedId -> accumulated query fees
    private readonly StorageMap<string, UInt256> _reporterEarnedFees;    // reporterHex -> earned fees

    // --- Slashing ---
    private readonly StorageValue<uint> _slashPercentageBps;            // basis points to slash for deviation

    private readonly byte[] _stakingPoolAddress;

    public OracleNetwork(uint slashPercentageBps = 500)
    {
        _nextFeedId = new StorageValue<ulong>("orc_nfeed");
        _feedNames = new StorageMap<string, string>("orc_fname");
        _feedOwners = new StorageMap<string, string>("orc_fown");
        _feedHeartbeatBlocks = new StorageMap<string, ulong>("orc_fhb");
        _feedDeviationThresholdBps = new StorageMap<string, uint>("orc_fdev");
        _feedMinReporters = new StorageMap<string, uint>("orc_fmin");
        _feedQueryFee = new StorageMap<string, UInt256>("orc_ffee");
        _feedPaused = new StorageMap<string, bool>("orc_fpause");
        _reporterStakes = new StorageMap<string, UInt256>("orc_rstake");
        _reporterActive = new StorageMap<string, bool>("orc_ract");
        _reporterCount = new StorageValue<uint>("orc_rcnt");
        _feedCurrentRound = new StorageMap<string, ulong>("orc_fcr");
        _roundOpenBlock = new StorageMap<string, ulong>("orc_ropn");
        _roundStatus = new StorageMap<string, string>("orc_rsts");
        _roundSubmissionCount = new StorageMap<string, uint>("orc_rsub");
        _roundSubmissions = new StorageMap<string, string>("orc_rval");
        _roundHasSubmitted = new StorageMap<string, bool>("orc_rhas");
        _roundMedianValue = new StorageMap<string, UInt256>("orc_rmed");
        _roundTimestamp = new StorageMap<string, ulong>("orc_rts");
        _feedLatestValue = new StorageMap<string, UInt256>("orc_flat");
        _feedLastUpdateBlock = new StorageMap<string, ulong>("orc_flub");
        _feedAccumulatedFees = new StorageMap<string, UInt256>("orc_facc");
        _reporterEarnedFees = new StorageMap<string, UInt256>("orc_rfee");
        _slashPercentageBps = new StorageValue<uint>("orc_slash");

        _slashPercentageBps.Set(slashPercentageBps);

        _stakingPoolAddress = new byte[20];
        _stakingPoolAddress[18] = 0x10;
        _stakingPoolAddress[19] = 0x05;
    }

    // ===================== Feed Management =====================

    [BasaltEntrypoint]
    public ulong CreateFeed(string name, ulong heartbeatBlocks, uint deviationThresholdBps,
        uint minReporters, UInt256 queryFee)
    {
        Context.Require(!string.IsNullOrEmpty(name), "ORACLE: name required");
        Context.Require(heartbeatBlocks > 0, "ORACLE: invalid heartbeat");
        Context.Require(minReporters >= 3, "ORACLE: need at least 3 reporters");

        var id = _nextFeedId.Get();
        _nextFeedId.Set(id + 1);
        var key = id.ToString();

        _feedNames.Set(key, name);
        _feedOwners.Set(key, Convert.ToHexString(Context.Caller));
        _feedHeartbeatBlocks.Set(key, heartbeatBlocks);
        _feedDeviationThresholdBps.Set(key, deviationThresholdBps);
        _feedMinReporters.Set(key, minReporters);
        _feedQueryFee.Set(key, queryFee);

        Context.Emit(new FeedCreatedEvent
        {
            FeedId = id, Name = name, HeartbeatBlocks = heartbeatBlocks,
            MinReporters = minReporters
        });
        return id;
    }

    [BasaltEntrypoint]
    public void PauseFeed(ulong feedId)
    {
        RequireFeedOwner(feedId);
        _feedPaused.Set(feedId.ToString(), true);
    }

    [BasaltEntrypoint]
    public void UnpauseFeed(ulong feedId)
    {
        RequireFeedOwner(feedId);
        _feedPaused.Set(feedId.ToString(), false);
    }

    // ===================== Reporter Management =====================

    [BasaltEntrypoint]
    public void RegisterReporter()
    {
        Context.Require(!Context.TxValue.IsZero, "ORACLE: must stake");
        Context.Require(Context.TxValue >= new UInt256(MinReporterStake), "ORACLE: below min stake");

        var hex = Convert.ToHexString(Context.Caller);
        Context.Require(!_reporterActive.Get(hex), "ORACLE: already registered");

        _reporterStakes.Set(hex, Context.TxValue);
        _reporterActive.Set(hex, true);
        _reporterCount.Set(_reporterCount.Get() + 1);

        Context.Emit(new ReporterRegisteredEvent
        {
            Reporter = Context.Caller, Stake = Context.TxValue
        });
    }

    [BasaltEntrypoint]
    public void UnregisterReporter()
    {
        var hex = Convert.ToHexString(Context.Caller);
        Context.Require(_reporterActive.Get(hex), "ORACLE: not registered");

        var stake = _reporterStakes.Get(hex);
        _reporterActive.Set(hex, false);
        _reporterStakes.Set(hex, UInt256.Zero);
        _reporterCount.Set(_reporterCount.Get() - 1);

        Context.TransferNative(Context.Caller, stake);

        Context.Emit(new ReporterUnregisteredEvent { Reporter = Context.Caller });
    }

    // ===================== Round Management =====================

    [BasaltEntrypoint]
    public void OpenRound(ulong feedId)
    {
        RequireActiveReporter();
        var feedKey = feedId.ToString();
        Context.Require(!_feedPaused.Get(feedKey), "ORACLE: feed paused");

        var lastUpdate = _feedLastUpdateBlock.Get(feedKey);
        var heartbeat = _feedHeartbeatBlocks.Get(feedKey);

        // Allow new round if heartbeat expired or this is the first round
        Context.Require(
            lastUpdate == 0 || Context.BlockHeight >= lastUpdate + heartbeat,
            "ORACLE: heartbeat not expired");

        var round = _feedCurrentRound.Get(feedKey) + 1;
        _feedCurrentRound.Set(feedKey, round);

        var roundKey = feedKey + ":" + round.ToString();
        _roundStatus.Set(roundKey, "open");
        _roundOpenBlock.Set(roundKey, Context.BlockHeight);

        Context.Emit(new RoundOpenedEvent { FeedId = feedId, Round = round });
    }

    [BasaltEntrypoint]
    public void SubmitValue(ulong feedId, UInt256 value)
    {
        RequireActiveReporter();
        var feedKey = feedId.ToString();
        var round = _feedCurrentRound.Get(feedKey);
        var roundKey = feedKey + ":" + round.ToString();

        Context.Require(_roundStatus.Get(roundKey) == "open", "ORACLE: round not open");

        var reporterHex = Convert.ToHexString(Context.Caller);
        var submissionKey = roundKey + ":" + reporterHex;
        Context.Require(!_roundHasSubmitted.Get(submissionKey), "ORACLE: already submitted");

        _roundHasSubmitted.Set(submissionKey, true);
        _roundSubmissions.Set(submissionKey, value.ToString());
        var count = _roundSubmissionCount.Get(roundKey) + 1;
        _roundSubmissionCount.Set(roundKey, count);

        Context.Emit(new ValueSubmittedEvent
        {
            FeedId = feedId, Round = round, Reporter = Context.Caller
        });
    }

    [BasaltEntrypoint]
    public void CloseRound(ulong feedId)
    {
        var feedKey = feedId.ToString();
        var round = _feedCurrentRound.Get(feedKey);
        var roundKey = feedKey + ":" + round.ToString();

        Context.Require(_roundStatus.Get(roundKey) == "open", "ORACLE: round not open");

        var count = _roundSubmissionCount.Get(roundKey);
        var minReporters = _feedMinReporters.Get(feedKey);
        Context.Require(count >= minReporters, "ORACLE: insufficient submissions");

        // Compute median (simplified -- in production, sort N submitted values)
        // The median is the (count/2)th value after sorting
        _roundStatus.Set(roundKey, "closed");
        _roundTimestamp.Set(roundKey, (ulong)Context.BlockTimestamp);

        Context.Emit(new RoundClosedEvent { FeedId = feedId, Round = round, SubmissionCount = count });
    }

    [BasaltEntrypoint]
    public void FinalizeRound(ulong feedId, UInt256 medianValue)
    {
        // In production this would be computed on-chain from submissions
        var feedKey = feedId.ToString();
        var round = _feedCurrentRound.Get(feedKey);
        var roundKey = feedKey + ":" + round.ToString();

        Context.Require(_roundStatus.Get(roundKey) == "closed", "ORACLE: round not closed");

        _roundMedianValue.Set(roundKey, medianValue);
        _feedLatestValue.Set(feedKey, medianValue);
        _feedLastUpdateBlock.Set(feedKey, Context.BlockHeight);
        _roundStatus.Set(roundKey, "finalized");

        Context.Emit(new RoundFinalizedEvent
        {
            FeedId = feedId, Round = round, MedianValue = medianValue
        });
    }

    // ===================== Consumer Queries =====================

    [BasaltView]
    public UInt256 GetLatestValue(ulong feedId) => _feedLatestValue.Get(feedId.ToString());

    [BasaltView]
    public ulong GetLastUpdateBlock(ulong feedId) => _feedLastUpdateBlock.Get(feedId.ToString());

    [BasaltView]
    public ulong GetCurrentRound(ulong feedId) => _feedCurrentRound.Get(feedId.ToString());

    [BasaltView]
    public UInt256 GetRoundMedian(ulong feedId, ulong round)
        => _roundMedianValue.Get(feedId.ToString() + ":" + round.ToString());

    [BasaltView]
    public string GetRoundStatus(ulong feedId, ulong round)
        => _roundStatus.Get(feedId.ToString() + ":" + round.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetReporterStake(byte[] reporter) => _reporterStakes.Get(Convert.ToHexString(reporter));

    [BasaltView]
    public bool IsReporterActive(byte[] reporter) => _reporterActive.Get(Convert.ToHexString(reporter));

    [BasaltView]
    public string GetFeedName(ulong feedId) => _feedNames.Get(feedId.ToString()) ?? "";

    [BasaltView]
    public UInt256 GetQueryFee(ulong feedId) => _feedQueryFee.Get(feedId.ToString());

    // ===================== Fee Management =====================

    [BasaltEntrypoint]
    public void PayQueryFee(ulong feedId)
    {
        var fee = _feedQueryFee.Get(feedId.ToString());
        Context.Require(Context.TxValue >= fee, "ORACLE: insufficient query fee");
        _feedAccumulatedFees.Set(feedId.ToString(),
            _feedAccumulatedFees.Get(feedId.ToString()) + Context.TxValue);
    }

    [BasaltEntrypoint]
    public void ClaimReporterFees()
    {
        var hex = Convert.ToHexString(Context.Caller);
        var earned = _reporterEarnedFees.Get(hex);
        Context.Require(!earned.IsZero, "ORACLE: no fees to claim");

        _reporterEarnedFees.Set(hex, UInt256.Zero);
        Context.TransferNative(Context.Caller, earned);
    }

    // ===================== Internal =====================

    private void RequireFeedOwner(ulong feedId)
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _feedOwners.Get(feedId.ToString()),
            "ORACLE: not feed owner");
    }

    private void RequireActiveReporter()
    {
        Context.Require(
            _reporterActive.Get(Convert.ToHexString(Context.Caller)),
            "ORACLE: not active reporter");
    }
}

// ===================== Events =====================

[BasaltEvent]
public class FeedCreatedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    public string Name { get; set; } = "";
    public ulong HeartbeatBlocks { get; set; }
    public uint MinReporters { get; set; }
}

[BasaltEvent]
public class ReporterRegisteredEvent
{
    [Indexed] public byte[] Reporter { get; set; } = null!;
    public UInt256 Stake { get; set; }
}

[BasaltEvent]
public class ReporterUnregisteredEvent
{
    [Indexed] public byte[] Reporter { get; set; } = null!;
}

[BasaltEvent]
public class RoundOpenedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    [Indexed] public ulong Round { get; set; }
}

[BasaltEvent]
public class ValueSubmittedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    [Indexed] public ulong Round { get; set; }
    [Indexed] public byte[] Reporter { get; set; } = null!;
}

[BasaltEvent]
public class RoundClosedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    [Indexed] public ulong Round { get; set; }
    public uint SubmissionCount { get; set; }
}

[BasaltEvent]
public class RoundFinalizedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    [Indexed] public ulong Round { get; set; }
    public UInt256 MedianValue { get; set; }
}
```

## Complexity

**High** -- The oracle contract involves multi-phase round management (open/submit/close/finalize), on-chain median computation over N values, economic slashing logic with deviation thresholds, fee distribution across reporters, and commit-reveal schemes for front-running prevention. Correct implementation of median aggregation in a gas-efficient manner is non-trivial. The slashing logic must correctly handle edge cases (ties, exactly-at-threshold deviations, rounds with insufficient submissions).

## Priority

**P0** -- A decentralized oracle is arguably the single most important piece of infrastructure after basic token standards and governance. Without reliable price feeds, no DeFi application can function: lending protocols cannot calculate collateral ratios, DEXes cannot determine fair prices, and stablecoins cannot maintain their peg. The oracle should be deployed as early as possible to enable the DeFi ecosystem.
