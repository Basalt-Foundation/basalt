# Conditional Escrow / Smart Legal Contract

## Category

Infrastructure -- Programmable Escrow and Contract Automation

## Summary

A generalized escrow system with programmable release conditions, supporting multi-party agreements, oracle-triggered milestones, dispute resolution, and templates for common commercial patterns such as real estate closings, M&A transactions, and service contracts. Unlike the base Escrow system contract (0x0103), this contract supports arbitrarily complex conditional logic, nested milestones, and integration with external data sources.

## Why It's Useful

- **Trustless Agreements**: Counterparties can enter commercial agreements without trusting each other or relying on a single intermediary, since funds are released only when verifiable conditions are met.
- **Reduced Legal Costs**: Automating escrow release conditions reduces the need for legal intermediaries, escrow agents, and dispute resolution services in straightforward transactions.
- **Programmable Conditions**: Beyond simple time locks, this escrow supports oracle data (delivery confirmation, inspection results), multi-party approval, compound conditions (AND/OR logic), and external contract state checks.
- **Template Library**: Pre-built templates for common transaction types (real estate, freelance work, M&A, supply chain) reduce setup time and codify best practices.
- **Dispute Resolution**: Built-in arbitration mechanism with staked arbiters provides a decentralized fallback when conditions are ambiguous or contested.
- **Milestone Payments**: Complex projects with multiple deliverables can use milestone-based release, where funds are released incrementally as each milestone is verified.
- **Transparency and Audit Trail**: All condition evaluations, releases, and disputes are recorded on-chain, providing a complete audit trail.

## Key Features

- **Condition Engine**: Define release conditions using a composable condition tree: AND, OR, NOT operators combining atomic conditions (time, oracle, approval, contract state).
- **Multi-Party Support**: Escrows involving more than two parties (e.g., buyer, seller, inspector, lender, title company in real estate).
- **Oracle-Triggered Conditions**: Conditions can depend on external oracle reports (delivery tracking, inspection results, price feeds).
- **Milestone System**: Break a single escrow into sequential or parallel milestones, each with its own conditions, amounts, and deadlines.
- **Dispute Resolution**: If conditions are contested, either party can initiate a dispute. Staked arbiters review evidence and vote on resolution.
- **Partial Release**: Release a portion of escrowed funds when partial conditions are met, holding the remainder for subsequent milestones.
- **Refund Conditions**: Define conditions under which funds automatically return to the depositor (e.g., deadline expired, inspection failed).
- **Template Registry**: Governance-curated templates for common patterns, each with pre-defined condition structures, party roles, and fee schedules.
- **Time Locks**: Minimum and maximum hold periods. Auto-release after timeout if no dispute is raised.
- **Multi-Currency Escrow**: Hold BST, WBSLT, any BST-20 token, or BST-721 NFTs in escrow.
- **Confidential Escrow**: Optional Pedersen commitment to hide escrow amounts while proving sufficient funds.
- **Recurring Escrow**: Templates for recurring payments (rent, subscriptions) with automatic renewal.
- **Document Hashing**: Attach document hashes (contracts, invoices, delivery receipts) to the escrow record for reference.

## Basalt-Specific Advantages

