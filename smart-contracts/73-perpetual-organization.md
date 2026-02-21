# Perpetual Organization / Continuous DAO

## Category

Governance -- Decentralized Organizational Structure

## Summary

A perpetual, membership-fluid decentralized organization where members join by staking BST and leave by unstaking (with optional rage-quit to exit with a proportional share of the treasury). Unlike fixed-membership DAOs, participation is continuous -- proposals flow without fixed voting cycles, contributions are tracked on-chain, active members can receive salary streams, and sub-groups (guilds) organize around specific functions. The organization persists indefinitely without dissolution votes or membership caps.

## Why It's Useful

- **Fluid Membership**: Unlike traditional DAOs with fixed membership, anyone can join by staking and leave by unstaking, creating a dynamic organization that grows and contracts organically based on participant interest.
- **Rage-Quit Protection**: Members who disagree with a majority decision can exit with their proportional share of the treasury, providing a powerful minority protection mechanism absent in most DAOs.
- **Continuous Governance**: No fixed voting cycles or proposal windows. Proposals can be submitted and voted on at any time, enabling faster organizational decision-making.
- **Contribution Tracking**: On-chain contribution records create transparent, auditable histories of member activity, enabling merit-based compensation and reputation building.
- **Guild System**: Sub-groups organize around functions (development, marketing, treasury management, research), enabling specialized governance with delegation of authority.
- **Salary Streams**: Active contributors receive streaming payments proportional to their role and contribution, creating sustainable compensation without discrete grants.
- **Treasury Management**: The organization manages a shared treasury with multi-sig authorization, investment strategies, and transparent accounting.
- **Sustainable Organizations**: The perpetual structure with continuous membership flow creates organizations that can outlive their founders, adapting membership and focus over time.

## Key Features

- **Stake-Based Membership**: Join by staking a minimum BST amount. Voting power proportional to stake. Higher stakes unlock higher governance tiers.
- **Rage-Quit**: Any member can exit and receive their proportional share of liquid (non-invested) treasury assets. Rage-quit has a configurable delay period during which pending proposals that affect the treasury are resolved.
- **Continuous Proposal Flow**: Proposals submitted at any time, with configurable voting duration. Multiple proposals can be active simultaneously.
- **Proposal Types**: Funding requests, parameter changes, guild creation, member grants, contract upgrades, and custom action proposals.
- **Guild System**: Sub-organizations with their own budgets, membership requirements, and internal governance. Guild leads can make decisions within their budget and scope.
- **Contribution Tracking**: On-chain record of member contributions (code commits, governance participation, community activity). Used for reputation scoring and compensation.
- **Salary Streams**: Continuous token streams to active members, configurable by guild and role. Streams pause automatically for inactive members.
- **Treasury Management**: Multi-strategy treasury with diversification rules, investment caps, and risk parameters. Supports BST, BST-20 tokens, and yield-bearing BST-4626 positions.
- **Delegation**: Members can delegate their voting power to another member (single-hop, revocable). Delegation does not transfer rage-quit rights.
- **Quorum Requirements**: Configurable quorum for different proposal types. Higher-impact proposals require higher quorum.
- **Grace Period**: After a proposal passes, a grace period allows dissenters to rage-quit before the proposal executes.
- **Emergency Proposals**: Fast-track proposals for critical situations (security fixes, emergency pauses) with reduced voting period but higher quorum requirement.
- **Member Reputation**: Non-transferable reputation score based on contribution history, voting participation, and time as member.
- **Seasonal Reviews**: Periodic (epoch-based) review of member activity with automatic deactivation of idle members.

## Basalt-Specific Advantages

