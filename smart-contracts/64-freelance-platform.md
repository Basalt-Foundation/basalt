# Decentralized Freelance Platform

## Category

Labor Market / Professional Services

## Summary

A comprehensive decentralized freelance platform that enables job posting, bidding, and milestone-based payment through the Escrow system contract. The platform features dispute arbitration by staked arbitrators, reputation building through soulbound tokens, ZK proof of professional skills via BST-VC credentials without revealing real-world identity, and cross-border payments with automatic currency-agnostic settlement. It serves as the full-service labor marketplace for the Basalt ecosystem.

## Why It's Useful

- **Market need**: Centralized freelance platforms (Upwork, Fiverr) charge 10-20% fees, restrict payment methods, impose geographic limitations, and control worker-client relationships. A decentralized alternative offers lower fees, global access, and trustless payment guarantees.
- **User benefit**: Freelancers receive guaranteed payment through escrow milestones -- work is never done "for free." Clients receive verified deliverables before payment is released. Both parties maintain portable reputation.
- **Cost reduction**: Platform fees of 2-5% versus 10-20% on centralized platforms. No payment processing delays or currency conversion fees. Direct peer-to-peer settlement.
- **Privacy and autonomy**: Freelancers can prove their skills and qualifications through BST-VC credentials without revealing their real-world identity, enabling anonymous professional work.
- **Global access**: Cross-border payments settle instantly in BST, bypassing traditional banking restrictions that prevent many freelancers in developing countries from accessing global markets.
- **Reputation portability**: Soulbound reputation tokens travel with the freelancer. Unlike centralized platform ratings that are lost when switching platforms, on-chain reputation is permanent and universal.

## Key Features

- **Job posting**: Clients post jobs with title, description, required skills, budget range, timeline, and deliverable specifications.
- **Bidding system**: Freelancers submit bids with proposed price, timeline, and approach. Clients review bids and select a freelancer.
- **Milestone-based payment**: Jobs are broken into milestones with individual budgets. Payment is released milestone-by-milestone as deliverables are approved.
- **Escrow management**: All job funds are held in the Escrow system contract. Clients deposit the full job budget upfront, ensuring funds are available for the entire project.
- **Dispute arbitration**: Staked arbitrators resolve disputes between clients and freelancers. Arbitrators must stake BST through StakingPool and earn fees for fair arbitration. Malicious arbitrators are slashed.
- **ZK credential verification**: Freelancers prove skills, certifications, and professional qualifications through BST-VC credentials verified via the ZK compliance layer, without exposing personal identity.
- **Soulbound reputation**: Completed jobs mint soulbound BST-721 reputation tokens. Tokens encode job category, client satisfaction rating, on-time delivery, and budget adherence.
- **Skill-based matching**: Jobs and freelancers are tagged with skills. The contract supports skill-based search and matching.
- **Encrypted communication**: Client-freelancer communication is enabled through the Messaging Registry (contract 62) for confidential project discussions.
- **Multi-milestone templates**: Common project structures (web development, design, content writing) have milestone templates for faster job setup.
- **Team projects**: Support for multi-freelancer jobs where different team members handle different milestones.
- **Client verification**: Clients can verify their identity or business through BST-VC credentials, building trust with freelancers.
- **Automated milestone acceptance**: If a client does not review a milestone within the review window, funds are automatically released.

## Basalt-Specific Advantages

- **Escrow system contract (0x...1004)**: Basalt's built-in Escrow provides trustless milestone payment management. Funds are locked at job creation and released per milestone, ensuring freelancers are guaranteed payment for approved work.
- **ZK compliance for anonymous professional work**: Basalt's ZK compliance layer enables freelancers to prove professional qualifications (university degrees, certifications, work history) through BST-VC credentials verified by ZK proofs. A developer can prove they have 5 years of experience without revealing their name or employer.
- **BST-VC professional credentials**: Verifiable credentials from trusted issuers (educational institutions, professional organizations, certification bodies) are verified through SchemaRegistry and IssuerRegistry, providing trustworthy skill verification.
- **BNS professional identity**: Freelancers build their professional brand through BNS names (e.g., "alexdev.basalt"), creating memorable, portable identities for their freelance business.
- **Ed25519 deliverable signing**: Freelancers sign milestone deliverables with Ed25519, creating verifiable proof of authorship and submission timing that cannot be disputed.
- **Cross-border via BridgeETH**: Clients from Ethereum can fund jobs using bridged assets through BridgeETH (0x...1008), expanding the client pool beyond native Basalt users.
- **Soulbound reputation via Achievement System**: Reputation tokens are soulbound BST-721 (contract 55), ensuring reputation cannot be bought, sold, or transferred. This creates authentic, earned reputation.
- **AOT-compiled milestone processing**: Milestone verification, escrow interactions, and reputation updates execute at native speed, keeping transaction costs low for the frequent interactions that active freelance projects generate.
- **Governance dispute resolution**: Final dispute appeals go to the Governance contract (0x...1003), providing a decentralized supreme court for complex labor disputes.

