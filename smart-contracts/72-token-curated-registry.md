# Token-Curated Registry (TCR)

## Category

Governance and Curation -- Decentralized Quality Signaling

## Summary

A community-curated list mechanism where applicants stake tokens to apply for inclusion, and token holders challenge questionable applications by counter-staking. Challenges trigger governance-style voting among staked participants, with the losing side's stake redistributed to winners. TCRs provide decentralized quality signals for ecosystem participants -- curating approved vendors, vetted projects, trusted oracles, or any list where community consensus determines inclusion.

## Why It's Useful

- **Decentralized Quality Signal**: Without centralized gatekeepers, TCRs provide community-driven quality curation for lists that matter -- trusted oracle providers, safe token contracts, reputable validators, vetted DeFi protocols.
- **Economic Incentives for Accuracy**: Staking mechanics ensure that applicants have skin in the game, and challengers are incentivized to identify low-quality applicants. Both sides risk capital, aligning incentives with list quality.
- **Ecosystem Trust Infrastructure**: A curated registry of trusted entities (oracles, bridges, contracts) serves as shared infrastructure that other protocols can reference, reducing duplicated due diligence.
- **Spam Prevention**: The staking requirement prevents low-effort or malicious applications from flooding the registry.
- **Revenue for Curators**: Active curators (challengers and voters) earn rewards from resolved challenges, creating a sustainable curation economy.
- **Composability**: Other contracts can query the TCR to gate access -- for example, a lending protocol might only accept collateral tokens that appear on the "safe tokens" TCR.
- **Community Governance Exercise**: TCR participation trains the community in stake-weighted governance, building muscle for broader DAO decision-making.

## Key Features

- **Application Lifecycle**: Apply (stake + data) -> Challenge Period -> If unchallenged: accepted -> If challenged: Vote -> Winner determined -> Stake redistribution.
- **Challenge Mechanism**: During the challenge period, anyone can challenge an application by depositing a counter-stake equal to the application stake. This triggers a vote.
- **Stake-Weighted Voting**: Token holders vote during the voting period. Votes are weighted by the amount of BST staked. Both the applicant's stake and challenger's stake are at risk.
- **Reveal-Commit Voting**: Two-phase voting (commit hash, then reveal vote) prevents vote copying and last-minute strategic voting.
- **Configurable Parameters**: Application stake, challenge period duration, voting period duration, vote quorum, and reward distribution ratios are all configurable per registry.
- **Multiple Registries**: Factory pattern allows creation of multiple TCRs with different purposes (token safety, oracle trust, vendor approval, etc.).
- **Listing Removal**: Existing listings can be challenged for removal, using the same stake-and-vote mechanism.
- **Listing Renewal**: Listings expire after a configurable period and must be renewed (with renewed stake) to remain active.
- **Registry Queries**: Other contracts can query whether an address or identifier is listed, enabling on-chain composability.
- **Application Metadata**: Applicants submit structured metadata (name, description, evidence, links) stored on-chain or as content hashes.
- **Parameterizer**: A meta-governance mechanism for updating TCR parameters through token-holder voting.
- **Reward Distribution**: When a challenge is resolved, the losing side's stake is distributed: a portion to the winning side, a portion to voters who voted with the majority, and a protocol fee.
- **Batch Operations**: Batch-process expired challenge periods, claim multiple rewards, or renew multiple listings in a single transaction.

## Basalt-Specific Advantages