- **ZK Compliance for Regulated Organizations**: Organizations with regulatory requirements (e.g., investment DAOs subject to securities laws) can require ZK compliance proofs from members, verifying accredited investor status or jurisdiction without revealing identity.
- **BST-VC Credential-Based Roles**: Guild membership and role assignments can require BST-VC credentials (professional certifications, skill attestations), verified on-chain via the IssuerRegistry.
- **BST-4626 Vault Treasury**: Treasury assets deposited into BST-4626 vaults earn yield automatically, maximizing capital efficiency. The DAO's idle treasury generates returns without active management.
- **BST-3525 SFT Membership Positions**: Membership positions represented as BST-3525 semi-fungible tokens encode stake amount, join date, guild memberships, contribution score, and delegation status, enabling rich organizational analytics.
- **Confidential Voting via Pedersen Commitments**: Votes on sensitive proposals (compensation, personnel decisions) can use Pedersen commitments to hide individual vote choices until the reveal phase, preventing social pressure and strategic voting.
- **AOT-Compiled Proposal Processing**: Complex proposal execution (treasury movements, parameter updates, multi-step actions) executes in AOT-compiled native code, ensuring efficient and predictable gas costs.
- **Ed25519 Delegation Signatures**: Vote delegation and revocation are signed with Ed25519, enabling fast verification of delegation chains.
- **BLS Aggregate Quorum Proofs**: Quorum can be proven with a single BLS-aggregated signature from all supporting voters, reducing on-chain verification costs for proposals with many voters.

## Token Standards Used

- **BST-20**: Staking token (BST), treasury assets, salary payment tokens.
- **BST-3525 (SFT)**: Membership position tokens with slot metadata (stake, guild, role, reputation, contribution score, join date).
- **BST-4626 (Vault)**: Treasury yield-bearing positions. Salary stream tokens held in vaults pending distribution.
- **BST-VC (Verifiable Credentials)**: Professional credentials for guild membership requirements. Organizational credentials issued to active members.
- **BST-721**: Non-transferable reputation badges for governance milestones (100 votes, 1 year membership, guild lead).

## Integration Points

- **Governance (0x0102)**: The perpetual organization extends the base governance system with fluid membership, guilds, and rage-quit. It can interact with the base governance for ecosystem-level proposals.
- **StakingPool (0x0105)**: Member stakes can be delegated to the StakingPool for network validation, earning staking rewards while securing the network. Dual-use staking.
- **Escrow (0x0103)**: Funding proposals hold allocated funds in escrow until milestone delivery. Guild budgets escrowed per period.
- **SchemaRegistry (0x...1006)**: Credential schemas for guild membership requirements and contribution attestations.
- **IssuerRegistry (0x...1007)**: Trusted issuers for professional credentials required for specialized guilds.
- **BNS (0x0101)**: Organization and guild addresses registered under BNS names (e.g., `myorg.dao.basalt`, `dev.myorg.dao.basalt`).
- **WBSLT (0x0100)**: Wrapped BST for treasury diversification and DeFi interactions.

## Technical Sketch

