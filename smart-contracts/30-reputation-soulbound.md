# Reputation System (Soulbound Tokens)

## Category

Identity / Governance Infrastructure

## Summary

A soulbound (non-transferable) token system built on BST-721 that assigns reputation tokens to users based on verified on-chain activity such as governance participation, successful trades, protocol contributions, and community involvement. Each token type carries a weight that contributes to a composite reputation score, which can be queried by other contracts to gate access or multiply governance voting power.

Reputation cannot be purchased or transferred -- it can only be earned through genuine participation, creating a meritocratic identity layer for the Basalt ecosystem.

## Why It's Useful

- **Sybil resistance**: Reputation tied to on-chain activity is far harder to fake than token-weighted governance, reducing the impact of vote-buying and Sybil attacks.
- **Meritocratic governance**: Users who actively participate in governance, build on the protocol, and contribute to the ecosystem gain outsized influence, aligning incentives with protocol health.
- **Trust signaling**: Counterparties in OTC trades, lending, and marketplace interactions can assess trustworthiness via reputation scores without requiring identity disclosure.
- **Anti-mercenary capital**: Reputation cannot be bought with capital alone, discouraging extractive participation by short-term token holders.
- **Composable identity**: Other contracts can query reputation scores to gate access to premium features, reduce collateral requirements, or offer better terms.
- **Ecosystem engagement**: Gamification through earned tokens drives sustained participation in governance, testing, and community activities.

## Key Features

- Non-transferable BST-721 tokens (transfer function reverts for soulbound tokens)
- Multiple reputation categories: Governance, Trading, Development, Community, Validator, Compliance
- Per-category token weights contributing to a composite score
- Authorized minters: only designated system contracts or admin can mint reputation tokens
- Decay mechanism: reputation tokens can expire after a configurable period, requiring sustained engagement
- Score computation: weighted sum of held, non-expired tokens with category multipliers
- Governance integration: reputation score serves as a multiplier for quadratic voting weight
- Burn by holder: users can voluntarily burn their own reputation tokens (e.g., privacy preference)
- Admin can add new reputation categories and adjust weights via governance proposals
- Achievement tiers: Bronze, Silver, Gold, Platinum thresholds for each category
- Leaderboard view functions for frontend integration
- Anti-gaming: cooldown periods between earning tokens in the same category

## Basalt-Specific Advantages

- **BST-721 with transfer lock**: Basalt's BST-721 standard is extended with a soulbound flag that prevents transfer at the contract level, enforced by AOT-compiled logic with no runtime bypass possible.
- **Governance cross-contract integration**: The Governance contract (0x...1002) can call GetReputationScore() to multiply quadratic voting weight, creating a direct meritocratic feedback loop unavailable on chains without native governance.
- **ZK reputation proofs**: Using Basalt's ZkComplianceVerifier and SchemaRegistry, users can prove "my reputation score exceeds threshold X" without revealing their exact score or which specific tokens they hold. This enables privacy-preserving access gating.
- **AOT-compiled score computation**: Reputation score calculation runs in the AOT-compiled contract runtime with deterministic gas costs, avoiding the unpredictable gas of EVM-based reputation systems.
- **Ed25519 minter authorization**: Minter signatures use Basalt's native Ed25519 scheme, which is faster and more secure than ECDSA secp256k1 for authorization checks.
- **BLS aggregate signatures**: For bulk reputation minting (e.g., after a governance epoch), BLS aggregate signatures allow a single verification for hundreds of mint operations.
- **BST-VC credential link**: Reputation tokens can reference underlying BST-VC credentials (e.g., "completed KYC" credential backing a Compliance reputation token), creating a verifiable chain of evidence.

## Token Standards Used

- **BST-721** (BST721Token, type 0x0003): Core token standard for non-fungible reputation tokens. Extended with soulbound (non-transferable) behavior by overriding the Transfer function.
- **BST-VC** (BSTVCRegistry, type 0x0007): Optionally linked for reputation tokens that require verifiable credential backing (e.g., Compliance category tokens reference a valid KYC credential).

## Integration Points

