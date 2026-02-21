# Bounty Board

## Category

Labor Market / Task Coordination

## Summary

A decentralized bounty board contract where task posters create bounties with BST rewards held in the Escrow system contract, workers submit proofs of completion, and designated reviewers (the poster or a third-party arbitrator) approve or reject submissions to trigger fund release. The system includes dispute resolution via governance escalation, skill-based matching through on-chain tags, reputation building through completed bounty history, and multi-reviewer consensus for high-value bounties.

## Why It's Useful

- **Market need**: Open-source development, bug bounties, content creation, and freelance tasks require trustless coordination between task posters and workers. Without escrow-backed bounties, workers risk non-payment and posters risk paying for incomplete work.
- **User benefit**: Workers see guaranteed funds locked in escrow before starting work. Posters receive verified deliverables before funds are released. Both parties are protected by the on-chain arbitration mechanism.
- **Ecosystem development**: A bounty board accelerates ecosystem development by enabling anyone to fund tasks (documentation, code contributions, testing, design) with trustless payment guarantees.
- **Reputation building**: Completed bounties build on-chain reputation that is portable across the ecosystem. Workers with strong track records can command higher rates and access premium bounties.
- **Composability**: Other contracts and DAOs can programmatically create bounties, enabling automated task creation based on on-chain events (e.g., automatically creating a bounty when a governance proposal is approved).

## Key Features

- **Bounty creation**: Post tasks with title, description, deliverable requirements, reward amount, deadline, and skill tags. Reward is locked in Escrow upon creation.
- **Skill tags**: Bounties are tagged with required skills (e.g., "solidity", "design", "documentation") for discovery and matching.
- **Application system**: Workers apply to bounties with a proposal and estimated timeline. Posters select from applicants.
- **Proof of completion**: Workers submit deliverables with description and evidence hash (BLAKE3 of the actual deliverable stored off-chain).
- **Multi-submission support**: Multiple workers can submit competing solutions. Poster selects the best submission.
- **Review and approval**: Poster reviews submissions and approves or requests revisions. Approval triggers fund release from escrow.
- **Arbitration**: Disputed bounties are escalated to staked arbitrators who review evidence and make binding decisions. Arbitrators are compensated from a small dispute fee.
- **Governance escalation**: If arbitration fails or is contested, disputes can be escalated to the Governance contract for community resolution.
- **Reputation system**: Workers and posters build on-chain reputation scores based on completed bounties, timeliness, and dispute outcomes.
- **Bounty tiers**: Complexity tiers (Beginner, Intermediate, Advanced, Expert) with different default review periods and arbitration thresholds.
- **Collaborative bounties**: Multi-worker bounties where the reward is split among approved contributors based on poster-defined ratios.
- **Recurring bounties**: Templates for regularly posted tasks (e.g., weekly community call notes, monthly security audits).

## Basalt-Specific Advantages

- **Escrow system contract**: All bounty rewards are held in Basalt's built-in Escrow contract (0x...1004), providing trustless fund custody without custom escrow implementation.
- **BNS identity**: Posters and workers are identified by BNS names, creating a professional identity layer for the labor market.
- **ZK skill verification**: Workers can prove professional qualifications (BST-VC credentials) without revealing their real-world identity, using Basalt's ZK compliance layer. A developer can prove they hold a certain certification without revealing their name.
- **Governance arbitration**: Basalt's Governance contract (0x...1003) provides a decentralized final-appeal mechanism for disputed bounties, ensuring no single party can unilaterally decide outcomes.
- **Achievement integration**: Completing bounties earns on-chain achievements (contract 55), building a portable reputation that benefits the worker across all ecosystem interactions.
- **AOT-compiled matching**: Skill-based bounty discovery and reputation score calculations execute at native speed, enabling efficient matching even with large numbers of bounties and workers.
- **Ed25519 deliverable signing**: Workers sign their submission evidence with Ed25519, creating verifiable proof of authorship that cannot be repudiated.
- **StakingPool for arbitrators**: Arbitrators must stake BST through StakingPool (0x...1005) to be eligible, providing economic security against malicious or negligent arbitration.

