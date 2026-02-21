# Crowdfunding Platform

## Category

Project Finance / Community Funding

## Summary

A decentralized crowdfunding contract where projects create campaigns with funding goals and deadlines, contributors deposit BST through the Escrow system contract, and funds are released to the project only if the goal is met by the deadline. Failed campaigns automatically refund all contributors. The platform supports milestone-based fund release for phased projects, BST-721 backer reward tokens for contributors, and configurable campaign parameters for flexible fundraising strategies.

## Why It's Useful

- **Market need**: Centralized crowdfunding platforms charge high fees (5-10% platform fees plus payment processing), impose content restrictions, and can freeze funds arbitrarily. A decentralized alternative provides censorship-resistant fundraising with lower fees and trustless fund management.
- **User benefit**: Contributors have guaranteed refunds if campaigns fail -- funds are held in escrow and released only upon goal achievement. Project creators receive funds without intermediary delays or deplatforming risk.
- **Trust mechanism**: Milestone-based release gives contributors ongoing oversight. Funds are released in tranches as the project delivers on promises, reducing the risk of fraud or project abandonment.
- **Community building**: BST-721 backer rewards create a lasting bond between projects and their supporters. Early backers receive verifiable proof of their support that may grant future benefits.
- **Governance integration**: Community governance can intervene in dispute scenarios, providing a decentralized arbitration mechanism for contested milestone completions.

## Key Features

- **Campaign creation**: Projects define a funding goal, deadline, description, and reward tiers. Campaigns are publicly listed on-chain.
- **Escrow-backed deposits**: All contributor deposits are held in the Escrow system contract. No funds touch the project address until release conditions are met.
- **All-or-nothing funding**: If the goal is not met by the deadline, all contributors are automatically eligible for refund.
- **Flexible funding option**: Projects can optionally accept partial funding (keep whatever is raised, regardless of goal).
- **Milestone-based release**: Projects can define milestones with percentage-based fund release. Milestone completion can be verified by contributors through a voting mechanism or by governance.
- **BST-721 backer rewards**: Contributors receive BST-721 tokens representing their backing level. Reward tiers correspond to contribution amounts with different reward metadata.
- **Early bird bonuses**: First N contributors or contributions within a time window receive bonus rewards or lower pricing.
- **Stretch goals**: Additional funding targets beyond the initial goal that unlock new project features or rewards.
- **Campaign updates**: Project creators can post on-chain updates to keep backers informed.
- **Refund claims**: Failed campaign contributors claim refunds individually. Partial refunds available for flexible-funding campaigns that underperform.
- **Platform fee**: Configurable fee (default 3%) charged on successfully funded campaigns.
- **Campaign categories**: Classification system for discovery (Technology, Art, Community, Infrastructure, Research).

## Basalt-Specific Advantages

- **Escrow system contract integration**: Basalt's built-in Escrow contract (0x...1004) provides battle-tested fund custody. Campaign deposits do not rely on custom escrow logic, inheriting the security guarantees of a core system contract.
- **BST-721 backer tokens**: Backer rewards are standard BST-721 tokens, tradeable on the NFT Marketplace (contract 51). Early backer tokens can appreciate in value, creating a secondary market for crowdfunding participation.
- **BNS campaign naming**: Campaigns can be linked to BNS names (e.g., "myproject.fund.basalt") for human-readable fundraising URLs.
- **ZK compliance for regulated fundraising**: Security token offerings or regulated fundraising can require BST-VC credentials from contributors, enforcing accredited investor verification through the ZK compliance layer without exposing personal data.
- **Governance arbitration**: Disputed milestones can be escalated to Basalt's Governance contract (0x...1003) for community-driven resolution, providing a decentralized dispute mechanism.
- **BridgeETH cross-chain funding**: Contributors can fund campaigns using bridged assets from Ethereum via BridgeETH (0x...1008), expanding the pool of potential backers.
- **AOT-compiled refund processing**: Batch refund processing for failed campaigns executes at native speed, critical when processing hundreds or thousands of individual refund transactions.
- **Ed25519 project verification**: Project creators can sign campaign metadata with their Ed25519 key, creating verifiable proof of campaign authenticity and preventing impersonation.

## Token Standards Used

- **BST-721**: Backer reward tokens representing contribution tier and amount.
- **BST-20**: Primary funding token (BST and other BST-20 tokens).
- **BST-VC**: Optional accredited investor credentials for regulated fundraising.

## Integration Points