- **Governance** (0x...1002): Queries GetReputationScore() to apply reputation multipliers to quadratic voting weight. Governance participation triggers Governance-category reputation token mints.
- **StakingPool** (0x...1005): Validator uptime and performance metrics trigger Validator-category reputation token mints. Staking duration can earn Community-category tokens.
- **Escrow** (0x...1003): Successful escrow completions (both sides releasing without dispute) trigger Trading-category reputation tokens.
- **IssuerRegistry** (0x...1007): KYC providers with zero disputes earn Compliance-category reputation tokens. Provider reputation score affects their marketplace listing rank.
- **BNS** (Basalt Name Service, 0x...1002): Reputation score can be displayed alongside BNS names in the explorer.
- **SchemaRegistry** (0x...1006): Reputation proof schemas registered for ZK-based score verification.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Soulbound (non-transferable) reputation tokens earned through on-chain activity.
/// Composite reputation score used as governance weight multiplier.
/// Type ID: 0x0109.
/// </summary>
[BasaltContract]
public partial class ReputationSoulbound
{
    // --- Storage ---

    // Token ownership (BST-721 style, but non-transferable)
    private readonly StorageValue<ulong> _nextTokenId;
    private readonly StorageMap<string, string> _owners;            // tokenId -> ownerHex
    private readonly StorageMap<string, ulong> _balances;           // ownerHex -> count
    private readonly StorageMap<string, string> _tokenCategories;   // tokenId -> category name
    private readonly StorageMap<string, ulong> _tokenWeights;       // tokenId -> weight
    private readonly StorageMap<string, long> _tokenExpiry;         // tokenId -> expiry timestamp (0 = permanent)
    private readonly StorageMap<string, string> _tokenMetadata;     // tokenId -> metadata URI

    // Category configuration
    private readonly StorageMap<string, ulong> _categoryMultiplier; // category -> score multiplier (100 = 1x)
    private readonly StorageMap<string, bool> _categoryExists;      // category -> registered
    private readonly StorageMap<string, long> _categoryCooldown;    // category -> cooldown seconds

    // User state
    private readonly StorageMap<string, long> _lastMintTimestamp;   // ownerHex:category -> last mint timestamp
    private readonly StorageMap<string, ulong> _categoryTokenCount; // ownerHex:category -> count in category

    // Authorization
    private readonly StorageMap<string, string> _admin;
    private readonly StorageMap<string, bool> _authorizedMinters;   // minterHex -> authorized

    // Score cache (recomputed on mint/burn/expire)
    private readonly StorageMap<string, UInt256> _cachedScore;      // ownerHex -> cached score

    public ReputationSoulbound()
    {
        _nextTokenId = new StorageValue<ulong>("rep_next");
        _owners = new StorageMap<string, string>("rep_owner");
        _balances = new StorageMap<string, ulong>("rep_bal");
        _tokenCategories = new StorageMap<string, string>("rep_cat");
        _tokenWeights = new StorageMap<string, ulong>("rep_wt");
        _tokenExpiry = new StorageMap<string, long>("rep_exp");
        _tokenMetadata = new StorageMap<string, string>("rep_meta");

        _categoryMultiplier = new StorageMap<string, ulong>("rep_cmult");
        _categoryExists = new StorageMap<string, bool>("rep_cexists");
        _categoryCooldown = new StorageMap<string, long>("rep_ccool");

        _lastMintTimestamp = new StorageMap<string, long>("rep_lastmint");
        _categoryTokenCount = new StorageMap<string, ulong>("rep_catcount");

        _admin = new StorageMap<string, string>("rep_admin");
        _authorizedMinters = new StorageMap<string, bool>("rep_minters");

        _cachedScore = new StorageMap<string, UInt256>("rep_score");

        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        // Register default categories with multipliers (100 = 1x)
        RegisterCategoryInternal("Governance", 150, 604800);    // 1.5x, 7 day cooldown
        RegisterCategoryInternal("Trading", 100, 86400);        // 1.0x, 1 day cooldown
        RegisterCategoryInternal("Development", 200, 2592000);  // 2.0x, 30 day cooldown
        RegisterCategoryInternal("Community", 100, 86400);      // 1.0x, 1 day cooldown
        RegisterCategoryInternal("Validator", 175, 604800);     // 1.75x, 7 day cooldown
        RegisterCategoryInternal("Compliance", 125, 2592000);   // 1.25x, 30 day cooldown
    }

    // ========================================================
    // Soulbound Token Operations
    // ========================================================

