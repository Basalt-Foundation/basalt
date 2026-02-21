# On-Chain Achievement System

## Category

Gamification / Ecosystem Engagement

## Summary

A soulbound BST-721 token system that awards non-transferable achievement badges to users who complete on-chain milestones across the Basalt ecosystem. Achievements range from simple actions (first transaction, first governance vote) to complex multi-step challenges (100 trades on the DEX, staking for 365 days), with rich metadata stored entirely on-chain. The system supports cross-protocol achievements that track activity across multiple contracts, serving as a comprehensive gamification layer for ecosystem engagement.

## Why It's Useful

- **Market need**: Blockchain ecosystems lack standardized mechanisms for recognizing and incentivizing user engagement. Achievement systems drive retention and create a sense of progression that keeps users active.
- **User benefit**: Users build a verifiable on-chain reputation through their achievements. Soulbound tokens ensure achievements represent genuine accomplishments rather than purchased credentials.
- **Ecosystem growth**: Achievements incentivize exploration of ecosystem features. A user who earns a "First Governance Vote" badge may continue participating in governance. Cross-protocol achievements drive usage across multiple dApps.
- **Social signaling**: Achievement badges serve as status symbols in the community. Profiles displaying rare achievements build social capital and community belonging.
- **Developer utility**: dApps can gate features or offer benefits based on achievement ownership (e.g., reduced fees for users with "Power Trader" achievement, early access for "Early Adopter" holders).

## Key Features

- **Soulbound tokens**: Achievement badges are non-transferable BST-721 tokens. They cannot be sold, traded, or moved to another address, ensuring they represent genuine accomplishment.
- **Achievement definitions**: Admin-defined achievements with name, description, category, difficulty tier, and unlock criteria stored on-chain.
- **Automatic detection**: For on-chain milestones, the contract can be called by authorized observer contracts that monitor blockchain activity and trigger achievement awards.
- **Manual claim with proof**: Some achievements require the user to submit proof (e.g., a Merkle proof of inclusion in a snapshot, or a signed attestation from an authorized service).
- **Progressive achievements**: Multi-tier achievements (Bronze/Silver/Gold/Platinum) that upgrade as the user meets increasingly difficult criteria.
- **Achievement metadata**: Full on-chain metadata including name, description, image hash, unlock timestamp, difficulty tier, and custom attributes.
- **Categories**: Achievements organized into categories (Trading, Governance, Staking, Social, Development, Gaming, Exploration) for display and filtering.
- **Cross-protocol tracking**: Achievements can require actions across multiple contracts (e.g., "DeFi Explorer" requires using the DEX, lending platform, and staking pool).
- **Rarity tracking**: The contract tracks how many addresses hold each achievement, enabling real-time rarity calculations.
- **Achievement points**: Each achievement awards points based on difficulty. Total points serve as a reputation score.
- **Seasonal achievements**: Time-limited achievements available only during specific periods, creating urgency and FOMO.
- **Revocation**: In rare cases (detected exploit, retroactive disqualification), achievements can be revoked by governance action.

## Basalt-Specific Advantages

- **ZK compliance credentials**: Achievements can serve as lightweight compliance credentials. A "KYC Verified" achievement (soulbound) indicates the user has completed verification without revealing personal details, leveraging Basalt's ZK compliance layer.
- **BST-VC integration**: Achievements can reference BST-VC verifiable credentials for professional or educational milestones. A developer who completes a Basalt certification can receive a soulbound achievement backed by a verifiable credential from a trusted issuer.
- **BNS profile integration**: Achievements are displayed alongside BNS names in the social profile contract (contract 57), creating a rich identity layer that combines human-readable names with verifiable accomplishments.
- **BLAKE3 proof verification**: Achievement proofs (Merkle proofs, snapshot inclusion proofs) use BLAKE3 for fast verification, making complex cross-protocol achievement validation gas-efficient.
- **AOT-compiled metadata storage**: On-chain metadata storage and retrieval executes at native speed, important for contracts or front-ends that need to read achievement data for multiple users in a single view.
- **Governance-controlled definitions**: Achievement definitions, categories, and point values are controlled by Governance proposals, ensuring the community shapes the incentive structure.
- **Ed25519 attestations**: Off-chain achievement attestations (from game servers, oracle services, or dApp backends) use Ed25519 signatures, native to Basalt and cheap to verify.

