# On-Chain Credit Scoring

## Category

Decentralized Finance (DeFi) -- Identity and Risk Assessment

## Summary

An on-chain credit scoring protocol that computes a creditworthiness score from a user's verifiable blockchain history -- including loan repayment records, account age, collateral ratios, transaction volume, and DeFi participation. The score is stored as a soulbound (non-transferable) BST-721 token with updateable metadata, and users can generate ZK proofs attesting to a score range (e.g., "above 700") without revealing the exact score. Lending protocols consume these scores to offer under-collateralized loans to creditworthy borrowers.

## Why It's Useful

- **Under-Collateralized Lending**: Current DeFi lending requires 150%+ collateralization, locking up enormous capital. Credit scoring enables under-collateralized loans for borrowers with proven track records, dramatically improving capital efficiency.
- **On-Chain Reputation**: A standardized credit score provides a composable reputation primitive that any protocol can consume, creating a portable financial identity across the Basalt ecosystem.
- **Privacy-Preserving Creditworthiness**: Users prove they meet creditworthiness thresholds without revealing their exact score, transaction history, or financial details -- a capability impossible in traditional credit bureaus.
- **Permissionless Credit History**: Unlike centralized credit bureaus (Equifax, TransUnion), anyone can build a credit history by using DeFi protocols, without gatekeepers or geographic restrictions.
- **Risk-Adjusted Pricing**: Lending protocols can offer variable interest rates based on credit scores, rewarding responsible borrowers with lower rates and pricing risk more accurately.
- **Sybil Resistance**: Credit scores based on long-term on-chain behavior are resistant to sybil attacks, as building a good score requires sustained, capital-intensive activity.
- **Financial Inclusion**: Users without traditional banking relationships can build creditworthiness through on-chain activity, creating a parallel financial reputation system.

## Key Features

- **Multi-Factor Scoring Model**: Score computed from weighted factors:
  - Repayment history (loan repayments on time vs. defaults)
  - Account age (time since first transaction)
  - Utilization ratio (average collateral ratio maintained in lending)
  - Transaction volume and frequency
  - Protocol diversity (number of distinct DeFi protocols used)
  - Liquidation history (number and severity of liquidations)
  - Staking history (staking duration and amount)
- **Soulbound Score Token**: Score stored as a non-transferable BST-721 token bound to the user's address. Cannot be sold, transferred, or delegated.
- **Score Range**: 300-850 (inspired by FICO, adapted for on-chain metrics). Stored as uint16.
- **ZK Score Proofs**: Generate Groth16 proofs attesting to score ranges (e.g., "my score is between 700 and 850") without revealing the exact score or underlying factors.
- **Updateable Metadata**: Score metadata is updated periodically (epoch-based) to reflect new on-chain activity. Previous scores are retained in history.
- **Oracle-Based Factor Collection**: Trusted oracles report on-chain activity metrics from various protocols (lending repayments, liquidations, staking history).
- **Score Decay**: Inactive accounts see gradual score decay, incentivizing continued DeFi participation.
- **Dispute Mechanism**: Users can dispute incorrect scoring inputs (e.g., a false liquidation report) through a governance process.
- **Score Tiers**: Named tiers (Poor, Fair, Good, Very Good, Excellent) for human-readable interpretation.
- **Multi-Protocol Reporting**: Any registered DeFi protocol can report credit events (repayment, default, liquidation) to the scoring contract.
- **Score History**: Complete score history with timestamps, enabling trend analysis and detecting improving or deteriorating creditworthiness.
- **Minimum History Requirement**: Accounts must have minimum activity (e.g., 6 months of history, 10 transactions) before receiving a score.
- **Score Aggregation**: Optionally aggregate scores across multiple addresses owned by the same user (proved via ZK ownership proofs).

## Basalt-Specific Advantages

