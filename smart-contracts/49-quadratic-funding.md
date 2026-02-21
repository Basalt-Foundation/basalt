# Quadratic Funding (Gitcoin-style)

## Category

Governance / Public Goods Funding

## Summary

A quadratic funding contract that amplifies small donations to public goods projects using a matching pool. Individual contributions are matched from the pool based on the number of unique contributors rather than the amount contributed, following the quadratic funding formula: a project's matched amount is proportional to the square of the sum of square roots of individual contributions. This mechanism optimally allocates funds to projects with broad community support while resisting plutocratic capture. ZK identity integration provides Sybil resistance, the Achilles' heel of quadratic funding on other chains.

## Why It's Useful

- **Optimal public goods funding**: Quadratic funding is mathematically proven to be the optimal mechanism for funding public goods in the presence of positive externalities (Buterin, Hitzig, Weyl 2019).
- **Democratic capital allocation**: Projects with many small donors receive more matching than projects with a few large donors, reflecting genuine community preference rather than whale dominance.
- **Ecosystem growth**: Funding public goods (developer tools, documentation, education, infrastructure) is essential for ecosystem health but is chronically underfunded by market mechanisms. Quadratic funding corrects this market failure.
- **Sybil resistance via ZK identity**: The biggest challenge in quadratic funding is preventing one person from splitting their donation across multiple accounts to game the matching. Basalt's ZK compliance layer provides native Sybil resistance.
- **Proven model**: Gitcoin Grants has distributed over $50M in quadratic funding rounds, demonstrating the viability of this mechanism.
- **Community engagement**: Funding rounds create community moments that drive engagement, discussion, and ecosystem awareness.
- **Transparent and auditable**: All contributions and matching calculations are on-chain, providing complete transparency.

## Key Features

- Grant round lifecycle: admin creates a round with a start block, end block, and matching pool. Projects apply for inclusion. Round starts, contributions flow, round ends, matching is calculated and distributed.
- Project registration: project owners register with a description, team, and wallet address. Optional curation (community or admin approval) before inclusion in a round.
- Individual contributions: during the round, anyone can contribute any amount to any project. Each unique contributor increases the project's matching weight.
- Quadratic matching formula: match for project i = (sum of sqrt(contribution_j) for all contributors j)^2 - sum(contribution_j). This is then scaled proportionally to the matching pool.
- Sybil resistance via ZK identity: contributors must present a valid ZK identity proof (via SchemaRegistry/IssuerRegistry) to participate. One identity = one contribution per project. Without this, quadratic funding is trivially gameable.
- Matching cap: per-project matching can be capped to prevent a single project from consuming the entire pool
- Contribution limits: minimum and maximum contribution per contributor per project
- Round admin: admin can pause/unpause the round, extend deadlines, and resolve disputes
- Multi-round support: the contract manages multiple funding rounds, each with its own pool, projects, and contributions
- Matching pool top-up: anyone can add to the matching pool during the round
- Payout distribution: after the round ends, a finalization step calculates matching and distributes funds
- Contribution refunds: if a round is cancelled, contributions are refunded

## Basalt-Specific Advantages

- **ZK identity for Sybil resistance**: This is the single most important Basalt advantage for quadratic funding. Basalt's ZK compliance layer (SchemaRegistry, IssuerRegistry, Groth16 verification) provides native, privacy-preserving identity verification. Contributors prove they are unique humans without revealing their identity, solving the fundamental Sybil vulnerability of quadratic funding on other chains. No centralized identity provider (Gitcoin Passport, BrightID) is needed.
- **Existing Governance contract integration**: The quadratic funding contract can use the existing Governance contract for round parameter decisions and dispute resolution. Governance can vote to create rounds, set matching pool amounts, and resolve challenges.
- **Confidential contributions via Pedersen commitments**: Contribution amounts can be committed using Pedersen commitments during the round, preventing front-running and strategic contribution splitting. Amounts are revealed only during the finalization phase.
- **BLAKE3 contribution hashing**: Contribution records are hashed with BLAKE3 for efficient verification and Merkle proof construction, enabling light client verification of matching calculations.
- **UInt256 precision for matching math**: The quadratic formula involves squaring sums of square roots, requiring high-precision arithmetic. Basalt's native `UInt256` type provides 256 bits of precision, sufficient for large rounds with many contributors.
- **Cross-contract fund movement**: Matching pool funds can be sourced from the DAO Treasury via cross-contract calls, and project payouts can be routed through the Escrow contract for milestone-based release.
- **BST-3525 SFT for contribution receipts**: Each contribution can be minted as a BST-3525 SFT where the slot represents the project and the value represents the contribution amount. These receipts serve as proof of participation and can unlock future benefits (retroactive airdrops, community recognition).
- **AOT-compiled matching calculation**: The integer square root operations and proportional scaling required for matching distribution execute efficiently under AOT compilation.

