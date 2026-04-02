using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Decentralized Oracle Network — median-of-N aggregation with staked reporters
/// and economic slashing for manipulation resistance.
///
/// Lifecycle: CreateFeed → RegisterReporter → OpenRound → SubmitValue (×N) → FinalizeRound
///
/// Type ID: 0x0108
/// </summary>
[BasaltContract]
public partial class OracleNetwork
{
    private const ulong MinReporterStake = 10_000;
    private const uint MinRoundWindowBlocks = 5;
    private const uint MaxSubmissionsPerRound = 256;

    // --- Feed configuration ---
    private readonly StorageValue<ulong> _nextFeedId;
    private readonly StorageMap<string, string> _feedNames;               // feedId → name
    private readonly StorageMap<string, string> _feedOwners;              // feedId → owner hex
    private readonly StorageMap<string, ulong> _feedHeartbeatBlocks;      // feedId → max blocks between updates
    private readonly StorageMap<string, uint> _feedDeviationThresholdBps; // feedId → basis points (100 = 1%)
    private readonly StorageMap<string, uint> _feedMinReporters;          // feedId → minimum reporters per round
    private readonly StorageMap<string, UInt256> _feedQueryFee;           // feedId → fee per query
    private readonly StorageMap<string, bool> _feedPaused;                // feedId → paused
    private readonly StorageMap<string, bool> _feedExists;                // feedId → exists

    // --- Reporter management ---
    private readonly StorageMap<string, UInt256> _reporterStakes;         // reporter hex → staked amount
    private readonly StorageMap<string, bool> _reporterActive;            // reporter hex → active
    private readonly StorageValue<uint> _reporterCount;

    // --- Round management ---
    private readonly StorageMap<string, ulong> _feedCurrentRound;         // feedId → current round number
    private readonly StorageMap<string, ulong> _roundOpenBlock;           // "feedId:round" → block opened
    private readonly StorageMap<string, string> _roundStatus;             // "feedId:round" → "open"|"finalized"
    private readonly StorageMap<string, uint> _roundSubmissionCount;      // "feedId:round" → number of submissions
    private readonly StorageMap<string, string> _roundSubmissionValues;   // "feedId:round:idx" → UInt256 string
    private readonly StorageMap<string, string> _roundSubmissionReporters;// "feedId:round:idx" → reporter hex
    private readonly StorageMap<string, bool> _roundHasSubmitted;         // "feedId:round:reporter" → true

    // --- Results ---
    private readonly StorageMap<string, UInt256> _roundMedianValue;       // "feedId:round" → median
    private readonly StorageMap<string, ulong> _roundTimestamp;           // "feedId:round" → block timestamp
    private readonly StorageMap<string, uint> _roundHonestCount;          // "feedId:round" → honest reporters
    private readonly StorageMap<string, UInt256> _feedLatestValue;        // feedId → latest value
    private readonly StorageMap<string, ulong> _feedLastUpdateBlock;      // feedId → last update block
    private readonly StorageMap<string, ulong> _feedLastUpdateRound;      // feedId → last finalized round

    // --- Fee pool ---
    private readonly StorageMap<string, UInt256> _feedAccumulatedFees;    // feedId → accumulated query fees
    private readonly StorageMap<string, UInt256> _reporterEarnedFees;     // reporter hex → earned fees

