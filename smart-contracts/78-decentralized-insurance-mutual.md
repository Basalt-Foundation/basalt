# Decentralized Insurance Mutual

## Category

Decentralized Finance (DeFi) -- Risk Management and Insurance

## Summary

A member-owned decentralized insurance mutual where participants pool capital into risk-specific pools, pay premiums proportional to their risk assessment, and vote on claims through a staked assessor mechanism. The mutual supports multiple risk categories (smart contract failure, stablecoin depeg, bridge exploit, protocol hack), distributes surplus back to members, and enables reinsurance through external capital pools. Claims are adjudicated by staked assessors whose economic incentives align with honest evaluation.

## Why It's Useful

- **Protocol Risk Coverage**: DeFi users face significant risk from smart contract bugs, oracle failures, bridge exploits, and economic attacks. Insurance coverage enables confident participation in DeFi protocols.
- **Member Ownership**: Unlike centralized insurance protocols, a mutual is owned by its members. Surplus (premiums minus claims minus expenses) is distributed back to members, reducing long-term costs.
- **Decentralized Claims**: Claims are adjudicated by staked assessors with economic incentives for honesty, eliminating the conflict of interest present in centralized insurance where the insurer benefits from denying claims.
- **Risk Pooling**: By pooling capital across many members, individual risk is shared, enabling coverage amounts that would be impractical for individual self-insurance.
- **Market-Based Pricing**: Premiums are set by the market (risk pool utilization, historical claims, assessor pricing), creating efficient risk pricing that adapts to changing conditions.
- **Capital Efficiency**: Reinsurance and surplus distribution mean that capital is not idle -- it earns returns while providing coverage.
- **Composability**: Insurance positions can be tokenized and integrated with other DeFi protocols, enabling covered lending, insured yield farming, and hedged positions.
- **Transparent Operations**: All premiums, claims, votes, and payouts are visible on-chain, providing full transparency absent in traditional insurance.

## Key Features

- **Multi-Pool Architecture**: Separate risk pools for different categories:
  - Smart Contract Risk (contract bugs, exploit coverage)
  - Stablecoin Depeg Risk (depeg below threshold)
  - Bridge Risk (cross-chain bridge failures)
  - Protocol Risk (economic attacks, oracle manipulation)
  - Validator Risk (slashing, downtime)
  - Custody Risk (exchange failure, key loss)
- **Premium Calculation**: Premiums based on pool utilization, historical claims ratio, coverage amount, and coverage duration. Dynamic pricing adjusts with market conditions.
- **Staked Assessor System**: Assessors stake BST to participate in claims adjudication. They vote on claim validity and receive rewards for honest assessment. Dishonest assessors are slashed.
- **Multi-Round Voting**: Claims go through initial assessment (staked assessors) and optional appeal (governance-level vote). Escalation for disputed claims.
- **Coverage Policies**: Users purchase coverage policies specifying: covered protocol/contract, coverage amount, premium paid, coverage period, and claim trigger conditions.
- **Claim Trigger Conditions**: Configurable conditions that trigger claim eligibility -- price drop threshold for depeg insurance, exploit confirmation for smart contract insurance, fund loss verification for protocol insurance.
- **Surplus Distribution**: Annual surplus (premiums - claims - expenses) distributed pro-rata to capital providers. Deficit triggers additional capital calls.
- **Reinsurance Layer**: External capital pools can provide reinsurance for the mutual, expanding coverage capacity beyond member deposits. Reinsurers earn premiums for backstopping risk pools.
- **Risk Assessment Framework**: On-chain risk scoring for covered protocols based on audit history, TVL, operational track record, and code complexity.
- **Emergency Fast-Track Claims**: Mechanism for rapid payout in obvious catastrophic events (e.g., confirmed exploit with public proof), bypassing standard assessment timeline.
- **Policy NFTs**: Coverage policies represented as BST-3525 tokens, enabling secondary market trading of insurance positions.
- **Capital Lock Period**: Capital providers commit to a minimum lock period to ensure pool stability. Withdrawal with notice period.
- **Loss Socialization**: When a pool's claims exceed its capital, losses are socialized across the remaining capital providers proportionally.