## Token Standards Used

- **BST-20**: Contributions and matching payouts in BST-20 tokens or native BST
- **BST-3525**: Contribution receipt SFTs (slot = project, value = contribution amount)
- **BST-721**: Project registration NFTs and contributor badges

## Integration Points

- **Governance (0x...1005 area)**: Round creation and parameters can be governance-controlled. Dispute resolution for contested projects uses governance proposals.
- **SchemaRegistry (0x...1006)**: ZK identity verification for contributor uniqueness -- the core Sybil resistance mechanism.
- **IssuerRegistry (0x...1007)**: Trusted identity issuers who can certify contributor uniqueness.
- **Escrow (0x...1003)**: Milestone-based payout of matched funds to projects.
- **DAO Treasury**: Matching pool funding from the DAO treasury.
- **BNS (0x...1002)**: Human-readable project and contributor names.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Quadratic Funding -- matching pool amplifies small donations quadratically.
/// ZK identity integration for Sybil resistance. Gitcoin Grants-style mechanism.
/// </summary>
[BasaltContract]
public partial class QuadraticFunding
{
    // --- Governance / Admin ---
    private readonly StorageMap<string, string> _admin;
    private readonly byte[] _schemaRegistryAddress;

    // --- Round management ---
    private readonly StorageValue<ulong> _nextRoundId;
    private readonly StorageMap<string, string> _roundStatus;          // roundId -> "pending"|"active"|"ended"|"finalized"|"cancelled"
    private readonly StorageMap<string, ulong> _roundStartBlocks;      // roundId -> start block
    private readonly StorageMap<string, ulong> _roundEndBlocks;        // roundId -> end block
    private readonly StorageMap<string, UInt256> _roundMatchingPools;   // roundId -> matching pool amount
    private readonly StorageMap<string, UInt256> _roundTotalContributed; // roundId -> total contributions
    private readonly StorageMap<string, uint> _roundProjectCount;       // roundId -> project count
    private readonly StorageMap<string, UInt256> _roundMaxMatchPerProject; // roundId -> cap per project

    // --- Project registry ---
    private readonly StorageValue<ulong> _nextProjectId;
    private readonly StorageMap<string, string> _projectNames;          // projectId -> name
    private readonly StorageMap<string, string> _projectDescriptions;   // projectId -> description
    private readonly StorageMap<string, string> _projectOwners;         // projectId -> owner hex
    private readonly StorageMap<string, string> _projectRecipients;     // projectId -> wallet hex
    private readonly StorageMap<string, bool> _projectApproved;         // projectId -> approved for round

    // --- Round-Project mapping ---
    private readonly StorageMap<string, bool> _roundProjects;           // "roundId:projectId" -> included
    private readonly StorageMap<string, UInt256> _projectRoundContributions; // "roundId:projectId" -> total contributions
    private readonly StorageMap<string, uint> _projectRoundContributorCount; // "roundId:projectId" -> unique contributor count
    private readonly StorageMap<string, UInt256> _projectRoundSumSqrt;  // "roundId:projectId" -> sum of sqrt(contributions)

    // --- Contributions ---
    private readonly StorageMap<string, UInt256> _contributions;        // "roundId:projectId:contributorHex" -> amount
    private readonly StorageMap<string, bool> _hasContributed;          // "roundId:projectId:contributorHex" -> true

    // --- ZK identity tracking ---
    private readonly StorageMap<string, bool> _identityVerified;        // "roundId:contributorHex" -> verified

    // --- Results ---
    private readonly StorageMap<string, UInt256> _projectMatchedAmount; // "roundId:projectId" -> matched amount
    private readonly StorageMap<string, bool> _projectPaidOut;          // "roundId:projectId" -> paid

    // --- Config ---
    private readonly StorageValue<UInt256> _minContribution;
    private readonly StorageValue<UInt256> _maxContribution;