- **ZK Compliance for Regulated Escrow**: Real estate, M&A, and other regulated escrow transactions can require ZK compliance proofs (KYC, accredited investor, jurisdiction verification) from all parties without revealing identity on-chain. This enables compliant escrow on a public blockchain.
- **BST-VC Credential Verification**: Party qualifications (licensed contractor, accredited investor, registered agent) can be verified via BST-VC credentials, ensuring only qualified parties participate in specialized escrow types.
- **Confidential Amounts via Pedersen Commitments**: Escrow amounts can be hidden from public view using Pedersen commitments while still proving to the condition engine that sufficient funds are held. Critical for M&A and real estate where deal sizes are commercially sensitive.
- **AOT-Compiled Condition Evaluation**: Complex condition trees with nested AND/OR/NOT logic execute in AOT-compiled native code, enabling efficient evaluation of sophisticated multi-condition escrows without excessive gas costs.
- **Ed25519 Multi-Party Signatures**: Multi-party approval conditions use Ed25519 signatures, which are faster to verify than ECDSA, reducing the cost of conditions requiring approval from many parties.
- **BLS Aggregate Arbiter Signatures**: Dispute resolution votes from multiple arbiters can be aggregated into a single BLS signature, making arbiter consensus efficient even with large arbiter panels.
- **BST-3525 SFT Escrow Positions**: Escrow positions (representing a party's claim on escrowed funds) can be tokenized as BST-3525 semi-fungible tokens, enabling assignment of escrow rights (e.g., factoring of receivables).

## Token Standards Used

- **BST-20**: Primary escrow denomination for fungible token escrows.
- **BST-721**: NFT escrow support (e.g., escrowing an NFT in exchange for payment).
- **BST-3525 (SFT)**: Escrow position tokens representing claims, with metadata for conditions, amounts, and status.
- **BST-VC (Verifiable Credentials)**: Party qualification credentials (KYC, licenses, accreditations).

## Integration Points

- **Escrow (0x0103)**: Extends the base escrow with programmable conditions. Can delegate simple holds to the base Escrow contract.
- **Governance (0x0102)**: Governs template registry, arbiter registration, fee parameters, and dispute resolution rules.
- **SchemaRegistry (0x...1006)**: Credential schemas for party qualifications and compliance requirements.
- **IssuerRegistry (0x...1007)**: Trusted issuers for party credential verification.
- **StakingPool (0x0105)**: Arbiters stake BST to participate in dispute resolution; slashed for provably wrong decisions.
- **BNS (0x0101)**: Escrow instances and templates registered under BNS names.
- **BridgeETH (0x...1008)**: Cross-chain escrow for deals involving assets on multiple chains.

## Technical Sketch

```csharp
// ============================================================
// ConditionalEscrow -- Programmable multi-party escrow
// ============================================================

[BasaltContract(TypeId = 0x0306)]
public partial class ConditionalEscrow : SdkContract
{
    // --- Storage ---

    private StorageValue<ulong> _nextEscrowId;
    private StorageMap<ulong, EscrowRecord> _escrows;
    private StorageMap<ulong, byte> _escrowStatus; // 0=created, 1=funded, 2=active,
                                                    // 3=releasing, 4=completed,
                                                    // 5=disputed, 6=refunded, 7=cancelled

    // Milestones: escrowId => milestoneIndex => Milestone
    private StorageMap<ulong, StorageMap<uint, Milestone>> _milestones;
    private StorageMap<ulong, uint> _milestoneCount;

    // Conditions: conditionId => Condition
    private StorageValue<ulong> _nextConditionId;
    private StorageMap<ulong, Condition> _conditions;

    // Parties: escrowId => partyRole => Address
    private StorageMap<ulong, StorageMap<uint, Address>> _parties;
    private StorageMap<ulong, uint> _partyCount;

    // Approvals: escrowId => milestoneIndex => party => approved
    private StorageMap<ulong, StorageMap<uint, StorageMap<Address, bool>>> _approvals;

    // Documents: escrowId => docIndex => hash
    private StorageMap<ulong, StorageMap<uint, Hash256>> _documents;
    private StorageMap<ulong, uint> _documentCount;

    // Disputes: escrowId => DisputeState
    private StorageMap<ulong, DisputeState> _disputes;

    // Arbiters
    private StorageMap<Address, bool> _registeredArbiters;
    private StorageMap<Address, UInt256> _arbiterStakes;

    // Templates: templateId => TemplateConfig
    private StorageMap<ulong, TemplateConfig> _templates;
    private StorageValue<ulong> _nextTemplateId;

    // Platform fee
    private StorageValue<uint> _platformFeeBps;
    private StorageValue<Address> _feeRecipient;

    // --- Data Structures ---

    public struct EscrowRecord
    {
        public ulong EscrowId;
        public Address Creator;
        public Address EscrowToken;        // BST-20 token or Address.Zero for native BST
        public UInt256 TotalAmount;
        public UInt256 ReleasedAmount;
        public UInt256 RefundedAmount;
        public ulong CreatedAtBlock;
        public ulong DeadlineBlock;        // Auto-refund after this block
        public ulong TemplateId;           // 0 for custom escrows
        public Hash256 AgreementHash;      // Hash of off-chain legal agreement
    }

    public struct Milestone
    {
        public uint MilestoneIndex;
        public string Description;
        public UInt256 Amount;
        public ulong ConditionId;          // Root condition for this milestone
        public ulong DeadlineBlock;
        public byte Status;               // 0=pending, 1=met, 2=failed, 3=disputed
        public Address Recipient;          // Who receives funds when conditions are met
    }

    public struct Condition
    {
        public byte ConditionType;
        // 0=TimeLock, 1=PartyApproval, 2=OracleValue,
        // 3=ContractState, 4=AND, 5=OR, 6=NOT
        public ulong Param1;              // Depends on type (block number, oracle address, etc.)
        public ulong Param2;
        public byte[] ParamBytes;         // Additional data (oracle query, contract method)
        public ulong ChildCondition1;     // For AND/OR/NOT composite conditions
        public ulong ChildCondition2;     // For AND/OR (second operand)
    }

    public struct DisputeState
    {
        public Address Initiator;
        public string Reason;
        public ulong InitiatedAtBlock;
        public uint VotesForRelease;
        public uint VotesForRefund;
        public uint TotalArbiters;
        public bool Resolved;
    }

    public struct TemplateConfig
    {
        public string Name;
        public string Category;           // "real-estate", "freelance", "m-and-a", "supply-chain"
        public uint RequiredParties;
        public string[] PartyRoles;       // "buyer", "seller", "inspector", "lender"
        public uint DefaultMilestones;
        public uint DefaultFeeBps;
    }

    // --- Escrow Creation ---

    /// <summary>
    /// Create a new conditional escrow with milestones and conditions.
    /// </summary>
    public ulong CreateEscrow(
        Address escrowToken,
        UInt256 totalAmount,
        ulong deadlineBlock,
        Address[] parties,
        uint[] partyRoles,
        Hash256 agreementHash,
        ulong templateId)
    {
        Require(parties.Length == partyRoles.Length, "PARTY_ROLE_MISMATCH");
        Require(parties.Length >= 2, "MIN_2_PARTIES");
        Require(deadlineBlock > Context.BlockNumber, "INVALID_DEADLINE");

        ulong escrowId = _nextEscrowId.Get();
        _nextEscrowId.Set(escrowId + 1);

        _escrows.Set(escrowId, new EscrowRecord
        {
            EscrowId = escrowId,
            Creator = Context.Sender,
            EscrowToken = escrowToken,
            TotalAmount = totalAmount,
            ReleasedAmount = UInt256.Zero,
            RefundedAmount = UInt256.Zero,
            CreatedAtBlock = Context.BlockNumber,
            DeadlineBlock = deadlineBlock,
            TemplateId = templateId,
            AgreementHash = agreementHash
        });

        _escrowStatus.Set(escrowId, 0); // created

        // Register parties
        for (int i = 0; i < parties.Length; i++)
        {
            _parties.Get(escrowId).Set(partyRoles[i], parties[i]);
        }
        _partyCount.Set(escrowId, (uint)parties.Length);

        EmitEvent("EscrowCreated", escrowId, totalAmount, deadlineBlock);
        return escrowId;
    }

    /// <summary>
    /// Add a milestone with release conditions to an escrow.
    /// </summary>
    public void AddMilestone(
        ulong escrowId,
        string description,
        UInt256 amount,
        ulong conditionId,
        ulong deadlineBlock,
        Address recipient)
    {
        Require(_escrowStatus.Get(escrowId) == 0, "ESCROW_NOT_CREATED");
        RequireParty(escrowId);

        uint index = _milestoneCount.Get(escrowId);
        _milestones.Get(escrowId).Set(index, new Milestone
        {
            MilestoneIndex = index,
            Description = description,
            Amount = amount,
            ConditionId = conditionId,
            DeadlineBlock = deadlineBlock,
            Status = 0,
            Recipient = recipient
        });
        _milestoneCount.Set(escrowId, index + 1);

        EmitEvent("MilestoneAdded", escrowId, index, amount, description);
    }

    /// <summary>
    /// Define a condition (atomic or composite).
    /// </summary>
    public ulong DefineCondition(
        byte conditionType,
        ulong param1,
        ulong param2,
        byte[] paramBytes,
        ulong childCondition1,
        ulong childCondition2)
    {
        ulong conditionId = _nextConditionId.Get();
        _nextConditionId.Set(conditionId + 1);

        _conditions.Set(conditionId, new Condition
        {
            ConditionType = conditionType,
            Param1 = param1,
            Param2 = param2,
            ParamBytes = paramBytes,
            ChildCondition1 = childCondition1,
            ChildCondition2 = childCondition2
        });

        EmitEvent("ConditionDefined", conditionId, conditionType);
        return conditionId;
    }

    /// <summary>
    /// Fund the escrow by depositing the required amount.
    /// </summary>
    public void FundEscrow(ulong escrowId)
    {
        var escrow = _escrows.Get(escrowId);
        Require(_escrowStatus.Get(escrowId) == 0, "INVALID_STATUS");

        if (escrow.EscrowToken == Address.Zero)
        {
            Require(Context.TxValue >= escrow.TotalAmount, "INSUFFICIENT_FUNDS");
        }
        else
        {
            TransferTokenIn(escrow.EscrowToken, Context.Sender, escrow.TotalAmount);
        }

        _escrowStatus.Set(escrowId, 1); // funded
        EmitEvent("EscrowFunded", escrowId, escrow.TotalAmount);
    }

    /// <summary>
    /// Activate the escrow (all parties must confirm).
    /// </summary>
    public void ActivateEscrow(ulong escrowId)
    {
        Require(_escrowStatus.Get(escrowId) == 1, "NOT_FUNDED");
        RequireAllPartiesApproved(escrowId);

        _escrowStatus.Set(escrowId, 2); // active
        EmitEvent("EscrowActivated", escrowId);
    }

    // --- Condition Evaluation ---

    /// <summary>
    /// Evaluate a condition and claim a milestone release if conditions are met.
    /// </summary>
    public void ClaimMilestone(ulong escrowId, uint milestoneIndex)
    {
        Require(_escrowStatus.Get(escrowId) == 2, "NOT_ACTIVE");

        var milestone = _milestones.Get(escrowId).Get(milestoneIndex);
        Require(milestone.Status == 0, "MILESTONE_NOT_PENDING");

        // Evaluate condition tree
        bool conditionsMet = EvaluateCondition(milestone.ConditionId, escrowId);
        Require(conditionsMet, "CONDITIONS_NOT_MET");

        // Release funds
        milestone.Status = 1; // met
        _milestones.Get(escrowId).Set(milestoneIndex, milestone);

        var escrow = _escrows.Get(escrowId);
        escrow.ReleasedAmount = escrow.ReleasedAmount + milestone.Amount;
        _escrows.Set(escrowId, escrow);

        // Deduct platform fee
        uint feeBps = _platformFeeBps.Get();
        UInt256 fee = (milestone.Amount * feeBps) / 10000;
        UInt256 netAmount = milestone.Amount - fee;

        if (escrow.EscrowToken == Address.Zero)
        {
            Context.TransferNative(milestone.Recipient, netAmount);
            if (!fee.IsZero)
                Context.TransferNative(_feeRecipient.Get(), fee);
        }
        else
        {
            TransferTokenOut(escrow.EscrowToken, milestone.Recipient, netAmount);
            if (!fee.IsZero)
                TransferTokenOut(escrow.EscrowToken, _feeRecipient.Get(), fee);
        }

        // Check if all milestones completed
        if (AllMilestonesComplete(escrowId))
        {
            _escrowStatus.Set(escrowId, 4); // completed
            EmitEvent("EscrowCompleted", escrowId);
        }

        EmitEvent("MilestoneReleased", escrowId, milestoneIndex, netAmount);
    }

    // --- Party Approval ---

    /// <summary>
    /// Approve a milestone (for conditions that require party approval).
    /// </summary>
    public void ApproveMilestone(ulong escrowId, uint milestoneIndex)
    {
        RequireParty(escrowId);
        _approvals.Get(escrowId).Get(milestoneIndex).Set(Context.Sender, true);
        EmitEvent("MilestoneApproved", escrowId, milestoneIndex, Context.Sender);
    }

    // --- Dispute Resolution ---

    /// <summary>
    /// Initiate a dispute on an escrow.
    /// </summary>
    public void InitiateDispute(ulong escrowId, string reason)
    {
        Require(_escrowStatus.Get(escrowId) == 2, "NOT_ACTIVE");
        RequireParty(escrowId);

        _escrowStatus.Set(escrowId, 5); // disputed
        _disputes.Set(escrowId, new DisputeState
        {
            Initiator = Context.Sender,
            Reason = reason,
            InitiatedAtBlock = Context.BlockNumber,
            VotesForRelease = 0,
            VotesForRefund = 0,
            TotalArbiters = 0,
            Resolved = false
        });

        EmitEvent("DisputeInitiated", escrowId, Context.Sender, reason);
    }

    /// <summary>
    /// Arbiter votes on a dispute resolution.
    /// </summary>
    public void VoteOnDispute(ulong escrowId, bool voteForRelease)
    {
        Require(_escrowStatus.Get(escrowId) == 5, "NOT_DISPUTED");
        Require(_registeredArbiters.Get(Context.Sender), "NOT_ARBITER");

        var dispute = _disputes.Get(escrowId);
        Require(!dispute.Resolved, "ALREADY_RESOLVED");

        if (voteForRelease)
            dispute.VotesForRelease++;
        else
            dispute.VotesForRefund++;
        dispute.TotalArbiters++;

        _disputes.Set(escrowId, dispute);

        // Check if threshold reached (simple majority of 3 arbiters)
        if (dispute.TotalArbiters >= 3)
        {
            dispute.Resolved = true;
            _disputes.Set(escrowId, dispute);

            if (dispute.VotesForRelease > dispute.VotesForRefund)
            {
                // Release to recipient
                _escrowStatus.Set(escrowId, 3); // releasing
                EmitEvent("DisputeResolved", escrowId, "RELEASE");
            }
            else
            {
                // Refund to depositor
                RefundEscrow(escrowId);
                EmitEvent("DisputeResolved", escrowId, "REFUND");
            }
        }

        EmitEvent("ArbiterVoted", escrowId, Context.Sender, voteForRelease);
    }

    // --- Timeout / Refund ---

    /// <summary>
    /// Claim automatic refund after deadline if milestones are not met.
    /// </summary>
    public void ClaimTimeoutRefund(ulong escrowId)
    {
        var escrow = _escrows.Get(escrowId);
        Require(Context.BlockNumber > escrow.DeadlineBlock, "DEADLINE_NOT_REACHED");
        byte status = _escrowStatus.Get(escrowId);
        Require(status == 1 || status == 2, "INVALID_STATUS");

        RefundEscrow(escrowId);
    }

    // --- Document Management ---

    /// <summary>
    /// Attach a document hash to the escrow record.
    /// </summary>
    public void AttachDocument(ulong escrowId, Hash256 documentHash)
    {
        RequireParty(escrowId);
        uint index = _documentCount.Get(escrowId);
        _documents.Get(escrowId).Set(index, documentHash);
        _documentCount.Set(escrowId, index + 1);
        EmitEvent("DocumentAttached", escrowId, index, documentHash);
    }

    // --- Template Management ---

    /// <summary>
    /// Register a new escrow template. Governance-only.
    /// </summary>
    public ulong RegisterTemplate(TemplateConfig config)
    {
        RequireGovernance();
        ulong templateId = _nextTemplateId.Get();
        _nextTemplateId.Set(templateId + 1);
        _templates.Set(templateId, config);
        EmitEvent("TemplateRegistered", templateId, config.Name);
        return templateId;
    }

    // --- Query Methods ---

    public EscrowRecord GetEscrow(ulong escrowId) => _escrows.Get(escrowId);
    public byte GetEscrowStatus(ulong escrowId) => _escrowStatus.Get(escrowId);
    public Milestone GetMilestone(ulong escrowId, uint index)
        => _milestones.Get(escrowId).Get(index);
    public uint GetMilestoneCount(ulong escrowId) => _milestoneCount.Get(escrowId);
    public DisputeState GetDispute(ulong escrowId) => _disputes.Get(escrowId);
    public TemplateConfig GetTemplate(ulong templateId) => _templates.Get(templateId);

    // --- Internal Helpers ---

    private bool EvaluateCondition(ulong conditionId, ulong escrowId)
    {
        var cond = _conditions.Get(conditionId);
        switch (cond.ConditionType)
        {
            case 0: // TimeLock
                return Context.BlockNumber >= cond.Param1;

            case 1: // PartyApproval
                Address party = _parties.Get(escrowId).Get((uint)cond.Param1);
                return _approvals.Get(escrowId).Get((uint)cond.Param2).Get(party);

            case 2: // OracleValue
                return CheckOracleCondition(cond);

            case 3: // ContractState
                return CheckContractState(cond);

            case 4: // AND
                return EvaluateCondition(cond.ChildCondition1, escrowId)
                    && EvaluateCondition(cond.ChildCondition2, escrowId);

            case 5: // OR
                return EvaluateCondition(cond.ChildCondition1, escrowId)
                    || EvaluateCondition(cond.ChildCondition2, escrowId);

            case 6: // NOT
                return !EvaluateCondition(cond.ChildCondition1, escrowId);

            default:
                return false;
        }
    }

    private void RefundEscrow(ulong escrowId)
    {
        var escrow = _escrows.Get(escrowId);
        UInt256 refundAmount = escrow.TotalAmount - escrow.ReleasedAmount;
        escrow.RefundedAmount = refundAmount;
        _escrows.Set(escrowId, escrow);
        _escrowStatus.Set(escrowId, 6); // refunded

        if (escrow.EscrowToken == Address.Zero)
            Context.TransferNative(escrow.Creator, refundAmount);
        else
            TransferTokenOut(escrow.EscrowToken, escrow.Creator, refundAmount);

        EmitEvent("EscrowRefunded", escrowId, refundAmount);
    }

    private bool AllMilestonesComplete(ulong escrowId)
    {
        uint count = _milestoneCount.Get(escrowId);
        for (uint i = 0; i < count; i++)
        {
            if (_milestones.Get(escrowId).Get(i).Status != 1)
                return false;
        }
        return true;
    }

    private void RequireParty(ulong escrowId) { /* verify sender is a party */ }
    private void RequireAllPartiesApproved(ulong escrowId) { /* ... */ }
    private bool CheckOracleCondition(Condition cond) { /* ... */ return true; }
    private bool CheckContractState(Condition cond) { /* ... */ return true; }
    private void RequireGovernance() { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
}
```

## Complexity

**High** -- The recursive condition evaluation engine (supporting AND/OR/NOT trees of atomic conditions) is the primary source of complexity. Oracle integration and contract state checking require careful validation to prevent manipulation. The dispute resolution mechanism with arbiter staking and voting adds governance complexity. Multi-party state management (ensuring all parties agree on escrow parameters before activation) requires careful coordination logic. Template management adds an additional configuration layer. The interaction between milestones, conditions, disputes, and timeouts creates a complex state machine with many possible transitions.

## Priority

**P1** -- Escrow functionality is foundational for real-world commercial activity on Basalt. Simple escrow (0x0103) exists but lacks the programmable conditions needed for complex transactions. This contract bridges blockchain technology and traditional commerce, enabling real estate, freelance work, and business-to-business transactions. It should be available early to attract enterprise and commercial users to the platform.