## Token Standards Used

- **BST-20**: Payment for jobs, milestones, and arbitration fees.
- **BST-721**: Soulbound reputation tokens for completed jobs and client/freelancer reviews.
- **BST-VC**: Professional credentials (degrees, certifications, work history) for ZK skill verification.

## Integration Points

- **Escrow (0x...1004)**: Milestone payment management. Full job budget deposited at creation, released per milestone upon approval.
- **BNS (0x...1002)**: Professional identity for freelancers and clients.
- **Governance (0x...1003)**: Final dispute resolution. Platform parameter governance.
- **StakingPool (0x...1005)**: Arbitrator staking and slashing for dispute resolution integrity.
- **SchemaRegistry (0x...1006)**: Professional credential schemas for skill verification.
- **IssuerRegistry (0x...1007)**: Trusted issuers for professional credentials.
- **BridgeETH (0x...1008)**: Cross-chain job funding from Ethereum.
- **Achievement System (contract 55)**: Soulbound reputation tokens for job completion milestones.
- **Social Profile (contract 57)**: Professional profile display with reputation, skills, and credentials.
- **Messaging Registry (contract 62)**: Encrypted client-freelancer communication.
- **Bounty Board (contract 61)**: Shared reputation system. Bounty completions contribute to freelance reputation.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum JobStatus : byte
{
    Open = 0,
    InProgress = 1,
    UnderReview = 2,
    Completed = 3,
    Disputed = 4,
    Cancelled = 5
}

public enum MilestoneStatus : byte
{
    Pending = 0,
    InProgress = 1,
    Submitted = 2,
    Approved = 3,
    Rejected = 4,
    Disputed = 5
}

public struct Job
{
    public ulong JobId;
    public Address Client;
    public string Title;
    public string Description;
    public byte[] SpecificationHash;           // BLAKE3 hash of detailed spec
    public UInt256 TotalBudget;
    public ulong Deadline;
    public JobStatus Status;
    public ulong MilestoneCount;
    public ulong BidCount;
    public Address SelectedFreelancer;
    public ulong CreatedAt;
    public ulong ReviewWindowSeconds;          // Default 7 days per milestone
    public bool IsTeamProject;
}

public struct JobBid
{
    public ulong BidId;
    public ulong JobId;
    public Address Freelancer;
    public UInt256 ProposedPrice;
    public ulong ProposedTimeline;             // Days
    public string Approach;
    public byte[] CredentialProof;             // Optional ZK credential proof
    public ulong Timestamp;
    public bool Selected;
}

public struct JobMilestone
{
    public ulong MilestoneId;
    public ulong JobId;
    public string Title;
    public string Description;
    public UInt256 Payment;
    public ulong Deadline;
    public MilestoneStatus Status;
    public Address AssignedTo;                 // For team projects
    public byte[] DeliverableHash;
    public byte[] DeliverableSignature;        // Ed25519 by freelancer
    public ulong SubmittedAt;
    public ulong ApprovedAt;
}

public struct FreelancerProfile
{
    public Address Freelancer;
    public string Title;                       // e.g., "Full Stack Developer"
    public string Description;
    public ulong CompletedJobs;
    public UInt256 TotalEarned;
    public ulong AverageRating;               // Scaled by 100
    public ulong OnTimeRate;                  // Percentage
    public ulong DisputeWinRate;              // Percentage
    public ulong ReputationScore;
}

public struct ClientProfile
{
    public Address Client;
    public ulong JobsPosted;
    public UInt256 TotalSpent;
    public ulong AverageRating;               // Scaled by 100
    public ulong DisputeRate;                 // Percentage
    public ulong ReputationScore;
}

