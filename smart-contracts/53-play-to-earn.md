# Play-to-Earn Rewards Engine

## Category

Gaming Infrastructure / Incentive Mechanism

## Summary

A rewards distribution contract that validates player achievement proofs submitted by authorized game servers and distributes BST-20 token rewards accordingly. The engine incorporates anti-bot measures including rate limiting, minimum play-time requirements, and behavioral analysis hooks, while supporting season-based reward pools with configurable emission schedules and on-chain leaderboard snapshots for competitive ranking.

## Why It's Useful

- **Market need**: Play-to-earn games require a trusted bridge between off-chain gameplay and on-chain rewards. Without a standardized rewards engine, every game must build and audit its own distribution logic, leading to inconsistent security postures.
- **User benefit**: Players receive guaranteed, verifiable rewards for their achievements. Anti-bot measures ensure legitimate players are not diluted by automated farming, preserving the economic value of earned tokens.
- **Developer benefit**: Game studios deploy a single rewards engine rather than building custom distribution contracts. Configurable parameters (rate limits, reward amounts, seasons) allow rapid iteration without redeployment.
- **Economic sustainability**: Season-based pools with emission curves prevent hyperinflation. Leaderboard-based bonus rewards incentivize competitive play and engagement.
- **Transparency**: All reward distributions are on-chain and auditable. Players can verify that the game server is not favoring certain accounts.

## Key Features

- **Achievement proof validation**: Game servers sign achievement proofs with their registered Ed25519 key. The contract verifies the signature before distributing rewards.
- **Multi-game support**: A single rewards engine can serve multiple games, each with its own reward pool, rate limits, and achievement types.
- **Season system**: Time-bounded seasons with dedicated reward pools. Unclaimed rewards from ended seasons can roll over or be reclaimed by the game developer.
- **Rate limiting**: Per-player, per-game limits on reward claims per epoch (e.g., maximum 100 claims per day). Prevents bot-driven reward extraction.
- **Minimum play time**: Achievement proofs include session duration. The contract enforces minimum play-time thresholds per achievement type.
- **Cooldown periods**: Configurable cooldown between claims for the same achievement type, preventing rapid-fire exploit loops.
- **Leaderboard snapshots**: At configurable intervals, the contract records the top N players by total rewards earned. Snapshot data is stored on-chain for bonus distribution.
- **Bonus multipliers**: Top leaderboard positions receive bonus reward multipliers during the next period.
- **Emission curves**: Reward pools follow configurable emission schedules (linear decay, exponential decay, or flat) to manage token inflation.
- **Referral rewards**: Optional referral system where referring players earn a percentage of their referrals' rewards.

## Basalt-Specific Advantages

- **Ed25519 server authentication**: Game servers authenticate achievement proofs using Ed25519 signatures, which are native to Basalt and verified at the VM level with minimal gas cost. This is significantly cheaper than ECDSA verification on EVM chains.
- **BLAKE3 proof hashing**: Achievement proof digests use BLAKE3 for fast, secure hashing. The combination of BLAKE3 speed and Ed25519 verification makes high-frequency reward claims economically viable.
- **AOT-compiled validation**: Signature verification, rate limit checks, and reward calculations execute at near-native speed. This matters for games generating hundreds of reward claims per block.
- **ZK compliance for regulated games**: Games operating in regulated jurisdictions can require players to present BST-VC credentials (age verification, jurisdiction compliance) before claiming rewards, leveraging SchemaRegistry and IssuerRegistry.
- **BST-3525 season passes**: Season passes can be implemented as BST-3525 semi-fungible tokens where the slot represents the season and the value represents the tier, enabling tiered reward multipliers.
- **BNS identity**: Player leaderboards display BNS names instead of raw addresses, improving the competitive experience and community engagement.
- **Flat state performance**: Per-player reward tracking and rate limiting require frequent storage reads. FlatStateDb's O(1) dictionary caches make these lookups constant-time rather than requiring Merkle trie traversals.

## Token Standards Used

- **BST-20**: Primary reward token distributed to players.
- **BST-3525**: Optional season pass tokens with tier-based value slots.
- **BST-721**: Optional soulbound achievement badges for milestone completions.

## Integration Points

