# Insurance Protocol

## Category

Decentralized Finance (DeFi) -- Risk Management

## Summary

A parametric insurance protocol enabling users to purchase coverage against on-chain risks (smart contract exploits, stablecoin depegs, oracle failures, slashing events) and stakers to provide underwriting capital in exchange for premium yield. Claims are resolved via oracle-triggered parametric conditions or governance vote, eliminating the need for subjective claim assessment while providing critical risk transfer infrastructure for the DeFi ecosystem.

## Why It's Useful

- **DeFi Risk Transfer**: Users can hedge against the risk of smart contract hacks, protocol exploits, and economic attacks by purchasing coverage, reducing the personal impact of DeFi failures.
- **Protocol Confidence**: Insured protocols attract more TVL because users know their deposits have a safety net. Insurance coverage becomes a competitive advantage for DeFi protocols.
- **Underwriting Yield**: Capital providers (stakers) earn premium income by underwriting risks, creating a new yield source that is uncorrelated with lending/trading yields.
- **Parametric Efficiency**: Parametric (event-triggered) claims settle automatically based on on-chain conditions (e.g., token price drops below threshold), eliminating slow and subjective manual claim processes.
- **Ecosystem Resilience**: Insurance absorbs shock from protocol failures, preventing panic-driven contagion across the DeFi ecosystem.
- **Regulatory Alignment**: As DeFi matures, regulators increasingly expect risk management infrastructure. On-chain insurance demonstrates ecosystem maturity.

## Key Features

- **Coverage Pools**: Each insurable risk has its own pool with dedicated underwriting capital. Users purchase coverage by paying premiums to the pool.
- **Parametric Claims**: Claims trigger automatically when an on-chain condition is met (e.g., stablecoin price < $0.95 for > 24 hours, protocol TVL drops > 50% in 1 hour).
- **Governance-Adjudicated Claims**: For complex events that cannot be parametrically detected (e.g., economic exploit vs. legitimate market movement), claims are resolved by governance vote.
- **Premium Pricing**: Premiums based on coverage amount, duration, and pool utilization. Higher utilization (more coverage sold relative to capital) increases premiums.
- **Staking for Underwriting**: Capital providers stake tokens into insurance pools. Their staked capital backs coverage claims and earns premium income.
- **Claims Payout**: Approved claims receive payout from the pool's underwriting capital. Payout is pro-rata if total claims exceed pool capital (shortfall socialization).
- **Coverage NFTs**: Active coverage positions are represented as BST-3525 SFTs with metadata (coverage amount, expiry, risk type, premium paid), enabling transferability.
- **Cooldown Period**: After a claim event is detected, a cooldown period allows for verification before payouts begin, preventing flash-attack-triggered claims.
- **Capital Lock Period**: Underwriters have a minimum staking period to prevent capital flight when claims are expected.
- **Reinsurance**: Pools can purchase reinsurance from other pools, diversifying risk across the protocol.

## Basalt-Specific Advantages

- **ZK Compliance for Regulated Insurance**: Insurance is a regulated product in many jurisdictions. ZK compliance proofs enable users to prove they are purchasing coverage in a permitted jurisdiction and meet eligibility criteria without revealing identity. Underwriters can also prove compliance (e.g., insurance license status).
- **AOT-Compiled Parametric Triggers**: Parametric claim detection logic runs as native AOT-compiled code, enabling rapid evaluation of trigger conditions during volatile market events when many claims may fire simultaneously.
- **BST-3525 SFT Coverage Positions**: Coverage positions as BST-3525 tokens with rich metadata (risk type, amount, expiry, trigger parameters) enable secondary market trading of insurance coverage, portfolio-level risk management, and institutional position tracking.
- **Confidential Coverage via Pedersen Commitments**: Coverage amounts can be hidden using Pedersen commitments, preventing adversaries from identifying heavily insured protocols (which might signal perceived vulnerability) or targeting users with known coverage for sophisticated attacks.
- **BLS Aggregated Oracle Proofs**: Parametric trigger verification can use BLS aggregate signatures from multiple oracle/validator nodes, providing a high-assurance trigger mechanism that requires threshold agreement before claims activate.
- **Governance Integration**: Basalt's native Governance contract (0x0102) with quadratic voting and delegation provides a robust mechanism for subjective claim adjudication, preventing governance capture by large token holders.