- **ZK Score Range Proofs (Native)**: Basalt's ZK compliance layer (Groth16 verifier, SchemaRegistry) enables users to prove their score falls within a range without revealing the exact value. This is critical for privacy-preserving credit -- borrowers prove creditworthiness without exposing financial details. On other chains, building a ZK range proof system from scratch is prohibitively complex.
- **BST-VC Credit Attestations**: Credit events (successful repayment, on-time loan closure) can be issued as BST-VC verifiable credentials by lending protocols, creating a W3C-compatible credit history that is portable and machine-verifiable.
- **Soulbound BST-721**: Basalt's BST-721 standard with transfer restrictions ensures credit scores cannot be traded or transferred, maintaining the integrity of the identity-score binding.
- **Confidential Score Updates via Pedersen Commitments**: Score updates can use Pedersen commitments to hide the new score value while proving it was correctly computed from the input factors. This prevents score-based discrimination in protocols that should not have access to exact scores.
- **AOT-Compiled Scoring Model**: The multi-factor scoring algorithm (weighted sums, decay calculations, percentile mapping) executes in AOT-compiled native code, enabling efficient batch updates of many scores per epoch without excessive gas costs.
- **Nullifier Anti-Correlation**: When users generate ZK proofs of their score range, Basalt's nullifier system prevents linking multiple proof presentations (e.g., a user proving their score to two different lending protocols cannot be linked as the same person).
- **Ed25519 Oracle Signatures**: Credit event reports from DeFi protocols are signed with Ed25519, providing fast verification of incoming data and enabling high-frequency reporting.
- **BLS Aggregate Oracle Reports**: Multiple protocol reports can be aggregated into a single BLS signature per epoch, reducing the overhead of multi-protocol credit data ingestion.

## Token Standards Used

- **BST-721**: Soulbound (non-transferable) credit score token. Each address has at most one score token. Metadata contains the current score, tier, factor breakdown, and update history.
- **BST-VC (Verifiable Credentials)**: Credit event credentials issued by DeFi protocols (repayment attestation, default notification). Score attestation credentials issued by the scoring contract.
- **BST-20**: Protocol fees for score computation and ZK proof generation.
- **BST-3525 (SFT)**: Optional detailed score breakdown as SFT with slots for each scoring factor, enabling granular analysis.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines credential schemas for credit events ("LoanRepaymentV1", "LiquidationEventV1", "CreditScoreAttestationV1") and score range proof schemas.
- **IssuerRegistry (0x...1007)**: DeFi protocols registered as trusted credit event issuers. Only events from registered protocols are included in score calculations.
- **Governance (0x0102)**: Governs scoring model parameters (factor weights, decay rates, tier thresholds), registered protocol list, and dispute resolution.
- **StakingPool (0x0105)**: Staking history is a credit scoring factor. StakingPool reports staking events to the credit scoring contract.
- **Escrow (0x0103)**: Dispute bonds held in escrow during credit score dispute resolution.
- **BNS (0x0101)**: Credit scoring contract registered under BNS name (e.g., `credit.basalt`).

## Technical Sketch