- **StakingPool (0x...1005)**: Game developers may be required to stake BST to register as authorized game servers, providing economic security against malicious reward inflation.
- **BNS (0x...1002)**: Leaderboard entries display BNS names. Players can claim rewards to their BNS-linked address.
- **Governance (0x...1003)**: Community governance over reward emission parameters, rate limits, and game server authorization for ecosystem-funded reward pools.
- **SchemaRegistry (0x...1006)**: Credential schemas for age verification, jurisdiction compliance, and game-specific player verification.
- **IssuerRegistry (0x...1007)**: Trusted issuers for player credential verification.
- **Escrow (0x...1004)**: Season reward pools held in escrow until distribution conditions are met.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum EmissionCurve : byte
{
    Flat = 0,
    LinearDecay = 1,
    ExponentialDecay = 2
}

public struct GameConfig
{
    public ulong GameId;
    public Address GameServer;            // Ed25519 public key / address of authorized server
    public Address Developer;             // Game developer address (admin)
    public ulong MaxClaimsPerEpoch;       // Rate limit per player per epoch
    public ulong ClaimCooldownSeconds;    // Minimum time between claims
    public ulong MinPlayTimeSeconds;      // Minimum session duration for valid claims
    public bool Active;
}

public struct Season
{
    public ulong SeasonId;
    public ulong GameId;
    public ulong StartTime;
    public ulong EndTime;
    public UInt256 TotalPool;             // Total rewards available this season
    public UInt256 DistributedAmount;     // Rewards already distributed
    public EmissionCurve Curve;
    public bool Active;
}

public struct AchievementType
{
    public ulong AchievementId;
    public ulong GameId;
    public string Name;
    public UInt256 BaseReward;            // Base reward amount
    public ulong MinPlayTime;            // Override per-achievement minimum play time
    public ulong CooldownSeconds;        // Override per-achievement cooldown
}

public struct AchievementProof
{
    public ulong GameId;
    public ulong AchievementId;
    public Address Player;
    public ulong SessionDuration;         // Play time in seconds
    public ulong Timestamp;
    public ulong Nonce;                   // Replay protection
    public byte[] ServerSignature;        // Ed25519 signature from game server
}

public struct PlayerStats
{
    public ulong TotalClaims;
    public UInt256 TotalRewardsEarned;
    public ulong ClaimsThisEpoch;
    public ulong LastClaimTimestamp;
    public ulong LastEpochReset;
}

public struct LeaderboardEntry
{
    public Address Player;
    public UInt256 SeasonRewards;
    public ulong Rank;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0202)]
public partial class PlayToEarnEngine : SdkContractBase
{
    // Storage
    private StorageMap<ulong, GameConfig> _games;
    private StorageMap<ulong, Season> _seasons;
    private StorageMap<ulong, AchievementType> _achievements;
    private StorageValue<ulong> _nextGameId;
    private StorageValue<ulong> _nextSeasonId;
    private StorageValue<ulong> _nextAchievementId;
    private StorageValue<ulong> _epochDurationSeconds;

    // Composite key storage (encoded as game:player or season:player)
    // PlayerStats keyed by BLAKE3(gameId || playerAddress)
    // LeaderboardEntry keyed by BLAKE3(seasonId || rank)
    // Nonce tracking keyed by BLAKE3(gameId || playerAddress || nonce)

    // --- Game Registration ---

    /// <summary>
    /// Register a new game with its authorized server key and configuration.
    /// Caller becomes the game developer (admin).
    /// </summary>
    public ulong RegisterGame(
        Address gameServerKey,
        ulong maxClaimsPerEpoch,
        ulong claimCooldownSeconds,
        ulong minPlayTimeSeconds
    );

    /// <summary>
    /// Update game configuration. Only callable by game developer.
    /// </summary>
    public void UpdateGameConfig(
        ulong gameId,
        ulong maxClaimsPerEpoch,
        ulong claimCooldownSeconds,
        ulong minPlayTimeSeconds
    );

    /// <summary>
    /// Rotate the authorized game server key.
    /// Only callable by game developer.
    /// </summary>
    public void RotateServerKey(ulong gameId, Address newServerKey);

    // --- Achievement Types ---