## Token Standards Used

- **BST-20**: Bounty reward payments in BST or other BST-20 tokens.
- **BST-721**: Optional soulbound reputation tokens for bounty completion milestones.
- **BST-VC**: Professional credential verification for skill-gated bounties.

## Integration Points

- **Escrow (0x...1004)**: All bounty rewards held in escrow. Conditional release upon approval. Automatic refund on expiry or cancellation.
- **BNS (0x...1002)**: Identity resolution for posters, workers, and arbitrators.
- **Governance (0x...1003)**: Final dispute resolution mechanism. Community governance over platform parameters.
- **StakingPool (0x...1005)**: Arbitrator staking requirement. Slashing for malicious arbitration.
- **SchemaRegistry (0x...1006)**: Credential schemas for professional skill verification.
- **IssuerRegistry (0x...1007)**: Trusted issuers for skill credentials.
- **Achievement System (contract 55)**: Achievement awards for bounty completion milestones.
- **Social Profile (contract 57)**: Reputation scores and bounty history displayed on profiles.
- **Freelance Platform (contract 64)**: Shared reputation system with the freelance platform.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum BountyStatus : byte
{
    Open = 0,
    InProgress = 1,
    UnderReview = 2,
    Disputed = 3,
    Completed = 4,
    Cancelled = 5,
    Expired = 6
}

public enum BountyTier : byte
{
    Beginner = 0,
    Intermediate = 1,
    Advanced = 2,
    Expert = 3
}

public struct Bounty
{
    public ulong BountyId;
    public Address Poster;
    public string Title;
    public string Description;
    public byte[] RequirementsHash;           // BLAKE3 hash of detailed requirements
    public UInt256 Reward;
    public ulong Deadline;
    public BountyTier Tier;
    public BountyStatus Status;
    public ulong MaxApplicants;
    public ulong CurrentApplicants;
    public Address AssignedWorker;            // Address.Zero if open
    public ulong CreatedAt;
    public ulong ReviewPeriodSeconds;         // Time poster has to review submissions
    public bool IsCollaborative;
}

public struct SkillTag
{
    public ulong BountyId;
    public string Tag;
}

public struct Application
{
    public ulong ApplicationId;
    public ulong BountyId;
    public Address Applicant;
    public string Proposal;
    public ulong EstimatedDays;
    public ulong Timestamp;
    public bool Selected;
}

public struct Submission
{
    public ulong SubmissionId;
    public ulong BountyId;
    public Address Worker;
    public string Description;
    public byte[] EvidenceHash;               // BLAKE3 hash of deliverable
    public byte[] WorkerSignature;            // Ed25519 signature over evidence
    public ulong Timestamp;
    public bool Approved;
    public bool Rejected;
    public string RevisionNotes;
}

public struct DisputeRecord
{
    public ulong DisputeId;
    public ulong BountyId;
    public Address Initiator;
    public string Reason;
    public Address Arbitrator;
    public bool Resolved;
    public bool WorkerFavored;
    public ulong ResolvedAt;
}

public struct ReputationRecord
{
    public Address User;
    public ulong CompletedBounties;
    public ulong PostedBounties;
    public ulong DisputesWon;
    public ulong DisputesLost;
    public ulong OnTimeCompletions;
    public ulong LateCompletions;
    public ulong ReputationScore;             // Computed score
}

public struct CollaboratorShare
{
    public Address Worker;
    public ulong ShareBps;                    // Basis points of reward
}

// ---- Contract API ----