- **Escrow (0x...1004)**: All campaign deposits held in escrow. Milestone-based release managed through escrow conditions. Automatic refund capability for failed campaigns.
- **BNS (0x...1002)**: Campaign naming and creator identity resolution.
- **Governance (0x...1003)**: Milestone dispute arbitration. Community governance over platform parameters. Campaign flagging and moderation.
- **SchemaRegistry (0x...1006)**: Credential schemas for regulated fundraising requirements.
- **IssuerRegistry (0x...1007)**: Trusted credential issuers for investor verification.
- **BridgeETH (0x...1008)**: Cross-chain contribution support.
- **NFT Marketplace (contract 51)**: Secondary market for backer reward tokens.
- **Social Profile (contract 57)**: Creator identity and reputation displayed on campaign pages.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum CampaignStatus : byte
{
    Active = 0,
    Funded = 1,
    Failed = 2,
    Cancelled = 3,
    Completed = 4          // All milestones delivered
}

public enum FundingModel : byte
{
    AllOrNothing = 0,
    FlexibleFunding = 1
}

public struct Campaign
{
    public ulong CampaignId;
    public Address Creator;
    public string Title;
    public string Description;
    public string Category;
    public byte[] ContentHash;              // BLAKE3 hash of full proposal
    public UInt256 Goal;
    public UInt256 RaisedAmount;
    public ulong Deadline;
    public FundingModel Model;
    public CampaignStatus Status;
    public ulong BackerCount;
    public ulong MilestoneCount;
    public ulong CreatedAt;
}

public struct RewardTier
{
    public ulong TierId;
    public ulong CampaignId;
    public string Name;
    public string Description;
    public UInt256 MinContribution;
    public ulong MaxBackers;               // 0 = unlimited
    public ulong CurrentBackers;
    public byte[] RewardMetadataHash;      // BLAKE3 hash of reward details
}

public struct Contribution
{
    public ulong ContributionId;
    public ulong CampaignId;
    public Address Contributor;
    public UInt256 Amount;
    public ulong TierId;
    public ulong RewardTokenId;            // BST-721 token ID
    public ulong Timestamp;
    public bool Refunded;
}

public struct Milestone
{
    public ulong MilestoneId;
    public ulong CampaignId;
    public string Title;
    public string Description;
    public ulong ReleaseBps;               // Percentage of funds to release (basis points)
    public ulong Deadline;
    public bool Completed;
    public bool Disputed;
    public ulong ApprovalVotes;
    public ulong RejectionVotes;
    public UInt256 ReleasedAmount;
}

public struct StretchGoal
{
    public ulong GoalId;
    public ulong CampaignId;
    public UInt256 TargetAmount;
    public string Description;
    public bool Reached;
}

public struct CampaignUpdate
{
    public ulong UpdateId;
    public ulong CampaignId;
    public string Title;
    public byte[] ContentHash;
    public ulong Timestamp;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0209)]
public partial class CrowdfundingPlatform : SdkContractBase
{
    // Storage
    private StorageMap<ulong, Campaign> _campaigns;
    private StorageMap<ulong, Contribution> _contributions;
    private StorageMap<ulong, RewardTier> _tiers;
    private StorageMap<ulong, Milestone> _milestones;
    private StorageMap<ulong, StretchGoal> _stretchGoals;
    private StorageMap<ulong, CampaignUpdate> _updates;
    private StorageValue<ulong> _nextCampaignId;
    private StorageValue<ulong> _nextContributionId;
    private StorageValue<ulong> _nextTierId;
    private StorageValue<ulong> _nextMilestoneId;
    private StorageValue<ulong> _nextGoalId;
    private StorageValue<ulong> _nextUpdateId;
    private StorageValue<ulong> _nextTokenId;
    private StorageValue<ulong> _platformFeeBps;         // Default 300 = 3%
    private StorageValue<Address> _treasury;

    // --- Campaign Management ---

    /// <summary>
    /// Create a new crowdfunding campaign with goal and deadline.
    /// </summary>
    public ulong CreateCampaign(
        string title,
        string description,
        string category,
        byte[] contentHash,
        UInt256 goal,
        ulong deadline,
        FundingModel model
    );

    /// <summary>
    /// Add a reward tier to the campaign. Must be done before
    /// the campaign receives its first contribution.
    /// </summary>
    public ulong AddRewardTier(
        ulong campaignId,
        string name,
        string description,
        UInt256 minContribution,
        ulong maxBackers,
        byte[] rewardMetadataHash
    );

    /// <summary>
    /// Add milestones to the campaign for phased fund release.
    /// Total release basis points across all milestones must equal 10000.
    /// Must be defined before the campaign receives contributions.
    /// </summary>
    public ulong AddMilestone(
        ulong campaignId,
        string title,
        string description,
        ulong releaseBps,
        ulong deadline
    );

    /// <summary>
    /// Add a stretch goal to the campaign.
    /// </summary>
    public ulong AddStretchGoal(
        ulong campaignId,
        UInt256 targetAmount,
        string description
    );

    /// <summary>
    /// Cancel a campaign before the deadline. All contributors are
    /// eligible for refund. Only callable by campaign creator.
    /// Cannot cancel after goal is met in AllOrNothing model.
    /// </summary>
    public void CancelCampaign(ulong campaignId);