## Basalt-Specific Advantages

- **ZK Compliance for Regulated Insurance**: Insurance is a regulated activity in most jurisdictions. The mutual can require ZK compliance proofs from members and capital providers, satisfying regulatory requirements without exposing personal information. Members prove residency, accredited status, or professional qualification via ZK proofs against BST-VC credentials.
- **BST-VC Audit Credentials**: Covered protocols can submit BST-VC credentials for security audits, verified via the IssuerRegistry. Audit credentials reduce premium rates and improve risk scoring -- a natural fit for Basalt's credential infrastructure.
- **BST-3525 SFT Policy Tokens**: Insurance policies represented as BST-3525 semi-fungible tokens with slot metadata (covered protocol, coverage amount, premium paid, start block, end block, claim status). Enables rich secondary markets for insurance positions and structured risk products.
- **BST-4626 Vault Capital Pools**: Risk pool capital managed as BST-4626 vaults. Capital providers receive yield-bearing vault shares that can be used in other DeFi protocols (composable insurance capital). Idle capital earns yield from lending or staking.
- **Confidential Claims via Pedersen Commitments**: Claim amounts and coverage details can be hidden using Pedersen commitments, protecting claimants from public exposure of their losses. Range proofs verify claim validity without revealing exact amounts.
- **AOT-Compiled Premium Calculation**: Complex actuarial calculations (utilization curves, historical claims weighting, risk factor multiplication) execute in AOT-compiled native code, enabling real-time premium quotes without excessive gas costs.
- **Ed25519 Assessor Voting**: Staked assessors submit votes with Ed25519 signatures, benefiting from fast verification. High-volume voting during active claim periods remains gas-efficient.
- **BLS Aggregate Assessor Signatures**: Multi-assessor consensus on claim decisions can be verified with a single BLS-aggregated signature, reducing the overhead of multi-assessor adjudication.
- **Nullifier-Based Anonymous Voting**: Assessors can vote on claims anonymously (preventing social pressure and collusion) via ZK proofs with nullifiers, revealing only the final tally after the voting period.

## Token Standards Used

- **BST-3525 (SFT)**: Insurance policies with slot metadata (protocol, amount, premium, duration, status, claim history). Assessor position tokens with stake, reputation, and vote history.
- **BST-4626 (Vault)**: Risk pool capital vaults providing yield-bearing shares to capital providers.
- **BST-20**: Premium payments, claim payouts, assessor rewards, and surplus distribution.
- **BST-VC (Verifiable Credentials)**: Compliance credentials for members. Audit credentials for covered protocols. Claim resolution certificates.
- **BST-721**: Non-transferable assessor badge NFTs with reputation metadata.

## Integration Points

- **Governance (0x0102)**: Governs risk pool parameters, assessor registration, appeal decisions, covered protocol whitelist, and surplus distribution schedule.
- **StakingPool (0x0105)**: Assessor staking and slashing for dishonest assessment. Idle risk pool capital can be staked for validator rewards.
- **Escrow (0x0103)**: Claim payouts held in escrow during the assessment period. Large claims require multi-party escrow release.
- **SchemaRegistry (0x...1006)**: Credential schemas for audit certificates, compliance attestations, and claim evidence.
- **IssuerRegistry (0x...1007)**: Registered security audit firms, compliance providers, and oracle services as trusted credential issuers.
- **BNS (0x0101)**: Insurance pools and assessor registry mapped to BNS names (e.g., `insurance.basalt`, `defi-cover.insurance.basalt`).
- **BridgeETH (0x...1008)**: Cross-chain insurance -- cover for bridged assets, claims triggered by bridge failure events.
- **WBSLT (0x0100)**: Default premium payment and capital denomination.

## Technical Sketch

