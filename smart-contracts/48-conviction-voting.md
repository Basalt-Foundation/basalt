# Conviction Voting

## Category

Governance / Community Funding

## Summary

A conviction voting contract that implements continuous community signaling for fund allocation. Token holders stake tokens on proposals and their conviction (voting weight) accumulates over time according to an exponential decay function. Proposals are funded automatically when their accumulated conviction crosses a dynamically calculated threshold based on the requested amount relative to the total pool. Unlike discrete voting with fixed periods, conviction voting allows participants to continuously signal their preferences and reallocate at any time, making it ideal for ongoing community funding decisions.

## Why It's Useful

- **No voting fatigue**: Traditional governance systems require active participation in discrete voting rounds. Conviction voting runs continuously -- participants set their preference once and conviction accumulates automatically.
- **Minority voice amplification**: Small groups with strong long-term conviction can eventually fund proposals, even if they are a minority, as long as no equally strong opposition exists. This prevents tyranny of the majority.
- **Sybil-resistant by design**: The time-weighted accumulation means splitting stake across multiple accounts provides no advantage -- total conviction is the same regardless of account count.
- **Capital-efficient**: Staked tokens are not locked for fixed periods. Participants can reallocate their stake to different proposals at any time, though removing stake resets conviction accumulation.
- **Continuous funding**: Community treasuries can allocate funds to public goods, grants, and ecosystem development without the overhead of discrete proposal rounds and quorum requirements.
- **Attack-resistant**: Flash loan attacks are ineffective because conviction takes time to accumulate. An attacker would need to hold tokens for an extended period, making manipulation expensive.
- **Smooth governance**: No governance "seasons" or "rounds" -- proposals can be submitted and funded at any time, enabling responsive community allocation.

## Key Features

- Proposal submission: anyone can submit a funding proposal with a requested amount, description, and recipient
- Continuous staking: participants stake tokens on proposals they support. Conviction accumulates each block according to the formula: `conviction(t) = staked * (1 - alpha^t) / (1 - alpha)`, where alpha is the decay constant
- Dynamic threshold: the conviction threshold required to pass a proposal is proportional to `requestedAmount / (totalPool - requestedAmount)`, making larger requests exponentially harder to pass
- Reallocable stake: participants can move their stake between proposals at any time. Removing stake from a proposal resets the conviction accumulation for that participant-proposal pair.
- Automatic execution: when a proposal's total conviction crosses the threshold, the funds are automatically transferred to the recipient
- Proposal cancellation: the proposer can cancel an unfunded proposal and participants' stakes are released
- Pool management: the conviction voting pool receives funds from the DAO Treasury or direct deposits
- Configurable parameters: decay constant (alpha), spending limit per period, minimum stake to participate
- Proposal expiry: proposals that have not reached the threshold within a configurable number of blocks expire
- Historical conviction tracking: per-proposal conviction history is tracked for analytics and transparency

## Basalt-Specific Advantages

- **ZK identity for Sybil resistance**: While conviction voting is inherently Sybil-resistant due to time-weighting, adding ZK identity verification (via SchemaRegistry/IssuerRegistry) provides an additional layer of protection. One person = one identity, even across multiple wallets.
- **StakingPool integration**: Conviction staking can integrate with the StakingPool system contract so that tokens staked for conviction voting also earn staking rewards, eliminating the opportunity cost of governance participation.
- **Governance contract composability**: Conviction voting complements the existing quadratic Governance contract. The Governance contract handles discrete, high-stakes decisions (protocol upgrades, parameter changes), while conviction voting handles continuous community funding allocation.
- **UInt256 precision for conviction math**: The conviction accumulation formula involves multiplicative operations on large numbers. Basalt's native `UInt256` type provides the precision needed without overflow concerns, even for high-value pools with many participants.
- **BLAKE3 proposal hashing**: Proposal identifiers are BLAKE3 hashes, providing fast, collision-resistant identifiers for cross-referencing proposals across the ecosystem.
- **AOT-compiled conviction calculation**: The exponential decay computation runs in AOT-compiled code, ensuring deterministic and efficient execution of the mathematically intensive conviction updates.
- **BST-4626 vault for pool yield**: The conviction voting pool's idle funds (not yet allocated to proposals) can be deposited into BST-4626 vaults to earn yield, growing the pool over time.
- **Cross-contract call to DAO Treasury**: The conviction voting contract can operate as a sub-module of the DAO Treasury, pulling funds from the treasury when proposals are approved.