[SdkContract(TypeId = 0x020A)]
public partial class BountyBoard : SdkContractBase
{
    // Storage
    private StorageMap<ulong, Bounty> _bounties;
    private StorageMap<ulong, Application> _applications;
    private StorageMap<ulong, Submission> _submissions;
    private StorageMap<ulong, DisputeRecord> _disputes;
    private StorageMap<Address, ReputationRecord> _reputations;
    private StorageValue<ulong> _nextBountyId;
    private StorageValue<ulong> _nextApplicationId;
    private StorageValue<ulong> _nextSubmissionId;
    private StorageValue<ulong> _nextDisputeId;
    private StorageValue<ulong> _platformFeeBps;          // Default 250 = 2.5%
    private StorageValue<ulong> _disputeFeeBps;           // Default 500 = 5%
    private StorageValue<Address> _treasury;
    private StorageValue<UInt256> _minArbitratorStake;

    // Composite key storage:
    // SkillTag keyed by BLAKE3(bountyId || tagIndex)
    // CollaboratorShare keyed by BLAKE3(bountyId || workerAddress)
    // Arbitrator eligibility keyed by arbitrator address -> bool

    // --- Bounty Management ---

    /// <summary>
    /// Create a bounty. Caller sends reward amount as tx value,
    /// which is deposited into the Escrow system contract.
    /// </summary>
    public ulong CreateBounty(
        string title,
        string description,
        byte[] requirementsHash,
        ulong deadline,
        BountyTier tier,
        ulong maxApplicants,
        ulong reviewPeriodSeconds,
        string[] skillTags,
        bool isCollaborative
    );

    /// <summary>
    /// Increase the bounty reward. Additional BST sent as tx value
    /// and added to escrow.
    /// </summary>
    public void IncreaseReward(ulong bountyId);

    /// <summary>
    /// Cancel an open bounty (no worker assigned yet).
    /// Full reward refunded from escrow.
    /// </summary>
    public void CancelBounty(ulong bountyId);

    /// <summary>
    /// Extend the deadline for a bounty.
    /// Only callable by poster.
    /// </summary>
    public void ExtendDeadline(ulong bountyId, ulong newDeadline);

    // --- Application Process ---

    /// <summary>
    /// Apply to work on a bounty. Worker submits a proposal
    /// and estimated timeline.
    /// </summary>
    public ulong ApplyToBounty(
        ulong bountyId,
        string proposal,
        ulong estimatedDays
    );

    /// <summary>
    /// Select an applicant to work on the bounty.
    /// Only callable by poster. Sets bounty status to InProgress.
    /// </summary>
    public void SelectApplicant(ulong bountyId, ulong applicationId);

    /// <summary>
    /// For collaborative bounties, set the reward split ratios.
    /// Only callable by poster. Shares must sum to 10000 bps.
    /// </summary>
    public void SetCollaboratorShares(
        ulong bountyId,
        Address[] workers,
        ulong[] shareBps
    );

    // --- Submission and Review ---

    /// <summary>
    /// Submit a deliverable for review. Worker provides description,
    /// evidence hash, and Ed25519 signature over the evidence.
    /// </summary>
    public ulong SubmitDeliverable(
        ulong bountyId,
        string description,
        byte[] evidenceHash,
        byte[] workerSignature
    );

    /// <summary>
    /// Approve a submission. Triggers fund release from escrow to worker
    /// (minus platform fee). Updates reputation scores.
    /// </summary>
    public void ApproveSubmission(ulong submissionId);

    /// <summary>
    /// Request revisions on a submission with feedback notes.
    /// Worker can resubmit within the original deadline.
    /// </summary>
    public void RequestRevision(ulong submissionId, string revisionNotes);

    /// <summary>
    /// Reject a submission. Worker can dispute the rejection.
    /// </summary>
    public void RejectSubmission(ulong submissionId, string reason);

    // --- Disputes ---

    /// <summary>
    /// Initiate a dispute on a bounty. Either poster or worker
    /// can initiate. A dispute fee is deducted from the initiator
    /// (refunded if they win).
    /// </summary>
    public ulong InitiateDispute(ulong bountyId, string reason);

