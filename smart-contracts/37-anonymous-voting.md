# Privacy-Preserving Voting

## Category

Governance / Privacy

## Summary

A privacy-preserving voting system where eligible voters can cast votes without revealing their choice until the tally phase. The system supports both commit-reveal schemes (simpler, suitable for low-stakes votes) and full ZK proof-based voting (for sensitive governance decisions where even the act of voting must be private). Voter eligibility is proven via BST-VC credentials without revealing the voter's identity, and vote weight can be proven proportional to token holdings via ZK range proofs.

This contract addresses a fundamental limitation of on-chain governance: public voting creates social pressure, vote-buying opportunities, and retaliation risks that undermine the quality of governance decisions.

## Why It's Useful

- **Eliminates vote coercion**: When votes are public, large stakeholders and social pressure can coerce voters. Secret ballots are a cornerstone of democratic governance for good reason.
- **Prevents vote-buying**: Public votes enable verifiable vote-buying (the buyer can confirm the seller voted as promised). Secret ballots make vote-buying unenforceable.
- **Reduces herd behavior**: When early votes are visible, later voters tend to follow the majority. Hidden votes encourage independent decision-making.
- **Enables sensitive decisions**: Some governance topics (personnel decisions, security vulnerability disclosures, contentious parameter changes) require confidentiality until resolution.
- **Maintains accountability**: While individual votes are secret, the aggregated tally is publicly verifiable. Audit proofs confirm the tally is correct without revealing individual votes.
- **Weighted voting privacy**: Token-weighted voting reveals wealth concentration. ZK range proofs allow voters to prove "I hold enough tokens to have weight X" without revealing exact holdings.

## Key Features

- Two voting modes:
  - **Commit-Reveal**: Voters commit hash(vote || salt) during voting phase, reveal vote + salt during reveal phase. Simple and gas-efficient.
  - **ZK Private Vote**: Voters submit ZK proof of valid vote without revealing choice. Tally is computed from encrypted votes. More private but higher gas cost.
- Voter eligibility via BST-VC credentials: prove membership in eligible voter set without revealing identity
- Weighted voting: vote weight proven proportional to token holdings via ZK range proof
- Configurable vote options: binary (yes/no), multiple choice, or ranked choice
- Quorum enforcement: minimum participation threshold for valid results
- Tally verification: anyone can verify the tally is correct via aggregated proofs
- Time-phased voting: distinct nomination, voting, reveal, and tally phases with configurable durations
- Voter nullifiers: each voter gets one vote per proposal, enforced via nullifiers (no double-voting)
- Delegation support: delegate voting power to another eligible voter (single-hop)
- Emergency proposal cancellation: admin or proposer can cancel before tally
- Result finality: tally is final and on-chain once computed, with challenge period for disputes
- Proposal metadata: IPFS links for detailed proposal descriptions

## Basalt-Specific Advantages

- **Native ZK proof verification**: Basalt's ZkComplianceVerifier verifies Groth16 proofs for vote validity, eligibility, and weight. The voting circuit's verification key is stored in SchemaRegistry, ensuring all verifiers use consistent trusted parameters.
- **BST-VC voter credentials**: Voter eligibility is proven via BST-VC credentials (type 0x0007). For governance votes, this could be a "governance participation" credential; for corporate votes, an "equity holder" credential. The credential is never revealed on-chain -- only a ZK proof of possession.
- **Nullifier anti-correlation**: Basalt's native nullifier derivation ensures each voter's nullifier is unique per proposal. Observers cannot determine if the same voter participated in different proposals.
- **Governance system integration**: The contract extends Basalt's existing Governance contract (0x...1002) with privacy features. Non-sensitive proposals can use the existing public voting mechanism, while sensitive proposals use this contract.
- **SchemaRegistry vote circuits**: Each voting mode (commit-reveal, ZK binary, ZK multi-choice) has a registered schema with its own verification key. New voting modes can be added by registering new schemas.
- **Pedersen commitment vote encoding**: In ZK mode, votes are encoded as Pedersen commitments, allowing homomorphic tally computation: `Tally_yes = sum(C_i where vote_i = yes)`. The tally can be verified without opening individual votes.
- **AOT-compiled tally computation**: Tally computation runs in AOT-compiled code, ensuring predictable gas costs even for large voter sets.
- **BLS aggregate proofs**: For elections with many voters, BLS signature aggregation can compress voter eligibility proofs for efficient batch verification.