## Token Standards Used

- **BST-20**: Premium tokens, underwriting stake tokens, and claim payout tokens are BST-20.
- **BST-3525 (SFT)**: Active coverage positions with metadata (risk parameters, coverage terms, trigger conditions).
- **BST-4626 (Vault)**: Underwriting pools can implement BST-4626 for composability with yield aggregators.

## Integration Points

- **Governance (0x0102)**: Claim adjudication for non-parametric events. Pool creation approval, premium model parameters, and trigger condition configuration.
- **StakingPool (0x0105)**: Slashing insurance pools cover validator slashing events. Underwriting capital can earn staking yield during idle periods.
- **Lending Protocol (0x0220)**: Smart contract coverage for lending protocol deposits. Coverage tokens potentially accepted as collateral.
- **Stablecoin CDP (0x0230)**: Depeg insurance for USDB holders.
- **AMM DEX (0x0200)**: Impermanent loss insurance for LP providers. Premium pricing can reference AMM TWAP for parametric triggers.
- **BNS**: Registered as `insurance.basalt`.
- **SchemaRegistry / IssuerRegistry**: ZK compliance for regulated insurance markets.
- **BridgeETH (0x...1008)**: Bridge failure insurance for cross-chain asset holders.

## Technical Sketch

```csharp
// ============================================================
// InsuranceProtocol -- Parametric insurance pools
// ============================================================

public enum ClaimStatus : byte
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Paid = 3
}

public enum TriggerType : byte
{
    Parametric = 0,      // automatic on-chain condition
    GovernanceVote = 1   // manual governance adjudication
}

[BasaltContract(TypeId = 0x02A0)]
public partial class InsuranceProtocol : SdkContract
{
    // --- Storage ---

    // poolId => InsurancePool
    private StorageMap<ulong, InsurancePool> _pools;
    private StorageValue<ulong> _nextPoolId;

    // coverId => Coverage
    private StorageMap<ulong, Coverage> _coverages;
    private StorageValue<ulong> _nextCoverId;

    // claimId => Claim
    private StorageMap<ulong, Claim> _claims;
    private StorageValue<ulong> _nextClaimId;

    // poolId + staker => StakerPosition
    private StorageMap<Hash256, StakerPosition> _stakerPositions;

    // poolId => total staked capital
    private StorageMap<ulong, UInt256> _totalStaked;

    // poolId => total active coverage amount
    private StorageMap<ulong, UInt256> _totalActiveCoverage;

    // poolId => accumulated premiums
    private StorageMap<ulong, UInt256> _accumulatedPremiums;

    // --- Structs ---

    public struct InsurancePool
    {
        public ulong PoolId;
        public string RiskName;           // e.g., "lending-protocol-exploit"
        public Address CoveredProtocol;   // protocol being insured
        public Address CapitalToken;      // token staked by underwriters
        public TriggerType Trigger;
        public UInt256 MaxCoverage;       // max total coverage amount
        public uint PremiumRateBps;       // annual premium rate
        public uint CapitalLockBlocks;    // min staking period
        public uint CooldownBlocks;       // post-trigger waiting period
        public bool Active;
    }

    public struct ParametricTrigger
    {
        public Address OracleAddress;
        public UInt256 ThresholdValue;    // e.g., stablecoin price < 0.95
        public ulong DurationBlocks;      // condition must hold for N blocks
        public bool TriggerBelow;         // true = trigger when value < threshold
    }

    public struct Coverage
    {
        public ulong CoverId;
        public ulong PoolId;
        public Address Holder;
        public UInt256 Amount;            // coverage amount
        public UInt256 PremiumPaid;
        public ulong StartBlock;
        public ulong ExpiryBlock;
        public bool Active;
    }

    public struct StakerPosition
    {
        public Address Staker;
        public ulong PoolId;
        public UInt256 StakedAmount;
        public ulong StakedAtBlock;
        public UInt256 PremiumsEarned;
        public UInt256 PremiumIndex;     // for proportional premium distribution
    }

    public struct Claim
    {
        public ulong ClaimId;
        public ulong PoolId;
        public Address Claimant;
        public UInt256 Amount;
        public ClaimStatus Status;
        public ulong SubmittedBlock;
        public ulong ResolvedBlock;
        public byte[] Evidence;           // hash of evidence data
    }

    // --- Pool Management ---

    /// <summary>
    /// Create a new insurance pool. Governance-only.
    /// </summary>
    public ulong CreatePool(
        string riskName,
        Address coveredProtocol,
        Address capitalToken,
        TriggerType trigger,
        UInt256 maxCoverage,
        uint premiumRateBps,
        uint capitalLockBlocks,
        uint cooldownBlocks)
    {
        RequireGovernance();

        var poolId = _nextPoolId.Get();
        _nextPoolId.Set(poolId + 1);

        _pools.Set(poolId, new InsurancePool
        {
            PoolId = poolId,
            RiskName = riskName,
            CoveredProtocol = coveredProtocol,
            CapitalToken = capitalToken,
            Trigger = trigger,
            MaxCoverage = maxCoverage,
            PremiumRateBps = premiumRateBps,
            CapitalLockBlocks = capitalLockBlocks,
            CooldownBlocks = cooldownBlocks,
            Active = true
        });

        EmitEvent("PoolCreated", poolId, riskName, coveredProtocol, maxCoverage);
        return poolId;
    }

    // --- Underwriting (Staking) ---

    /// <summary>
    /// Stake capital into an insurance pool as an underwriter.
    /// Earns premium income; capital is at risk if claims are approved.
    /// </summary>
    public void StakeCapital(ulong poolId, UInt256 amount)
    {
        var pool = _pools.Get(poolId);
        Require(pool.Active, "POOL_INACTIVE");
        Require(amount > UInt256.Zero, "ZERO_AMOUNT");

        TransferTokenIn(pool.CapitalToken, Context.Sender, amount);

        var key = ComputeStakerKey(poolId, Context.Sender);
        var position = _stakerPositions.Get(key);

        // Distribute accumulated premiums before updating position
        DistributePremiums(poolId, ref position);

        position.Staker = Context.Sender;
        position.PoolId = poolId;
        position.StakedAmount += amount;
        position.StakedAtBlock = Context.BlockNumber;
        _stakerPositions.Set(key, position);

        _totalStaked.Set(poolId, _totalStaked.Get(poolId) + amount);

        EmitEvent("CapitalStaked", poolId, Context.Sender, amount);
    }

    /// <summary>
    /// Withdraw staked capital. Subject to lock period and available capital
    /// (cannot withdraw if it would make pool undercollateralized).
    /// </summary>
    public UInt256 WithdrawCapital(ulong poolId, UInt256 amount)
    {
        var pool = _pools.Get(poolId);
        var key = ComputeStakerKey(poolId, Context.Sender);
        var position = _stakerPositions.Get(key);

        Require(position.StakedAmount >= amount, "INSUFFICIENT_STAKE");
        Require(Context.BlockNumber >= position.StakedAtBlock + pool.CapitalLockBlocks,
                "LOCK_PERIOD");

        // Ensure pool remains adequately capitalized
        var totalStaked = _totalStaked.Get(poolId);
        var activeCoverage = _totalActiveCoverage.Get(poolId);
        Require(totalStaked - amount >= activeCoverage, "UNDERCOLLATERALIZED");

        DistributePremiums(poolId, ref position);

        position.StakedAmount -= amount;
        _stakerPositions.Set(key, position);
        _totalStaked.Set(poolId, totalStaked - amount);

        TransferTokenOut(pool.CapitalToken, Context.Sender, amount);

        EmitEvent("CapitalWithdrawn", poolId, Context.Sender, amount);
        return amount;
    }

    /// <summary>
    /// Claim accumulated premium earnings.
    /// </summary>
    public UInt256 ClaimPremiums(ulong poolId)
    {
        var key = ComputeStakerKey(poolId, Context.Sender);
        var position = _stakerPositions.Get(key);

        DistributePremiums(poolId, ref position);

        var earned = position.PremiumsEarned;
        Require(earned > UInt256.Zero, "NO_PREMIUMS");

        position.PremiumsEarned = UInt256.Zero;
        _stakerPositions.Set(key, position);

        var pool = _pools.Get(poolId);
        TransferTokenOut(pool.CapitalToken, Context.Sender, earned);

        EmitEvent("PremiumsClaimed", poolId, Context.Sender, earned);
        return earned;
    }

    // --- Coverage Purchase ---

    /// <summary>
    /// Purchase insurance coverage. Pays premium upfront for the duration.
    /// </summary>
    public ulong PurchaseCoverage(
        ulong poolId,
        UInt256 coverageAmount,
        ulong durationBlocks)
    {
        var pool = _pools.Get(poolId);
        Require(pool.Active, "POOL_INACTIVE");

        var totalCoverage = _totalActiveCoverage.Get(poolId);
        Require(totalCoverage + coverageAmount <= pool.MaxCoverage, "COVERAGE_CAP");
        Require(totalCoverage + coverageAmount <= _totalStaked.Get(poolId),
                "INSUFFICIENT_CAPITAL");

        // Calculate premium
        var annualPremium = coverageAmount * pool.PremiumRateBps / 10000;
        var blocksPerYear = 2_628_000UL; // approximate
        var premium = (annualPremium * durationBlocks) / blocksPerYear;
        Require(premium > UInt256.Zero, "PREMIUM_TOO_LOW");

        TransferTokenIn(pool.CapitalToken, Context.Sender, premium);

        var coverId = _nextCoverId.Get();
        _nextCoverId.Set(coverId + 1);

        _coverages.Set(coverId, new Coverage
        {
            CoverId = coverId,
            PoolId = poolId,
            Holder = Context.Sender,
            Amount = coverageAmount,
            PremiumPaid = premium,
            StartBlock = Context.BlockNumber,
            ExpiryBlock = Context.BlockNumber + durationBlocks,
            Active = true
        });

        _totalActiveCoverage.Set(poolId, totalCoverage + coverageAmount);
        _accumulatedPremiums.Set(poolId, _accumulatedPremiums.Get(poolId) + premium);

        EmitEvent("CoveragePurchased", coverId, poolId, Context.Sender,
                  coverageAmount, premium, durationBlocks);
        return coverId;
    }

    // --- Claims ---

    /// <summary>
    /// Submit a claim against an active coverage position.
    /// For parametric triggers, the claim auto-resolves if conditions are met.
    /// For governance triggers, a governance vote is initiated.
    /// </summary>
    public ulong SubmitClaim(ulong coverId, UInt256 claimAmount, byte[] evidence)
    {
        var coverage = _coverages.Get(coverId);
        Require(coverage.Holder == Context.Sender, "NOT_HOLDER");
        Require(coverage.Active, "COVERAGE_INACTIVE");
        Require(Context.BlockNumber <= coverage.ExpiryBlock, "COVERAGE_EXPIRED");
        Require(claimAmount <= coverage.Amount, "EXCEEDS_COVERAGE");

        var pool = _pools.Get(coverage.PoolId);

        var claimId = _nextClaimId.Get();
        _nextClaimId.Set(claimId + 1);

        var claim = new Claim
        {
            ClaimId = claimId,
            PoolId = coverage.PoolId,
            Claimant = Context.Sender,
            Amount = claimAmount,
            Status = ClaimStatus.Pending,
            SubmittedBlock = Context.BlockNumber,
            ResolvedBlock = 0,
            Evidence = evidence
        };

        // Auto-resolve for parametric triggers
        if (pool.Trigger == TriggerType.Parametric)
        {
            if (CheckParametricTrigger(coverage.PoolId))
            {
                claim.Status = ClaimStatus.Approved;
                claim.ResolvedBlock = Context.BlockNumber;
            }
            else
            {
                claim.Status = ClaimStatus.Rejected;
                claim.ResolvedBlock = Context.BlockNumber;
            }
        }
        // Governance triggers require a vote (handled externally)

        _claims.Set(claimId, claim);
        EmitEvent("ClaimSubmitted", claimId, coverId, claimAmount);
        return claimId;
    }

    /// <summary>
    /// Resolve a governance-adjudicated claim. Called by governance.
    /// </summary>
    public void ResolveClaim(ulong claimId, bool approved)
    {
        RequireGovernance();

        var claim = _claims.Get(claimId);
        Require(claim.Status == ClaimStatus.Pending, "NOT_PENDING");

        claim.Status = approved ? ClaimStatus.Approved : ClaimStatus.Rejected;
        claim.ResolvedBlock = Context.BlockNumber;
        _claims.Set(claimId, claim);

        EmitEvent("ClaimResolved", claimId, approved);
    }

    /// <summary>
    /// Pay out an approved claim after the cooldown period.
    /// </summary>
    public UInt256 PayClaim(ulong claimId)
    {
        var claim = _claims.Get(claimId);
        Require(claim.Status == ClaimStatus.Approved, "NOT_APPROVED");
        Require(claim.Status != ClaimStatus.Paid, "ALREADY_PAID");

        var pool = _pools.Get(claim.PoolId);
        Require(Context.BlockNumber >= claim.ResolvedBlock + pool.CooldownBlocks,
                "COOLDOWN_PERIOD");

        var available = _totalStaked.Get(claim.PoolId);
        var payout = UInt256.Min(claim.Amount, available);

        claim.Status = ClaimStatus.Paid;
        _claims.Set(claimId, claim);

        _totalStaked.Set(claim.PoolId, available - payout);

        TransferTokenOut(pool.CapitalToken, claim.Claimant, payout);

        EmitEvent("ClaimPaid", claimId, payout);
        return payout;
    }

    // --- Queries ---

    public InsurancePool GetPool(ulong poolId) => _pools.Get(poolId);
    public Coverage GetCoverage(ulong coverId) => _coverages.Get(coverId);
    public Claim GetClaim(ulong claimId) => _claims.Get(claimId);

    public UInt256 GetPoolCapital(ulong poolId) => _totalStaked.Get(poolId);
    public UInt256 GetPoolActiveCoverage(ulong poolId) => _totalActiveCoverage.Get(poolId);

    public UInt256 GetCoveragePremium(ulong poolId, UInt256 amount, ulong duration)
    {
        var pool = _pools.Get(poolId);
        var annualPremium = amount * pool.PremiumRateBps / 10000;
        return (annualPremium * duration) / 2_628_000UL;
    }

    public StakerPosition GetStakerPosition(ulong poolId, Address staker)
    {
        var key = ComputeStakerKey(poolId, staker);
        return _stakerPositions.Get(key);
    }

    // --- Internal Helpers ---

    private bool CheckParametricTrigger(ulong poolId) { /* Oracle-based condition check */ }
    private void DistributePremiums(ulong poolId, ref StakerPosition position) { /* ... */ }
    private Hash256 ComputeStakerKey(ulong poolId, Address staker) { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**High** -- Insurance protocols combine several complex subsystems: premium pricing models that must balance pool sustainability with competitive rates, parametric trigger evaluation with oracle dependencies and timing constraints, governance-based claim adjudication with voter incentive design, pro-rata premium distribution to underwriters, capital adequacy enforcement, and shortfall socialization during mass claims. The interaction between coverage expiry, capital lock periods, and claim cooldowns creates subtle edge cases.

## Priority

**P2** -- Insurance is essential for DeFi ecosystem maturity and user confidence but depends on having protocols worth insuring (lending, stablecoin, AMM) and a robust governance system for claim adjudication. It should be deployed after the core DeFi stack is established and has accumulated meaningful TVL.