    /// <summary>
    /// Define an achievement type with base reward and validation parameters.
    /// Only callable by game developer.
    /// </summary>
    public ulong DefineAchievement(
        ulong gameId,
        string name,
        UInt256 baseReward,
        ulong minPlayTime,
        ulong cooldownSeconds
    );

    // --- Season Management ---

    /// <summary>
    /// Create a new season with a reward pool. Developer must deposit
    /// the total pool amount as tx value.
    /// </summary>
    public ulong CreateSeason(
        ulong gameId,
        ulong startTime,
        ulong endTime,
        EmissionCurve curve
    );

    /// <summary>
    /// End a season early. Undistributed rewards are returned to the developer.
    /// </summary>
    public void EndSeason(ulong seasonId);

    // --- Reward Claims ---

    /// <summary>
    /// Claim a reward for a completed achievement. The achievement proof
    /// must be signed by the authorized game server. Validates:
    /// 1. Server signature authenticity (Ed25519)
    /// 2. Nonce uniqueness (replay protection)
    /// 3. Rate limit not exceeded
    /// 4. Cooldown period elapsed
    /// 5. Minimum play time met
    /// 6. Season is active and has remaining pool
    /// </summary>
    public UInt256 ClaimReward(
        ulong seasonId,
        ulong achievementId,
        ulong sessionDuration,
        ulong timestamp,
        ulong nonce,
        byte[] serverSignature
    );

    /// <summary>
    /// Batch claim multiple rewards in a single transaction.
    /// Each proof is validated independently.
    /// </summary>
    public UInt256 BatchClaimRewards(
        ulong seasonId,
        ulong[] achievementIds,
        ulong[] sessionDurations,
        ulong[] timestamps,
        ulong[] nonces,
        byte[][] serverSignatures
    );

    // --- Leaderboard ---

    /// <summary>
    /// Snapshot the current season leaderboard. Stores the top N players
    /// by total rewards earned this season. Callable by anyone;
    /// only succeeds once per snapshot interval.
    /// </summary>
    public void SnapshotLeaderboard(ulong seasonId, ulong topN);

    /// <summary>
    /// Distribute bonus rewards to top leaderboard positions.
    /// Callable after season ends. Bonus pool funded by game developer.
    /// </summary>
    public void DistributeLeaderboardBonuses(
        ulong seasonId,
        ulong[] bonusMultipliersBps      // Basis points multiplier per rank
    );

    // --- View Functions ---

    public GameConfig GetGameConfig(ulong gameId);
    public Season GetSeason(ulong seasonId);
    public AchievementType GetAchievement(ulong achievementId);
    public PlayerStats GetPlayerStats(ulong gameId, Address player);
    public LeaderboardEntry GetLeaderboardEntry(ulong seasonId, ulong rank);
    public UInt256 GetRemainingPool(ulong seasonId);
    public ulong GetRemainingClaimsThisEpoch(ulong gameId, Address player);

    // --- Internal Helpers ---

    /// <summary>
    /// Verify an Ed25519 signature from the game server over the
    /// achievement proof payload.
    /// </summary>
    private bool VerifyServerSignature(
        GameConfig game,
        ulong achievementId,
        Address player,
        ulong sessionDuration,
        ulong timestamp,
        ulong nonce,
        byte[] signature
    );

    /// <summary>
    /// Calculate the current reward amount based on emission curve
    /// and season progress.
    /// </summary>
    private UInt256 CalculateEmissionReward(
        Season season,
        UInt256 baseReward,
        ulong currentTimestamp
    );

    /// <summary>
    /// Check and update rate limiting state for a player.
    /// Returns true if within limits.
    /// </summary>
    private bool CheckRateLimit(ulong gameId, Address player);
}
```

## Complexity

**High** -- The contract must perform Ed25519 signature verification on every claim, manage per-player rate limiting with epoch-based resets, enforce cooldown periods across achievement types, calculate emission-curve-adjusted rewards, maintain replay protection via nonce tracking, and support leaderboard snapshots with sorting. Batch claims multiply the validation complexity. Season pool management with overflow protection adds further state management requirements.

## Priority

**P1** -- Play-to-earn infrastructure is a significant driver of blockchain gaming adoption. While dependent on the existence of games building on Basalt, having a standardized rewards engine ready accelerates game developer onboarding and ensures consistent security practices across the gaming ecosystem.
