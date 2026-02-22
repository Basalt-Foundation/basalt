using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// On-chain governance with stake-weighted quadratic voting, delegation, timelock, and executable proposals.
/// Type ID: 0x0102
/// </summary>
[BasaltContract]
public partial class Governance
{
    // Proposal core state
    private readonly StorageValue<ulong> _nextProposalId;
    private readonly StorageMap<string, string> _descriptions;
    private readonly StorageMap<string, ulong> _endBlocks;
    private readonly StorageMap<string, string> _proposers;
    private readonly StorageMap<string, string> _status;
    private readonly StorageMap<string, UInt256> _votesFor;
    private readonly StorageMap<string, UInt256> _votesAgainst;
    private readonly StorageMap<string, string> _hasVoted;
    private readonly StorageMap<string, UInt256> _voterWeight;

    // Proposal type & execution payload
    private readonly StorageMap<string, string> _proposalTypes;
    private readonly StorageMap<string, string> _targets;
    private readonly StorageMap<string, string> _callMethods;

    // Timelock
    private readonly StorageMap<string, ulong> _timelockExpiry;
    private readonly StorageValue<ulong> _timelockDelay;

    // Delegation
    private readonly StorageMap<string, string> _delegates;
    private readonly StorageMap<string, UInt256> _delegatedPower;
    private readonly StorageMap<string, UInt256> _delegatorStake;

    // Configuration
    private readonly StorageValue<ulong> _quorumBps;
    private readonly StorageValue<UInt256> _proposalThreshold;
    private readonly StorageValue<ulong> _votingPeriod;
    private readonly StorageMap<string, UInt256> _totalStakeSnapshot;

    // StakingPool system contract address (0x...1005)
    private readonly byte[] _stakingPoolAddress;

    public Governance(
        ulong quorumBps = 400,
        UInt256 proposalThreshold = default,
        ulong votingPeriodBlocks = 216000,
        ulong timelockDelayBlocks = 432000)
    {
        if (proposalThreshold.IsZero) proposalThreshold = new UInt256(1000);
        _nextProposalId = new StorageValue<ulong>("gov_next");
        _descriptions = new StorageMap<string, string>("gov_desc");
        _endBlocks = new StorageMap<string, ulong>("gov_end");
        _proposers = new StorageMap<string, string>("gov_proposer");
        _status = new StorageMap<string, string>("gov_status");
        _votesFor = new StorageMap<string, UInt256>("gov_for");
        _votesAgainst = new StorageMap<string, UInt256>("gov_against");
        _hasVoted = new StorageMap<string, string>("gov_voted");
        _voterWeight = new StorageMap<string, UInt256>("gov_vw");
        _proposalTypes = new StorageMap<string, string>("gov_type");
        _targets = new StorageMap<string, string>("gov_target");
        _callMethods = new StorageMap<string, string>("gov_method");
        _timelockExpiry = new StorageMap<string, ulong>("gov_tl");
        _timelockDelay = new StorageValue<ulong>("gov_tl_delay");
        _delegates = new StorageMap<string, string>("gov_del");
        _delegatedPower = new StorageMap<string, UInt256>("gov_dp");
        _delegatorStake = new StorageMap<string, UInt256>("gov_ds");
        _quorumBps = new StorageValue<ulong>("gov_quorum");
        _proposalThreshold = new StorageValue<UInt256>("gov_pthresh");
        _votingPeriod = new StorageValue<ulong>("gov_vperiod");
        _totalStakeSnapshot = new StorageMap<string, UInt256>("gov_snap");

        _quorumBps.Set(quorumBps);
        _proposalThreshold.Set(proposalThreshold);
        _votingPeriod.Set(votingPeriodBlocks);
        _timelockDelay.Set(timelockDelayBlocks);

        _stakingPoolAddress = new byte[20];
        _stakingPoolAddress[18] = 0x10;
        _stakingPoolAddress[19] = 0x05;
    }

    // --- Entrypoints ---

    /// <summary>
    /// Create a text-only (signaling) proposal.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateProposal(string description, ulong votingPeriodBlocks)
    {
        Context.Require(!string.IsNullOrEmpty(description), "GOV: description required");
        Context.Require(votingPeriodBlocks > 0, "GOV: voting period must be > 0");

        return CreateProposalInternal(description, votingPeriodBlocks, "text", [], "");
    }