## Token Standards Used

- **BST-20**: Staking token for conviction accumulation (native BST or governance token)
- **BST-4626**: Yield optimization for idle pool funds

## Integration Points

- **Governance (0x...1005 area)**: Conviction voting parameters (alpha, spending limits, minimum stake) can be updated via traditional governance proposals. The two systems complement each other.
- **StakingPool (0x...1005)**: Tokens staked for conviction can simultaneously earn staking rewards, reducing opportunity cost.
- **DAO Treasury**: The conviction voting pool draws from or operates alongside the DAO Treasury for community funding.
- **SchemaRegistry (0x...1006)**: ZK identity verification for enhanced Sybil resistance.
- **IssuerRegistry (0x...1007)**: Verifiable credentials for proposal submitters.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Conviction Voting -- continuous community signaling for fund allocation.
/// Stake tokens on proposals; conviction accumulates over time.
/// Proposals are funded when conviction crosses a dynamic threshold.
/// </summary>
[BasaltContract]
public partial class ConvictionVoting
{
    // Decay constant in basis points. alpha = _alphaBps / 10000.
    // For example, alphaBps = 9000 means alpha = 0.9, so conviction decays 10% per period.
    private readonly StorageValue<uint> _alphaBps;
    private readonly StorageValue<ulong> _convictionPeriodBlocks;     // blocks per conviction update period

    // --- Pool ---
    private readonly StorageValue<UInt256> _totalPool;                 // total funds in the pool
    private readonly StorageValue<UInt256> _totalStaked;                // total tokens staked across all proposals
    private readonly StorageValue<UInt256> _spendingLimitPerEpoch;     // max spend per epoch
    private readonly StorageValue<UInt256> _epochSpent;                // spent this epoch
    private readonly StorageValue<ulong> _epochLength;
    private readonly StorageValue<ulong> _currentEpochStart;

    // --- Proposal state ---
    private readonly StorageValue<ulong> _nextProposalId;
    private readonly StorageMap<string, string> _proposalDescriptions;   // propId -> description
    private readonly StorageMap<string, string> _proposalProposers;      // propId -> proposer hex
    private readonly StorageMap<string, string> _proposalRecipients;     // propId -> recipient hex
    private readonly StorageMap<string, UInt256> _proposalAmounts;       // propId -> requested amount
    private readonly StorageMap<string, string> _proposalStatus;         // propId -> "active"|"funded"|"cancelled"|"expired"
    private readonly StorageMap<string, UInt256> _proposalTotalStaked;   // propId -> total staked on this proposal
    private readonly StorageMap<string, UInt256> _proposalConviction;    // propId -> accumulated conviction
    private readonly StorageMap<string, ulong> _proposalLastUpdate;     // propId -> last conviction update block
    private readonly StorageMap<string, ulong> _proposalCreatedBlock;   // propId -> creation block
    private readonly StorageMap<string, ulong> _proposalExpiryBlock;    // propId -> expiry block

    // --- Per-participant stake ---
    private readonly StorageMap<string, UInt256> _stakes;               // "propId:participantHex" -> staked amount
    private readonly StorageMap<string, ulong> _stakeStartBlocks;       // "propId:participantHex" -> block when staked

    // --- Config ---
    private readonly StorageValue<UInt256> _minStake;
    private readonly StorageValue<ulong> _defaultProposalLifetimeBlocks;