    // --- Slashing ---
    private readonly StorageValue<uint> _slashPercentageBps;              // basis points to slash

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
        _feedExists = new StorageMap<string, bool>("orc_fexist");
        _reporterStakes = new StorageMap<string, UInt256>("orc_rstake");
        _reporterActive = new StorageMap<string, bool>("orc_ract");
        _reporterCount = new StorageValue<uint>("orc_rcnt");
        _feedCurrentRound = new StorageMap<string, ulong>("orc_fcr");
        _roundOpenBlock = new StorageMap<string, ulong>("orc_ropn");
        _roundStatus = new StorageMap<string, string>("orc_rsts");
        _roundSubmissionCount = new StorageMap<string, uint>("orc_rsub");
        _roundSubmissionValues = new StorageMap<string, string>("orc_rval");
        _roundSubmissionReporters = new StorageMap<string, string>("orc_rrep");
        _roundHasSubmitted = new StorageMap<string, bool>("orc_rhas");
        _roundMedianValue = new StorageMap<string, UInt256>("orc_rmed");
        _roundTimestamp = new StorageMap<string, ulong>("orc_rts");
        _roundHonestCount = new StorageMap<string, uint>("orc_rhon");
        _feedLatestValue = new StorageMap<string, UInt256>("orc_flat");
        _feedLastUpdateBlock = new StorageMap<string, ulong>("orc_flub");
        _feedLastUpdateRound = new StorageMap<string, ulong>("orc_flur");
        _feedAccumulatedFees = new StorageMap<string, UInt256>("orc_facc");
        _reporterEarnedFees = new StorageMap<string, UInt256>("orc_rfee");
        _slashPercentageBps = new StorageValue<uint>("orc_slash");