    /// <summary>
    /// Create an executable proposal that calls a target contract method after passage + timelock.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateExecutableProposal(
        string description, ulong votingPeriodBlocks,
        byte[] targetContract, string methodName)
    {
        Context.Require(!string.IsNullOrEmpty(description), "GOV: description required");
        Context.Require(votingPeriodBlocks > 0, "GOV: voting period must be > 0");
        Context.Require(targetContract.Length > 0, "GOV: target required");
        Context.Require(!string.IsNullOrEmpty(methodName), "GOV: method required");

        return CreateProposalInternal(description, votingPeriodBlocks, "executable", targetContract, methodName);
    }

    /// <summary>
    /// Vote on a proposal. Voting power = isqrt(stake + delegatedPower) using quadratic formula.
    /// </summary>
    [BasaltEntrypoint]
    public void Vote(ulong proposalId, bool support, ulong poolId)
    {
        var key = proposalId.ToString();
        var status = _status.Get(key);
        Context.Require(status == "active", "GOV: proposal not active");
        Context.Require(Context.BlockHeight <= _endBlocks.Get(key), "GOV: voting ended");

        var callerHex = Convert.ToHexString(Context.Caller);
        var voteKey = key + ":" + callerHex;
        Context.Require(_hasVoted.Get(voteKey) != "1", "GOV: already voted");

        // Delegated voters cannot vote directly
        var delegatee = _delegates.Get(callerHex);
        Context.Require(string.IsNullOrEmpty(delegatee), "GOV: vote delegated");

        // Get stake from StakingPool
        var ownStake = Context.CallContract<UInt256>(_stakingPoolAddress, "GetDelegation", poolId, Context.Caller);
        var delegated = _delegatedPower.Get(callerHex);
        var rawPower = ownStake + delegated;
        var weight = IntegerSqrt(rawPower);

        Context.Require(!weight.IsZero, "GOV: no voting power");

        _hasVoted.Set(voteKey, "1");
        _voterWeight.Set(voteKey, weight);

        if (support)
            _votesFor.Set(key, _votesFor.Get(key) + weight);
        else
            _votesAgainst.Set(key, _votesAgainst.Get(key) + weight);

        Context.Emit(new VoteCastEvent
        {
            ProposalId = proposalId,
            Voter = Context.Caller,
            Support = support,
            Weight = weight,
        });
    }

    /// <summary>
    /// Delegate voting power to another address (single-hop).
    /// </summary>
    [BasaltEntrypoint]
    public void DelegateVote(byte[] delegatee, ulong poolId)
    {
        Context.Require(delegatee.Length > 0, "GOV: delegatee required");
        var callerHex = Convert.ToHexString(Context.Caller);
        var delegateeHex = Convert.ToHexString(delegatee);
        Context.Require(callerHex != delegateeHex, "GOV: cannot delegate to self");

        // H-11: Enforce single-hop delegation — delegatee must not have delegated themselves
        var delegateeDelegatee = _delegates.Get(delegateeHex);
        Context.Require(string.IsNullOrEmpty(delegateeDelegatee), "GOV: delegatee has already delegated (no multi-hop)");

        // Remove existing delegation if any
        UndelegateInternal(callerHex);

        // Get caller's stake from StakingPool
        var stake = Context.CallContract<UInt256>(_stakingPoolAddress, "GetDelegation", poolId, Context.Caller);
        Context.Require(!stake.IsZero, "GOV: no stake to delegate");

        _delegates.Set(callerHex, delegateeHex);
        _delegatorStake.Set(callerHex, stake);
        _delegatedPower.Set(delegateeHex, _delegatedPower.Get(delegateeHex) + stake);

        Context.Emit(new DelegateChangedEvent
        {
            Delegator = Context.Caller,
            Delegatee = delegatee,
        });
    }

    /// <summary>
    /// Remove vote delegation.
    /// </summary>
    [BasaltEntrypoint]
    public void Undelegate()
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        var delegateeHex = _delegates.Get(callerHex);
        Context.Require(!string.IsNullOrEmpty(delegateeHex), "GOV: not delegated");

        UndelegateInternal(callerHex);

        Context.Emit(new DelegateChangedEvent
        {
            Delegator = Context.Caller,
            Delegatee = [],
        });
    }

    /// <summary>
    /// N-2: Refresh delegated power from current staking balance.
    /// Call after stake changes to ensure delegated voting power stays accurate.
    /// </summary>
    [BasaltEntrypoint]
    public void RefreshDelegation(ulong poolId)
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        var delegateeHex = _delegates.Get(callerHex);
        Context.Require(!string.IsNullOrEmpty(delegateeHex), "GOV: not delegated");

        var oldStake = _delegatorStake.Get(callerHex);
        var newStake = Context.CallContract<UInt256>(_stakingPoolAddress, "GetDelegation", poolId, Context.Caller);

        // Update delegated power: remove old, add new
        var currentPower = _delegatedPower.Get(delegateeHex);
        var adjusted = currentPower >= oldStake ? currentPower - oldStake : UInt256.Zero;
        _delegatedPower.Set(delegateeHex, adjusted + newStake);
        _delegatorStake.Set(callerHex, newStake);
    }

    /// <summary>
    /// Queue a passed proposal for timelock execution.
    /// </summary>
    [BasaltEntrypoint]
    public void QueueProposal(ulong proposalId)
    {
        var key = proposalId.ToString();
        var status = _status.Get(key);
        Context.Require(status == "active", "GOV: proposal not active");
        Context.Require(Context.BlockHeight > _endBlocks.Get(key), "GOV: voting not ended");

        var votesFor = _votesFor.Get(key);
        var votesAgainst = _votesAgainst.Get(key);
        var totalVotes = votesFor + votesAgainst;

        // Check quorum (in quadratic units)
        var totalStake = _totalStakeSnapshot.Get(key);
        var quorumBps = _quorumBps.Get();
        var quorumThreshold = IntegerSqrt(totalStake * new UInt256(quorumBps) / new UInt256(10000));
        if (quorumThreshold.IsZero) quorumThreshold = UInt256.One; // minimum 1 vote

        if (totalVotes >= quorumThreshold && votesFor > votesAgainst)
        {
            var expiry = Context.BlockHeight + _timelockDelay.Get();
            _status.Set(key, "queued");
            _timelockExpiry.Set(key, expiry);

            Context.Emit(new ProposalQueuedEvent
            {
                ProposalId = proposalId,
                TimelockExpiry = expiry,
            });
        }
        else
        {
            _status.Set(key, "rejected");
            Context.Emit(new ProposalExecutedEvent { ProposalId = proposalId, Passed = false });
        }
    }

    /// <summary>
    /// Execute a queued proposal after timelock expires.
    /// </summary>
    [BasaltEntrypoint]
    public void ExecuteProposal(ulong proposalId)
    {
        var key = proposalId.ToString();
        var status = _status.Get(key);
        Context.Require(status == "queued", "GOV: proposal not queued");
        Context.Require(Context.BlockHeight >= _timelockExpiry.Get(key), "GOV: timelock not expired");

        _status.Set(key, "executed");

        // Execute cross-contract call for executable proposals
        var proposalType = _proposalTypes.Get(key);
        if (proposalType == "executable")
        {
            var targetHex = _targets.Get(key);
            var method = _callMethods.Get(key);
            if (!string.IsNullOrEmpty(targetHex) && !string.IsNullOrEmpty(method))
            {
                var target = Convert.FromHexString(targetHex);
                Context.CallContract(target, method);
            }
        }

        Context.Emit(new ProposalExecutedEvent { ProposalId = proposalId, Passed = true });
    }

    /// <summary>
    /// Cancel a proposal (proposer only, while active or queued).
    /// </summary>
    [BasaltEntrypoint]
    public void CancelProposal(ulong proposalId)
    {
        var key = proposalId.ToString();
        var status = _status.Get(key);
        Context.Require(status == "active" || status == "queued", "GOV: cannot cancel");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _proposers.Get(key),
            "GOV: not proposer");

        _status.Set(key, "canceled");

        Context.Emit(new ProposalCanceledEvent { ProposalId = proposalId });
    }

    // --- Views ---

    [BasaltView]
    public string GetStatus(ulong proposalId)
        => _status.Get(proposalId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetVotesFor(ulong proposalId)
        => _votesFor.Get(proposalId.ToString());

    [BasaltView]
    public UInt256 GetVotesAgainst(ulong proposalId)
        => _votesAgainst.Get(proposalId.ToString());

    [BasaltView]
    public string GetProposalType(ulong proposalId)
        => _proposalTypes.Get(proposalId.ToString()) ?? "";

    [BasaltView]
    public ulong GetTimelockExpiry(ulong proposalId)
        => _timelockExpiry.Get(proposalId.ToString());

    [BasaltView]
    public string GetDelegate(byte[] voter)
        => _delegates.Get(Convert.ToHexString(voter)) ?? "";

    [BasaltView]
    public UInt256 GetDelegatedPower(byte[] delegatee)
        => _delegatedPower.Get(Convert.ToHexString(delegatee));

    [BasaltView]
    public ulong GetQuorumBps() => _quorumBps.Get();

    [BasaltView]
    public UInt256 GetProposalThreshold() => _proposalThreshold.Get();

    [BasaltView]
    public ulong GetVotingPeriod() => _votingPeriod.Get();

    [BasaltView]
    public ulong GetTimelockDelay() => _timelockDelay.Get();

    [BasaltView]
    public UInt256 GetVoterWeight(ulong proposalId, byte[] voter)
        => _voterWeight.Get(proposalId.ToString() + ":" + Convert.ToHexString(voter));

    // --- Internal helpers ---

    private ulong CreateProposalInternal(
        string description, ulong votingPeriodBlocks,
        string proposalType, byte[] targetContract, string methodName)
    {
        // C-8: Enforce proposal threshold — proposer must have sufficient stake
        var threshold = _proposalThreshold.Get();
        if (!threshold.IsZero)
        {
            // Query proposer's total stake from all pools (use pool 0 as primary)
            var proposerStake = Context.CallContract<UInt256>(_stakingPoolAddress, "GetDelegation", (ulong)0, Context.Caller);
            Context.Require(proposerStake >= threshold, "GOV: proposer stake below threshold");
        }

        // C-7: Read totalStake from StakingPool instead of accepting caller-supplied value
        var totalStake = Context.CallContract<UInt256>(_stakingPoolAddress, "GetPoolStake", (ulong)0);

        var id = _nextProposalId.Get();
        _nextProposalId.Set(id + 1);

        var key = id.ToString();
        var endBlock = Context.BlockHeight + votingPeriodBlocks;

        _descriptions.Set(key, description);
        _endBlocks.Set(key, endBlock);
        _proposers.Set(key, Convert.ToHexString(Context.Caller));
        _status.Set(key, "active");
        _proposalTypes.Set(key, proposalType);
        _totalStakeSnapshot.Set(key, totalStake);

        if (proposalType == "executable")
        {
            _targets.Set(key, Convert.ToHexString(targetContract));
            _callMethods.Set(key, methodName);
        }

        Context.Emit(new ProposalCreatedEvent
        {
            ProposalId = id,
            Proposer = Context.Caller,
            Description = description,
            EndBlock = endBlock,
            ProposalType = proposalType,
        });

        return id;
    }

    private void UndelegateInternal(string callerHex)
    {
        var delegateeHex = _delegates.Get(callerHex);
        if (string.IsNullOrEmpty(delegateeHex)) return;

        var stake = _delegatorStake.Get(callerHex);
        var currentPower = _delegatedPower.Get(delegateeHex);
        _delegatedPower.Set(delegateeHex, currentPower >= stake ? currentPower - stake : UInt256.Zero);

        _delegates.Delete(callerHex);
        _delegatorStake.Delete(callerHex);
    }

    private static UInt256 IntegerSqrt(UInt256 n)
    {
        if (n.IsZero) return UInt256.Zero;
        if (n == UInt256.One) return UInt256.One;
        // Newton's method: x = (x + n/x) / 2
        var x = n;
        var y = (x + UInt256.One) / new UInt256(2);
        while (y < x)
        {
            x = y;
            y = (x + n / x) / new UInt256(2);
        }
        return x;
    }
}

[BasaltEvent]
public class ProposalCreatedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Proposer { get; set; } = null!;
    public string Description { get; set; } = "";
    public ulong EndBlock { get; set; }
    public string ProposalType { get; set; } = "";
}

[BasaltEvent]
public class VoteCastEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Voter { get; set; } = null!;
    public bool Support { get; set; }
    public UInt256 Weight { get; set; }
}

[BasaltEvent]
public class ProposalQueuedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public ulong TimelockExpiry { get; set; }
}

[BasaltEvent]
public class ProposalExecutedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public bool Passed { get; set; }
}

[BasaltEvent]
public class ProposalCanceledEvent
{
    [Indexed] public ulong ProposalId { get; set; }
}

[BasaltEvent]
public class DelegateChangedEvent
{
    [Indexed] public byte[] Delegator { get; set; } = null!;
    [Indexed] public byte[] Delegatee { get; set; } = null!;
}