public struct Review
{
    public ulong ReviewId;
    public ulong JobId;
    public Address Reviewer;
    public Address Reviewed;
    public ulong Rating;                      // 1-5
    public string Comment;
    public bool IsClientReview;               // True if client reviewing freelancer
    public ulong Timestamp;
}

public struct ArbitrationCase
{
    public ulong CaseId;
    public ulong JobId;
    public ulong MilestoneId;
    public Address Initiator;
    public string Claim;
    public byte[] EvidenceHash;
    public Address Arbitrator;
    public bool Resolved;
    public bool FreelancerFavored;
    public string Decision;
    public UInt256 ArbitrationFee;
    public ulong ResolvedAt;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x020D)]
public partial class FreelancePlatform : SdkContractBase
{
    // Storage
    private StorageMap<ulong, Job> _jobs;
    private StorageMap<ulong, JobBid> _bids;
    private StorageMap<ulong, JobMilestone> _milestones;
    private StorageMap<Address, FreelancerProfile> _freelancerProfiles;
    private StorageMap<Address, ClientProfile> _clientProfiles;
    private StorageMap<ulong, Review> _reviews;
    private StorageMap<ulong, ArbitrationCase> _arbitrations;
    private StorageValue<ulong> _nextJobId;
    private StorageValue<ulong> _nextBidId;
    private StorageValue<ulong> _nextMilestoneId;
    private StorageValue<ulong> _nextReviewId;
    private StorageValue<ulong> _nextCaseId;
    private StorageValue<ulong> _platformFeeBps;          // Default 300 = 3%
    private StorageValue<ulong> _arbitrationFeeBps;       // Default 500 = 5%
    private StorageValue<UInt256> _minArbitratorStake;
    private StorageValue<Address> _treasury;

    // --- Freelancer Profile ---

    /// <summary>
    /// Register as a freelancer with professional profile.
    /// </summary>
    public void RegisterFreelancer(
        string title,
        string description,
        string[] skills
    );

    /// <summary>
    /// Attach a ZK credential proof to the freelancer profile.
    /// Verifies the credential through SchemaRegistry and IssuerRegistry
    /// without revealing the underlying identity.
    /// </summary>
    public void AttachCredential(
        byte[] credentialProof,
        string credentialType
    );

    /// <summary>
    /// Update freelancer profile.
    /// </summary>
    public void UpdateFreelancerProfile(string title, string description);

    // --- Job Management ---

    /// <summary>
    /// Post a job. Client sends the full budget as tx value,
    /// which is deposited into Escrow.
    /// </summary>
    public ulong PostJob(
        string title,
        string description,
        byte[] specificationHash,
        ulong deadline,
        ulong reviewWindowSeconds,
        bool isTeamProject
    );

    /// <summary>
    /// Define milestones for a job. Must be done before selecting
    /// a freelancer. Milestone payments must sum to the total budget.
    /// </summary>
    public ulong AddMilestone(
        ulong jobId,
        string title,
        string description,
        UInt256 payment,
        ulong deadline
    );

    /// <summary>
    /// Cancel a job before a freelancer is selected.
    /// Full budget refunded from escrow.
    /// </summary>
    public void CancelJob(ulong jobId);

    // --- Bidding ---

    /// <summary>
    /// Submit a bid on a job. Freelancer provides proposed price,
    /// timeline, and approach. Optional ZK credential proof.
    /// </summary>
    public ulong SubmitBid(
        ulong jobId,
        UInt256 proposedPrice,
        ulong proposedTimeline,
        string approach,
        byte[] credentialProof
    );

    /// <summary>
    /// Select a freelancer's bid. Sets the job to InProgress.
    /// If the bid price is less than the budget, the difference
    /// is refunded to the client from escrow.
    /// </summary>
    public void SelectBid(ulong jobId, ulong bidId);

    // --- Milestone Delivery ---

    /// <summary>
    /// Submit a milestone deliverable. Freelancer provides the
    /// deliverable hash and Ed25519 signature as proof of authorship.
    /// </summary>
    public void SubmitMilestone(
        ulong milestoneId,
        byte[] deliverableHash,
        byte[] deliverableSignature
    );

    /// <summary>
    /// Approve a milestone. Releases the milestone payment from
    /// escrow to the freelancer (minus platform fee).
    /// </summary>
    public void ApproveMilestone(ulong milestoneId);