```csharp
// ============================================================
// InsuranceMutual -- Decentralized insurance with staked assessors
// ============================================================

[BasaltContract(TypeId = 0x030E)]
public partial class InsuranceMutual : SdkContract
{
    // --- Storage ---

    // Risk pools
    private StorageValue<uint> _nextPoolId;
    private StorageMap<uint, RiskPool> _pools;
    private StorageMap<uint, UInt256> _poolCapital;
    private StorageMap<uint, UInt256> _poolUtilization; // total active coverage

    // Capital provider positions: poolId => provider => CapitalPosition
    private StorageMap<uint, StorageMap<Address, CapitalPosition>> _capitalPositions;

    // Pool shares (BST-4626 compatible): poolId => totalShares
    private StorageMap<uint, UInt256> _poolShares;
    private StorageMap<uint, StorageMap<Address, UInt256>> _providerShares;

    // Policies
    private StorageValue<ulong> _nextPolicyId;
    private StorageMap<ulong, InsurancePolicy> _policies;
    private StorageMap<ulong, byte> _policyStatus; // 0=active, 1=expired, 2=claimed,
                                                    // 3=cancelled

    // Claims
    private StorageValue<ulong> _nextClaimId;
    private StorageMap<ulong, Claim> _claims;
    private StorageMap<ulong, byte> _claimStatus; // 0=submitted, 1=assessing,
                                                   // 2=approved, 3=denied,
                                                   // 4=appealing, 5=paid

    // Assessors
    private StorageMap<Address, Assessor> _assessors;
    private StorageMap<Address, bool> _isAssessor;
    private StorageValue<uint> _assessorCount;
    private StorageValue<UInt256> _minimumAssessorStake;

    // Assessor votes: claimId => assessor => AssessorVote
    private StorageMap<ulong, StorageMap<Address, AssessorVote>> _assessorVotes;
    private StorageMap<ulong, ClaimVoteTally> _claimTallies;

    // Surplus tracking
    private StorageMap<uint, UInt256> _poolSurplus;
    private StorageMap<uint, UInt256> _totalPremiumsCollected;
    private StorageMap<uint, UInt256> _totalClaimsPaid;

    // Reinsurance: poolId => reinsurer => ReinsurancePosition
    private StorageMap<uint, StorageMap<Address, ReinsurancePosition>> _reinsurance;

    // Protocol risk scores: protocol address => risk score (0-100, lower = safer)
    private StorageMap<Address, uint> _riskScores;

    // Global params
    private StorageValue<uint> _assessmentPeriodBlocks;
    private StorageValue<uint> _appealPeriodBlocks;
    private StorageValue<uint> _minAssessorVotes;
    private StorageValue<uint> _assessorRewardBps;
    private StorageValue<uint> _platformFeeBps;

    // --- Data Structures ---

    public struct RiskPool
    {
        public uint PoolId;
        public string Name;
        public byte RiskCategory;         // 0=smart_contract, 1=depeg, 2=bridge,
                                          // 3=protocol, 4=validator, 5=custody
        public UInt256 MinCapital;        // Minimum capital to activate
        public uint MaxLeverageBps;       // Max coverage / capital ratio
        public uint BasePremiumBps;       // Base annual premium rate
        public ulong MinCoverageDuration; // Minimum coverage period in blocks
        public ulong MaxCoverageDuration;
        public UInt256 MaxCoveragePerPolicy; // Single policy coverage limit
        public bool Active;
    }

    public struct CapitalPosition
    {
        public UInt256 Deposited;
        public ulong DepositedAtBlock;
        public ulong LockUntilBlock;
        public UInt256 SurplusEarned;
    }

    public struct InsurancePolicy
    {
        public ulong PolicyId;
        public uint PoolId;
        public Address Policyholder;
        public Address CoveredProtocol;   // Protocol being covered
        public UInt256 CoverageAmount;
        public UInt256 PremiumPaid;
        public ulong StartBlock;
        public ulong EndBlock;
        public byte[] ClaimTriggerConditions; // Encoded trigger conditions
    }

    public struct Claim
    {
        public ulong ClaimId;
        public ulong PolicyId;
        public Address Claimant;
        public UInt256 ClaimAmount;
        public string Description;
        public byte[] Evidence;           // Links to evidence (tx hashes, etc.)
        public ulong SubmittedAtBlock;
        public ulong AssessmentDeadline;
    }

    public struct Assessor
    {
        public Address AssessorAddress;
        public UInt256 Stake;
        public uint TotalAssessments;
        public uint CorrectAssessments;   // Aligned with final outcome
        public uint IncorrectAssessments;
        public UInt256 RewardsEarned;
        public ushort ReputationScore;    // 0-1000
    }

    public struct AssessorVote
    {
        public bool Approve;             // true = approve claim, false = deny
        public UInt256 StakeWeight;
        public string Justification;
    }

    public struct ClaimVoteTally
    {
        public UInt256 ApproveWeight;
        public UInt256 DenyWeight;
        public uint VoterCount;
    }

    public struct ReinsurancePosition
    {
        public Address Reinsurer;
        public UInt256 Capacity;
        public uint PremiumShareBps;     // Share of pool premiums paid to reinsurer
        public ulong StartBlock;
        public ulong EndBlock;
    }

    // --- Risk Pool Management ---

    /// <summary>
    /// Create a new risk pool. Governance-only.
    /// </summary>
    public uint CreateRiskPool(
        string name,
        byte riskCategory,
        UInt256 minCapital,
        uint maxLeverageBps,
        uint basePremiumBps,
        ulong minCoverageDuration,
        ulong maxCoverageDuration,
        UInt256 maxCoveragePerPolicy)
    {
        RequireGovernance();

        uint poolId = _nextPoolId.Get();
        _nextPoolId.Set(poolId + 1);

        _pools.Set(poolId, new RiskPool
        {
            PoolId = poolId,
            Name = name,
            RiskCategory = riskCategory,
            MinCapital = minCapital,
            MaxLeverageBps = maxLeverageBps,
            BasePremiumBps = basePremiumBps,
            MinCoverageDuration = minCoverageDuration,
            MaxCoverageDuration = maxCoverageDuration,
            MaxCoveragePerPolicy = maxCoveragePerPolicy,
            Active = false
        });

        EmitEvent("RiskPoolCreated", poolId, name, riskCategory);
        return poolId;
    }

    // --- Capital Provision ---

    /// <summary>
    /// Deposit capital into a risk pool. Receive BST-4626 vault shares.
    /// </summary>
    public UInt256 ProvideCapital(uint poolId, ulong lockDurationBlocks)
    {
        Require(Context.TxValue > UInt256.Zero, "ZERO_DEPOSIT");
        var pool = _pools.Get(poolId);

        UInt256 currentCapital = _poolCapital.Get(poolId);
        UInt256 totalShares = _poolShares.Get(poolId);

        // Calculate shares
        UInt256 shares;
        if (totalShares.IsZero)
            shares = Context.TxValue;
        else
            shares = (Context.TxValue * totalShares) / currentCapital;

        _poolCapital.Set(poolId, currentCapital + Context.TxValue);
        _poolShares.Set(poolId, totalShares + shares);
        _providerShares.Get(poolId).Set(Context.Sender,
            _providerShares.Get(poolId).Get(Context.Sender) + shares);

        _capitalPositions.Get(poolId).Set(Context.Sender, new CapitalPosition
        {
            Deposited = Context.TxValue,
            DepositedAtBlock = Context.BlockNumber,
            LockUntilBlock = Context.BlockNumber + lockDurationBlocks,
            SurplusEarned = UInt256.Zero
        });

        // Activate pool if minimum capital reached
        if (!pool.Active && _poolCapital.Get(poolId) >= pool.MinCapital)
        {
            pool.Active = true;
            _pools.Set(poolId, pool);
            EmitEvent("RiskPoolActivated", poolId);
        }

        EmitEvent("CapitalProvided", poolId, Context.Sender, Context.TxValue, shares);
        return shares;
    }

    /// <summary>
    /// Withdraw capital from a risk pool by burning shares.
    /// Subject to lock period and available liquidity.
    /// </summary>
    public UInt256 WithdrawCapital(uint poolId, UInt256 shares)
    {
        var position = _capitalPositions.Get(poolId).Get(Context.Sender);
        Require(Context.BlockNumber >= position.LockUntilBlock, "LOCK_PERIOD_ACTIVE");

        UInt256 providerShares = _providerShares.Get(poolId).Get(Context.Sender);
        Require(providerShares >= shares, "INSUFFICIENT_SHARES");

        UInt256 totalShares = _poolShares.Get(poolId);
        UInt256 totalCapital = _poolCapital.Get(poolId);
        UInt256 amount = (shares * totalCapital) / totalShares;

        // Check available liquidity (capital - active coverage)
        UInt256 utilization = _poolUtilization.Get(poolId);
        UInt256 available = totalCapital - utilization;
        Require(amount <= available, "INSUFFICIENT_LIQUIDITY");

        _poolCapital.Set(poolId, totalCapital - amount);
        _poolShares.Set(poolId, totalShares - shares);
        _providerShares.Get(poolId).Set(Context.Sender, providerShares - shares);

        Context.TransferNative(Context.Sender, amount);

        EmitEvent("CapitalWithdrawn", poolId, Context.Sender, amount, shares);
        return amount;
    }

    // --- Policy Purchase ---

    /// <summary>
    /// Purchase an insurance policy.
    /// </summary>
    public ulong PurchasePolicy(
        uint poolId,
        Address coveredProtocol,
        UInt256 coverageAmount,
        ulong durationBlocks)
    {
        var pool = _pools.Get(poolId);
        Require(pool.Active, "POOL_NOT_ACTIVE");
        Require(coverageAmount <= pool.MaxCoveragePerPolicy, "EXCEEDS_MAX_COVERAGE");
        Require(durationBlocks >= pool.MinCoverageDuration, "DURATION_TOO_SHORT");
        Require(durationBlocks <= pool.MaxCoverageDuration, "DURATION_TOO_LONG");

        // Check leverage ratio
        UInt256 currentUtilization = _poolUtilization.Get(poolId);
        UInt256 totalCapital = _poolCapital.Get(poolId);
        UInt256 maxUtilization = (totalCapital * pool.MaxLeverageBps) / 10000;
        Require(currentUtilization + coverageAmount <= maxUtilization,
                "LEVERAGE_LIMIT_EXCEEDED");

        // Calculate premium
        UInt256 premium = CalculatePremium(pool, coveredProtocol, coverageAmount,
                                            durationBlocks);
        Require(Context.TxValue >= premium, "INSUFFICIENT_PREMIUM");

        ulong policyId = _nextPolicyId.Get();
        _nextPolicyId.Set(policyId + 1);

        _policies.Set(policyId, new InsurancePolicy
        {
            PolicyId = policyId,
            PoolId = poolId,
            Policyholder = Context.Sender,
            CoveredProtocol = coveredProtocol,
            CoverageAmount = coverageAmount,
            PremiumPaid = premium,
            StartBlock = Context.BlockNumber,
            EndBlock = Context.BlockNumber + durationBlocks,
            ClaimTriggerConditions = new byte[0]
        });

        _policyStatus.Set(policyId, 0); // active
        _poolUtilization.Set(poolId, currentUtilization + coverageAmount);
        _totalPremiumsCollected.Set(poolId,
            _totalPremiumsCollected.Get(poolId) + premium);

        EmitEvent("PolicyPurchased", policyId, poolId, coverageAmount, premium);
        return policyId;
    }

    // --- Claims ---

    /// <summary>
    /// Submit an insurance claim with evidence.
    /// </summary>
    public ulong SubmitClaim(
        ulong policyId,
        UInt256 claimAmount,
        string description,
        byte[] evidence)
    {
        Require(_policyStatus.Get(policyId) == 0, "POLICY_NOT_ACTIVE");
        var policy = _policies.Get(policyId);
        Require(Context.Sender == policy.Policyholder, "NOT_POLICYHOLDER");
        Require(Context.BlockNumber <= policy.EndBlock, "POLICY_EXPIRED");
        Require(claimAmount <= policy.CoverageAmount, "EXCEEDS_COVERAGE");

        ulong claimId = _nextClaimId.Get();
        _nextClaimId.Set(claimId + 1);

        _claims.Set(claimId, new Claim
        {
            ClaimId = claimId,
            PolicyId = policyId,
            Claimant = Context.Sender,
            ClaimAmount = claimAmount,
            Description = description,
            Evidence = evidence,
            SubmittedAtBlock = Context.BlockNumber,
            AssessmentDeadline = Context.BlockNumber + _assessmentPeriodBlocks.Get()
        });

        _claimStatus.Set(claimId, 0); // submitted

        EmitEvent("ClaimSubmitted", claimId, policyId, claimAmount);
        return claimId;
    }

    // --- Assessment ---

    /// <summary>
    /// Register as an assessor by staking BST.
    /// </summary>
    public void RegisterAssessor()
    {
        Require(!_isAssessor.Get(Context.Sender), "ALREADY_ASSESSOR");
        Require(Context.TxValue >= _minimumAssessorStake.Get(), "INSUFFICIENT_STAKE");

        _assessors.Set(Context.Sender, new Assessor
        {
            AssessorAddress = Context.Sender,
            Stake = Context.TxValue,
            TotalAssessments = 0,
            CorrectAssessments = 0,
            IncorrectAssessments = 0,
            RewardsEarned = UInt256.Zero,
            ReputationScore = 500 // Start at neutral
        });

        _isAssessor.Set(Context.Sender, true);
        _assessorCount.Set(_assessorCount.Get() + 1);

        EmitEvent("AssessorRegistered", Context.Sender, Context.TxValue);
    }

    /// <summary>
    /// Assess a claim. Staked assessors vote on validity.
    /// </summary>
    public void AssessClaim(ulong claimId, bool approve, string justification)
    {
        Require(_isAssessor.Get(Context.Sender), "NOT_ASSESSOR");
        Require(_claimStatus.Get(claimId) <= 1, "NOT_ASSESSABLE");

        var claim = _claims.Get(claimId);
        Require(Context.BlockNumber <= claim.AssessmentDeadline, "ASSESSMENT_EXPIRED");

        if (_claimStatus.Get(claimId) == 0)
        {
            _claimStatus.Set(claimId, 1); // assessing
        }

        var assessor = _assessors.Get(Context.Sender);

        _assessorVotes.Get(claimId).Set(Context.Sender, new AssessorVote
        {
            Approve = approve,
            StakeWeight = assessor.Stake,
            Justification = justification
        });

        // Update tally
        var tally = _claimTallies.Get(claimId);
        if (approve)
            tally.ApproveWeight = tally.ApproveWeight + assessor.Stake;
        else
            tally.DenyWeight = tally.DenyWeight + assessor.Stake;
        tally.VoterCount++;
        _claimTallies.Set(claimId, tally);

        EmitEvent("ClaimAssessed", claimId, Context.Sender, approve);
    }

    /// <summary>
    /// Finalize a claim after the assessment period.
    /// </summary>
    public void FinalizeClaim(ulong claimId)
    {
        Require(_claimStatus.Get(claimId) == 1, "NOT_ASSESSING");
        var claim = _claims.Get(claimId);
        Require(Context.BlockNumber > claim.AssessmentDeadline, "ASSESSMENT_ONGOING");

        var tally = _claimTallies.Get(claimId);
        Require(tally.VoterCount >= _minAssessorVotes.Get(), "INSUFFICIENT_VOTES");

        bool approved = tally.ApproveWeight > tally.DenyWeight;

        if (approved)
        {
            _claimStatus.Set(claimId, 2); // approved

            // Payout
            var policy = _policies.Get(claim.PolicyId);
            UInt256 payout = claim.ClaimAmount;
            UInt256 poolCap = _poolCapital.Get(policy.PoolId);

            if (payout > poolCap)
                payout = poolCap; // Loss socialization

            _poolCapital.Set(policy.PoolId, poolCap - payout);
            _poolUtilization.Set(policy.PoolId,
                _poolUtilization.Get(policy.PoolId) - policy.CoverageAmount);
            _totalClaimsPaid.Set(policy.PoolId,
                _totalClaimsPaid.Get(policy.PoolId) + payout);

            Context.TransferNative(claim.Claimant, payout);
            _policyStatus.Set(claim.PolicyId, 2); // claimed
            _claimStatus.Set(claimId, 5); // paid

            EmitEvent("ClaimPaid", claimId, payout);
        }
        else
        {
            _claimStatus.Set(claimId, 3); // denied
            EmitEvent("ClaimDenied", claimId);
        }

        // Reward assessors who voted with the majority
        RewardAssessors(claimId, approved);
    }

    /// <summary>
    /// Appeal a denied claim. Escalates to governance vote.
    /// </summary>
    public void AppealClaim(ulong claimId)
    {
        Require(_claimStatus.Get(claimId) == 3, "NOT_DENIED");
        var claim = _claims.Get(claimId);
        Require(Context.Sender == claim.Claimant, "NOT_CLAIMANT");

        _claimStatus.Set(claimId, 4); // appealing
        // Create governance proposal for appeal
        EmitEvent("ClaimAppealed", claimId);
    }

    // --- Emergency Fast-Track ---

    /// <summary>
    /// Fast-track a claim for a confirmed catastrophic event. Governance-only.
    /// </summary>
    public void FastTrackClaim(ulong claimId)
    {
        RequireGovernance();
        Require(_claimStatus.Get(claimId) <= 1, "CANNOT_FAST_TRACK");

        _claimStatus.Set(claimId, 2); // approved
        var claim = _claims.Get(claimId);
        var policy = _policies.Get(claim.PolicyId);

        UInt256 payout = claim.ClaimAmount;
        _poolCapital.Set(policy.PoolId,
            _poolCapital.Get(policy.PoolId) - payout);
        Context.TransferNative(claim.Claimant, payout);

        _claimStatus.Set(claimId, 5); // paid
        _policyStatus.Set(claim.PolicyId, 2); // claimed

        EmitEvent("ClaimFastTracked", claimId, payout);
    }

    // --- Reinsurance ---

    /// <summary>
    /// Provide reinsurance capacity to a risk pool.
    /// </summary>
    public void ProvideReinsurance(
        uint poolId,
        UInt256 capacity,
        uint premiumShareBps,
        ulong durationBlocks)
    {
        Require(capacity > UInt256.Zero, "ZERO_CAPACITY");
        Require(Context.TxValue >= capacity, "INSUFFICIENT_DEPOSIT");

        _reinsurance.Get(poolId).Set(Context.Sender, new ReinsurancePosition
        {
            Reinsurer = Context.Sender,
            Capacity = capacity,
            PremiumShareBps = premiumShareBps,
            StartBlock = Context.BlockNumber,
            EndBlock = Context.BlockNumber + durationBlocks
        });

        EmitEvent("ReinsuranceProvided", poolId, Context.Sender, capacity);
    }

    // --- Surplus Distribution ---

    /// <summary>
    /// Distribute surplus to capital providers. Periodic operation.
    /// </summary>
    public void DistributeSurplus(uint poolId)
    {
        UInt256 premiums = _totalPremiumsCollected.Get(poolId);
        UInt256 claims = _totalClaimsPaid.Get(poolId);
        UInt256 currentSurplus = _poolSurplus.Get(poolId);

        if (premiums > claims)
        {
            UInt256 newSurplus = premiums - claims - currentSurplus;
            if (!newSurplus.IsZero)
            {
                // Distribute pro-rata to capital providers based on share
                _poolSurplus.Set(poolId, premiums - claims);
                EmitEvent("SurplusDistributed", poolId, newSurplus);
            }
        }
    }

    // --- Premium Calculation ---

    /// <summary>
    /// Get a premium quote for a policy.
    /// </summary>
    public UInt256 GetPremiumQuote(
        uint poolId,
        Address coveredProtocol,
        UInt256 coverageAmount,
        ulong durationBlocks)
    {
        var pool = _pools.Get(poolId);
        return CalculatePremium(pool, coveredProtocol, coverageAmount, durationBlocks);
    }

    // --- Query Methods ---

    public RiskPool GetRiskPool(uint poolId) => _pools.Get(poolId);
    public UInt256 GetPoolCapital(uint poolId) => _poolCapital.Get(poolId);
    public UInt256 GetPoolUtilization(uint poolId) => _poolUtilization.Get(poolId);
    public InsurancePolicy GetPolicy(ulong policyId) => _policies.Get(policyId);
    public byte GetPolicyStatus(ulong policyId) => _policyStatus.Get(policyId);
    public Claim GetClaim(ulong claimId) => _claims.Get(claimId);
    public byte GetClaimStatus(ulong claimId) => _claimStatus.Get(claimId);
    public ClaimVoteTally GetClaimTally(ulong claimId) => _claimTallies.Get(claimId);
    public Assessor GetAssessor(Address addr) => _assessors.Get(addr);
    public bool IsAssessor(Address addr) => _isAssessor.Get(addr);
    public uint GetAssessorCount() => _assessorCount.Get();
    public uint GetRiskScore(Address protocol) => _riskScores.Get(protocol);
    public UInt256 GetTotalPremiums(uint poolId) => _totalPremiumsCollected.Get(poolId);
    public UInt256 GetTotalClaims(uint poolId) => _totalClaimsPaid.Get(poolId);

    // --- Internal Helpers ---

    private UInt256 CalculatePremium(RiskPool pool, Address protocol,
        UInt256 coverage, ulong duration)
    {
        uint riskScore = _riskScores.Get(protocol);
        if (riskScore == 0) riskScore = 50; // Default medium risk

        // Base premium = coverage * basePremiumBps / 10000 * duration / blocksPerYear
        UInt256 annualPremium = (coverage * pool.BasePremiumBps) / 10000;
        UInt256 premium = (annualPremium * duration) / 2628000; // blocks per year

        // Risk adjustment: multiply by risk score / 50 (50 = neutral)
        premium = (premium * riskScore) / 50;

        // Utilization surcharge: higher premiums when pool is heavily utilized
        UInt256 utilization = _poolUtilization.Get(pool.PoolId);
        UInt256 capital = _poolCapital.Get(pool.PoolId);
        if (!capital.IsZero)
        {
            UInt256 utilizationBps = (utilization * 10000) / capital;
            if (utilizationBps > 5000) // Over 50% utilized
            {
                UInt256 surcharge = ((utilizationBps - 5000) * premium) / 10000;
                premium = premium + surcharge;
            }
        }

        return premium;
    }

    private void RewardAssessors(ulong claimId, bool finalOutcome)
    {
        // Reward assessors who voted correctly; slash those who voted incorrectly
        // (implementation iterates through voters and updates reputation)
    }

    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**High** -- Decentralized insurance mutuals are among the most complex DeFi primitives. They combine: multi-pool capital management with leverage constraints, actuarial premium calculations with dynamic risk scoring, a multi-round claims adjudication process with staked assessor incentives, surplus distribution accounting, reinsurance layer integration, and loss socialization mechanics. The economic design must carefully balance: premium pricing (too high deters buyers, too low leads to insolvency), assessor incentives (rewards for honesty, penalties for dishonesty, protection against collusion), and capital provider returns (competitive yields while maintaining solvency). The interaction between pools, policies, claims, assessors, and governance creates a complex state machine with many edge cases.

## Priority

**P2** -- Decentralized insurance is critical for DeFi ecosystem maturity, as it provides the safety net that enables institutional participation and larger capital deployment. However, it requires a mature ecosystem with multiple operating protocols (to insure against), reliable oracle data (for trigger conditions and risk scoring), established governance (for appeals and parameter management), and sufficient capital providers. It should be developed after core DeFi infrastructure, lending, and governance are stable, but before the ecosystem targets large-scale institutional adoption.