    /// <summary>
    /// Claim a dispute as arbitrator. Must be a registered arbitrator
    /// with sufficient stake in StakingPool.
    /// </summary>
    public void ClaimDispute(ulong disputeId);

    /// <summary>
    /// Resolve a dispute as the assigned arbitrator.
    /// If worker favored: funds released to worker.
    /// If poster favored: funds returned to poster.
    /// Dispute fee awarded to the winning party.
    /// </summary>
    public void ResolveDispute(ulong disputeId, bool favorWorker, string reasoning);

    /// <summary>
    /// Escalate a dispute to governance if either party disagrees
    /// with the arbitrator's decision.
    /// </summary>
    public void EscalateToGovernance(ulong disputeId);

    // --- Arbitrator Management ---

    /// <summary>
    /// Register as an arbitrator. Must have minimum stake in StakingPool.
    /// </summary>
    public void RegisterArbitrator();

    /// <summary>
    /// Deregister as an arbitrator.
    /// Cannot deregister with active disputes.
    /// </summary>
    public void DeregisterArbitrator();

    // --- Reputation ---

    /// <summary>
    /// Get the reputation record for a user.
    /// Score formula: completions * 100 + onTime * 20 + disputesWon * 10
    ///              - disputesLost * 30 - lateCompletions * 15
    /// </summary>
    public ReputationRecord GetReputation(Address user);

    // --- Auto-completion ---

    /// <summary>
    /// Auto-complete a bounty if the review period has expired
    /// without the poster reviewing the submission.
    /// Funds are released to the worker. Callable by anyone.
    /// </summary>
    public void AutoComplete(ulong bountyId);

    /// <summary>
    /// Auto-expire a bounty that has passed its deadline without
    /// any submission. Funds returned to poster.
    /// </summary>
    public void AutoExpire(ulong bountyId);

    // --- View Functions ---

    public Bounty GetBounty(ulong bountyId);
    public Application GetApplication(ulong applicationId);
    public Submission GetSubmission(ulong submissionId);
    public DisputeRecord GetDispute(ulong disputeId);
    public ulong GetActiveBountyCount();
    public bool IsArbitrator(Address addr);

    /// <summary>
    /// Search bounties by skill tag. Returns matching bounty IDs.
    /// </summary>
    public ulong[] SearchBySkill(string skillTag);

    /// <summary>
    /// Get all bounties posted by an address.
    /// </summary>
    public ulong[] GetPostedBounties(Address poster);

    /// <summary>
    /// Get all bounties a worker has completed.
    /// </summary>
    public ulong[] GetCompletedBounties(Address worker);

    // --- Admin (Governance) ---

    public void SetPlatformFee(ulong basisPoints);
    public void SetDisputeFee(ulong basisPoints);
    public void SetMinArbitratorStake(UInt256 minimumStake);
    public void SetTreasury(Address newTreasury);

    /// <summary>
    /// Governance resolution of an escalated dispute.
    /// Only callable by governance contract.
    /// </summary>
    public void GovernanceResolveDispute(ulong disputeId, bool favorWorker);
}
```

## Complexity

**High** -- The contract manages a complex state machine for bounty lifecycle (Open -> InProgress -> UnderReview -> Completed/Disputed), with branching paths for disputes, escalations, auto-completion, and expiry. The arbitration system with staked arbitrators adds another participant role with its own incentive structure. Collaborative bounties with multi-worker reward splitting, reputation score computation across multiple dimensions, and cross-contract interactions (Escrow, StakingPool, Governance) create significant integration complexity.

## Priority

**P1** -- A bounty board is essential infrastructure for ecosystem development. It enables permissionless task coordination that funds documentation, code contributions, security audits, and community work. The contract is immediately useful upon deployment and becomes more valuable as the ecosystem grows, making it a high-priority early deployment.