    /// <summary>
    /// Request revisions on a milestone with feedback.
    /// </summary>
    public void RequestMilestoneRevision(ulong milestoneId, string feedback);

    /// <summary>
    /// Reject a milestone with reason. Freelancer can dispute.
    /// </summary>
    public void RejectMilestone(ulong milestoneId, string reason);

    /// <summary>
    /// Auto-approve a milestone if the client does not review
    /// within the review window. Callable by anyone.
    /// </summary>
    public void AutoApproveMilestone(ulong milestoneId);

    // --- Job Completion ---

    /// <summary>
    /// Complete a job after all milestones are approved.
    /// Triggers mutual review period and reputation token minting.
    /// </summary>
    public void CompleteJob(ulong jobId);

    // --- Reviews ---

    /// <summary>
    /// Submit a review (client reviews freelancer or vice versa).
    /// Only available after job completion. Rating 1-5.
    /// </summary>
    public ulong SubmitReview(
        ulong jobId,
        ulong rating,
        string comment
    );

    // --- Dispute Resolution ---

    /// <summary>
    /// Initiate a dispute on a milestone.
    /// Dispute fee deducted from initiator (refunded if they win).
    /// </summary>
    public ulong InitiateDispute(
        ulong milestoneId,
        string claim,
        byte[] evidenceHash
    );

    /// <summary>
    /// Counter-evidence submission by the other party.
    /// </summary>
    public void SubmitCounterEvidence(
        ulong caseId,
        byte[] counterEvidenceHash,
        string response
    );

    /// <summary>
    /// Claim a dispute as an arbitrator. Must have sufficient
    /// stake in StakingPool.
    /// </summary>
    public void ClaimArbitration(ulong caseId);

    /// <summary>
    /// Resolve a dispute as arbitrator. Decide whether to release
    /// milestone funds to freelancer or refund to client.
    /// </summary>
    public void ResolveArbitration(
        ulong caseId,
        bool favorFreelancer,
        string decision
    );

    /// <summary>
    /// Escalate to governance for final appeal.
    /// </summary>
    public void EscalateToGovernance(ulong caseId);

    // --- Team Projects ---

    /// <summary>
    /// Assign a team member to a specific milestone.
    /// Only for team projects. Client assigns based on bids.
    /// </summary>
    public void AssignMilestoneWorker(ulong milestoneId, Address worker);

    // --- View Functions ---

    public Job GetJob(ulong jobId);
    public JobBid GetBid(ulong bidId);
    public JobMilestone GetMilestone(ulong milestoneId);
    public FreelancerProfile GetFreelancerProfile(Address freelancer);
    public ClientProfile GetClientProfile(Address client);
    public Review GetReview(ulong reviewId);
    public ArbitrationCase GetArbitrationCase(ulong caseId);
    public ulong GetActiveJobCount();
    public bool IsArbitrator(Address addr);

    /// <summary>
    /// Verify a freelancer's ZK credential without revealing identity.
    /// Returns true if the credential proof is valid.
    /// </summary>
    public bool VerifyCredential(Address freelancer, string credentialType);

    // --- Admin (Governance) ---

    public void SetPlatformFee(ulong basisPoints);
    public void SetArbitrationFee(ulong basisPoints);
    public void SetMinArbitratorStake(UInt256 minimumStake);
    public void SetTreasury(Address newTreasury);

    /// <summary>
    /// Governance resolution of an escalated dispute.
    /// </summary>
    public void GovernanceResolveDispute(ulong caseId, bool favorFreelancer);
}
```

## Complexity

**High** -- The contract manages the complete lifecycle of freelance engagements: job posting, multi-bid evaluation, freelancer selection, multi-milestone project execution, deliverable review with revision cycles, mutual reputation reviews, and multi-tier dispute resolution (direct negotiation, arbitration, governance appeal). Team projects add multi-freelancer coordination. ZK credential verification requires cross-contract calls. The interaction between escrow management, milestone state machine, dispute resolution, and reputation system creates significant state management complexity.

## Priority

**P1** -- Decentralized freelancing is a high-impact application that addresses a real market need (high platform fees, geographic restrictions, payment delays). The contract drives sustained economic activity through ongoing job creation and completion. It is a showcase application for Basalt's unique combination of escrow, ZK credentials, and cross-border payment capabilities.