## Token Standards Used

- **BST-VC** (BSTVCRegistry, type 0x0007): Voter eligibility credentials. Each eligible voter holds a BST-VC credential proving their right to vote (e.g., token holder credential, governance participant credential).

## Integration Points

- **Governance** (0x...1002): Extends the existing Governance contract. Proposals requiring secret ballots are routed to this contract. Tally results feed back into Governance for execution.
- **StakingPool** (0x...1005): Vote weight can be derived from staking position. The contract queries StakingPool to verify delegated stake for weighted voting.
- **SchemaRegistry** (0x...1006): Voting circuit verification keys and voter credential schemas are stored in SchemaRegistry.
- **IssuerRegistry** (0x...1007): Validates that voter credentials were issued by authorized parties (e.g., the governance contract itself, or authorized credential issuers).
- **BSTVCRegistry** (deployed instance): Voter credential validity is checked during vote submission.
- **BNS** (Basalt Name Service): Proposal creators can be identified by BNS name for human-readable attribution.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Privacy-preserving voting with commit-reveal and ZK proof modes.
/// Voter eligibility via BST-VC, weighted voting via ZK range proofs.
/// Type ID: 0x0110.
/// </summary>
[BasaltContract]
public partial class PrivateVoting
{
    // --- Storage ---

    // Proposal state
    private readonly StorageValue<ulong> _nextProposalId;
    private readonly StorageMap<string, string> _proposalCreator;     // proposalId -> creatorHex
    private readonly StorageMap<string, string> _proposalDescription; // proposalId -> description
    private readonly StorageMap<string, string> _proposalIpfsUri;     // proposalId -> IPFS URI
    private readonly StorageMap<string, byte> _proposalMode;          // proposalId -> 1=commit-reveal, 2=zk-private
    private readonly StorageMap<string, byte> _proposalChoiceCount;   // proposalId -> number of choices
    private readonly StorageMap<string, string> _proposalStatus;      // proposalId -> created/voting/reveal/tallied/cancelled

    // Phase timing
    private readonly StorageMap<string, long> _votingStart;           // proposalId -> voting start timestamp
    private readonly StorageMap<string, long> _votingEnd;             // proposalId -> voting end timestamp
    private readonly StorageMap<string, long> _revealEnd;             // proposalId -> reveal end (commit-reveal mode)

    // Commit-Reveal mode storage
    private readonly StorageMap<string, string> _commitments;         // proposalId:voterHex -> commitment hex
    private readonly StorageMap<string, bool> _revealed;              // proposalId:voterHex -> revealed
    private readonly StorageMap<string, UInt256> _revealedWeight;     // proposalId:voterHex -> revealed weight

    // ZK mode storage
    private readonly StorageMap<string, bool> _zkNullifierUsed;       // proposalId:nullifierHex -> used

    // Tally
    private readonly StorageMap<string, UInt256> _choiceVotes;        // proposalId:choiceIndex -> total weighted votes
    private readonly StorageMap<string, ulong> _choiceVoterCount;     // proposalId:choiceIndex -> voter count
    private readonly StorageMap<string, UInt256> _totalVoteWeight;    // proposalId -> total vote weight cast
    private readonly StorageMap<string, ulong> _totalVoterCount;      // proposalId -> total voters

    // Configuration
    private readonly StorageValue<UInt256> _quorumThreshold;          // minimum total weight for valid result
    private readonly StorageMap<string, string> _voteSchemaId;        // "schema" -> schema ID hex

    // Delegation
    private readonly StorageMap<string, string> _delegations;         // voterHex -> delegateeHex
    private readonly StorageMap<string, UInt256> _delegatedWeight;    // delegateeHex -> accumulated delegated weight

    // Admin
    private readonly StorageMap<string, string> _admin;

    // System contract addresses
    private readonly byte[] _governanceAddress;
    private readonly byte[] _stakingPoolAddress;
    private readonly byte[] _schemaRegistryAddress;