    public QuadraticFunding(byte[] schemaRegistryAddress,
        UInt256 minContribution = default, UInt256 maxContribution = default)
    {
        _schemaRegistryAddress = schemaRegistryAddress;

        _admin = new StorageMap<string, string>("qf_admin");
        _nextRoundId = new StorageValue<ulong>("qf_nrnd");
        _roundStatus = new StorageMap<string, string>("qf_rsts");
        _roundStartBlocks = new StorageMap<string, ulong>("qf_rstart");
        _roundEndBlocks = new StorageMap<string, ulong>("qf_rend");
        _roundMatchingPools = new StorageMap<string, UInt256>("qf_rpool");
        _roundTotalContributed = new StorageMap<string, UInt256>("qf_rtotal");
        _roundProjectCount = new StorageMap<string, uint>("qf_rpcnt");
        _roundMaxMatchPerProject = new StorageMap<string, UInt256>("qf_rmmax");
        _nextProjectId = new StorageValue<ulong>("qf_nproj");
        _projectNames = new StorageMap<string, string>("qf_pname");
        _projectDescriptions = new StorageMap<string, string>("qf_pdesc");
        _projectOwners = new StorageMap<string, string>("qf_pown");
        _projectRecipients = new StorageMap<string, string>("qf_prec");
        _projectApproved = new StorageMap<string, bool>("qf_pappr");
        _roundProjects = new StorageMap<string, bool>("qf_rp");
        _projectRoundContributions = new StorageMap<string, UInt256>("qf_prc");
        _projectRoundContributorCount = new StorageMap<string, uint>("qf_prcc");
        _projectRoundSumSqrt = new StorageMap<string, UInt256>("qf_prss");
        _contributions = new StorageMap<string, UInt256>("qf_cont");
        _hasContributed = new StorageMap<string, bool>("qf_hcont");
        _identityVerified = new StorageMap<string, bool>("qf_idv");
        _projectMatchedAmount = new StorageMap<string, UInt256>("qf_pmatch");
        _projectPaidOut = new StorageMap<string, bool>("qf_ppaid");
        _minContribution = new StorageValue<UInt256>("qf_minc");
        _maxContribution = new StorageValue<UInt256>("qf_maxc");

        _admin.Set("admin", Convert.ToHexString(Context.Caller));
        if (minContribution.IsZero) minContribution = UInt256.One;
        _minContribution.Set(minContribution);
        if (maxContribution.IsZero) maxContribution = new UInt256(1_000_000);
        _maxContribution.Set(maxContribution);
    }

    // ===================== Round Management =====================

    /// <summary>
    /// Create a new funding round. Send matching pool funds as value.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateRound(ulong startBlock, ulong endBlock, UInt256 maxMatchPerProject)
    {
        RequireAdmin();
        Context.Require(!Context.TxValue.IsZero, "QF: must fund matching pool");
        Context.Require(endBlock > startBlock, "QF: invalid period");

        var id = _nextRoundId.Get();
        _nextRoundId.Set(id + 1);
        var key = id.ToString();

        _roundStatus.Set(key, "pending");
        _roundStartBlocks.Set(key, startBlock);
        _roundEndBlocks.Set(key, endBlock);
        _roundMatchingPools.Set(key, Context.TxValue);
        _roundMaxMatchPerProject.Set(key, maxMatchPerProject);

        Context.Emit(new RoundCreatedEvent
        {
            RoundId = id, MatchingPool = Context.TxValue,
            StartBlock = startBlock, EndBlock = endBlock
        });
        return id;
    }

    /// <summary>
    /// Start a round (transitions from pending to active).
    /// </summary>
    [BasaltEntrypoint]
    public void StartRound(ulong roundId)
    {
        RequireAdmin();
        var key = roundId.ToString();
        Context.Require(_roundStatus.Get(key) == "pending", "QF: not pending");
        Context.Require(Context.BlockHeight >= _roundStartBlocks.Get(key), "QF: too early");
        _roundStatus.Set(key, "active");
    }