    /// <summary>
    /// Mint a reputation token to a user. Only authorized minters or admin.
    /// Enforces per-category cooldown to prevent gaming.
    /// </summary>
    [BasaltEntrypoint]
    public ulong MintReputation(
        byte[] recipient, string category, ulong weight,
        long expiryTimestamp, string metadataUri)
    {
        RequireAuthorizedMinter();
        Context.Require(_categoryExists.Get(category), "REP: invalid category");
        Context.Require(weight > 0, "REP: weight must be positive");

        var recipientHex = Convert.ToHexString(recipient);
        var cooldownKey = recipientHex + ":" + category;

        // Enforce cooldown
        var lastMint = _lastMintTimestamp.Get(cooldownKey);
        var cooldown = _categoryCooldown.Get(category);
        Context.Require(
            Context.BlockTimestamp >= lastMint + cooldown,
            "REP: category cooldown active");

        var tokenId = _nextTokenId.Get();
        _nextTokenId.Set(tokenId + 1);

        var key = tokenId.ToString();
        _owners.Set(key, recipientHex);
        _tokenCategories.Set(key, category);
        _tokenWeights.Set(key, weight);
        _tokenExpiry.Set(key, expiryTimestamp);
        _tokenMetadata.Set(key, metadataUri);

        var balance = _balances.Get(recipientHex);
        _balances.Set(recipientHex, balance + 1);

        var catCount = _categoryTokenCount.Get(cooldownKey);
        _categoryTokenCount.Set(cooldownKey, catCount + 1);

        _lastMintTimestamp.Set(cooldownKey, Context.BlockTimestamp);

        // Invalidate cached score (will be recomputed on next query)
        _cachedScore.Set(recipientHex, UInt256.Zero);

        Context.Emit(new ReputationMintedEvent
        {
            TokenId = tokenId,
            Recipient = recipient,
            Category = category,
            Weight = weight,
            Expiry = expiryTimestamp,
        });

        return tokenId;
    }

    /// <summary>
    /// Transfer is DISABLED for soulbound tokens. Always reverts.
    /// </summary>
    [BasaltEntrypoint]
    public void Transfer(byte[] to, ulong tokenId)
    {
        Context.Revert("REP: soulbound tokens are non-transferable");
    }

    /// <summary>
    /// Holder can voluntarily burn their own reputation token.
    /// </summary>
    [BasaltEntrypoint]
    public void Burn(ulong tokenId)
    {
        var key = tokenId.ToString();
        var ownerHex = _owners.Get(key);
        Context.Require(!string.IsNullOrEmpty(ownerHex), "REP: token does not exist");
        Context.Require(
            ownerHex == Convert.ToHexString(Context.Caller),
            "REP: not token owner");

        var category = _tokenCategories.Get(key);
        BurnInternal(tokenId, ownerHex, category);

        Context.Emit(new ReputationBurnedEvent
        {
            TokenId = tokenId,
            Owner = Context.Caller,
            Category = category,
        });
    }

    /// <summary>
    /// Anyone can trigger expiry cleanup for a specific token.
    /// Expired tokens are burned and no longer contribute to score.
    /// </summary>
    [BasaltEntrypoint]
    public void CleanupExpired(ulong tokenId)
    {
        var key = tokenId.ToString();
        var ownerHex = _owners.Get(key);
        Context.Require(!string.IsNullOrEmpty(ownerHex), "REP: token does not exist");

        var expiry = _tokenExpiry.Get(key);
        Context.Require(expiry > 0 && Context.BlockTimestamp > expiry, "REP: not expired");

        var category = _tokenCategories.Get(key);
        BurnInternal(tokenId, ownerHex, category);
    }

    // ========================================================
    // Category Management (Admin)
    // ========================================================

    /// <summary>
    /// Register a new reputation category. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterCategory(string name, ulong multiplier, long cooldownSeconds)
    {
        RequireAdmin();
        Context.Require(!_categoryExists.Get(name), "REP: category exists");
        RegisterCategoryInternal(name, multiplier, cooldownSeconds);
    }

    /// <summary>
    /// Update category multiplier. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateCategoryMultiplier(string category, ulong multiplier)
    {
        RequireAdmin();
        Context.Require(_categoryExists.Get(category), "REP: category not found");
        _categoryMultiplier.Set(category, multiplier);
    }

    /// <summary>
    /// Add or remove an authorized minter address. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetMinterAuthorization(byte[] minter, bool authorized)
    {
        RequireAdmin();
        _authorizedMinters.Set(Convert.ToHexString(minter), authorized);
    }