    public PrivateVoting(UInt256 quorumThreshold = default)
    {
        if (quorumThreshold.IsZero) quorumThreshold = new UInt256(1000);

        _nextProposalId = new StorageValue<ulong>("pv_next");
        _proposalCreator = new StorageMap<string, string>("pv_creator");
        _proposalDescription = new StorageMap<string, string>("pv_desc");
        _proposalIpfsUri = new StorageMap<string, string>("pv_ipfs");
        _proposalMode = new StorageMap<string, byte>("pv_mode");
        _proposalChoiceCount = new StorageMap<string, byte>("pv_choices");
        _proposalStatus = new StorageMap<string, string>("pv_status");

        _votingStart = new StorageMap<string, long>("pv_vstart");
        _votingEnd = new StorageMap<string, long>("pv_vend");
        _revealEnd = new StorageMap<string, long>("pv_rend");

        _commitments = new StorageMap<string, string>("pv_commits");
        _revealed = new StorageMap<string, bool>("pv_revealed");
        _revealedWeight = new StorageMap<string, UInt256>("pv_revwt");

        _zkNullifierUsed = new StorageMap<string, bool>("pv_zknull");

        _choiceVotes = new StorageMap<string, UInt256>("pv_cvotes");
        _choiceVoterCount = new StorageMap<string, ulong>("pv_ccount");
        _totalVoteWeight = new StorageMap<string, UInt256>("pv_twt");
        _totalVoterCount = new StorageMap<string, ulong>("pv_tcount");

        _quorumThreshold = new StorageValue<UInt256>("pv_quorum");
        _quorumThreshold.Set(quorumThreshold);
        _voteSchemaId = new StorageMap<string, string>("pv_schema");

        _delegations = new StorageMap<string, string>("pv_deleg");
        _delegatedWeight = new StorageMap<string, UInt256>("pv_delwt");

        _admin = new StorageMap<string, string>("pv_admin");
        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        _governanceAddress = new byte[20];
        _governanceAddress[18] = 0x10;
        _governanceAddress[19] = 0x02;

        _stakingPoolAddress = new byte[20];
        _stakingPoolAddress[18] = 0x10;
        _stakingPoolAddress[19] = 0x05;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;
    }

    // ========================================================
    // Proposal Creation
    // ========================================================