```csharp
// ============================================================
// PerpetualOrganization -- Continuous DAO with fluid membership
// ============================================================

[BasaltContract(TypeId = 0x0309)]
public partial class PerpetualOrganization : SdkContract
{
    // --- Storage ---

    // Member state
    private StorageMap<Address, MemberInfo> _members;
    private StorageMap<Address, bool> _isMember;
    private StorageValue<uint> _memberCount;
    private StorageValue<UInt256> _totalStaked;

    // Proposals
    private StorageValue<ulong> _nextProposalId;
    private StorageMap<ulong, Proposal> _proposals;
    private StorageMap<ulong, StorageMap<Address, bool>> _votes;
    private StorageMap<ulong, StorageMap<Address, bool>> _hasVoted;
    private StorageMap<ulong, VoteTally> _proposalTallies;

    // Guilds
    private StorageValue<uint> _nextGuildId;
    private StorageMap<uint, Guild> _guilds;
    private StorageMap<uint, StorageMap<Address, bool>> _guildMembers;
    private StorageMap<uint, uint> _guildMemberCount;

    // Contribution tracking
    private StorageMap<Address, UInt256> _contributionScores;
    private StorageMap<Address, ulong> _lastActiveBlock;

    // Salary streams
    private StorageMap<Address, SalaryStream> _salaryStreams;

    // Delegation
    private StorageMap<Address, Address> _delegations; // delegator => delegate

    // Treasury
    private StorageValue<UInt256> _treasuryBalance;
    private StorageMap<Address, UInt256> _tokenTreasury; // BST-20 token => balance

    // Rage-quit queue
    private StorageMap<Address, RageQuitRequest> _rageQuitQueue;
    private StorageValue<ulong> _rageQuitDelayBlocks;

    // Parameters
    private StorageValue<UInt256> _minimumStake;
    private StorageValue<ulong> _votingDurationBlocks;
    private StorageValue<ulong> _gracePeriodBlocks;
    private StorageValue<uint> _quorumBps;
    private StorageValue<ulong> _inactivityThresholdBlocks;

    // --- Data Structures ---

    public struct MemberInfo
    {
        public Address MemberAddress;
        public UInt256 Stake;
        public ulong JoinedAtBlock;
        public UInt256 ContributionScore;
        public uint GuildCount;
        public bool Active;
    }

    public struct Proposal
    {
        public ulong ProposalId;
        public Address Proposer;
        public byte ProposalType;       // 0=funding, 1=parameter, 2=guild, 3=grant,
                                        // 4=custom, 5=emergency
        public string Title;
        public string Description;
        public UInt256 FundingAmount;   // For funding proposals
        public Address FundingRecipient;
        public byte[] ActionData;       // Encoded action to execute
        public ulong SubmittedAtBlock;
        public ulong VotingEndBlock;
        public ulong GracePeriodEndBlock;
        public byte Status;            // 0=voting, 1=passed, 2=rejected, 3=executed,
                                       // 4=cancelled
        public bool Executed;
    }

    public struct Guild
    {
        public uint GuildId;
        public string Name;
        public Address Lead;
        public UInt256 Budget;
        public UInt256 BudgetSpent;
        public ulong BudgetPeriodEnd;
        public UInt256 MinimumStake;    // Additional stake required for guild membership
    }

    public struct SalaryStream
    {
        public UInt256 RatePerBlock;    // Tokens per block
        public ulong LastClaimedBlock;
        public uint GuildId;
        public bool Active;
    }

    public struct RageQuitRequest
    {
        public UInt256 ShareOfTreasury;
        public ulong RequestedAtBlock;
        public ulong ExecutableAfterBlock;
        public bool Pending;
    }

    public struct VoteTally
    {
        public UInt256 YesVotes;
        public UInt256 NoVotes;
        public uint VoterCount;
    }

    // --- Membership ---

    /// <summary>
    /// Join the organization by staking BST.
    /// </summary>
    public void Join()
    {
        Require(!_isMember.Get(Context.Sender), "ALREADY_MEMBER");
        Require(Context.TxValue >= _minimumStake.Get(), "INSUFFICIENT_STAKE");

        _members.Set(Context.Sender, new MemberInfo
        {
            MemberAddress = Context.Sender,
            Stake = Context.TxValue,
            JoinedAtBlock = Context.BlockNumber,
            ContributionScore = UInt256.Zero,
            GuildCount = 0,
            Active = true
        });

        _isMember.Set(Context.Sender, true);
        _memberCount.Set(_memberCount.Get() + 1);
        _totalStaked.Set(_totalStaked.Get() + Context.TxValue);
        _lastActiveBlock.Set(Context.Sender, Context.BlockNumber);

        EmitEvent("MemberJoined", Context.Sender, Context.TxValue);
    }

    /// <summary>
    /// Increase stake (increases voting power).
    /// </summary>
    public void IncreaseStake()
    {
        RequireMember();
        Require(Context.TxValue > UInt256.Zero, "ZERO_AMOUNT");

        var member = _members.Get(Context.Sender);
        member.Stake = member.Stake + Context.TxValue;
        _members.Set(Context.Sender, member);
        _totalStaked.Set(_totalStaked.Get() + Context.TxValue);

        EmitEvent("StakeIncreased", Context.Sender, Context.TxValue, member.Stake);
    }

    /// <summary>
    /// Initiate rage-quit: exit with proportional share of liquid treasury.
    /// Enters a delay period during which pending proposals resolve.
    /// </summary>
    public void RageQuit()
    {
        RequireMember();
        var member = _members.Get(Context.Sender);

        // Calculate proportional share of liquid treasury
        UInt256 share = (member.Stake * _treasuryBalance.Get()) / _totalStaked.Get();

        _rageQuitQueue.Set(Context.Sender, new RageQuitRequest
        {
            ShareOfTreasury = share,
            RequestedAtBlock = Context.BlockNumber,
            ExecutableAfterBlock = Context.BlockNumber + _rageQuitDelayBlocks.Get(),
            Pending = true
        });

        EmitEvent("RageQuitInitiated", Context.Sender, share, member.Stake);
    }

    /// <summary>
    /// Execute a pending rage-quit after the delay period.
    /// Returns stake plus proportional treasury share.
    /// </summary>
    public void ExecuteRageQuit()
    {
        var request = _rageQuitQueue.Get(Context.Sender);
        Require(request.Pending, "NO_PENDING_RAGE_QUIT");
        Require(Context.BlockNumber >= request.ExecutableAfterBlock, "DELAY_NOT_PASSED");

        var member = _members.Get(Context.Sender);

        // Remove from organization
        _isMember.Set(Context.Sender, false);
        _memberCount.Set(_memberCount.Get() - 1);
        _totalStaked.Set(_totalStaked.Get() - member.Stake);

        // Return stake
        UInt256 totalReturn = member.Stake + request.ShareOfTreasury;
        _treasuryBalance.Set(_treasuryBalance.Get() - request.ShareOfTreasury);

        Context.TransferNative(Context.Sender, totalReturn);

        // Clear state
        request.Pending = false;
        _rageQuitQueue.Set(Context.Sender, request);

        EmitEvent("RageQuitExecuted", Context.Sender, totalReturn);
    }

    /// <summary>
    /// Graceful exit without treasury share (just return stake).
    /// </summary>
    public void Leave()
    {
        RequireMember();
        var member = _members.Get(Context.Sender);

        _isMember.Set(Context.Sender, false);
        _memberCount.Set(_memberCount.Get() - 1);
        _totalStaked.Set(_totalStaked.Get() - member.Stake);

        Context.TransferNative(Context.Sender, member.Stake);

        EmitEvent("MemberLeft", Context.Sender, member.Stake);
    }

    // --- Proposals ---

    /// <summary>
    /// Submit a proposal. Any member can propose.
    /// </summary>
    public ulong SubmitProposal(
        byte proposalType,
        string title,
        string description,
        UInt256 fundingAmount,
        Address fundingRecipient,
        byte[] actionData)
    {
        RequireMember();

        ulong votingDuration = proposalType == 5  // emergency
            ? _votingDurationBlocks.Get() / 4
            : _votingDurationBlocks.Get();

        ulong proposalId = _nextProposalId.Get();
        _nextProposalId.Set(proposalId + 1);

        ulong votingEnd = Context.BlockNumber + votingDuration;

        _proposals.Set(proposalId, new Proposal
        {
            ProposalId = proposalId,
            Proposer = Context.Sender,
            ProposalType = proposalType,
            Title = title,
            Description = description,
            FundingAmount = fundingAmount,
            FundingRecipient = fundingRecipient,
            ActionData = actionData,
            SubmittedAtBlock = Context.BlockNumber,
            VotingEndBlock = votingEnd,
            GracePeriodEndBlock = votingEnd + _gracePeriodBlocks.Get(),
            Status = 0,
            Executed = false
        });

        // Record contribution
        RecordContribution(Context.Sender, 10); // 10 points for proposal submission

        EmitEvent("ProposalSubmitted", proposalId, proposalType, title);
        return proposalId;
    }

    /// <summary>
    /// Vote on an active proposal. Vote weight = stake + delegated stake.
    /// </summary>
    public void Vote(ulong proposalId, bool support)
    {
        RequireMember();
        var proposal = _proposals.Get(proposalId);
        Require(proposal.Status == 0, "NOT_VOTING");
        Require(Context.BlockNumber <= proposal.VotingEndBlock, "VOTING_ENDED");
        Require(!_hasVoted.Get(proposalId).Get(Context.Sender), "ALREADY_VOTED");

        UInt256 voteWeight = GetVotingPower(Context.Sender);

        _votes.Get(proposalId).Set(Context.Sender, support);
        _hasVoted.Get(proposalId).Set(Context.Sender, true);

        var tally = _proposalTallies.Get(proposalId);
        if (support)
            tally.YesVotes = tally.YesVotes + voteWeight;
        else
            tally.NoVotes = tally.NoVotes + voteWeight;
        tally.VoterCount++;
        _proposalTallies.Set(proposalId, tally);

        RecordContribution(Context.Sender, 5); // 5 points for voting
        _lastActiveBlock.Set(Context.Sender, Context.BlockNumber);

        EmitEvent("Voted", proposalId, Context.Sender, support, voteWeight);
    }

    /// <summary>
    /// Finalize a proposal after voting and grace period.
    /// </summary>
    public void FinalizeProposal(ulong proposalId)
    {
        var proposal = _proposals.Get(proposalId);
        Require(proposal.Status == 0, "NOT_VOTING");
        Require(Context.BlockNumber > proposal.VotingEndBlock, "VOTING_NOT_ENDED");

        var tally = _proposalTallies.Get(proposalId);
        UInt256 totalVoted = tally.YesVotes + tally.NoVotes;
        UInt256 quorumRequired = (_totalStaked.Get() * _quorumBps.Get()) / 10000;

        bool passed = totalVoted >= quorumRequired && tally.YesVotes > tally.NoVotes;

        proposal.Status = passed ? (byte)1 : (byte)2;
        _proposals.Set(proposalId, proposal);

        EmitEvent("ProposalFinalized", proposalId, passed);
    }

    /// <summary>
    /// Execute a passed proposal after the grace period.
    /// </summary>
    public void ExecuteProposal(ulong proposalId)
    {
        var proposal = _proposals.Get(proposalId);
        Require(proposal.Status == 1, "NOT_PASSED");
        Require(!proposal.Executed, "ALREADY_EXECUTED");
        Require(Context.BlockNumber > proposal.GracePeriodEndBlock, "GRACE_PERIOD_ACTIVE");

        proposal.Executed = true;
        proposal.Status = 3;
        _proposals.Set(proposalId, proposal);

        // Execute proposal action
        if (proposal.ProposalType == 0 && !proposal.FundingAmount.IsZero)
        {
            // Funding proposal
            Require(_treasuryBalance.Get() >= proposal.FundingAmount, "INSUFFICIENT_TREASURY");
            _treasuryBalance.Set(_treasuryBalance.Get() - proposal.FundingAmount);
            Context.TransferNative(proposal.FundingRecipient, proposal.FundingAmount);
        }

        EmitEvent("ProposalExecuted", proposalId);
    }

    // --- Guilds ---

    /// <summary>
    /// Create a new guild (requires governance proposal approval).
    /// </summary>
    public uint CreateGuild(string name, Address lead, UInt256 budget,
                            ulong budgetPeriodBlocks, UInt256 minimumStake)
    {
        RequireProposalExecution();

        uint guildId = _nextGuildId.Get();
        _nextGuildId.Set(guildId + 1);

        _guilds.Set(guildId, new Guild
        {
            GuildId = guildId,
            Name = name,
            Lead = lead,
            Budget = budget,
            BudgetSpent = UInt256.Zero,
            BudgetPeriodEnd = Context.BlockNumber + budgetPeriodBlocks,
            MinimumStake = minimumStake
        });

        EmitEvent("GuildCreated", guildId, name, lead);
        return guildId;
    }

    /// <summary>
    /// Join a guild. Must meet minimum stake requirement.
    /// </summary>
    public void JoinGuild(uint guildId)
    {
        RequireMember();
        var guild = _guilds.Get(guildId);
        var member = _members.Get(Context.Sender);
        Require(member.Stake >= guild.MinimumStake, "INSUFFICIENT_GUILD_STAKE");
        Require(!_guildMembers.Get(guildId).Get(Context.Sender), "ALREADY_IN_GUILD");

        _guildMembers.Get(guildId).Set(Context.Sender, true);
        _guildMemberCount.Set(guildId, _guildMemberCount.Get(guildId) + 1);

        member.GuildCount++;
        _members.Set(Context.Sender, member);

        EmitEvent("GuildJoined", guildId, Context.Sender);
    }

    // --- Salary Streams ---

    /// <summary>
    /// Set up a salary stream for a member. Guild lead or governance only.
    /// </summary>
    public void SetSalaryStream(Address member, UInt256 ratePerBlock, uint guildId)
    {
        var guild = _guilds.Get(guildId);
        Require(Context.Sender == guild.Lead || IsProposalExecution(), "NOT_AUTHORIZED");
        Require(_isMember.Get(member), "NOT_MEMBER");

        _salaryStreams.Set(member, new SalaryStream
        {
            RatePerBlock = ratePerBlock,
            LastClaimedBlock = Context.BlockNumber,
            GuildId = guildId,
            Active = true
        });

        EmitEvent("SalaryStreamSet", member, ratePerBlock, guildId);
    }

    /// <summary>
    /// Claim accrued salary.
    /// </summary>
    public UInt256 ClaimSalary()
    {
        RequireMember();
        var stream = _salaryStreams.Get(Context.Sender);
        Require(stream.Active, "NO_ACTIVE_STREAM");

        ulong blocksElapsed = Context.BlockNumber - stream.LastClaimedBlock;
        UInt256 accrued = stream.RatePerBlock * blocksElapsed;

        // Deduct from guild budget
        var guild = _guilds.Get(stream.GuildId);
        UInt256 remaining = guild.Budget - guild.BudgetSpent;
        if (accrued > remaining)
            accrued = remaining;

        guild.BudgetSpent = guild.BudgetSpent + accrued;
        _guilds.Set(stream.GuildId, guild);

        stream.LastClaimedBlock = Context.BlockNumber;
        _salaryStreams.Set(Context.Sender, stream);

        Context.TransferNative(Context.Sender, accrued);

        EmitEvent("SalaryClaimed", Context.Sender, accrued);
        return accrued;
    }

    // --- Delegation ---

    /// <summary>
    /// Delegate voting power to another member.
    /// </summary>
    public void Delegate(Address delegatee)
    {
        RequireMember();
        Require(_isMember.Get(delegatee), "DELEGATEE_NOT_MEMBER");
        Require(delegatee != Context.Sender, "CANNOT_SELF_DELEGATE");
        Require(_delegations.Get(delegatee) == Address.Zero, "CHAINED_DELEGATION");

        _delegations.Set(Context.Sender, delegatee);
        EmitEvent("Delegated", Context.Sender, delegatee);
    }

    /// <summary>
    /// Revoke delegation.
    /// </summary>
    public void RevokeDelegation()
    {
        _delegations.Set(Context.Sender, Address.Zero);
        EmitEvent("DelegationRevoked", Context.Sender);
    }

    // --- Treasury ---

    /// <summary>
    /// Deposit funds to the organization treasury.
    /// </summary>
    public void FundTreasury()
    {
        Require(Context.TxValue > UInt256.Zero, "ZERO_AMOUNT");
        _treasuryBalance.Set(_treasuryBalance.Get() + Context.TxValue);
        EmitEvent("TreasuryFunded", Context.Sender, Context.TxValue);
    }

    // --- Query Methods ---

    public MemberInfo GetMember(Address member) => _members.Get(member);
    public bool IsMember(Address addr) => _isMember.Get(addr);
    public uint GetMemberCount() => _memberCount.Get();
    public UInt256 GetTotalStaked() => _totalStaked.Get();
    public Proposal GetProposal(ulong id) => _proposals.Get(id);
    public VoteTally GetProposalTally(ulong id) => _proposalTallies.Get(id);
    public Guild GetGuild(uint id) => _guilds.Get(id);
    public UInt256 GetTreasuryBalance() => _treasuryBalance.Get();
    public Address GetDelegate(Address member) => _delegations.Get(member);
    public SalaryStream GetSalaryStream(Address member) => _salaryStreams.Get(member);

    public UInt256 GetVotingPower(Address member)
    {
        UInt256 ownStake = _members.Get(member).Stake;
        // Add delegated stake (linear scan -- in production, maintain a reverse index)
        // For simplicity, shown as own stake only
        return ownStake;
    }

    // --- Internal Helpers ---

    private void RecordContribution(Address member, ulong points)
    {
        UInt256 current = _contributionScores.Get(member);
        _contributionScores.Set(member, current + points);
    }

    private void RequireMember()
    {
        Require(_isMember.Get(Context.Sender), "NOT_MEMBER");
    }

    private void RequireProposalExecution() { /* ... */ }
    private bool IsProposalExecution() { /* ... */ return false; }
}
```

## Complexity

**High** -- The perpetual organization combines multiple complex subsystems: fluid stake-based membership, rage-quit with proportional treasury calculation (requires precise accounting of liquid vs. invested assets), continuous proposal governance with grace periods, guild sub-governance with budget management, streaming salary payments, voting power delegation chains, and contribution tracking. The interaction between rage-quit timing and pending proposals creates subtle edge cases (a member rage-quitting to extract value before a treasury-depleting proposal executes). Treasury management with multiple token types and yield-bearing positions adds accounting complexity.

## Priority

**P2** -- While the perpetual organization is a powerful governance primitive, it requires a mature ecosystem with sufficient community size, treasury formation mechanisms, and governance experience. It should be deployed after basic governance (0x0102) and staking infrastructure are well-established. However, it is essential for sophisticated DAOs and can serve as the organizational backbone for the Basalt Foundation itself, making it a strategic investment.