    /// <summary>
    /// Add to the matching pool during a round.
    /// </summary>
    [BasaltEntrypoint]
    public void TopUpMatchingPool(ulong roundId)
    {
        Context.Require(!Context.TxValue.IsZero, "QF: must send value");
        var key = roundId.ToString();
        var status = _roundStatus.Get(key);
        Context.Require(status == "pending" || status == "active", "QF: round not accepting funds");

        _roundMatchingPools.Set(key, _roundMatchingPools.Get(key) + Context.TxValue);

        Context.Emit(new PoolToppedUpEvent { RoundId = roundId, Amount = Context.TxValue });
    }

    // ===================== Project Management =====================

    /// <summary>
    /// Register a project for funding rounds.
    /// </summary>
    [BasaltEntrypoint]
    public ulong RegisterProject(string name, string description, byte[] recipientWallet)
    {
        Context.Require(!string.IsNullOrEmpty(name), "QF: name required");
        Context.Require(recipientWallet.Length > 0, "QF: wallet required");

        var id = _nextProjectId.Get();
        _nextProjectId.Set(id + 1);
        var key = id.ToString();

        _projectNames.Set(key, name);
        _projectDescriptions.Set(key, description);
        _projectOwners.Set(key, Convert.ToHexString(Context.Caller));
        _projectRecipients.Set(key, Convert.ToHexString(recipientWallet));

        Context.Emit(new ProjectRegisteredEvent
        {
            ProjectId = id, Name = name, Owner = Context.Caller
        });
        return id;
    }

    /// <summary>
    /// Approve a project for inclusion in a round.
    /// </summary>
    [BasaltEntrypoint]
    public void ApproveProjectForRound(ulong roundId, ulong projectId)
    {
        RequireAdmin();
        var rpKey = roundId.ToString() + ":" + projectId.ToString();
        _roundProjects.Set(rpKey, true);

        var count = _roundProjectCount.Get(roundId.ToString());
        _roundProjectCount.Set(roundId.ToString(), count + 1);
    }

    // ===================== Contributions =====================

    /// <summary>
    /// Contribute to a project in a round. Requires ZK identity verification.
    /// Send contribution as value.
    /// </summary>
    [BasaltEntrypoint]
    public void Contribute(ulong roundId, ulong projectId)
    {
        var roundKey = roundId.ToString();
        Context.Require(_roundStatus.Get(roundKey) == "active", "QF: round not active");
        Context.Require(Context.BlockHeight <= _roundEndBlocks.Get(roundKey), "QF: round ended");

        var rpKey = roundKey + ":" + projectId.ToString();
        Context.Require(_roundProjects.Get(rpKey), "QF: project not in round");

        Context.Require(!Context.TxValue.IsZero, "QF: must contribute");
        Context.Require(Context.TxValue >= _minContribution.Get(), "QF: below minimum");
        Context.Require(Context.TxValue <= _maxContribution.Get(), "QF: above maximum");

        var contributorHex = Convert.ToHexString(Context.Caller);

        // ZK identity check -- contributor must be verified
        var idKey = roundKey + ":" + contributorHex;
        // In production: Context.CallContract<bool>(_schemaRegistryAddress, "HasValidCredential", Context.Caller);
        // For now, we track verification status
        Context.Require(_identityVerified.Get(idKey), "QF: identity not verified");

        // Check unique contribution (one contribution per contributor per project per round)
        var contribKey = rpKey + ":" + contributorHex;
        Context.Require(!_hasContributed.Get(contribKey), "QF: already contributed");

        _hasContributed.Set(contribKey, true);
        _contributions.Set(contribKey, Context.TxValue);

        // Update project stats
        var totalContrib = _projectRoundContributions.Get(rpKey);
        _projectRoundContributions.Set(rpKey, totalContrib + Context.TxValue);

        var contribCount = _projectRoundContributorCount.Get(rpKey);
        _projectRoundContributorCount.Set(rpKey, contribCount + 1);

        // Update sum of square roots (in basis points for precision)
        var sqrtContrib = IntegerSqrt(Context.TxValue);
        var sumSqrt = _projectRoundSumSqrt.Get(rpKey);
        _projectRoundSumSqrt.Set(rpKey, sumSqrt + sqrtContrib);

        // Update round total
        _roundTotalContributed.Set(roundKey, _roundTotalContributed.Get(roundKey) + Context.TxValue);

        Context.Emit(new ContributionMadeEvent
        {
            RoundId = roundId, ProjectId = projectId,
            Contributor = Context.Caller, Amount = Context.TxValue
        });
    }