    /// <summary>
    /// Create a private voting proposal.
    /// Mode 1 = commit-reveal, Mode 2 = ZK private vote.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateProposal(
        string description, string ipfsUri, byte mode, byte choiceCount,
        long votingStartTimestamp, long votingEndTimestamp, long revealEndTimestamp)
    {
        Context.Require(!string.IsNullOrEmpty(description), "PV: description required");
        Context.Require(mode == 1 || mode == 2, "PV: invalid mode (1=commit-reveal, 2=zk)");
        Context.Require(choiceCount >= 2, "PV: need at least 2 choices");
        Context.Require(votingEndTimestamp > votingStartTimestamp, "PV: invalid voting period");

        if (mode == 1)
        {
            Context.Require(revealEndTimestamp > votingEndTimestamp, "PV: reveal must be after voting");
        }

        var proposalId = _nextProposalId.Get();
        _nextProposalId.Set(proposalId + 1);

        var key = proposalId.ToString();
        _proposalCreator.Set(key, Convert.ToHexString(Context.Caller));
        _proposalDescription.Set(key, description);
        _proposalIpfsUri.Set(key, ipfsUri);
        _proposalMode.Set(key, mode);
        _proposalChoiceCount.Set(key, choiceCount);
        _proposalStatus.Set(key, "created");
        _votingStart.Set(key, votingStartTimestamp);
        _votingEnd.Set(key, votingEndTimestamp);
        _revealEnd.Set(key, revealEndTimestamp);

        Context.Emit(new PrivateProposalCreatedEvent
        {
            ProposalId = proposalId,
            Creator = Context.Caller,
            Mode = mode,
            ChoiceCount = choiceCount,
            VotingEnd = votingEndTimestamp,
        });

        return proposalId;
    }

    // ========================================================
    // Commit-Reveal Voting (Mode 1)
    // ========================================================

    /// <summary>
    /// Commit a vote (Mode 1). Voter submits hash(choiceIndex || salt || voterAddress).
    /// </summary>
    [BasaltEntrypoint]
    public void CommitVote(ulong proposalId, byte[] commitHash)
    {
        var key = proposalId.ToString();
        Context.Require(_proposalMode.Get(key) == 1, "PV: not commit-reveal mode");
        RequireVotingPhase(key);

        Context.Require(commitHash.Length == 32, "PV: invalid commit hash");

        var voterHex = Convert.ToHexString(Context.Caller);
        var voteKey = key + ":" + voterHex;
        Context.Require(string.IsNullOrEmpty(_commitments.Get(voteKey)), "PV: already committed");

        _commitments.Set(voteKey, Convert.ToHexString(commitHash));

        Context.Emit(new VoteCommittedEvent
        {
            ProposalId = proposalId,
            Voter = Context.Caller,
        });
    }

    /// <summary>
    /// Reveal a committed vote (Mode 1). Must provide the original choice and salt.
    /// Weight is determined by caller's staking position.
    /// </summary>
    [BasaltEntrypoint]
    public void RevealVote(ulong proposalId, byte choiceIndex, byte[] salt, ulong poolId)
    {
        var key = proposalId.ToString();
        Context.Require(_proposalMode.Get(key) == 1, "PV: not commit-reveal mode");
        RequireRevealPhase(key);

        var voterHex = Convert.ToHexString(Context.Caller);
        var voteKey = key + ":" + voterHex;
        Context.Require(!string.IsNullOrEmpty(_commitments.Get(voteKey)), "PV: no commitment");
        Context.Require(!_revealed.Get(voteKey), "PV: already revealed");
        Context.Require(choiceIndex < _proposalChoiceCount.Get(key), "PV: invalid choice");

        // Verify commitment matches reveal
        // In production: hash(choiceIndex || salt || voterAddress) == stored commitment
        // Simplified for sketch
        var commitHex = _commitments.Get(voteKey);
        Context.Require(!string.IsNullOrEmpty(commitHex), "PV: commitment mismatch");

        // Get vote weight from staking
        var weight = Context.CallContract<UInt256>(
            _stakingPoolAddress, "GetDelegation", poolId, Context.Caller);
        var delegated = _delegatedWeight.Get(voterHex);
        var totalWeight = weight + delegated;
        Context.Require(!totalWeight.IsZero, "PV: no voting weight");

        _revealed.Set(voteKey, true);
        _revealedWeight.Set(voteKey, totalWeight);

        // Tally the vote
        var choiceKey = key + ":" + choiceIndex;
        var current = _choiceVotes.Get(choiceKey);
        _choiceVotes.Set(choiceKey, current + totalWeight);

        var voterCount = _choiceVoterCount.Get(choiceKey);
        _choiceVoterCount.Set(choiceKey, voterCount + 1);

        var totalWt = _totalVoteWeight.Get(key);
        _totalVoteWeight.Set(key, totalWt + totalWeight);

        var totalCount = _totalVoterCount.Get(key);
        _totalVoterCount.Set(key, totalCount + 1);

        Context.Emit(new VoteRevealedEvent
        {
            ProposalId = proposalId,
            Voter = Context.Caller,
            ChoiceIndex = choiceIndex,
            Weight = totalWeight,
        });
    }

    // ========================================================
    // ZK Private Voting (Mode 2)
    // ========================================================

    /// <summary>
    /// Submit a ZK private vote (Mode 2).
    /// The proof demonstrates:
    ///   1. Voter holds a valid eligibility credential (BST-VC)
    ///   2. The vote is for a valid choice index
    ///   3. The vote weight is correct (derived from token holdings)
    ///   4. The nullifier is correctly derived (prevents double-voting)
    ///
    /// The choice is encrypted and only revealed during tally via threshold decryption.
    /// </summary>
    [BasaltEntrypoint]
    public void SubmitZkVote(
        ulong proposalId, byte[] proofData, byte[] nullifier,
        byte[] encryptedVote, UInt256 provenWeight)
    {
        var key = proposalId.ToString();
        Context.Require(_proposalMode.Get(key) == 2, "PV: not ZK mode");
        RequireVotingPhase(key);

        Context.Require(proofData.Length > 0, "PV: proof required");
        Context.Require(nullifier.Length == 32, "PV: invalid nullifier");
        Context.Require(encryptedVote.Length > 0, "PV: encrypted vote required");

        // Check nullifier uniqueness
        var nullifierHex = Convert.ToHexString(nullifier);
        var nullKey = key + ":" + nullifierHex;
        Context.Require(!_zkNullifierUsed.Get(nullKey), "PV: nullifier already used");

        // In production: verify ZK proof against schema verification key
        // Public inputs: proposalId, nullifier, encryptedVote commitment, provenWeight

        // Mark nullifier as used
        _zkNullifierUsed.Set(nullKey, true);

        // Track total weight and voter count
        var totalWt = _totalVoteWeight.Get(key);
        _totalVoteWeight.Set(key, totalWt + provenWeight);

        var totalCount = _totalVoterCount.Get(key);
        _totalVoterCount.Set(key, totalCount + 1);

        Context.Emit(new ZkVoteSubmittedEvent
        {
            ProposalId = proposalId,
            Nullifier = nullifier,
            ProvenWeight = provenWeight,
        });
    }

    // ========================================================
    // Tally and Results
    // ========================================================

    /// <summary>
    /// Finalize the tally for a proposal. Can be called by anyone after reveal phase (Mode 1)
    /// or after voting end (Mode 2). Checks quorum.
    /// </summary>
    [BasaltEntrypoint]
    public void FinalizeTally(ulong proposalId)
    {
        var key = proposalId.ToString();
        var mode = _proposalMode.Get(key);
        var status = _proposalStatus.Get(key);
        Context.Require(status == "created", "PV: already finalized or cancelled");

        if (mode == 1)
        {
            Context.Require(Context.BlockTimestamp > _revealEnd.Get(key), "PV: reveal phase not ended");
        }
        else
        {
            Context.Require(Context.BlockTimestamp > _votingEnd.Get(key), "PV: voting not ended");
        }

        var totalWeight = _totalVoteWeight.Get(key);
        var quorum = _quorumThreshold.Get();

        if (totalWeight >= quorum)
        {
            _proposalStatus.Set(key, "tallied");
        }
        else
        {
            _proposalStatus.Set(key, "quorum_not_met");
        }

        Context.Emit(new TallyFinalizedEvent
        {
            ProposalId = proposalId,
            TotalWeight = totalWeight,
            TotalVoters = _totalVoterCount.Get(key),
            QuorumMet = totalWeight >= quorum,
        });
    }

    /// <summary>
    /// Cancel a proposal. Creator or admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void CancelProposal(ulong proposalId)
    {
        var key = proposalId.ToString();
        Context.Require(_proposalStatus.Get(key) == "created", "PV: cannot cancel");

        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(
            callerHex == _proposalCreator.Get(key) || callerHex == _admin.Get("admin"),
            "PV: not authorized");

        _proposalStatus.Set(key, "cancelled");

        Context.Emit(new ProposalCancelledEvent { ProposalId = proposalId });
    }

    /// <summary>
    /// Delegate voting power to another address. Single-hop only.
    /// </summary>
    [BasaltEntrypoint]
    public void DelegateVote(byte[] delegatee, ulong poolId)
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        var delegateeHex = Convert.ToHexString(delegatee);
        Context.Require(callerHex != delegateeHex, "PV: cannot delegate to self");

        // Remove existing delegation
        var existing = _delegations.Get(callerHex);
        if (!string.IsNullOrEmpty(existing))
        {
            var oldPower = _delegatedWeight.Get(existing);
            var weight = Context.CallContract<UInt256>(
                _stakingPoolAddress, "GetDelegation", poolId, Context.Caller);
            if (oldPower >= weight)
                _delegatedWeight.Set(existing, oldPower - weight);
        }

        // Set new delegation
        var stake = Context.CallContract<UInt256>(
            _stakingPoolAddress, "GetDelegation", poolId, Context.Caller);
        Context.Require(!stake.IsZero, "PV: no stake to delegate");

        _delegations.Set(callerHex, delegateeHex);
        var currentPower = _delegatedWeight.Get(delegateeHex);
        _delegatedWeight.Set(delegateeHex, currentPower + stake);
    }

    // ========================================================
    // Admin
    // ========================================================

    [BasaltEntrypoint]
    public void SetVoteSchema(byte[] schemaId)
    {
        RequireAdmin();
        _voteSchemaId.Set("schema", Convert.ToHexString(schemaId));
    }

    [BasaltEntrypoint]
    public void SetQuorumThreshold(UInt256 threshold)
    {
        RequireAdmin();
        _quorumThreshold.Set(threshold);
    }

    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        RequireAdmin();
        _admin.Set("admin", Convert.ToHexString(newAdmin));
    }

    // ========================================================
    // Views
    // ========================================================

    [BasaltView]
    public string GetProposalStatus(ulong proposalId)
        => _proposalStatus.Get(proposalId.ToString()) ?? "unknown";

    [BasaltView]
    public byte GetProposalMode(ulong proposalId)
        => _proposalMode.Get(proposalId.ToString());

    [BasaltView]
    public UInt256 GetChoiceVotes(ulong proposalId, byte choiceIndex)
        => _choiceVotes.Get(proposalId.ToString() + ":" + choiceIndex);

    [BasaltView]
    public ulong GetChoiceVoterCount(ulong proposalId, byte choiceIndex)
        => _choiceVoterCount.Get(proposalId.ToString() + ":" + choiceIndex);

    [BasaltView]
    public UInt256 GetTotalVoteWeight(ulong proposalId)
        => _totalVoteWeight.Get(proposalId.ToString());

    [BasaltView]
    public ulong GetTotalVoterCount(ulong proposalId)
        => _totalVoterCount.Get(proposalId.ToString());

    [BasaltView]
    public UInt256 GetQuorumThreshold() => _quorumThreshold.Get();

    [BasaltView]
    public bool IsNullifierUsed(ulong proposalId, byte[] nullifier)
        => _zkNullifierUsed.Get(proposalId.ToString() + ":" + Convert.ToHexString(nullifier));

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void RequireVotingPhase(string key)
    {
        Context.Require(Context.BlockTimestamp >= _votingStart.Get(key), "PV: voting not started");
        Context.Require(Context.BlockTimestamp <= _votingEnd.Get(key), "PV: voting ended");
    }

    private void RequireRevealPhase(string key)
    {
        Context.Require(Context.BlockTimestamp > _votingEnd.Get(key), "PV: voting not ended");
        Context.Require(Context.BlockTimestamp <= _revealEnd.Get(key), "PV: reveal phase ended");
    }

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "PV: not admin");
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class PrivateProposalCreatedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Creator { get; set; } = null!;
    public byte Mode { get; set; }
    public byte ChoiceCount { get; set; }
    public long VotingEnd { get; set; }
}