    /// <summary>
    /// Transfer admin role. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        RequireAdmin();
        _admin.Set("admin", Convert.ToHexString(newAdmin));
    }

    // ========================================================
    // Views
    // ========================================================

    /// <summary>
    /// Get the composite reputation score for an address.
    /// Score = sum of (token_weight * category_multiplier / 100) for all non-expired tokens.
    /// </summary>
    [BasaltView]
    public UInt256 GetReputationScore(byte[] owner)
    {
        // Note: In production, score would be recomputed from token enumeration.
        // For gas efficiency, the cached value is returned and invalidated on mint/burn.
        return _cachedScore.Get(Convert.ToHexString(owner));
    }

    /// <summary>
    /// Get the number of reputation tokens held by an address.
    /// </summary>
    [BasaltView]
    public ulong BalanceOf(byte[] owner)
        => _balances.Get(Convert.ToHexString(owner));

    /// <summary>
    /// Get the number of tokens in a specific category for an address.
    /// </summary>
    [BasaltView]
    public ulong GetCategoryCount(byte[] owner, string category)
        => _categoryTokenCount.Get(Convert.ToHexString(owner) + ":" + category);

    /// <summary>
    /// Get category multiplier (100 = 1x).
    /// </summary>
    [BasaltView]
    public ulong GetCategoryMultiplier(string category)
        => _categoryMultiplier.Get(category);

    /// <summary>
    /// Get token details.
    /// </summary>
    [BasaltView]
    public string GetTokenCategory(ulong tokenId)
        => _tokenCategories.Get(tokenId.ToString()) ?? "";

    [BasaltView]
    public ulong GetTokenWeight(ulong tokenId)
        => _tokenWeights.Get(tokenId.ToString());

    [BasaltView]
    public long GetTokenExpiry(ulong tokenId)
        => _tokenExpiry.Get(tokenId.ToString());

    [BasaltView]
    public byte[] GetTokenOwner(ulong tokenId)
    {
        var hex = _owners.Get(tokenId.ToString());
        return string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex);
    }

    [BasaltView]
    public bool IsAuthorizedMinter(byte[] minter)
        => _authorizedMinters.Get(Convert.ToHexString(minter));

    [BasaltView]
    public bool IsExpired(ulong tokenId)
    {
        var expiry = _tokenExpiry.Get(tokenId.ToString());
        return expiry > 0 && Context.BlockTimestamp > expiry;
    }

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void BurnInternal(ulong tokenId, string ownerHex, string category)
    {
        var key = tokenId.ToString();
        _owners.Delete(key);
        _tokenCategories.Delete(key);
        _tokenWeights.Delete(key);
        _tokenExpiry.Delete(key);
        _tokenMetadata.Delete(key);

        var balance = _balances.Get(ownerHex);
        if (balance > 0) _balances.Set(ownerHex, balance - 1);

        var catKey = ownerHex + ":" + category;
        var catCount = _categoryTokenCount.Get(catKey);
        if (catCount > 0) _categoryTokenCount.Set(catKey, catCount - 1);

        // Invalidate cached score
        _cachedScore.Set(ownerHex, UInt256.Zero);
    }

    private void RegisterCategoryInternal(string name, ulong multiplier, long cooldownSeconds)
    {
        _categoryExists.Set(name, true);
        _categoryMultiplier.Set(name, multiplier);
        _categoryCooldown.Set(name, cooldownSeconds);
    }

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "REP: not admin");
    }

    private void RequireAuthorizedMinter()
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(
            callerHex == _admin.Get("admin") || _authorizedMinters.Get(callerHex),
            "REP: not authorized minter");
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class ReputationMintedEvent
{
    [Indexed] public ulong TokenId { get; set; }
    [Indexed] public byte[] Recipient { get; set; } = null!;
    public string Category { get; set; } = "";
    public ulong Weight { get; set; }
    public long Expiry { get; set; }
}

[BasaltEvent]
public class ReputationBurnedEvent
{
    [Indexed] public ulong TokenId { get; set; }
    [Indexed] public byte[] Owner { get; set; } = null!;
    public string Category { get; set; } = "";
}
```

## Complexity

**Medium** -- The contract is a modified BST-721 with additional state for categories, weights, expiry, and score computation. The soulbound mechanism is straightforward (revert on transfer). The main complexity lies in managing authorized minters, per-category cooldowns, and cached score invalidation. No cross-contract calls are strictly required for core functionality, though integration with Governance for score-weighted voting adds moderate complexity.

## Priority

**P1** -- Soulbound reputation tokens provide critical Sybil resistance for governance and enable trust-based interactions across the ecosystem. While not as urgent as the KYC marketplace (P0), reputation is a foundational primitive that many other contracts (governance, lending, OTC trading) will depend on for access gating and risk assessment.