    /// <summary>
    /// Verify a contributor's ZK identity for a round.
    /// In production, this would verify a ZK proof via SchemaRegistry.
    /// </summary>
    [BasaltEntrypoint]
    public void VerifyIdentity(ulong roundId, byte[] contributor)
    {
        // Admin or automated verification via SchemaRegistry
        RequireAdmin();
        var key = roundId.ToString() + ":" + Convert.ToHexString(contributor);
        _identityVerified.Set(key, true);

        Context.Emit(new IdentityVerifiedEvent
        {
            RoundId = roundId, Contributor = contributor
        });
    }

    // ===================== Finalization =====================

    /// <summary>
    /// End a round and calculate matching.
    /// </summary>
    [BasaltEntrypoint]
    public void EndRound(ulong roundId)
    {
        RequireAdmin();
        var key = roundId.ToString();
        Context.Require(_roundStatus.Get(key) == "active", "QF: not active");
        _roundStatus.Set(key, "ended");
    }

    /// <summary>
    /// Calculate and record the matched amount for a project.
    /// Called per-project during finalization.
    ///
    /// Quadratic matching formula:
    ///   raw_match = (sum of sqrt(c_j))^2 - sum(c_j)
    ///   scaled_match = raw_match * matchingPool / total_raw_match_all_projects
    /// </summary>
    [BasaltEntrypoint]
    public void CalculateMatch(ulong roundId, ulong projectId, UInt256 totalSumSqrtSquaredAllProjects)
    {
        RequireAdmin();
        var roundKey = roundId.ToString();
        Context.Require(_roundStatus.Get(roundKey) == "ended", "QF: not ended");

        var rpKey = roundKey + ":" + projectId.ToString();

        var sumSqrt = _projectRoundSumSqrt.Get(rpKey);
        var sumSqrtSquared = sumSqrt * sumSqrt;
        var directContributions = _projectRoundContributions.Get(rpKey);

        // Raw match = (sum_sqrt)^2 - direct_contributions
        var rawMatch = sumSqrtSquared > directContributions
            ? sumSqrtSquared - directContributions
            : UInt256.Zero;

        // Scale to matching pool
        var pool = _roundMatchingPools.Get(roundKey);
        var scaledMatch = !totalSumSqrtSquaredAllProjects.IsZero
            ? rawMatch * pool / totalSumSqrtSquaredAllProjects
            : UInt256.Zero;

        // Apply per-project cap
        var maxMatch = _roundMaxMatchPerProject.Get(roundKey);
        if (!maxMatch.IsZero && scaledMatch > maxMatch)
            scaledMatch = maxMatch;

        _projectMatchedAmount.Set(rpKey, scaledMatch);

        Context.Emit(new MatchCalculatedEvent
        {
            RoundId = roundId, ProjectId = projectId,
            RawMatch = rawMatch, ScaledMatch = scaledMatch,
            ContributorCount = _projectRoundContributorCount.Get(rpKey)
        });
    }

    /// <summary>
    /// Pay out matched funds to a project.
    /// </summary>
    [BasaltEntrypoint]
    public void PayoutProject(ulong roundId, ulong projectId)
    {
        RequireAdmin();
        var roundKey = roundId.ToString();
        var rpKey = roundKey + ":" + projectId.ToString();

        Context.Require(!_projectPaidOut.Get(rpKey), "QF: already paid");

        var matchedAmount = _projectMatchedAmount.Get(rpKey);
        var directContributions = _projectRoundContributions.Get(rpKey);
        var totalPayout = matchedAmount + directContributions;

        Context.Require(!totalPayout.IsZero, "QF: nothing to pay");

        _projectPaidOut.Set(rpKey, true);
        var recipient = Convert.FromHexString(_projectRecipients.Get(projectId.ToString()));
        Context.TransferNative(recipient, totalPayout);

        Context.Emit(new ProjectPaidEvent
        {
            RoundId = roundId, ProjectId = projectId,
            DirectContributions = directContributions,
            MatchedAmount = matchedAmount, TotalPayout = totalPayout
        });
    }

    /// <summary>
    /// Finalize the round after all projects are paid.
    /// </summary>
    [BasaltEntrypoint]
    public void FinalizeRound(ulong roundId)
    {
        RequireAdmin();
        var key = roundId.ToString();
        Context.Require(_roundStatus.Get(key) == "ended", "QF: not ended");
        _roundStatus.Set(key, "finalized");

        Context.Emit(new RoundFinalizedEvent { RoundId = roundId });
    }

