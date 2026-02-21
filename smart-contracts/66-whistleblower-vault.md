# Whistleblower Vault

## Category

Privacy and Compliance -- Confidential Disclosure Infrastructure

## Summary

A confidential evidence submission system enabling insiders to submit encrypted evidence with zero-knowledge proof of employment or organizational membership (via BST-VC credentials) without revealing their identity. Evidence is stored encrypted on-chain, decryptable only by a designated recipient or upon governance vote. Verified disclosures earn bounties funded by a staking pool.

## Why It's Useful

- **Whistleblower Protection**: Employees, contractors, and insiders can report fraud, corruption, or safety violations without risking retaliation, since their identity is cryptographically hidden behind ZK proofs.
- **Regulatory Compliance**: Many jurisdictions (EU Whistleblower Directive 2019/1937, US Dodd-Frank, SOX) mandate whistleblower channels; this provides a tamper-proof, auditable channel that satisfies regulatory requirements.
- **Corporate Governance**: Boards and compliance officers gain access to a trustworthy, censorship-resistant disclosure mechanism that cannot be suppressed by management.
- **Evidence Integrity**: On-chain storage with timestamps provides irrefutable proof of when evidence was submitted, preventing backdating or tampering.
- **Bounty Incentives**: Financial rewards for verified disclosures encourage reporting and offset the personal risk whistleblowers take.
- **Decentralized Adjudication**: Governance-based revelation prevents any single party from unilaterally suppressing or revealing evidence.

## Key Features

- **Anonymous Submission**: Submit evidence encrypted with a designated recipient's public key. The submitter's identity is never stored on-chain -- only a ZK proof of credential validity.
- **ZK Credential Verification**: Submitter proves they hold a valid BST-VC employment credential from a registered issuer (verified via IssuerRegistry) without revealing which specific credential or employee they are.
- **Tiered Evidence Classification**: Evidence classified by severity (informational, significant, critical, emergency) with different handling procedures for each tier.
- **Designated Recipient System**: Each vault has one or more designated recipients whose public keys are used for encryption. Recipients can be compliance officers, board members, or external auditors.
- **Governance-Triggered Revelation**: For cases where the designated recipient is compromised or unresponsive, a governance vote can authorize revelation using a threshold decryption scheme.
- **Bounty Pool**: Stakers fund a bounty pool. Verified disclosures earn bounties proportional to severity and impact. Bounty amounts are governed by the DAO.
- **Evidence Lifecycle**: Submissions progress through states: submitted, acknowledged, under-review, verified, rejected, or revealed.
- **Anti-Spam Staking**: Submitters must lock a small stake (refunded upon verification) to prevent spam submissions. Stake is forfeited for provably false submissions.
- **Nullifier-Based Anti-Duplication**: Each submission includes a nullifier derived from the evidence hash, preventing the same evidence from being submitted twice without linking submissions to identities.
- **Time-Locked Auto-Reveal**: Optional time-lock on evidence: if no action is taken within a configurable period, evidence is automatically revealed to all designated recipients.
- **Encrypted Metadata**: Submission metadata (category, severity, affected parties) is encrypted alongside evidence, preventing even metadata-based deanonymization.
- **Viewing Key Delegation**: Designated recipients can delegate viewing access to investigators without revealing their own private key.

## Basalt-Specific Advantages

- **ZK Compliance Layer (Native)**: Basalt's built-in ZK compliance infrastructure (SchemaRegistry, IssuerRegistry, ZkComplianceVerifier, Groth16 proofs) provides the exact primitives needed for proving credential validity without identity revelation. On other chains, this would require deploying an entire ZK stack from scratch.
- **BST-VC Credential Standard**: The W3C-compatible BST-VC standard on Basalt enables verifiable employment credentials that are natively understood by the contract runtime. No custom credential formats or off-chain verification needed.
- **Nullifier Anti-Correlation**: Basalt's nullifier system (already used in ZK compliance proofs) prevents linking multiple submissions to the same whistleblower, a critical privacy guarantee that is extremely difficult to implement correctly without native support.
- **Confidential Transactions via Pedersen Commitments**: Bounty payments can be made using confidential transfers, hiding the amount paid to the whistleblower and preventing correlation between bounty size and disclosure severity (which could narrow the anonymity set).
- **AOT-Compiled Proof Verification**: Groth16 proof verification compiled ahead-of-time executes significantly faster than interpreted verification on EVM chains, reducing the gas cost of each anonymous submission.
- **Ed25519 for Recipient Keys**: Designated recipients use the same Ed25519 keys used throughout Basalt for encryption (via X25519 key exchange derived from Ed25519 keys), eliminating the need for separate encryption key infrastructure.
- **BLS Signature Aggregation**: Governance votes to authorize evidence revelation can be aggregated into a single BLS signature, making threshold revelation efficient even with many governance participants.