## Token Standards Used

- **BST-721**: Soulbound (non-transferable) tokens for achievement badges.
- **BST-VC**: Verifiable credentials backing professional and compliance-related achievements.

## Integration Points

- **BNS (0x...1002)**: Achievement badges are associated with BNS-named profiles. Users can showcase achievements through their BNS identity.
- **Governance (0x...1003)**: Achievement definitions and point values are governed by community proposals. Governance participation achievements track voting activity.
- **StakingPool (0x...1005)**: Staking-related achievements (e.g., "Diamond Hands: Staked for 1 year") query staking state for validation.
- **SchemaRegistry (0x...1006)**: Achievement metadata schemas registered for cross-application interoperability.
- **IssuerRegistry (0x...1007)**: Trusted issuers for credential-backed achievements.
- **Escrow (0x...1004)**: Achievement-gated escrow releases (e.g., bounty bonuses for highly-reputed users).
- **Social Profile (contract 57)**: Achievements displayed on user profiles.
- **Play-to-Earn (contract 53)**: Gaming achievements integrated with the rewards engine.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum AchievementCategory : byte
{
    Trading = 0,
    Governance = 1,
    Staking = 2,
    Social = 3,
    Development = 4,
    Gaming = 5,
    Exploration = 6,
    Compliance = 7,
    Seasonal = 8
}

public enum DifficultyTier : byte
{
    Bronze = 0,
    Silver = 1,
    Gold = 2,
    Platinum = 3,
    Diamond = 4
}

public struct AchievementDefinition
{
    public ulong AchievementId;
    public string Name;
    public string Description;
    public AchievementCategory Category;
    public DifficultyTier Tier;
    public ulong Points;
    public byte[] ImageHash;               // BLAKE3 hash of achievement image
    public ulong MaxHolders;               // 0 = unlimited
    public ulong CurrentHolders;
    public ulong SeasonStart;              // 0 = permanent
    public ulong SeasonEnd;                // 0 = permanent
    public bool RequiresProof;
    public bool Active;
}

public struct AchievementProgress
{
    public Address User;
    public ulong AchievementId;
    public ulong CurrentValue;             // Progress toward threshold
    public ulong TargetValue;              // Threshold for unlock
    public bool Completed;
}

public struct AwardedAchievement
{
    public ulong TokenId;                  // BST-721 soulbound token ID
    public ulong AchievementId;
    public Address Holder;
    public ulong AwardedAt;                // Block timestamp
    public ulong AwardedAtBlock;           // Block number
    public DifficultyTier Tier;
}

public struct UserProfile
{
    public Address User;
    public ulong TotalPoints;
    public ulong AchievementCount;
    public ulong FirstAchievementAt;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0204)]
public partial class AchievementSystem : SdkContractBase
{
    // Storage
    private StorageMap<ulong, AchievementDefinition> _definitions;
    private StorageMap<ulong, AwardedAchievement> _awarded;
    private StorageValue<ulong> _nextAchievementId;
    private StorageValue<ulong> _nextTokenId;
    private StorageValue<Address> _admin;

    // Composite key storage:
    // Progress keyed by BLAKE3(userId || achievementId)
    // UserProfile keyed by user address
    // Has-achievement keyed by BLAKE3(userId || achievementId) -> bool

    // --- Achievement Definition (Admin/Governance) ---

    /// <summary>
    /// Define a new achievement with full metadata.
    /// Only callable by admin or governance contract.
    /// </summary>
    public ulong DefineAchievement(
        string name,
        string description,
        AchievementCategory category,
        DifficultyTier tier,
        ulong points,
        byte[] imageHash,
        ulong maxHolders,
        ulong seasonStart,
        ulong seasonEnd,
        bool requiresProof
    );

    /// <summary>
    /// Update an existing achievement definition.
    /// Cannot change criteria after any awards have been made.
    /// </summary>
    public void UpdateAchievement(
        ulong achievementId,
        string name,
        string description,
        ulong points,
        byte[] imageHash
    );