    // ===================== Views =====================

    [BasaltView]
    public string GetRoundStatus(ulong roundId) => _roundStatus.Get(roundId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetMatchingPool(ulong roundId) => _roundMatchingPools.Get(roundId.ToString());

    [BasaltView]
    public UInt256 GetProjectContributions(ulong roundId, ulong projectId)
        => _projectRoundContributions.Get(roundId.ToString() + ":" + projectId.ToString());

    [BasaltView]
    public uint GetProjectContributorCount(ulong roundId, ulong projectId)
        => _projectRoundContributorCount.Get(roundId.ToString() + ":" + projectId.ToString());

    [BasaltView]
    public UInt256 GetProjectMatchedAmount(ulong roundId, ulong projectId)
        => _projectMatchedAmount.Get(roundId.ToString() + ":" + projectId.ToString());

    [BasaltView]
    public UInt256 GetContribution(ulong roundId, ulong projectId, byte[] contributor)
        => _contributions.Get(roundId.ToString() + ":" + projectId.ToString() + ":" + Convert.ToHexString(contributor));

    [BasaltView]
    public string GetProjectName(ulong projectId) => _projectNames.Get(projectId.ToString()) ?? "";

    [BasaltView]
    public uint GetRoundProjectCount(ulong roundId) => _roundProjectCount.Get(roundId.ToString());

    // ===================== Internal =====================

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "QF: not admin");
    }

    /// <summary>
    /// Integer square root using Newton's method (same as Governance contract).
    /// </summary>
    private static UInt256 IntegerSqrt(UInt256 n)
    {
        if (n.IsZero) return UInt256.Zero;
        if (n == UInt256.One) return UInt256.One;
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

// ===================== Events =====================

[BasaltEvent]
public class RoundCreatedEvent
{
    [Indexed] public ulong RoundId { get; set; }
    public UInt256 MatchingPool { get; set; }
    public ulong StartBlock { get; set; }
    public ulong EndBlock { get; set; }
}

[BasaltEvent]
public class PoolToppedUpEvent
{
    [Indexed] public ulong RoundId { get; set; }
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class ProjectRegisteredEvent
{
    [Indexed] public ulong ProjectId { get; set; }
    public string Name { get; set; } = "";
    [Indexed] public byte[] Owner { get; set; } = null!;
}

[BasaltEvent]
public class ContributionMadeEvent
{
    [Indexed] public ulong RoundId { get; set; }
    [Indexed] public ulong ProjectId { get; set; }
    [Indexed] public byte[] Contributor { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class IdentityVerifiedEvent
{
    [Indexed] public ulong RoundId { get; set; }
    public byte[] Contributor { get; set; } = null!;
}

[BasaltEvent]
public class MatchCalculatedEvent
{
    [Indexed] public ulong RoundId { get; set; }
    [Indexed] public ulong ProjectId { get; set; }
    public UInt256 RawMatch { get; set; }
    public UInt256 ScaledMatch { get; set; }
    public uint ContributorCount { get; set; }
}

[BasaltEvent]
public class ProjectPaidEvent
{
    [Indexed] public ulong RoundId { get; set; }
    [Indexed] public ulong ProjectId { get; set; }
    public UInt256 DirectContributions { get; set; }
    public UInt256 MatchedAmount { get; set; }
    public UInt256 TotalPayout { get; set; }
}

[BasaltEvent]
public class RoundFinalizedEvent
{
    [Indexed] public ulong RoundId { get; set; }
}
```

## Complexity

**High** -- The quadratic matching formula requires precise integer square root computation (which Basalt's Governance contract already implements) and careful proportional scaling across all projects. The multi-phase lifecycle (round creation -> project registration -> contribution -> finalization -> payout) involves significant state management. The ZK identity verification integration adds cross-contract complexity. The finalization step must process all projects and normalize matching amounts against the pool, which may require gas-efficient batching for rounds with many projects.

## Priority

**P1** -- Quadratic funding is one of the most impactful governance mechanisms for ecosystem health. It directly incentivizes public goods creation and channels matching funds to projects with genuine community support. Basalt's ZK identity layer gives it a unique advantage in solving the Sybil problem that plagues quadratic funding on other chains. This should be deployed within the first year of mainnet, ideally in time for the first community funding round.