        if (Context.IsDeploying)
            _slashPercentageBps.Set(slashPercentageBps);
    }

    // ===================== Feed Management =====================

    [BasaltEntrypoint]
    public ulong CreateFeed(string name, ulong heartbeatBlocks, uint deviationThresholdBps,
        uint minReporters, UInt256 queryFee)
    {
        Context.Require(!string.IsNullOrEmpty(name), "ORACLE: name required");
        Context.Require(heartbeatBlocks > 0, "ORACLE: invalid heartbeat");
        Context.Require(minReporters >= 3, "ORACLE: need at least 3 reporters");
        Context.Require(deviationThresholdBps > 0 && deviationThresholdBps <= 10_000,
            "ORACLE: invalid deviation threshold");

        var id = _nextFeedId.Get();
        _nextFeedId.Set(id + 1);
        var key = id.ToString();

        _feedExists.Set(key, true);
        _feedNames.Set(key, name);
        _feedOwners.Set(key, Convert.ToHexString(Context.Caller));
        _feedHeartbeatBlocks.Set(key, heartbeatBlocks);
        _feedDeviationThresholdBps.Set(key, deviationThresholdBps);
        _feedMinReporters.Set(key, minReporters);
        _feedQueryFee.Set(key, queryFee);

        Context.Emit(new FeedCreatedEvent
        {
            FeedId = id,
            Name = name,
            Owner = Context.Caller,
            HeartbeatBlocks = heartbeatBlocks,
            DeviationThresholdBps = deviationThresholdBps,
            MinReporters = minReporters
        });
        return id;
    }

    [BasaltEntrypoint]
    public void UpdateFeedParameters(ulong feedId, ulong heartbeatBlocks,
        uint deviationThresholdBps, UInt256 queryFee)
    {
        RequireFeedOwner(feedId);
        Context.Require(heartbeatBlocks > 0, "ORACLE: invalid heartbeat");
        Context.Require(deviationThresholdBps > 0 && deviationThresholdBps <= 10_000,
            "ORACLE: invalid deviation threshold");

        var key = feedId.ToString();
        _feedHeartbeatBlocks.Set(key, heartbeatBlocks);
        _feedDeviationThresholdBps.Set(key, deviationThresholdBps);
        _feedQueryFee.Set(key, queryFee);

        Context.Emit(new FeedUpdatedEvent
        {
            FeedId = feedId,
            HeartbeatBlocks = heartbeatBlocks,
            DeviationThresholdBps = deviationThresholdBps
        });
    }

    [BasaltEntrypoint]
    public void PauseFeed(ulong feedId)
    {
        RequireFeedOwner(feedId);
        _feedPaused.Set(feedId.ToString(), true);
        Context.Emit(new FeedPausedEvent { FeedId = feedId });
    }

    [BasaltEntrypoint]
    public void UnpauseFeed(ulong feedId)
    {
        RequireFeedOwner(feedId);
        _feedPaused.Set(feedId.ToString(), false);
        Context.Emit(new FeedUnpausedEvent { FeedId = feedId });
    }

    // ===================== Reporter Management =====================

    [BasaltEntrypoint]
    public void RegisterReporter()
    {
        Context.Require(!Context.TxValue.IsZero, "ORACLE: must stake");
        Context.Require(Context.TxValue >= new UInt256(MinReporterStake),
            "ORACLE: below min stake");

        var hex = Convert.ToHexString(Context.Caller);
        Context.Require(!_reporterActive.Get(hex), "ORACLE: already registered");

        _reporterStakes.Set(hex, Context.TxValue);
        _reporterActive.Set(hex, true);
        _reporterCount.Set(_reporterCount.Get() + 1);

        Context.Emit(new ReporterRegisteredEvent
        {
            Reporter = Context.Caller,
            Stake = Context.TxValue
        });
    }

    [BasaltEntrypoint]
    public void IncreaseStake()
    {
        Context.Require(!Context.TxValue.IsZero, "ORACLE: must send value");
        var hex = Convert.ToHexString(Context.Caller);
        Context.Require(_reporterActive.Get(hex), "ORACLE: not registered");

        var current = _reporterStakes.Get(hex);
        _reporterStakes.Set(hex, UInt256.CheckedAdd(current, Context.TxValue));
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

        if (!stake.IsZero)
            Context.TransferNative(Context.Caller, stake);

        Context.Emit(new ReporterUnregisteredEvent { Reporter = Context.Caller });
    }

    // ===================== Round Management =====================

    [BasaltEntrypoint]
    public ulong OpenRound(ulong feedId)
    {
        RequireActiveReporter();
        var feedKey = feedId.ToString();
        RequireFeedExists(feedId);
        Context.Require(!_feedPaused.Get(feedKey), "ORACLE: feed paused");

        var currentRound = _feedCurrentRound.Get(feedKey);
        if (currentRound > 0)
        {
            var prevRoundKey = feedKey + ":" + currentRound;
            var prevStatus = _roundStatus.Get(prevRoundKey);
            Context.Require(prevStatus != "open", "ORACLE: previous round still open");
        }

        var lastUpdate = _feedLastUpdateBlock.Get(feedKey);
        var heartbeat = _feedHeartbeatBlocks.Get(feedKey);

        // Allow new round if heartbeat expired or first round ever
        Context.Require(
            lastUpdate == 0 || Context.BlockHeight >= lastUpdate + heartbeat,
            "ORACLE: heartbeat not expired");

        var round = currentRound + 1;
        _feedCurrentRound.Set(feedKey, round);

        var roundKey = feedKey + ":" + round;
        _roundStatus.Set(roundKey, "open");
        _roundOpenBlock.Set(roundKey, Context.BlockHeight);

        Context.Emit(new RoundOpenedEvent
        {
            FeedId = feedId,
            Round = round,
            OpenedBy = Context.Caller
        });
        return round;
    }

    [BasaltEntrypoint]
    public void SubmitValue(ulong feedId, UInt256 value)
    {
        RequireActiveReporter();
        var feedKey = feedId.ToString();
        RequireFeedExists(feedId);
        var round = _feedCurrentRound.Get(feedKey);
        Context.Require(round > 0, "ORACLE: no active round");
        var roundKey = feedKey + ":" + round;

        Context.Require(_roundStatus.Get(roundKey) == "open", "ORACLE: round not open");

        var reporterHex = Convert.ToHexString(Context.Caller);
        var submissionKey = roundKey + ":" + reporterHex;
        Context.Require(!_roundHasSubmitted.Get(submissionKey), "ORACLE: already submitted");

        var count = _roundSubmissionCount.Get(roundKey);
        Context.Require(count < MaxSubmissionsPerRound, "ORACLE: max submissions reached");

        // Store indexed submission for median computation
        var idxKey = roundKey + ":" + count;
        _roundSubmissionValues.Set(idxKey, value.ToString());
        _roundSubmissionReporters.Set(idxKey, reporterHex);
        _roundHasSubmitted.Set(submissionKey, true);
        _roundSubmissionCount.Set(roundKey, count + 1);

        Context.Emit(new ValueSubmittedEvent
        {
            FeedId = feedId,
            Round = round,
            Reporter = Context.Caller
        });
    }

    [BasaltEntrypoint]
    public UInt256 FinalizeRound(ulong feedId)
    {
        var feedKey = feedId.ToString();
        RequireFeedExists(feedId);
        var round = _feedCurrentRound.Get(feedKey);
        Context.Require(round > 0, "ORACLE: no active round");
        var roundKey = feedKey + ":" + round;

        Context.Require(_roundStatus.Get(roundKey) == "open", "ORACLE: round not open");

        // Enforce minimum window so reporters have time to submit
        var openBlock = _roundOpenBlock.Get(roundKey);
        Context.Require(Context.BlockHeight >= openBlock + MinRoundWindowBlocks,
            "ORACLE: round window not elapsed");

        var count = _roundSubmissionCount.Get(roundKey);
        var minReporters = _feedMinReporters.Get(feedKey);
        Context.Require(count >= minReporters, "ORACLE: insufficient submissions");

        // Collect all submission values
        var values = new UInt256[count];
        var reporters = new string[count];
        for (uint i = 0; i < count; i++)
        {
            var idxKey = roundKey + ":" + i;
            UInt256.TryParse(_roundSubmissionValues.Get(idxKey), out var v);
            values[i] = v;
            reporters[i] = _roundSubmissionReporters.Get(idxKey);
        }

        // Compute median
        var median = ComputeMedian(values);

        // Apply deviation slashing and identify honest reporters
        var deviationBps = _feedDeviationThresholdBps.Get(feedKey);
        var slashBps = _slashPercentageBps.Get();
        var totalSlashed = UInt256.Zero;
        uint honestCount = 0;

        for (uint i = 0; i < count; i++)
        {
            if (IsWithinThreshold(values[i], median, deviationBps))
            {
                honestCount++;
            }
            else
            {
                // Slash this reporter
                var reporterHex = reporters[i];
                var stake = _reporterStakes.Get(reporterHex);
                if (!stake.IsZero && slashBps > 0)
                {
                    var slashAmount = stake * new UInt256(slashBps) / new UInt256(10_000);
                    if (!slashAmount.IsZero)
                    {
                        _reporterStakes.Set(reporterHex, UInt256.CheckedSub(stake, slashAmount));
                        totalSlashed = UInt256.CheckedAdd(totalSlashed, slashAmount);

                        Context.Emit(new ReporterSlashedEvent
                        {
                            FeedId = feedId,
                            Round = round,
                            Reporter = Convert.FromHexString(reporterHex),
                            SlashedAmount = slashAmount,
                            SubmittedValue = values[i],
                            MedianValue = median
                        });
                    }
                }
            }
        }

        // Distribute slashed funds + accumulated query fees to honest reporters
        var accumulatedFees = _feedAccumulatedFees.Get(feedKey);
        var totalReward = UInt256.CheckedAdd(totalSlashed, accumulatedFees);

        if (honestCount > 0 && !totalReward.IsZero)
        {
            var rewardPerReporter = totalReward / new UInt256(honestCount);
            if (!rewardPerReporter.IsZero)
            {
                for (uint i = 0; i < count; i++)
                {
                    if (IsWithinThreshold(values[i], median, deviationBps))
                    {
                        var reporterHex = reporters[i];
                        var earned = _reporterEarnedFees.Get(reporterHex);
                        _reporterEarnedFees.Set(reporterHex,
                            UInt256.CheckedAdd(earned, rewardPerReporter));
                    }
                }
            }
            _feedAccumulatedFees.Set(feedKey, UInt256.Zero);
        }

        // Store results
        _roundMedianValue.Set(roundKey, median);
        _roundTimestamp.Set(roundKey, (ulong)Context.BlockTimestamp);
        _roundHonestCount.Set(roundKey, honestCount);
        _roundStatus.Set(roundKey, "finalized");
        _feedLatestValue.Set(feedKey, median);
        _feedLastUpdateBlock.Set(feedKey, Context.BlockHeight);
        _feedLastUpdateRound.Set(feedKey, round);

        Context.Emit(new RoundFinalizedEvent
        {
            FeedId = feedId,
            Round = round,
            MedianValue = median,
            SubmissionCount = count,
            HonestCount = honestCount,
            TotalSlashed = totalSlashed
        });

        return median;
    }

    // ===================== Consumer Queries =====================

    [BasaltEntrypoint]
    public UInt256 QueryLatestValue(ulong feedId)
    {
        var feedKey = feedId.ToString();
        RequireFeedExists(feedId);
        var fee = _feedQueryFee.Get(feedKey);
        Context.Require(Context.TxValue >= fee, "ORACLE: insufficient query fee");

        if (!Context.TxValue.IsZero)
        {
            _feedAccumulatedFees.Set(feedKey,
                UInt256.CheckedAdd(_feedAccumulatedFees.Get(feedKey), Context.TxValue));
        }

        return _feedLatestValue.Get(feedKey);
    }

    [BasaltView]
    public UInt256 GetLatestValue(ulong feedId) => _feedLatestValue.Get(feedId.ToString());

    [BasaltView]
    public ulong GetLastUpdateBlock(ulong feedId) => _feedLastUpdateBlock.Get(feedId.ToString());

    [BasaltView]
    public ulong GetCurrentRound(ulong feedId) => _feedCurrentRound.Get(feedId.ToString());

    [BasaltView]
    public UInt256 GetRoundMedian(ulong feedId, ulong round)
        => _roundMedianValue.Get(feedId.ToString() + ":" + round);

    [BasaltView]
    public string GetRoundStatus(ulong feedId, ulong round)
        => _roundStatus.Get(feedId.ToString() + ":" + round) ?? "unknown";

    [BasaltView]
    public uint GetRoundSubmissionCount(ulong feedId, ulong round)
        => _roundSubmissionCount.Get(feedId.ToString() + ":" + round);

    [BasaltView]
    public uint GetRoundHonestCount(ulong feedId, ulong round)
        => _roundHonestCount.Get(feedId.ToString() + ":" + round);

    [BasaltView]
    public UInt256 GetReporterStake(byte[] reporter)
        => _reporterStakes.Get(Convert.ToHexString(reporter));

    [BasaltView]
    public bool IsReporterActive(byte[] reporter)
        => _reporterActive.Get(Convert.ToHexString(reporter));

    [BasaltView]
    public string GetFeedName(ulong feedId) => _feedNames.Get(feedId.ToString()) ?? "";

    [BasaltView]
    public UInt256 GetQueryFee(ulong feedId) => _feedQueryFee.Get(feedId.ToString());

    [BasaltView]
    public ulong GetFeedCount() => _nextFeedId.Get();

    [BasaltView]
    public uint GetReporterCount() => _reporterCount.Get();

    [BasaltView]
    public bool IsFeedPaused(ulong feedId) => _feedPaused.Get(feedId.ToString());

    [BasaltView]
    public UInt256 GetReporterEarnedFees(byte[] reporter)
        => _reporterEarnedFees.Get(Convert.ToHexString(reporter));

    [BasaltView]
    public UInt256 GetFeedAccumulatedFees(ulong feedId)
        => _feedAccumulatedFees.Get(feedId.ToString());

    [BasaltView]
    public string GetFeedOwner(ulong feedId) => _feedOwners.Get(feedId.ToString()) ?? "";

    [BasaltView]
    public ulong GetFeedHeartbeatBlocks(ulong feedId)
        => _feedHeartbeatBlocks.Get(feedId.ToString());

    [BasaltView]
    public uint GetFeedDeviationThresholdBps(ulong feedId)
        => _feedDeviationThresholdBps.Get(feedId.ToString());

    [BasaltView]
    public uint GetFeedMinReporters(ulong feedId)
        => _feedMinReporters.Get(feedId.ToString());

    // ===================== Fee Management =====================

    [BasaltEntrypoint]
    public void ClaimReporterFees()
    {
        var hex = Convert.ToHexString(Context.Caller);
        var earned = _reporterEarnedFees.Get(hex);
        Context.Require(!earned.IsZero, "ORACLE: no fees to claim");

        _reporterEarnedFees.Set(hex, UInt256.Zero);
        Context.TransferNative(Context.Caller, earned);

        Context.Emit(new FeesClaimedEvent
        {
            Reporter = Context.Caller,
            Amount = earned
        });
    }

    // ===================== Internal =====================

    private void RequireFeedExists(ulong feedId)
    {
        Context.Require(_feedExists.Get(feedId.ToString()), "ORACLE: feed does not exist");
    }

    private void RequireFeedOwner(ulong feedId)
    {
        RequireFeedExists(feedId);
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

    /// <summary>
    /// Compute the median of an array of UInt256 values.
    /// Uses a copy to avoid mutating the input. Returns the middle element
    /// for odd counts, or the lower-median for even counts.
    /// </summary>
    public static UInt256 ComputeMedian(UInt256[] values)
    {
        var sorted = new UInt256[values.Length];
        Array.Copy(values, sorted, values.Length);
        Array.Sort(sorted);

        var mid = sorted.Length / 2;
        if (sorted.Length % 2 == 1)
            return sorted[mid];

        // Even count: average of two middle values (lower + upper) / 2
        var lo = sorted[mid - 1];
        var hi = sorted[mid];
        // Safe average: (lo + hi) / 2 = lo + (hi - lo) / 2 (avoids overflow)
        return lo + (hi - lo) / new UInt256(2);
    }

    /// <summary>
    /// Check if a submitted value is within the deviation threshold of the median.
    /// Returns true if |value - median| * 10000 &lt;= median * thresholdBps.
    /// Special case: if median is zero, only zero values are considered within threshold.
    /// </summary>
    public static bool IsWithinThreshold(UInt256 value, UInt256 median, uint thresholdBps)
    {
        if (median.IsZero)
            return value.IsZero;

        // |value - median| (unsigned absolute difference)
        var deviation = value > median ? value - median : median - value;

        // deviation * 10000 <= median * thresholdBps
        // Both sides fit comfortably in UInt256 for realistic oracle values
        var lhs = deviation * new UInt256(10_000);
        var rhs = median * new UInt256(thresholdBps);
        return lhs <= rhs;
    }
}