```csharp
// ============================================================
// CreditScoring -- On-chain creditworthiness assessment
// ============================================================

[BasaltContract(TypeId = 0x030A)]
public partial class CreditScoring : SdkContract
{
    // --- Storage ---

    // User scores: address => CreditScore
    private StorageMap<Address, CreditScore> _scores;
    private StorageMap<Address, bool> _hasScore;

    // Soulbound token ID mapping: address => tokenId
    private StorageMap<Address, ulong> _scoreTokenIds;
    private StorageValue<ulong> _nextTokenId;

    // Scoring factors: address => FactorData
    private StorageMap<Address, FactorData> _factors;

    // Credit events: address => eventIndex => CreditEvent
    private StorageMap<Address, StorageMap<ulong, CreditEvent>> _events;
    private StorageMap<Address, ulong> _eventCount;

    // Score history: address => historyIndex => HistoricalScore
    private StorageMap<Address, StorageMap<ulong, HistoricalScore>> _scoreHistory;
    private StorageMap<Address, ulong> _historyCount;

    // Registered reporting protocols
    private StorageMap<Address, bool> _registeredProtocols;
    private StorageMap<Address, string> _protocolNames;

    // Scoring model parameters
    private StorageValue<ScoringModel> _model;

    // Minimum requirements for scoring
    private StorageValue<ulong> _minimumAccountAgeBlocks;
    private StorageValue<ulong> _minimumTransactionCount;

    // Score decay rate per epoch (basis points)
    private StorageValue<uint> _decayRateBps;
    private StorageValue<ulong> _epochLengthBlocks;

    // Dispute parameters
    private StorageValue<UInt256> _disputeBond;
    private StorageMap<ulong, Dispute> _disputes;
    private StorageValue<ulong> _nextDisputeId;

    // Admin
    private StorageValue<Address> _admin;

    // --- Data Structures ---

    public struct CreditScore
    {
        public ushort Score;              // 300-850
        public byte Tier;                // 0=Poor, 1=Fair, 2=Good, 3=VeryGood, 4=Excellent
        public ulong LastUpdatedBlock;
        public ulong FirstScoredBlock;
        public uint UpdateCount;
    }

    public struct FactorData
    {
        public ulong TotalLoans;
        public ulong OnTimeRepayments;
        public ulong LateRepayments;
        public ulong Defaults;
        public ulong Liquidations;
        public ulong FirstTransactionBlock;
        public UInt256 TotalBorrowVolume;
        public UInt256 TotalRepaidVolume;
        public UInt256 AverageCollateralRatioBps; // Average across all loans
        public ulong ProtocolsUsed;
        public ulong StakingDurationBlocks;
        public UInt256 TotalStaked;
        public ulong TransactionCount;
    }

    public struct CreditEvent
    {
        public Address ReportingProtocol;
        public byte EventType;           // 0=repayment, 1=late_repayment, 2=default,
                                         // 3=liquidation, 4=loan_opened, 5=loan_closed,
                                         // 6=stake_deposit, 7=stake_withdraw
        public UInt256 Amount;
        public ulong BlockNumber;
        public byte[] EventData;         // Protocol-specific data
    }

    public struct HistoricalScore
    {
        public ushort Score;
        public ulong BlockNumber;
        public byte ChangeReason;        // 0=scheduled_update, 1=event_update, 2=decay, 3=dispute
    }

    public struct ScoringModel
    {
        public uint RepaymentWeight;      // Weight for repayment history (basis points)
        public uint AccountAgeWeight;     // Weight for account age
        public uint CollateralWeight;     // Weight for collateral ratio history
        public uint VolumeWeight;         // Weight for transaction volume
        public uint DiversityWeight;      // Weight for protocol diversity
        public uint LiquidationPenalty;   // Penalty for liquidations
        public uint StakingBonus;         // Bonus for staking history
        // Weights should sum to 10000
    }

    public struct Dispute
    {
        public Address Disputant;
        public ulong EventIndex;
        public string Reason;
        public UInt256 Bond;
        public byte Status;             // 0=pending, 1=resolved_for, 2=resolved_against
    }

    // --- Credit Event Reporting ---

    /// <summary>
    /// Report a credit event for a user. Only registered protocols can report.
    /// </summary>
    public void ReportCreditEvent(
        Address user,
        byte eventType,
        UInt256 amount,
        byte[] eventData)
    {
        Require(_registeredProtocols.Get(Context.Sender), "NOT_REGISTERED_PROTOCOL");

        ulong eventIndex = _eventCount.Get(user);
        _events.Get(user).Set(eventIndex, new CreditEvent
        {
            ReportingProtocol = Context.Sender,
            EventType = eventType,
            Amount = amount,
            BlockNumber = Context.BlockNumber,
            EventData = eventData
        });
        _eventCount.Set(user, eventIndex + 1);

        // Update factor data
        UpdateFactors(user, eventType, amount);

        // Recalculate score if user has one
        if (_hasScore.Get(user))
        {
            RecalculateScore(user);
        }

        EmitEvent("CreditEventReported", user, Context.Sender, eventType, amount);
    }

    /// <summary>
    /// Report multiple credit events in batch. Registered protocol only.
    /// </summary>
    public void ReportBatch(
        Address[] users,
        byte[] eventTypes,
        UInt256[] amounts)
    {
        Require(_registeredProtocols.Get(Context.Sender), "NOT_REGISTERED_PROTOCOL");
        Require(users.Length == eventTypes.Length && users.Length == amounts.Length,
                "LENGTH_MISMATCH");

        for (int i = 0; i < users.Length; i++)
        {
            ReportCreditEventInternal(users[i], eventTypes[i], amounts[i]);
        }
    }

    // --- Score Computation ---

    /// <summary>
    /// Request initial credit score computation. User must meet minimum requirements.
    /// </summary>
    public ulong RequestScore()
    {
        Require(!_hasScore.Get(Context.Sender), "ALREADY_HAS_SCORE");

        var factors = _factors.Get(Context.Sender);
        Require(factors.FirstTransactionBlock > 0, "NO_HISTORY");
        Require(Context.BlockNumber - factors.FirstTransactionBlock >=
                _minimumAccountAgeBlocks.Get(), "ACCOUNT_TOO_YOUNG");
        Require(factors.TransactionCount >= _minimumTransactionCount.Get(),
                "INSUFFICIENT_ACTIVITY");

        ushort score = ComputeScore(factors);
        byte tier = ScoreToTier(score);

        _scores.Set(Context.Sender, new CreditScore
        {
            Score = score,
            Tier = tier,
            LastUpdatedBlock = Context.BlockNumber,
            FirstScoredBlock = Context.BlockNumber,
            UpdateCount = 1
        });

        _hasScore.Set(Context.Sender, true);

        // Mint soulbound BST-721 token
        ulong tokenId = _nextTokenId.Get();
        _nextTokenId.Set(tokenId + 1);
        _scoreTokenIds.Set(Context.Sender, tokenId);
        MintSoulboundToken(Context.Sender, tokenId, score, tier);

        // Record history
        RecordScoreHistory(Context.Sender, score, 0);

        EmitEvent("ScoreCreated", Context.Sender, score, tier, tokenId);
        return tokenId;
    }

    /// <summary>
    /// Trigger score recalculation. Can be called by the user or automatically.
    /// </summary>
    public void UpdateScore(Address user)
    {
        Require(_hasScore.Get(user), "NO_SCORE");

        var currentScore = _scores.Get(user);
        var factors = _factors.Get(user);

        ushort newScore = ComputeScore(factors);

        // Apply decay for inactivity
        ulong blocksSinceUpdate = Context.BlockNumber - currentScore.LastUpdatedBlock;
        ulong epochLength = _epochLengthBlocks.Get();
        if (blocksSinceUpdate > epochLength)
        {
            uint epochsInactive = (uint)(blocksSinceUpdate / epochLength);
            uint decayBps = _decayRateBps.Get();
            for (uint i = 0; i < epochsInactive; i++)
            {
                newScore = (ushort)(newScore - (newScore * decayBps / 10000));
            }
            if (newScore < 300) newScore = 300;
        }

        byte tier = ScoreToTier(newScore);

        _scores.Set(user, new CreditScore
        {
            Score = newScore,
            Tier = tier,
            LastUpdatedBlock = Context.BlockNumber,
            FirstScoredBlock = currentScore.FirstScoredBlock,
            UpdateCount = currentScore.UpdateCount + 1
        });

        // Update soulbound token metadata
        ulong tokenId = _scoreTokenIds.Get(user);
        UpdateTokenMetadata(tokenId, newScore, tier);

        RecordScoreHistory(user, newScore, 1);

        EmitEvent("ScoreUpdated", user, newScore, tier);
    }

    // --- ZK Score Proofs ---

    /// <summary>
    /// Generate a ZK proof that the user's score falls within a specified range.
    /// Returns proof data that can be verified by lending protocols.
    /// </summary>
    public byte[] GenerateScoreRangeProof(
        ushort lowerBound,
        ushort upperBound,
        byte[] blindingFactor)
    {
        Require(_hasScore.Get(Context.Sender), "NO_SCORE");
        var score = _scores.Get(Context.Sender);
        Require(score.Score >= lowerBound && score.Score <= upperBound,
                "SCORE_NOT_IN_RANGE");

        // Generate Groth16 proof:
        // Public inputs: lowerBound, upperBound, contract address, block number
        // Private witness: exact score, scoring factors, blinding factor
        byte[] proof = GenerateGroth16Proof(
            score.Score, lowerBound, upperBound, blindingFactor);

        EmitEvent("ScoreProofGenerated", Context.Sender, lowerBound, upperBound);
        return proof;
    }

    /// <summary>
    /// Verify a ZK score range proof. Called by lending protocols.
    /// </summary>
    public bool VerifyScoreRangeProof(
        Address user,
        ushort lowerBound,
        ushort upperBound,
        byte[] proof)
    {
        // Verify the Groth16 proof
        bool valid = VerifyGroth16Proof(proof, user, lowerBound, upperBound);
        EmitEvent("ScoreProofVerified", user, lowerBound, upperBound, valid);
        return valid;
    }

    // --- Protocol Registration ---

    /// <summary>
    /// Register a DeFi protocol as a credit event reporter. Governance-only.
    /// </summary>
    public void RegisterProtocol(Address protocol, string name)
    {
        RequireGovernance();
        _registeredProtocols.Set(protocol, true);
        _protocolNames.Set(protocol, name);
        EmitEvent("ProtocolRegistered", protocol, name);
    }

    /// <summary>
    /// Deregister a protocol. Governance-only.
    /// </summary>
    public void DeregisterProtocol(Address protocol)
    {
        RequireGovernance();
        _registeredProtocols.Set(protocol, false);
        EmitEvent("ProtocolDeregistered", protocol);
    }

    // --- Disputes ---

    /// <summary>
    /// Dispute a credit event. Requires a bond.
    /// </summary>
    public ulong DisputeEvent(Address user, ulong eventIndex, string reason)
    {
        Require(Context.Sender == user, "CAN_ONLY_DISPUTE_OWN");
        Require(Context.TxValue >= _disputeBond.Get(), "INSUFFICIENT_BOND");

        ulong disputeId = _nextDisputeId.Get();
        _nextDisputeId.Set(disputeId + 1);

        _disputes.Set(disputeId, new Dispute
        {
            Disputant = user,
            EventIndex = eventIndex,
            Reason = reason,
            Bond = Context.TxValue,
            Status = 0
        });

        EmitEvent("EventDisputed", disputeId, user, eventIndex);
        return disputeId;
    }

    /// <summary>
    /// Resolve a dispute. Governance-only.
    /// </summary>
    public void ResolveDispute(ulong disputeId, bool inFavorOfUser)
    {
        RequireGovernance();
        var dispute = _disputes.Get(disputeId);
        Require(dispute.Status == 0, "ALREADY_RESOLVED");

        if (inFavorOfUser)
        {
            dispute.Status = 1;
            // Remove the disputed event and recalculate score
            Context.TransferNative(dispute.Disputant, dispute.Bond);
            RecalculateScore(dispute.Disputant);
        }
        else
        {
            dispute.Status = 2;
            // Bond forfeited
        }

        _disputes.Set(disputeId, dispute);
        EmitEvent("DisputeResolved", disputeId, inFavorOfUser);
    }

    // --- Model Management ---

    /// <summary>
    /// Update the scoring model parameters. Governance-only.
    /// </summary>
    public void UpdateScoringModel(ScoringModel newModel)
    {
        RequireGovernance();

        uint totalWeight = newModel.RepaymentWeight + newModel.AccountAgeWeight +
                          newModel.CollateralWeight + newModel.VolumeWeight +
                          newModel.DiversityWeight;
        Require(totalWeight == 10000, "WEIGHTS_MUST_SUM_TO_10000");

        _model.Set(newModel);
        EmitEvent("ScoringModelUpdated");
    }

    // --- Query Methods ---

    public CreditScore GetScore(Address user) => _scores.Get(user);
    public bool HasScore(Address user) => _hasScore.Get(user);
    public FactorData GetFactors(Address user) => _factors.Get(user);
    public ulong GetEventCount(Address user) => _eventCount.Get(user);
    public CreditEvent GetEvent(Address user, ulong index) => _events.Get(user).Get(index);
    public HistoricalScore GetHistoricalScore(Address user, ulong index)
        => _scoreHistory.Get(user).Get(index);
    public ulong GetScoreHistoryCount(Address user) => _historyCount.Get(user);
    public ScoringModel GetScoringModel() => _model.Get();
    public bool IsProtocolRegistered(Address protocol) => _registeredProtocols.Get(protocol);

    public string GetTierName(byte tier) => tier switch
    {
        0 => "Poor",
        1 => "Fair",
        2 => "Good",
        3 => "Very Good",
        4 => "Excellent",
        _ => "Unknown"
    };

    // --- Internal Helpers ---

    private ushort ComputeScore(FactorData factors)
    {
        var model = _model.Get();

        // Repayment factor: ratio of on-time repayments to total loans
        uint repaymentFactor = 0;
        if (factors.TotalLoans > 0)
        {
            repaymentFactor = (uint)((factors.OnTimeRepayments * 10000) / factors.TotalLoans);
        }

        // Account age factor: logarithmic scaling
        ulong ageBlocks = Context.BlockNumber - factors.FirstTransactionBlock;
        uint ageFactor = (uint)IntegerLog2(ageBlocks + 1) * 1000; // Capped

        // Collateral ratio factor
        uint collateralFactor = (uint)(factors.AverageCollateralRatioBps > 20000
            ? 10000 : factors.AverageCollateralRatioBps / 2);

        // Volume factor: log of total repaid volume
        uint volumeFactor = (uint)(IntegerLog2((ulong)(factors.TotalRepaidVolume) + 1) * 800);

        // Diversity factor
        uint diversityFactor = (uint)(factors.ProtocolsUsed * 1000);
        if (diversityFactor > 10000) diversityFactor = 10000;

        // Weighted sum
        uint rawScore =
            (repaymentFactor * model.RepaymentWeight +
             ageFactor * model.AccountAgeWeight +
             collateralFactor * model.CollateralWeight +
             volumeFactor * model.VolumeWeight +
             diversityFactor * model.DiversityWeight) / 10000;

        // Apply liquidation penalty
        if (factors.Liquidations > 0)
        {
            uint penalty = (uint)(factors.Liquidations * model.LiquidationPenalty);
            rawScore = rawScore > penalty ? rawScore - penalty : 0;
        }

        // Apply staking bonus
        if (factors.StakingDurationBlocks > 0)
        {
            uint bonus = model.StakingBonus;
            rawScore = rawScore + bonus;
        }

        // Map to 300-850 range
        ushort score = (ushort)(300 + (rawScore * 550 / 10000));
        if (score > 850) score = 850;
        if (score < 300) score = 300;

        return score;
    }

    private byte ScoreToTier(ushort score)
    {
        if (score >= 800) return 4; // Excellent
        if (score >= 740) return 3; // Very Good
        if (score >= 670) return 2; // Good
        if (score >= 580) return 1; // Fair
        return 0;                   // Poor
    }

    private void UpdateFactors(Address user, byte eventType, UInt256 amount) { /* ... */ }
    private void RecalculateScore(Address user) { /* ... */ }
    private void RecordScoreHistory(Address user, ushort score, byte reason) { /* ... */ }
    private void MintSoulboundToken(Address to, ulong tokenId, ushort score, byte tier) { /* ... */ }
    private void UpdateTokenMetadata(ulong tokenId, ushort score, byte tier) { /* ... */ }
    private void ReportCreditEventInternal(Address user, byte eventType, UInt256 amount) { /* ... */ }
    private byte[] GenerateGroth16Proof(ushort score, ushort lower, ushort upper,
                                         byte[] blinding) { /* ... */ return new byte[0]; }
    private bool VerifyGroth16Proof(byte[] proof, Address user, ushort lower,
                                     ushort upper) { /* ... */ return true; }
    private ulong IntegerLog2(ulong x) { /* ... */ return 0; }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**High** -- The credit scoring model involves multiple weighted factors with non-linear transformations (logarithmic scaling, decay curves), requiring careful calibration to produce meaningful and manipulation-resistant scores. ZK range proof generation and verification add significant cryptographic complexity. The multi-protocol reporting system must handle conflicting reports, missing data, and gaming attempts (e.g., self-lending to inflate repayment history). Score decay and history management create storage scalability challenges. The soulbound token mechanism requires custom transfer restriction logic. Dispute resolution adds governance complexity. The interaction between private scores and public ZK proofs requires careful privacy analysis.

## Priority

**P2** -- On-chain credit scoring is a transformative DeFi primitive that unlocks under-collateralized lending, but it requires a mature ecosystem with multiple operating lending protocols, sufficient historical data, and established governance for model parameter management. It should be deployed after core lending protocols, oracles, and the governance system are stable. However, its development should begin early as the scoring model requires extensive backtesting and calibration.
