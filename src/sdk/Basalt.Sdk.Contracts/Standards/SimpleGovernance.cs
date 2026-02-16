namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Simple on-chain governance — create proposals, vote, and execute.
/// Type ID: 0x0102
/// </summary>
[BasaltContract]
public partial class SimpleGovernance
{
    private readonly StorageValue<ulong> _nextProposalId;
    private readonly StorageMap<string, string> _descriptions;   // id -> desc
    private readonly StorageMap<string, ulong> _endBlocks;       // id -> end block
    private readonly StorageMap<string, ulong> _votesFor;        // id -> count
    private readonly StorageMap<string, ulong> _votesAgainst;    // id -> count
    private readonly StorageMap<string, string> _hasVoted;       // "id:voter" -> "1"
    private readonly StorageMap<string, string> _status;         // id -> "active"/"executed"/"rejected"
    private readonly StorageMap<string, string> _proposers;      // id -> proposer hex
    private readonly ulong _quorumThreshold;

    public SimpleGovernance(ulong quorumThreshold = 3)
    {
        _quorumThreshold = quorumThreshold;
        _nextProposalId = new StorageValue<ulong>("gov_next");
        _descriptions = new StorageMap<string, string>("gov_desc");
        _endBlocks = new StorageMap<string, ulong>("gov_end");
        _votesFor = new StorageMap<string, ulong>("gov_for");
        _votesAgainst = new StorageMap<string, ulong>("gov_against");
        _hasVoted = new StorageMap<string, string>("gov_voted");
        _status = new StorageMap<string, string>("gov_status");
        _proposers = new StorageMap<string, string>("gov_proposer");
    }

    /// <summary>
    /// Create a new proposal. Returns the proposal ID.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateProposal(string description, ulong endBlock)
    {
        Context.Require(!string.IsNullOrEmpty(description), "GOV: description required");
        Context.Require(endBlock > Context.BlockHeight, "GOV: end block must be in future");

        var id = _nextProposalId.Get();
        _nextProposalId.Set(id + 1);

        var key = id.ToString();
        _descriptions.Set(key, description);
        _endBlocks.Set(key, endBlock);
        _status.Set(key, "active");
        _proposers.Set(key, Convert.ToHexString(Context.Caller));

        Context.Emit(new ProposalCreatedEvent
        {
            ProposalId = id,
            Proposer = Context.Caller,
            Description = description,
            EndBlock = endBlock,
        });

        return id;
    }

    /// <summary>
    /// Vote on a proposal.
    /// </summary>
    [BasaltEntrypoint]
    public void Vote(ulong proposalId, bool support)
    {
        var key = proposalId.ToString();
        var status = _status.Get(key);
        Context.Require(status == "active", "GOV: proposal not active");
        Context.Require(Context.BlockHeight <= _endBlocks.Get(key), "GOV: voting ended");

        var voteKey = key + ":" + Convert.ToHexString(Context.Caller);
        Context.Require(_hasVoted.Get(voteKey) != "1", "GOV: already voted");

        _hasVoted.Set(voteKey, "1");

        if (support)
            _votesFor.Set(key, _votesFor.Get(key) + 1);
        else
            _votesAgainst.Set(key, _votesAgainst.Get(key) + 1);

        Context.Emit(new VoteCastEvent
        {
            ProposalId = proposalId,
            Voter = Context.Caller,
            Support = support,
        });
    }

    /// <summary>
    /// Execute a proposal after voting ends, if it passed.
    /// </summary>
    [BasaltEntrypoint]
    public void ExecuteProposal(ulong proposalId)
    {
        var key = proposalId.ToString();
        var status = _status.Get(key);
        Context.Require(status == "active", "GOV: proposal not active");
        Context.Require(Context.BlockHeight > _endBlocks.Get(key), "GOV: voting not ended");

        var votesFor = _votesFor.Get(key);
        var votesAgainst = _votesAgainst.Get(key);
        var totalVotes = votesFor + votesAgainst;

        if (totalVotes >= _quorumThreshold && votesFor > votesAgainst)
        {
            _status.Set(key, "executed");
            Context.Emit(new ProposalExecutedEvent { ProposalId = proposalId, Passed = true });
        }
        else
        {
            _status.Set(key, "rejected");
            Context.Emit(new ProposalExecutedEvent { ProposalId = proposalId, Passed = false });
        }
    }

    [BasaltView]
    public string GetStatus(ulong proposalId)
    {
        return _status.Get(proposalId.ToString()) ?? "unknown";
    }

    [BasaltView]
    public ulong GetVotesFor(ulong proposalId)
    {
        return _votesFor.Get(proposalId.ToString());
    }

    [BasaltView]
    public ulong GetVotesAgainst(ulong proposalId)
    {
        return _votesAgainst.Get(proposalId.ToString());
    }
}

[BasaltEvent]
public class ProposalCreatedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Proposer { get; set; } = null!;
    public string Description { get; set; } = "";
    public ulong EndBlock { get; set; }
}

[BasaltEvent]
public class VoteCastEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Voter { get; set; } = null!;
    public bool Support { get; set; }
}

[BasaltEvent]
public class ProposalExecutedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public bool Passed { get; set; }
}