    // --- Contributing ---

    /// <summary>
    /// Contribute to a campaign. Caller sends BST as tx value.
    /// Funds are deposited into the Escrow system contract.
    /// A BST-721 backer reward token is minted if a tier is selected.
    /// </summary>
    public ulong Contribute(ulong campaignId, ulong tierId);

    /// <summary>
    /// Increase an existing contribution. Additional BST sent as tx value.
    /// May upgrade the contributor to a higher reward tier.
    /// </summary>
    public void IncreaseContribution(ulong contributionId);

    /// <summary>
    /// Claim a refund for a failed or cancelled campaign.
    /// Only available after deadline passes with goal unmet (AllOrNothing)
    /// or after cancellation. Burns the backer reward token.
    /// </summary>
    public void ClaimRefund(ulong contributionId);

    // --- Milestone Management ---

    /// <summary>
    /// Submit a milestone as completed. Creator provides evidence hash.
    /// Starts the milestone approval voting period.
    /// </summary>
    public void SubmitMilestone(ulong milestoneId, byte[] evidenceHash);

    /// <summary>
    /// Vote on a milestone completion as a backer.
    /// Voting weight proportional to contribution amount.
    /// </summary>
    public void VoteMilestone(ulong milestoneId, bool approve);

    /// <summary>
    /// Finalize a milestone after the voting period.
    /// If approved (>50% approval by contribution weight), releases
    /// the milestone's share of funds from escrow to the creator.
    /// If rejected, the milestone is marked as disputed.
    /// </summary>
    public void FinalizeMilestone(ulong milestoneId);

    /// <summary>
    /// Escalate a disputed milestone to governance for arbitration.
    /// </summary>
    public void EscalateMilestoneDispute(ulong milestoneId);

    // --- Campaign Finalization ---

    /// <summary>
    /// Finalize a campaign after the deadline.
    /// For AllOrNothing: if goal met, marks as Funded and enables
    /// milestone releases (or full release if no milestones).
    /// If goal not met, marks as Failed.
    /// For FlexibleFunding: always marks as Funded if any contributions exist.
    /// </summary>
    public void FinalizeCampaign(ulong campaignId);

    /// <summary>
    /// Release funds for a campaign with no milestones.
    /// Only callable after campaign is marked as Funded.
    /// Platform fee deducted. Remaining sent to creator.
    /// </summary>
    public void ReleaseFunds(ulong campaignId);

    // --- Campaign Updates ---

    /// <summary>
    /// Post an update to the campaign. Only callable by creator.
    /// </summary>
    public ulong PostUpdate(
        ulong campaignId,
        string title,
        byte[] contentHash
    );

    // --- View Functions ---

    public Campaign GetCampaign(ulong campaignId);
    public RewardTier GetRewardTier(ulong tierId);
    public Contribution GetContribution(ulong contributionId);
    public Milestone GetMilestone(ulong milestoneId);
    public StretchGoal GetStretchGoal(ulong goalId);
    public CampaignUpdate GetUpdate(ulong updateId);

    /// <summary>
    /// Get the total contributions by an address to a specific campaign.
    /// </summary>
    public UInt256 GetTotalContribution(Address contributor, ulong campaignId);

    /// <summary>
    /// Get the funding progress as basis points (raised / goal * 10000).
    /// </summary>
    public ulong GetFundingProgressBps(ulong campaignId);

    /// <summary>
    /// Check if a stretch goal has been reached.
    /// </summary>
    public bool IsStretchGoalReached(ulong campaignId, ulong goalId);

    /// <summary>
    /// Get the total number of active campaigns.
    /// </summary>
    public ulong GetActiveCampaignCount();

    // --- Admin (Governance) ---

    public void SetPlatformFee(ulong basisPoints);
    public void SetTreasury(Address newTreasury);

    /// <summary>
    /// Resolve a milestone dispute via governance decision.
    /// Only callable by governance contract.
    /// </summary>
    public void ResolveDispute(ulong milestoneId, bool approve);
}
```

## Complexity

**High** -- The contract manages the full lifecycle of crowdfunding campaigns with multiple interacting systems: campaign state machine (Active/Funded/Failed/Cancelled/Completed), escrow-backed deposit and refund management, milestone-based phased release with backer voting, stretch goal tracking, reward tier management with BST-721 minting, and governance dispute escalation. The interaction between milestone voting, fund release, and campaign status creates complex state transitions that must be handled atomically and correctly in all edge cases.

## Priority

**P1** -- Crowdfunding is a high-visibility use case that demonstrates the practical value of smart contracts for real-world applications. It drives community engagement, funds ecosystem projects, and showcases Basalt's escrow and governance infrastructure. Early deployment enables the ecosystem to fund its own growth through decentralized project financing.