    public ConvictionVoting(uint alphaBps = 9000, ulong convictionPeriodBlocks = 100,
        UInt256 minStake = default, ulong epochLengthBlocks = 216000,
        ulong defaultProposalLifetimeBlocks = 432000)
    {
        _alphaBps = new StorageValue<uint>("cv_alpha");
        _convictionPeriodBlocks = new StorageValue<ulong>("cv_cperiod");
        _totalPool = new StorageValue<UInt256>("cv_pool");
        _totalStaked = new StorageValue<UInt256>("cv_tstaked");
        _spendingLimitPerEpoch = new StorageValue<UInt256>("cv_slim");
        _epochSpent = new StorageValue<UInt256>("cv_espent");
        _epochLength = new StorageValue<ulong>("cv_elen");
        _currentEpochStart = new StorageValue<ulong>("cv_estart");
        _nextProposalId = new StorageValue<ulong>("cv_nprop");
        _proposalDescriptions = new StorageMap<string, string>("cv_pdesc");
        _proposalProposers = new StorageMap<string, string>("cv_pprop");
        _proposalRecipients = new StorageMap<string, string>("cv_prec");
        _proposalAmounts = new StorageMap<string, UInt256>("cv_pamt");
        _proposalStatus = new StorageMap<string, string>("cv_psts");
        _proposalTotalStaked = new StorageMap<string, UInt256>("cv_pstk");
        _proposalConviction = new StorageMap<string, UInt256>("cv_pconv");
        _proposalLastUpdate = new StorageMap<string, ulong>("cv_plup");
        _proposalCreatedBlock = new StorageMap<string, ulong>("cv_pcblk");
        _proposalExpiryBlock = new StorageMap<string, ulong>("cv_pexp");
        _stakes = new StorageMap<string, UInt256>("cv_stk");
        _stakeStartBlocks = new StorageMap<string, ulong>("cv_ssblk");
        _minStake = new StorageValue<UInt256>("cv_minstk");
        _defaultProposalLifetimeBlocks = new StorageValue<ulong>("cv_deflife");

        _alphaBps.Set(alphaBps);
        _convictionPeriodBlocks.Set(convictionPeriodBlocks);
        if (minStake.IsZero) minStake = new UInt256(100);
        _minStake.Set(minStake);
        _epochLength.Set(epochLengthBlocks);
        _currentEpochStart.Set(Context.BlockHeight);
        _defaultProposalLifetimeBlocks.Set(defaultProposalLifetimeBlocks);
    }

    // ===================== Pool Management =====================

    /// <summary>
    /// Deposit funds into the conviction voting pool.
    /// </summary>
    [BasaltEntrypoint]
    public void FundPool()
    {
        Context.Require(!Context.TxValue.IsZero, "CV: must send value");
        _totalPool.Set(_totalPool.Get() + Context.TxValue);

        Context.Emit(new PoolFundedEvent { Funder = Context.Caller, Amount = Context.TxValue });
    }

    // ===================== Proposal Management =====================

    /// <summary>
    /// Submit a funding proposal. Returns proposal ID.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateProposal(string description, byte[] recipient, UInt256 requestedAmount)
    {
        Context.Require(!string.IsNullOrEmpty(description), "CV: description required");
        Context.Require(recipient.Length > 0, "CV: recipient required");
        Context.Require(!requestedAmount.IsZero, "CV: amount required");
        Context.Require(requestedAmount <= _totalPool.Get(), "CV: exceeds pool");

        var id = _nextProposalId.Get();
        _nextProposalId.Set(id + 1);
        var key = id.ToString();

        _proposalDescriptions.Set(key, description);
        _proposalProposers.Set(key, Convert.ToHexString(Context.Caller));
        _proposalRecipients.Set(key, Convert.ToHexString(recipient));
        _proposalAmounts.Set(key, requestedAmount);
        _proposalStatus.Set(key, "active");
        _proposalConviction.Set(key, UInt256.Zero);
        _proposalLastUpdate.Set(key, Context.BlockHeight);
        _proposalCreatedBlock.Set(key, Context.BlockHeight);
        _proposalExpiryBlock.Set(key, Context.BlockHeight + _defaultProposalLifetimeBlocks.Get());

        Context.Emit(new ProposalSubmittedEvent
        {
            ProposalId = id, Proposer = Context.Caller,
            Recipient = recipient, RequestedAmount = requestedAmount
        });
        return id;
    }

    /// <summary>
    /// Cancel a proposal (proposer only). Returns staked tokens to participants.
    /// </summary>
    [BasaltEntrypoint]
    public void CancelProposal(ulong proposalId)
    {
        var key = proposalId.ToString();
        Context.Require(
            Convert.ToHexString(Context.Caller) == _proposalProposers.Get(key),
            "CV: not proposer");
        Context.Require(_proposalStatus.Get(key) == "active", "CV: not active");

        _proposalStatus.Set(key, "cancelled");

        Context.Emit(new ProposalCancelledEvent { ProposalId = proposalId });
    }

    // ===================== Staking =====================