// ===================== Events =====================

[BasaltEvent]
public class FeedCreatedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    public string Name { get; set; } = "";
    [Indexed] public byte[] Owner { get; set; } = null!;
    public ulong HeartbeatBlocks { get; set; }
    public uint DeviationThresholdBps { get; set; }
    public uint MinReporters { get; set; }
}

[BasaltEvent]
public class FeedUpdatedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    public ulong HeartbeatBlocks { get; set; }
    public uint DeviationThresholdBps { get; set; }
}

[BasaltEvent]
public class FeedPausedEvent
{
    [Indexed] public ulong FeedId { get; set; }
}

[BasaltEvent]
public class FeedUnpausedEvent
{
    [Indexed] public ulong FeedId { get; set; }
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
    public byte[] OpenedBy { get; set; } = null!;
}

[BasaltEvent]
public class ValueSubmittedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    [Indexed] public ulong Round { get; set; }
    [Indexed] public byte[] Reporter { get; set; } = null!;
}

[BasaltEvent]
public class RoundFinalizedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    [Indexed] public ulong Round { get; set; }
    public UInt256 MedianValue { get; set; }
    public uint SubmissionCount { get; set; }
    public uint HonestCount { get; set; }
    public UInt256 TotalSlashed { get; set; }
}

[BasaltEvent]
public class ReporterSlashedEvent
{
    [Indexed] public ulong FeedId { get; set; }
    [Indexed] public ulong Round { get; set; }
    [Indexed] public byte[] Reporter { get; set; } = null!;
    public UInt256 SlashedAmount { get; set; }
    public UInt256 SubmittedValue { get; set; }
    public UInt256 MedianValue { get; set; }
}

[BasaltEvent]
public class FeesClaimedEvent
{
    [Indexed] public byte[] Reporter { get; set; } = null!;
    public UInt256 Amount { get; set; }
}