    /// <summary>
    /// Deactivate an achievement. No new awards, existing badges remain.
    /// </summary>
    public void DeactivateAchievement(ulong achievementId);

    // --- Achievement Awarding ---

    /// <summary>
    /// Award an achievement to a user. Called by authorized observer
    /// contracts that detect on-chain milestones.
    /// Mints a soulbound BST-721 token to the user.
    /// </summary>
    public ulong AwardAchievement(Address user, ulong achievementId);

    /// <summary>
    /// Claim an achievement with proof. User submits a proof
    /// (Merkle proof, signed attestation, etc.) that the contract validates.
    /// </summary>
    public ulong ClaimAchievement(
        ulong achievementId,
        byte[] proof
    );

    /// <summary>
    /// Batch award achievements to multiple users (e.g., snapshot-based).
    /// Only callable by admin or governance.
    /// </summary>
    public void BatchAward(
        ulong achievementId,
        Address[] users
    );

    // --- Progressive Achievements ---

    /// <summary>
    /// Update progress toward a progressive achievement.
    /// Called by authorized observer contracts.
    /// If progress reaches the target, the achievement is automatically awarded.
    /// </summary>
    public void UpdateProgress(
        Address user,
        ulong achievementId,
        ulong incrementBy
    );

    /// <summary>
    /// Upgrade an achievement to a higher tier (e.g., Bronze -> Silver).
    /// Burns the existing soulbound token and mints a new one at the higher tier.
    /// </summary>
    public ulong UpgradeTier(
        Address user,
        ulong achievementId,
        DifficultyTier newTier
    );

    // --- Revocation ---

    /// <summary>
    /// Revoke an achievement (governance action only).
    /// Burns the soulbound token and decrements holder count.
    /// </summary>
    public void RevokeAchievement(Address user, ulong achievementId);

    // --- Observer Registration ---

    /// <summary>
    /// Register a contract as an authorized achievement observer.
    /// Observers can call AwardAchievement and UpdateProgress.
    /// Only callable by admin or governance.
    /// </summary>
    public void RegisterObserver(Address observerContract);

    /// <summary>
    /// Remove an observer's authorization.
    /// </summary>
    public void RemoveObserver(Address observerContract);

    // --- View Functions ---

    public AchievementDefinition GetDefinition(ulong achievementId);
    public AwardedAchievement GetAwardedAchievement(ulong tokenId);
    public bool HasAchievement(Address user, ulong achievementId);
    public AchievementProgress GetProgress(Address user, ulong achievementId);
    public UserProfile GetUserProfile(Address user);
    public ulong GetTotalPoints(Address user);
    public ulong GetAchievementCount(Address user);
    public ulong GetHolderCount(ulong achievementId);

    /// <summary>
    /// Calculate the rarity of an achievement as a percentage
    /// (holders / total eligible users).
    /// </summary>
    public ulong GetRarityBps(ulong achievementId, ulong totalUsers);

    /// <summary>
    /// Get all achievement IDs held by a user.
    /// </summary>
    public ulong[] GetUserAchievements(Address user);

    // --- Internal ---

    /// <summary>
    /// Mint a soulbound BST-721 token. Overrides transfer to revert,
    /// ensuring non-transferability.
    /// </summary>
    private ulong MintSoulbound(Address recipient, ulong achievementId, DifficultyTier tier);

    /// <summary>
    /// Validate a proof submission against the achievement's
    /// verification requirements.
    /// </summary>
    private bool ValidateProof(ulong achievementId, Address claimer, byte[] proof);
}
```

## Complexity

**Medium** -- The core logic (define achievement, award soulbound token, track progress) is straightforward. Complexity increases with cross-protocol observer integration, progressive achievement tier upgrades, proof validation for claimed achievements, and rarity calculations. The soulbound constraint (blocking transfers) requires overriding BST-721 transfer functions, which needs careful implementation to avoid breaking the token standard interface.

## Priority

**P1** -- An achievement system is a low-cost, high-impact engagement tool that benefits the entire ecosystem. It can be deployed early to incentivize exploration of core features (staking, governance, trading) and grows in value as more dApps integrate observer contracts. It is a prerequisite for meaningful social profiles (contract 57).