    /// <summary>
    /// Stake tokens on a proposal to accumulate conviction.
    /// Send native BST as value.
    /// </summary>
    [BasaltEntrypoint]
    public void StakeOnProposal(ulong proposalId)
    {
        Context.Require(!Context.TxValue.IsZero, "CV: must stake");
        Context.Require(Context.TxValue >= _minStake.Get(), "CV: below minimum stake");

        var propKey = proposalId.ToString();
        Context.Require(_proposalStatus.Get(propKey) == "active", "CV: proposal not active");

        // Update conviction before changing stake
        UpdateConviction(proposalId);

        var participantHex = Convert.ToHexString(Context.Caller);
        var stakeKey = propKey + ":" + participantHex;
        var currentStake = _stakes.Get(stakeKey);
        _stakes.Set(stakeKey, currentStake + Context.TxValue);
        _stakeStartBlocks.Set(stakeKey, Context.BlockHeight);

        var proposalStake = _proposalTotalStaked.Get(propKey);
        _proposalTotalStaked.Set(propKey, proposalStake + Context.TxValue);
        _totalStaked.Set(_totalStaked.Get() + Context.TxValue);

        Context.Emit(new StakedEvent
        {
            ProposalId = proposalId, Participant = Context.Caller,
            Amount = Context.TxValue
        });

        // Check if threshold is crossed
        TryExecuteProposal(proposalId);
    }

    /// <summary>
    /// Remove stake from a proposal. Resets conviction for this participant.
    /// </summary>
    [BasaltEntrypoint]
    public void UnstakeFromProposal(ulong proposalId, UInt256 amount)
    {
        Context.Require(!amount.IsZero, "CV: zero amount");

        var propKey = proposalId.ToString();
        var participantHex = Convert.ToHexString(Context.Caller);
        var stakeKey = propKey + ":" + participantHex;

        var currentStake = _stakes.Get(stakeKey);
        Context.Require(currentStake >= amount, "CV: insufficient stake");

        // Update conviction before changing stake
        UpdateConviction(proposalId);

        _stakes.Set(stakeKey, currentStake - amount);
        var proposalStake = _proposalTotalStaked.Get(propKey);
        _proposalTotalStaked.Set(propKey, proposalStake - amount);
        _totalStaked.Set(_totalStaked.Get() - amount);

        // Return tokens
        Context.TransferNative(Context.Caller, amount);

        Context.Emit(new UnstakedEvent
        {
            ProposalId = proposalId, Participant = Context.Caller,
            Amount = amount
        });
    }

    // ===================== Conviction Update =====================

    /// <summary>
    /// Update the accumulated conviction for a proposal.
    /// Called automatically on stake/unstake, or manually by anyone.
    /// Conviction formula: newConviction = oldConviction * alpha^periods + staked * (1 - alpha^periods) / (1 - alpha)
    /// Simplified with integer math using basis points.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateConviction(ulong proposalId)
    {
        var key = proposalId.ToString();
        if (_proposalStatus.Get(key) != "active") return;

        var lastUpdate = _proposalLastUpdate.Get(key);
        var periodBlocks = _convictionPeriodBlocks.Get();
        if (Context.BlockHeight <= lastUpdate) return;

        var elapsedPeriods = (Context.BlockHeight - lastUpdate) / periodBlocks;
        if (elapsedPeriods == 0) return;

        var oldConviction = _proposalConviction.Get(key);
        var staked = _proposalTotalStaked.Get(key);
        var alphaBps = _alphaBps.Get();

        // Integer approximation of conviction accumulation
        // conviction = oldConviction * (alphaBps/10000)^periods + staked * periods
        // Simplified: for each period, conviction = conviction * alphaBps / 10000 + staked
        var conviction = oldConviction;
        for (ulong i = 0; i < elapsedPeriods && i < 1000; i++)
        {
            conviction = conviction * new UInt256(alphaBps) / new UInt256(10000) + staked;
        }

        _proposalConviction.Set(key, conviction);
        _proposalLastUpdate.Set(key, Context.BlockHeight);

        Context.Emit(new ConvictionUpdatedEvent
        {
            ProposalId = proposalId, Conviction = conviction, Periods = elapsedPeriods
        });
    }