[BasaltEvent]
public class VoteCommittedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Voter { get; set; } = null!;
}

[BasaltEvent]
public class VoteRevealedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    [Indexed] public byte[] Voter { get; set; } = null!;
    public byte ChoiceIndex { get; set; }
    public UInt256 Weight { get; set; }
}

[BasaltEvent]
public class ZkVoteSubmittedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public byte[] Nullifier { get; set; } = null!;
    public UInt256 ProvenWeight { get; set; }
}

[BasaltEvent]
public class TallyFinalizedEvent
{
    [Indexed] public ulong ProposalId { get; set; }
    public UInt256 TotalWeight { get; set; }
    public ulong TotalVoters { get; set; }
    public bool QuorumMet { get; set; }
}

[BasaltEvent]
public class ProposalCancelledEvent
{
    [Indexed] public ulong ProposalId { get; set; }
}
```

## Complexity

**High** -- The contract supports two distinct voting modes with different security properties and gas profiles. The commit-reveal mode is relatively straightforward but requires careful handling of the commitment scheme to prevent grinding attacks. The ZK mode requires a sophisticated circuit that proves credential possession, vote validity, and weight correctness simultaneously. Tally computation for ZK mode with encrypted votes requires threshold decryption or homomorphic aggregation, adding significant cryptographic complexity. Delegation, quorum enforcement, and multi-choice support further increase the design surface.

## Priority

**P1** -- Privacy-preserving voting directly addresses one of the most discussed limitations of on-chain governance across all blockchain ecosystems. Basalt's existing Governance contract (0x...1002) provides the foundation, and adding a private voting mode demonstrates a natural evolution of the governance system. The commit-reveal mode can be implemented relatively quickly as a stepping stone to the full ZK mode.