## Token Standards Used

- **BST-VC (Verifiable Credentials)**: Employment/membership credentials issued by organizations, verified in ZK to prove insider status without revealing identity.
- **BST-20**: Bounty token (native BST or wrapped BST) for rewarding verified disclosures. Anti-spam stake denominated in BST-20.
- **BST-721**: Optional non-transferable (soulbound) receipt token issued to the whistleblower's anonymous address as proof of submission.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines credential schemas for employment verification (e.g., "EmployeeCredentialV1" schema with fields: organization, role-level, department-category, start-date).
- **IssuerRegistry (0x...1007)**: Validates that the credential issuer is a registered and trusted organization. Only credentials from registered issuers are accepted.
- **Governance (0x0102)**: Governance votes authorize evidence revelation in cases where the designated recipient is unavailable. Also governs bounty pool parameters and severity classifications.
- **Escrow (0x0103)**: Bounty payouts are held in escrow during the verification period, released upon confirmation or returned to the pool upon rejection.
- **StakingPool (0x0105)**: A portion of staking rewards can be directed to the whistleblower bounty pool, providing sustainable funding.
- **BNS (0x0101)**: The vault contract registers under a human-readable name (e.g., `whistleblower.basalt`) for discoverability.

## Technical Sketch

```csharp
// ============================================================
// WhistleblowerVault -- Anonymous evidence submission system
// ============================================================

[BasaltContract(TypeId = 0x0300)]
public partial class WhistleblowerVault : SdkContract
{
    // --- Storage ---

    // Submission counter
    private StorageValue<ulong> _nextSubmissionId;

    // submissionId => EncryptedSubmission
    private StorageMap<ulong, EncryptedSubmission> _submissions;

    // submissionId => SubmissionStatus (0=submitted, 1=acknowledged, 2=under-review,
    //                                    3=verified, 4=rejected, 5=revealed)
    private StorageMap<ulong, byte> _status;

    // submissionId => bounty amount awarded
    private StorageMap<ulong, UInt256> _bountyAwarded;

    // nullifier => bool (prevents duplicate submissions)
    private StorageMap<Hash256, bool> _usedNullifiers;

    // Designated recipient index => public key (Ed25519)
    private StorageMap<uint, byte[]> _recipients;
    private StorageValue<uint> _recipientCount;

    // Bounty pool balance
    private StorageValue<UInt256> _bountyPool;

    // Anti-spam stake requirement
    private StorageValue<UInt256> _minimumStake;

    // submissionId => anti-spam stake deposited
    private StorageMap<ulong, UInt256> _stakes;

    // submissionId => staker address (anonymous address, not real identity)
    private StorageMap<ulong, Address> _stakers;

    // Severity-based bounty multipliers (basis points)
    private StorageMap<byte, uint> _severityMultipliers;

    // Base bounty amount
    private StorageValue<UInt256> _baseBounty;

    // Auto-reveal timeout in blocks (0 = disabled)
    private StorageValue<ulong> _autoRevealTimeout;

    // submissionId => block number of submission
    private StorageMap<ulong, ulong> _submissionBlock;

    // Admin / governance address
    private StorageValue<Address> _admin;

    // --- Data Structures ---

    public struct EncryptedSubmission
    {
        public byte[] EncryptedEvidence;      // Encrypted with recipient's public key
        public byte[] EncryptedMetadata;      // Category, severity, affected parties
        public byte[] ZkProof;                // Groth16 proof of credential validity
        public Hash256 Nullifier;             // Anti-correlation nullifier
        public byte Severity;                 // 0=info, 1=significant, 2=critical, 3=emergency
        public Hash256 EvidenceCommitment;    // Pedersen commitment to evidence hash
    }

    // --- Constructor ---

    public void Initialize(Address admin, UInt256 minimumStake, UInt256 baseBounty,
                           ulong autoRevealTimeout)
    {
        Require(Context.Sender == admin, "UNAUTHORIZED");
        _admin.Set(admin);
        _minimumStake.Set(minimumStake);
        _baseBounty.Set(baseBounty);
        _autoRevealTimeout.Set(autoRevealTimeout);
        _nextSubmissionId.Set(1);

        // Default severity multipliers (basis points: 10000 = 1x)
        _severityMultipliers.Set(0, 5000);   // informational: 0.5x
        _severityMultipliers.Set(1, 10000);  // significant: 1x
        _severityMultipliers.Set(2, 25000);  // critical: 2.5x
        _severityMultipliers.Set(3, 50000);  // emergency: 5x
    }

    // --- Core Submission ---

    /// <summary>
    /// Submit encrypted evidence with ZK proof of insider status.
    /// The caller must hold a valid BST-VC employment credential from a registered issuer.
    /// The ZK proof demonstrates credential validity without revealing the credential itself.
    /// </summary>
    public ulong SubmitEvidence(
        byte[] encryptedEvidence,
        byte[] encryptedMetadata,
        byte[] zkProof,
        Hash256 nullifier,
        byte severity,
        Hash256 evidenceCommitment,
        byte[] credentialSchemaHash,
        byte[] issuerPublicInputs)
    {
        // Verify anti-spam stake
        Require(Context.TxValue >= _minimumStake.Get(), "INSUFFICIENT_STAKE");

        // Verify nullifier has not been used (prevents duplicate submissions)
        Require(!_usedNullifiers.Get(nullifier), "DUPLICATE_NULLIFIER");

        // Verify severity is valid
        Require(severity <= 3, "INVALID_SEVERITY");

        // Verify the ZK proof:
        // 1. The submitter holds a valid BST-VC credential
        // 2. The credential was issued by a registered issuer
        // 3. The credential matches the required employment schema
        // 4. The nullifier is correctly derived from the credential
        bool proofValid = VerifyCredentialProof(
            zkProof, nullifier, credentialSchemaHash, issuerPublicInputs);
        Require(proofValid, "INVALID_ZK_PROOF");

        // Store submission
        ulong submissionId = _nextSubmissionId.Get();
        _nextSubmissionId.Set(submissionId + 1);

        _submissions.Set(submissionId, new EncryptedSubmission
        {
            EncryptedEvidence = encryptedEvidence,
            EncryptedMetadata = encryptedMetadata,
            ZkProof = zkProof,
            Nullifier = nullifier,
            Severity = severity,
            EvidenceCommitment = evidenceCommitment
        });

        _status.Set(submissionId, 0); // submitted
        _usedNullifiers.Set(nullifier, true);
        _stakes.Set(submissionId, Context.TxValue);
        _stakers.Set(submissionId, Context.Sender);
        _submissionBlock.Set(submissionId, Context.BlockNumber);

        EmitEvent("EvidenceSubmitted", submissionId, severity, evidenceCommitment);
        return submissionId;
    }

    // --- Recipient Management ---

    /// <summary>
    /// Register a designated recipient who can decrypt evidence.
    /// Admin-only.
    /// </summary>
    public uint AddRecipient(byte[] recipientPublicKey)
    {
        RequireAdmin();
        Require(recipientPublicKey.Length == 32, "INVALID_KEY_LENGTH");

        uint index = _recipientCount.Get();
        _recipients.Set(index, recipientPublicKey);
        _recipientCount.Set(index + 1);

        EmitEvent("RecipientAdded", index, recipientPublicKey);
        return index;
    }

    /// <summary>
    /// Remove a designated recipient. Admin-only.
    /// </summary>
    public void RemoveRecipient(uint recipientIndex)
    {
        RequireAdmin();
        Require(recipientIndex < _recipientCount.Get(), "INVALID_INDEX");
        _recipients.Set(recipientIndex, new byte[0]);
        EmitEvent("RecipientRemoved", recipientIndex);
    }

    // --- Evidence Lifecycle ---

    /// <summary>
    /// Acknowledge receipt of a submission. Recipient-only.
    /// </summary>
    public void AcknowledgeSubmission(ulong submissionId)
    {
        RequireRecipient();
        Require(_status.Get(submissionId) == 0, "INVALID_STATUS");
        _status.Set(submissionId, 1); // acknowledged
        EmitEvent("SubmissionAcknowledged", submissionId, Context.Sender);
    }

    /// <summary>
    /// Mark a submission as under review. Recipient-only.
    /// </summary>
    public void MarkUnderReview(ulong submissionId)
    {
        RequireRecipient();
        byte currentStatus = _status.Get(submissionId);
        Require(currentStatus == 0 || currentStatus == 1, "INVALID_STATUS");
        _status.Set(submissionId, 2); // under-review
        EmitEvent("SubmissionUnderReview", submissionId);
    }

    /// <summary>
    /// Verify a submission and award bounty. Recipient-only.
    /// </summary>
    public void VerifySubmission(ulong submissionId)
    {
        RequireRecipient();
        byte currentStatus = _status.Get(submissionId);
        Require(currentStatus == 1 || currentStatus == 2, "INVALID_STATUS");

        _status.Set(submissionId, 3); // verified

        // Calculate and award bounty
        var submission = _submissions.Get(submissionId);
        uint multiplier = _severityMultipliers.Get(submission.Severity);
        UInt256 bounty = (_baseBounty.Get() * multiplier) / 10000;

        UInt256 poolBalance = _bountyPool.Get();
        if (bounty > poolBalance)
            bounty = poolBalance;

        if (!bounty.IsZero)
        {
            _bountyPool.Set(poolBalance - bounty);
            _bountyAwarded.Set(submissionId, bounty);

            // Transfer bounty to the anonymous submitter address
            Address staker = _stakers.Get(submissionId);
            Context.TransferNative(staker, bounty);
        }

        // Refund anti-spam stake
        UInt256 stake = _stakes.Get(submissionId);
        if (!stake.IsZero)
        {
            Address staker = _stakers.Get(submissionId);
            Context.TransferNative(staker, stake);
            _stakes.Set(submissionId, UInt256.Zero);
        }

        EmitEvent("SubmissionVerified", submissionId, bounty);
    }

    /// <summary>
    /// Reject a submission. Forfeits the anti-spam stake. Recipient-only.
    /// </summary>
    public void RejectSubmission(ulong submissionId, byte[] encryptedReason)
    {
        RequireRecipient();
        byte currentStatus = _status.Get(submissionId);
        Require(currentStatus <= 2, "INVALID_STATUS");

        _status.Set(submissionId, 4); // rejected

        // Forfeit stake to bounty pool
        UInt256 stake = _stakes.Get(submissionId);
        if (!stake.IsZero)
        {
            _bountyPool.Set(_bountyPool.Get() + stake);
            _stakes.Set(submissionId, UInt256.Zero);
        }

        EmitEvent("SubmissionRejected", submissionId);
    }

    // --- Governance Revelation ---

    /// <summary>
    /// Governance-authorized revelation of evidence.
    /// Called by the Governance contract after a successful vote.
    /// </summary>
    public byte[] GovernanceReveal(ulong submissionId, ulong governanceProposalId)
    {
        Require(Context.Sender == GetGovernanceAddress(), "NOT_GOVERNANCE");
        byte currentStatus = _status.Get(submissionId);
        Require(currentStatus != 5, "ALREADY_REVEALED");

        _status.Set(submissionId, 5); // revealed
        var submission = _submissions.Get(submissionId);

        EmitEvent("EvidenceRevealed", submissionId, governanceProposalId);
        return submission.EncryptedEvidence;
    }

    /// <summary>
    /// Auto-reveal evidence if the timeout has passed and no action was taken.
    /// Can be called by anyone.
    /// </summary>
    public void TriggerAutoReveal(ulong submissionId)
    {
        ulong timeout = _autoRevealTimeout.Get();
        Require(timeout > 0, "AUTO_REVEAL_DISABLED");

        byte currentStatus = _status.Get(submissionId);
        Require(currentStatus == 0, "ALREADY_PROCESSED");

        ulong submittedAt = _submissionBlock.Get(submissionId);
        Require(Context.BlockNumber >= submittedAt + timeout, "TIMEOUT_NOT_REACHED");

        _status.Set(submissionId, 5); // revealed
        EmitEvent("EvidenceAutoRevealed", submissionId);
    }

    // --- Bounty Pool Management ---

    /// <summary>
    /// Fund the bounty pool. Anyone can contribute.
    /// </summary>
    public void FundBountyPool()
    {
        Require(Context.TxValue > UInt256.Zero, "ZERO_FUNDING");
        _bountyPool.Set(_bountyPool.Get() + Context.TxValue);
        EmitEvent("BountyPoolFunded", Context.Sender, Context.TxValue);
    }

    /// <summary>
    /// Governance-only: update bounty parameters.
    /// </summary>
    public void UpdateBountyParameters(UInt256 baseBounty, UInt256 minimumStake)
    {
        RequireGovernance();
        _baseBounty.Set(baseBounty);
        _minimumStake.Set(minimumStake);
        EmitEvent("BountyParametersUpdated", baseBounty, minimumStake);
    }

    /// <summary>
    /// Governance-only: update severity multipliers.
    /// </summary>
    public void UpdateSeverityMultiplier(byte severity, uint multiplierBasisPoints)
    {
        RequireGovernance();
        Require(severity <= 3, "INVALID_SEVERITY");
        _severityMultipliers.Set(severity, multiplierBasisPoints);
    }

    // --- Viewing Key Delegation ---

    /// <summary>
    /// Delegate viewing access to an investigator for a specific submission.
    /// Recipient-only. Emits an event with re-encrypted evidence key.
    /// </summary>
    public void DelegateViewingAccess(
        ulong submissionId,
        Address investigator,
        byte[] reEncryptedKey)
    {
        RequireRecipient();
        EmitEvent("ViewingAccessDelegated", submissionId, investigator, reEncryptedKey);
    }

    // --- Query Methods ---

    public byte GetSubmissionStatus(ulong submissionId)
        => _status.Get(submissionId);

    public UInt256 GetBountyPoolBalance()
        => _bountyPool.Get();

    public UInt256 GetMinimumStake()
        => _minimumStake.Get();

    public UInt256 GetBountyAwarded(ulong submissionId)
        => _bountyAwarded.Get(submissionId);

    public uint GetRecipientCount()
        => _recipientCount.Get();

    public ulong GetSubmissionCount()
        => _nextSubmissionId.Get() - 1;

    // --- Internal Helpers ---

    private bool VerifyCredentialProof(
        byte[] zkProof, Hash256 nullifier,
        byte[] credentialSchemaHash, byte[] issuerPublicInputs)
    {
        // Calls into the ZkComplianceVerifier to verify Groth16 proof
        // Checks: (1) valid credential, (2) registered issuer, (3) matching schema,
        //         (4) nullifier correctly derived
        // Returns true if all checks pass
        /* ... */
        return true;
    }

    private void RequireAdmin()
    {
        Require(Context.Sender == _admin.Get(), "NOT_ADMIN");
    }

    private void RequireGovernance()
    {
        Require(Context.Sender == GetGovernanceAddress(), "NOT_GOVERNANCE");
    }

    private void RequireRecipient()
    {
        bool isRecipient = false;
        uint count = _recipientCount.Get();
        byte[] senderBytes = Context.Sender.ToArray();
        for (uint i = 0; i < count; i++)
        {
            byte[] recipientKey = _recipients.Get(i);
            if (recipientKey.Length > 0 && DeriveAddress(recipientKey) == Context.Sender)
            {
                isRecipient = true;
                break;
            }
        }
        Require(isRecipient, "NOT_RECIPIENT");
    }

    private Address GetGovernanceAddress() { /* returns 0x...1003 */ }
    private Address DeriveAddress(byte[] publicKey) { /* Keccak-256 derivation */ }
}
```

## Complexity

**High** -- This contract combines multiple advanced cryptographic primitives: ZK proof verification (Groth16), encrypted storage, nullifier management, Pedersen commitments, and Ed25519-to-X25519 key derivation for encryption. The evidence lifecycle state machine with governance-triggered revelation adds significant complexity. Correct handling of anti-correlation (ensuring multiple submissions cannot be linked to the same person) requires careful nullifier scheme design. The intersection of privacy guarantees and bounty incentives creates subtle economic attack vectors that must be analyzed.

## Priority

**P2** -- While this is a unique and high-impact application that showcases Basalt's privacy capabilities, it serves a specialized use case (corporate whistleblowing) rather than core DeFi infrastructure. It should be built after foundational DeFi primitives (DEX, lending, stablecoins) and governance tooling are stable. However, it is an excellent flagship demonstration of Basalt's ZK compliance layer and could attract significant enterprise interest.