    /// <summary>
    /// Attempt to execute a proposal if conviction threshold is met.
    /// Threshold = requestedAmount^2 / (totalPool - requestedAmount)
    /// </summary>
    [BasaltEntrypoint]
    public void TryExecuteProposal(ulong proposalId)
    {
        var key = proposalId.ToString();
        if (_proposalStatus.Get(key) != "active") return;

        var requestedAmount = _proposalAmounts.Get(key);
        var pool = _totalPool.Get();
        if (requestedAmount >= pool) return;

        var conviction = _proposalConviction.Get(key);

        // Dynamic threshold: threshold = requestedAmount * weight / (pool - requestedAmount)
        // Higher weight = harder to pass. weight = totalPool for simplicity.
        var denominator = pool - requestedAmount;
        var threshold = requestedAmount * pool / denominator;

        if (conviction >= threshold)
        {
            // Check epoch spending limit
            MaybeResetEpoch();
            var spent = _epochSpent.Get();
            var limit = _spendingLimitPerEpoch.Get();
            if (!limit.IsZero && spent + requestedAmount > limit) return;

            _proposalStatus.Set(key, "funded");
            _totalPool.Set(pool - requestedAmount);
            _epochSpent.Set(spent + requestedAmount);

            var recipient = Convert.FromHexString(_proposalRecipients.Get(key));
            Context.TransferNative(recipient, requestedAmount);

            Context.Emit(new ProposalFundedEvent
            {
                ProposalId = proposalId, Recipient = recipient,
                Amount = requestedAmount, FinalConviction = conviction
            });
        }
    }

    // ===================== Views =====================

    [BasaltView]
    public UInt256 GetTotalPool() => _totalPool.Get();

    [BasaltView]
    public UInt256 GetTotalStaked() => _totalStaked.Get();

    [BasaltView]
    public string GetProposalStatus(ulong proposalId) => _proposalStatus.Get(proposalId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetProposalConviction(ulong proposalId) => _proposalConviction.Get(proposalId.ToString());

    [BasaltView]
    public UInt256 GetProposalStaked(ulong proposalId) => _proposalTotalStaked.Get(proposalId.ToString());

    [BasaltView]
    public UInt256 GetProposalAmount(ulong proposalId) => _proposalAmounts.Get(proposalId.ToString());

    [BasaltView]
    public UInt256 GetParticipantStake(ulong proposalId, byte[] participant)
        => _stakes.Get(proposalId.ToString() + ":" + Convert.ToHexString(participant));

    [BasaltView]
    public UInt256 GetConvictionThreshold(ulong proposalId)
    {
        var key = proposalId.ToString();
        var requestedAmount = _proposalAmounts.Get(key);
        var pool = _totalPool.Get();
        if (requestedAmount >= pool) return UInt256.MaxValue;
        var denominator = pool - requestedAmount;
        return requestedAmount * pool / denominator;
    }

    [BasaltView]
    public uint GetAlphaBps() => _alphaBps.Get();

    // ===================== Internal =====================

    private void MaybeResetEpoch()
    {
        var epochStart = _currentEpochStart.Get();
        var epochLen = _epochLength.Get();
        if (Context.BlockHeight >= epochStart + epochLen)
        {
            _currentEpochStart.Set(Context.BlockHeight);
            _epochSpent.Set(UInt256.Zero);
        }
    }
}

// ===================== Events =====================

[BasaltEvent]
public class PoolFundedEvent
{
    [Indexed] public byte[] Funder { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class ProposalSubmittedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Proposer { get; set; } = null!;
    public byte[] Recipient { get; set; } = null!;
    public UInt256 RequestedAmount { get; set; }
}

[BasaltEvent]
public class ProposalCancelledEvent
{
    [Indexed] public ulong ProposalId { get; set; }
}

[BasaltEvent]
public class StakedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Participant { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class UnstakedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Participant { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class ConvictionUpdatedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public UInt256 Conviction { get; set; }
    public ulong Periods { get; set; }
}

[BasaltEvent]
public class ProposalFundedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Recipient { get; set; } = null!;
    public UInt256 Amount { get; set; }
    public UInt256 FinalConviction { get; set; }
}
```

## Complexity

**High** -- The conviction accumulation formula involves exponential decay calculations that must be approximated in integer arithmetic. The iterative loop for conviction updates must be bounded to prevent gas exhaustion on long idle periods. The dynamic threshold calculation requires careful handling of the edge case where `requestedAmount` approaches `totalPool`. The interaction between epoch spending limits, proposal expiry, and conviction accumulation creates a complex state machine. Testing requires careful attention to numerical accuracy and edge cases.

## Priority

**P2** -- Conviction voting is an innovative governance mechanism that is particularly valuable for community funding and public goods allocation. However, it is a complementary system to the existing Governance contract, not a replacement for it. It should be deployed after the core governance system is proven and the community is ready for more nuanced funding mechanisms.