- **ZK Compliance for Sensitive Registries**: For regulated registries (e.g., accredited investor lists, licensed vendor registries), applicants can prove compliance via ZK proofs without revealing their identity. A KYC-verified entity can apply and be listed by address without publicly linking to a real-world identity.
- **BST-VC Credential-Based Applications**: Applicants can submit BST-VC credentials as evidence of qualification (professional certification, security audit certificate, regulatory license), verified on-chain via the IssuerRegistry without manual document review.
- **AOT-Compiled Vote Counting**: Stake-weighted vote tallying with commit-reveal mechanics executes in AOT-compiled native code, enabling efficient resolution of challenges with many voters.
- **Confidential Stakes via Pedersen Commitments**: Application and challenge stakes can be hidden using Pedersen commitments, preventing strategic behavior based on visible stake sizes (e.g., adjusting challenge stakes to match the applicant's known stake).
- **BLS Signature Aggregation for Batch Votes**: Multiple voters can submit votes with BLS-aggregated signatures, reducing transaction costs for popular challenges.
- **Ed25519 Commit-Reveal Efficiency**: Vote commit hashes signed with Ed25519 benefit from fast signature verification, keeping the commit phase gas-efficient.
- **BST-3525 SFT Listing Tokens**: Accepted listings can be represented as BST-3525 semi-fungible tokens with metadata (category, approval date, last renewal, challenge history), enabling on-chain provenance tracking and secondary market for curated positions.

## Token Standards Used

- **BST-20**: Staking token for applications, challenges, and voting. Reward distribution in BST-20.
- **BST-3525 (SFT)**: Listing tokens representing accepted registry entries with slot metadata (category, approval date, expiry, challenge history).
- **BST-VC (Verifiable Credentials)**: Credential-based evidence for applications. Issued credentials for accepted listings that other contracts can verify.
- **BST-721**: Optional non-transferable registry membership badge.

## Integration Points

- **Governance (0x0102)**: Parameterizer changes (stake amounts, voting periods, quorum requirements) go through governance voting. Emergency pause and registry decommissioning governed by the DAO.
- **StakingPool (0x0105)**: Application and challenge stakes can optionally be directed through the StakingPool, earning yield while locked. Protocol fee revenue directed to staking rewards.
- **SchemaRegistry (0x...1006)**: Credential schemas for application evidence (e.g., "SecurityAuditCertificate", "OracleUptime Attestation").
- **IssuerRegistry (0x...1007)**: Trusted issuers for credential-based applications.
- **BNS (0x0101)**: Registries and accepted listings mapped to BNS names (e.g., `trusted-oracles.tcr.basalt`).
- **Escrow (0x0103)**: Challenge stakes held in escrow during voting period.

## Technical Sketch

```csharp
// ============================================================
// TcrFactory -- Create and manage token-curated registries
// ============================================================

[BasaltContract(TypeId = 0x0307)]
public partial class TcrFactory : SdkContract
{
    private StorageValue<ulong> _nextRegistryId;
    private StorageMap<ulong, Address> _registries;

    /// <summary>
    /// Create a new token-curated registry with configurable parameters.
    /// </summary>
    public Address CreateRegistry(
        string name,
        string category,
        UInt256 applicationStake,
        ulong challengePeriodBlocks,
        ulong votingPeriodBlocks,
        ulong revealPeriodBlocks,
        uint quorumBps,
        uint winnerRewardBps,
        uint voterRewardBps,
        ulong listingExpiryBlocks)
    {
        ulong registryId = _nextRegistryId.Get();
        _nextRegistryId.Set(registryId + 1);

        Address registryAddress = DeriveRegistryAddress(registryId);
        _registries.Set(registryId, registryAddress);

        EmitEvent("RegistryCreated", registryId, registryAddress, name, category);
        return registryAddress;
    }

    private Address DeriveRegistryAddress(ulong id) { /* ... */ }
}


// ============================================================
// TokenCuratedRegistry -- Individual curated registry
// ============================================================

[BasaltContract(TypeId = 0x0308)]
public partial class TokenCuratedRegistry : SdkContract
{
    // --- Storage ---

    // Registry parameters
    private StorageValue<string> _name;
    private StorageValue<UInt256> _applicationStake;
    private StorageValue<ulong> _challengePeriodBlocks;
    private StorageValue<ulong> _votingPeriodBlocks;
    private StorageValue<ulong> _revealPeriodBlocks;
    private StorageValue<uint> _quorumBps;
    private StorageValue<uint> _winnerRewardBps;
    private StorageValue<uint> _voterRewardBps;
    private StorageValue<ulong> _listingExpiryBlocks;

    // Applications
    private StorageValue<ulong> _nextApplicationId;
    private StorageMap<ulong, Application> _applications;

    // Listings: listingHash => ListingEntry
    private StorageMap<Hash256, ListingEntry> _listings;
    private StorageMap<Hash256, bool> _isListed;

    // Challenges: challengeId => Challenge
    private StorageValue<ulong> _nextChallengeId;
    private StorageMap<ulong, Challenge> _challenges;

    // Votes: challengeId => voter => VoteCommit
    private StorageMap<ulong, StorageMap<Address, VoteCommit>> _voteCommits;

    // Vote tallies: challengeId => VoteTally
    private StorageMap<ulong, VoteTally> _voteTallies;

    // Rewards: voter => claimable rewards
    private StorageMap<Address, UInt256> _claimableRewards;

    // Protocol fee
    private StorageValue<uint> _protocolFeeBps;

    // --- Data Structures ---

    public struct Application
    {
        public ulong ApplicationId;
        public Address Applicant;
        public Hash256 ListingHash;       // Unique identifier for the listing
        public byte[] Metadata;           // Structured application data
        public UInt256 Stake;
        public ulong AppliedAtBlock;
        public ulong ChallengeDeadline;   // End of challenge period
        public byte Status;              // 0=pending, 1=accepted, 2=rejected, 3=challenged
        public ulong ChallengeId;        // If challenged
    }

    public struct ListingEntry
    {
        public Hash256 ListingHash;
        public Address Owner;
        public byte[] Metadata;
        public ulong AcceptedAtBlock;
        public ulong ExpiryBlock;
        public UInt256 Stake;            // Locked stake
        public uint ChallengesSucceeded; // Number of challenges survived
    }

    public struct Challenge
    {
        public ulong ChallengeId;
        public Address Challenger;
        public Hash256 TargetListingHash;
        public UInt256 ChallengerStake;
        public string Reason;
        public ulong VotingStartBlock;
        public ulong VotingEndBlock;
        public ulong RevealEndBlock;
        public byte Status;             // 0=voting, 1=revealing, 2=resolved
        public bool ChallengerWon;
        public bool IsRemovalChallenge; // true = challenge to remove existing listing
    }

    public struct VoteCommit
    {
        public Hash256 CommitHash;       // keccak256(vote || salt)
        public UInt256 StakeWeight;
        public bool Revealed;
        public bool VotedForChallenger;
    }

    public struct VoteTally
    {
        public UInt256 VotesForApplicant;
        public UInt256 VotesForChallenger;
        public uint VoterCount;
    }

    // --- Application ---

    /// <summary>
    /// Apply for inclusion in the registry. Requires staking the application amount.
    /// </summary>
    public ulong Apply(Hash256 listingHash, byte[] metadata)
    {
        Require(!_isListed.Get(listingHash), "ALREADY_LISTED");
        Require(Context.TxValue >= _applicationStake.Get(), "INSUFFICIENT_STAKE");

        ulong appId = _nextApplicationId.Get();
        _nextApplicationId.Set(appId + 1);

        _applications.Set(appId, new Application
        {
            ApplicationId = appId,
            Applicant = Context.Sender,
            ListingHash = listingHash,
            Metadata = metadata,
            Stake = Context.TxValue,
            AppliedAtBlock = Context.BlockNumber,
            ChallengeDeadline = Context.BlockNumber + _challengePeriodBlocks.Get(),
            Status = 0,
            ChallengeId = 0
        });

        EmitEvent("ApplicationSubmitted", appId, listingHash, Context.Sender);
        return appId;
    }

    /// <summary>
    /// Apply with a BST-VC credential as supporting evidence.
    /// </summary>
    public ulong ApplyWithCredential(
        Hash256 listingHash,
        byte[] metadata,
        byte[] zkCredentialProof,
        Hash256 credentialNullifier)
    {
        // Verify ZK proof of credential validity
        bool valid = VerifyCredentialProof(zkCredentialProof, credentialNullifier);
        Require(valid, "INVALID_CREDENTIAL");

        return Apply(listingHash, metadata);
    }

    /// <summary>
    /// Finalize an unchallenged application after the challenge period expires.
    /// </summary>
    public void FinalizeApplication(ulong applicationId)
    {
        var app = _applications.Get(applicationId);
        Require(app.Status == 0, "NOT_PENDING");
        Require(Context.BlockNumber > app.ChallengeDeadline, "CHALLENGE_PERIOD_ACTIVE");

        // Accept the listing
        app.Status = 1;
        _applications.Set(applicationId, app);

        _listings.Set(app.ListingHash, new ListingEntry
        {
            ListingHash = app.ListingHash,
            Owner = app.Applicant,
            Metadata = app.Metadata,
            AcceptedAtBlock = Context.BlockNumber,
            ExpiryBlock = Context.BlockNumber + _listingExpiryBlocks.Get(),
            Stake = app.Stake,
            ChallengesSucceeded = 0
        });
        _isListed.Set(app.ListingHash, true);

        EmitEvent("ApplicationAccepted", applicationId, app.ListingHash);
    }

    // --- Challenge ---

    /// <summary>
    /// Challenge a pending application or existing listing.
    /// Requires depositing a counter-stake.
    /// </summary>
    public ulong ChallengeApplication(ulong applicationId, string reason)
    {
        var app = _applications.Get(applicationId);
        Require(app.Status == 0, "NOT_PENDING");
        Require(Context.BlockNumber <= app.ChallengeDeadline, "CHALLENGE_PERIOD_EXPIRED");
        Require(Context.TxValue >= app.Stake, "INSUFFICIENT_COUNTER_STAKE");

        ulong challengeId = _nextChallengeId.Get();
        _nextChallengeId.Set(challengeId + 1);

        ulong votingEnd = Context.BlockNumber + _votingPeriodBlocks.Get();
        ulong revealEnd = votingEnd + _revealPeriodBlocks.Get();

        _challenges.Set(challengeId, new Challenge
        {
            ChallengeId = challengeId,
            Challenger = Context.Sender,
            TargetListingHash = app.ListingHash,
            ChallengerStake = Context.TxValue,
            Reason = reason,
            VotingStartBlock = Context.BlockNumber,
            VotingEndBlock = votingEnd,
            RevealEndBlock = revealEnd,
            Status = 0,
            ChallengerWon = false,
            IsRemovalChallenge = false
        });

        app.Status = 3; // challenged
        app.ChallengeId = challengeId;
        _applications.Set(applicationId, app);

        EmitEvent("ApplicationChallenged", applicationId, challengeId, Context.Sender);
        return challengeId;
    }

    /// <summary>
    /// Challenge an existing listing for removal.
    /// </summary>
    public ulong ChallengeListing(Hash256 listingHash, string reason)
    {
        Require(_isListed.Get(listingHash), "NOT_LISTED");
        var listing = _listings.Get(listingHash);
        Require(Context.TxValue >= listing.Stake, "INSUFFICIENT_COUNTER_STAKE");

        ulong challengeId = _nextChallengeId.Get();
        _nextChallengeId.Set(challengeId + 1);

        ulong votingEnd = Context.BlockNumber + _votingPeriodBlocks.Get();
        ulong revealEnd = votingEnd + _revealPeriodBlocks.Get();

        _challenges.Set(challengeId, new Challenge
        {
            ChallengeId = challengeId,
            Challenger = Context.Sender,
            TargetListingHash = listingHash,
            ChallengerStake = Context.TxValue,
            Reason = reason,
            VotingStartBlock = Context.BlockNumber,
            VotingEndBlock = votingEnd,
            RevealEndBlock = revealEnd,
            Status = 0,
            ChallengerWon = false,
            IsRemovalChallenge = true
        });

        EmitEvent("ListingChallenged", listingHash, challengeId, Context.Sender);
        return challengeId;
    }

    // --- Commit-Reveal Voting ---

    /// <summary>
    /// Commit a vote during the voting period. Vote is hidden until reveal.
    /// </summary>
    public void CommitVote(ulong challengeId, Hash256 commitHash, UInt256 stakeWeight)
    {
        var challenge = _challenges.Get(challengeId);
        Require(challenge.Status == 0, "NOT_VOTING");
        Require(Context.BlockNumber <= challenge.VotingEndBlock, "VOTING_ENDED");
        Require(Context.TxValue >= stakeWeight, "INSUFFICIENT_VOTE_STAKE");

        _voteCommits.Get(challengeId).Set(Context.Sender, new VoteCommit
        {
            CommitHash = commitHash,
            StakeWeight = stakeWeight,
            Revealed = false,
            VotedForChallenger = false
        });

        EmitEvent("VoteCommitted", challengeId, Context.Sender);
    }

    /// <summary>
    /// Reveal a previously committed vote during the reveal period.
    /// </summary>
    public void RevealVote(ulong challengeId, bool voteForChallenger, uint salt)
    {
        var challenge = _challenges.Get(challengeId);
        Require(Context.BlockNumber > challenge.VotingEndBlock, "VOTING_NOT_ENDED");
        Require(Context.BlockNumber <= challenge.RevealEndBlock, "REVEAL_ENDED");

        if (challenge.Status == 0)
        {
            challenge.Status = 1; // revealing
            _challenges.Set(challengeId, challenge);
        }

        var commit = _voteCommits.Get(challengeId).Get(Context.Sender);
        Require(!commit.Revealed, "ALREADY_REVEALED");

        // Verify commit hash
        Hash256 expectedHash = ComputeCommitHash(voteForChallenger, salt);
        Require(commit.CommitHash == expectedHash, "INVALID_REVEAL");

        commit.Revealed = true;
        commit.VotedForChallenger = voteForChallenger;
        _voteCommits.Get(challengeId).Set(Context.Sender, commit);

        // Update tally
        var tally = _voteTallies.Get(challengeId);
        if (voteForChallenger)
            tally.VotesForChallenger = tally.VotesForChallenger + commit.StakeWeight;
        else
            tally.VotesForApplicant = tally.VotesForApplicant + commit.StakeWeight;
        tally.VoterCount++;
        _voteTallies.Set(challengeId, tally);

        EmitEvent("VoteRevealed", challengeId, Context.Sender, voteForChallenger);
    }

    // --- Challenge Resolution ---

    /// <summary>
    /// Resolve a challenge after the reveal period ends.
    /// </summary>
    public void ResolveChallenge(ulong challengeId)
    {
        var challenge = _challenges.Get(challengeId);
        Require(Context.BlockNumber > challenge.RevealEndBlock, "REVEAL_NOT_ENDED");
        Require(challenge.Status <= 1, "ALREADY_RESOLVED");

        var tally = _voteTallies.Get(challengeId);
        challenge.ChallengerWon = tally.VotesForChallenger > tally.VotesForApplicant;
        challenge.Status = 2; // resolved
        _challenges.Set(challengeId, challenge);

        if (challenge.ChallengerWon)
        {
            // Challenger wins: applicant loses stake
            if (challenge.IsRemovalChallenge)
            {
                // Remove listing
                _isListed.Set(challenge.TargetListingHash, false);
                EmitEvent("ListingRemoved", challenge.TargetListingHash, challengeId);
            }

            // Distribute applicant's stake to challenger and voters
            DistributeRewards(challengeId, true);
        }
        else
        {
            // Applicant wins: challenger loses stake
            if (!challenge.IsRemovalChallenge)
            {
                // Accept the application
                EmitEvent("ChallengeDefeated", challengeId);
            }

            // Distribute challenger's stake to applicant and voters
            DistributeRewards(challengeId, false);
        }

        EmitEvent("ChallengeResolved", challengeId, challenge.ChallengerWon);
    }

    /// <summary>
    /// Claim accumulated voting rewards.
    /// </summary>
    public UInt256 ClaimRewards()
    {
        UInt256 rewards = _claimableRewards.Get(Context.Sender);
        Require(!rewards.IsZero, "NO_REWARDS");

        _claimableRewards.Set(Context.Sender, UInt256.Zero);
        Context.TransferNative(Context.Sender, rewards);

        EmitEvent("RewardsClaimed", Context.Sender, rewards);
        return rewards;
    }

    // --- Listing Management ---

    /// <summary>
    /// Renew an expiring listing by extending the expiry and maintaining the stake.
    /// </summary>
    public void RenewListing(Hash256 listingHash)
    {
        Require(_isListed.Get(listingHash), "NOT_LISTED");
        var listing = _listings.Get(listingHash);
        Require(Context.Sender == listing.Owner, "NOT_OWNER");

        listing.ExpiryBlock = Context.BlockNumber + _listingExpiryBlocks.Get();
        _listings.Set(listingHash, listing);

        EmitEvent("ListingRenewed", listingHash, listing.ExpiryBlock);
    }

    /// <summary>
    /// Voluntarily exit the registry and reclaim stake.
    /// </summary>
    public void ExitListing(Hash256 listingHash)
    {
        Require(_isListed.Get(listingHash), "NOT_LISTED");
        var listing = _listings.Get(listingHash);
        Require(Context.Sender == listing.Owner, "NOT_OWNER");

        _isListed.Set(listingHash, false);
        Context.TransferNative(listing.Owner, listing.Stake);

        EmitEvent("ListingExited", listingHash, listing.Owner);
    }

    // --- Query Methods ---

    public bool IsListed(Hash256 listingHash) => _isListed.Get(listingHash);
    public ListingEntry GetListing(Hash256 listingHash) => _listings.Get(listingHash);
    public Application GetApplication(ulong appId) => _applications.Get(appId);
    public Challenge GetChallenge(ulong challengeId) => _challenges.Get(challengeId);
    public VoteTally GetVoteTally(ulong challengeId) => _voteTallies.Get(challengeId);
    public UInt256 GetClaimableRewards(Address voter) => _claimableRewards.Get(voter);

    // --- Internal Helpers ---

    private void DistributeRewards(ulong challengeId, bool challengerWon)
    {
        var challenge = _challenges.Get(challengeId);
        UInt256 losingStake = challengerWon
            ? _applicationStake.Get()
            : challenge.ChallengerStake;

        uint winnerBps = _winnerRewardBps.Get();
        uint voterBps = _voterRewardBps.Get();
        uint protocolBps = _protocolFeeBps.Get();

        UInt256 winnerReward = (losingStake * winnerBps) / 10000;
        UInt256 voterPool = (losingStake * voterBps) / 10000;
        UInt256 protocolFee = (losingStake * protocolBps) / 10000;

        // Winner gets their reward
        Address winner = challengerWon ? challenge.Challenger : GetApplicantAddress(challenge);
        _claimableRewards.Set(winner,
            _claimableRewards.Get(winner) + winnerReward);

        // Voter rewards distributed proportionally to majority-side voters
        // (implementation distributes based on individual stake weights)

        EmitEvent("RewardsDistributed", challengeId, winnerReward, voterPool);
    }

    private Hash256 ComputeCommitHash(bool vote, uint salt) { /* keccak256 */ }
    private Address GetApplicantAddress(Challenge challenge) { /* ... */ }
    private bool VerifyCredentialProof(byte[] proof, Hash256 nullifier) { /* ... */ return true; }
}
```

## Complexity

**Medium** -- The core TCR mechanism (apply, challenge, vote, resolve) is a well-studied pattern with known game-theoretic properties. The commit-reveal voting scheme adds cryptographic complexity but is straightforward to implement. The primary challenges are: correct stake redistribution math (ensuring no funds are locked or lost), handling edge cases (no votes, tied votes, voter non-reveal), and parameterizer governance for updating TCR parameters. The factory pattern and per-registry parameterization add moderate configuration complexity.

## Priority

**P2** -- TCRs are valuable governance infrastructure but depend on sufficient community size and engagement to function effectively. They should be deployed after core governance (0x0102) and staking mechanisms are established, as they extend the community's ability to curate ecosystem quality. Useful for oracle provider curation, token safety lists, and validator trust scoring -- all of which become important as the ecosystem matures.
