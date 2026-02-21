# Real Estate Fractionalization

## Category

Real-World Assets (RWA) / Property Investment / Tokenized Securities

## Summary

Real Estate Fractionalization is a BST-3525 semi-fungible token contract that tokenizes property ownership into tradable fractional shares. Each property is represented by a unique slot (property ID), and the token value represents the ownership percentage (in basis points out of 10,000, allowing fractional ownership down to 0.01%). The contract handles rental income distribution proportional to ownership, KYC-gated transfers, and on-chain governance for property management decisions such as maintenance, renovations, or sale authorizations.

A designated property manager role is responsible for operational management (collecting rent, authorizing maintenance), while ownership governance follows a stake-weighted voting model. This creates a fully on-chain REIT-like structure where property investors can buy, sell, and trade fractional real estate positions on the secondary market with compliance verification built into the protocol.

## Why It's Useful

- Real estate is the world's largest asset class (~$330 trillion globally), yet it remains one of the most illiquid; tokenization enables 24/7 fractional trading with settlement in seconds rather than months.
- Minimum investment in commercial real estate typically starts at $50,000-$250,000; fractionalization lowers this to whatever threshold the property issuer sets, democratizing access to institutional-grade properties.
- Rental income distribution in traditional REITs involves multiple intermediaries, quarterly reporting delays, and opaque fee structures; on-chain distribution is transparent, auditable, and can occur at any frequency.
- Cross-border real estate investment is hindered by complex regulatory requirements; ZK-based KYC verification enables compliant international investment without revealing personal data to other investors or public observers.
- Property governance in co-ownership structures (tenancy-in-common, fractional REITs) is often contentious and legally complex; on-chain weighted voting provides transparent, immutable decision-making with verifiable outcomes.
- Property provenance and ownership history are often fragmented across multiple registries; on-chain records provide a single source of truth for the full ownership chain.

## Key Features

- Property registration by verified issuers: each property gets a unique slot ID with metadata URI linking to legal documents, appraisals, and inspection reports.
- Fractional ownership minting: property tokens are minted with value representing ownership basis points (1 = 0.01%, 10000 = 100%).
- KYC-gated primary and secondary markets: all buyers must have valid KYC credentials verified through the ZK compliance layer before acquiring tokens.
- Rental income distribution: property manager deposits rental income into the contract, and owners claim proportional shares based on their ownership percentage.
- Property governance: owners vote on proposals (maintenance, renovation, sale authorization, property manager replacement) with voting power proportional to ownership share.
- Property manager role: designated manager handles operational tasks (rent collection, expense payments, maintenance authorization) with on-chain accountability.
- Property manager replacement: governance vote can replace the property manager if a supermajority of ownership votes for removal.
- Expense tracking: property manager records expenses against the property, deducted from rental income before distribution.
- Property sale mechanism: if a governance proposal to sell passes, the property enters a sale process where proceeds are distributed proportionally to all owners.
- Appraisal updates: registered appraisers (verified via IssuerRegistry) can submit updated property valuations stored on-chain.
- Lockup periods: configurable transfer lockup after initial purchase to prevent speculative flipping.

## Basalt-Specific Advantages

- **BST-3525 Semi-Fungible Tokens**: The slot-based model perfectly represents property portfolios: each property is a slot, and ownership shares within a property are fungible by value. Owners can split, merge, or transfer fractional positions without affecting other owners in the same property. This is a natural fit that ERC-20 (one token per property) or ERC-721 (indivisible) cannot achieve.
- **ZK Compliance Layer**: KYC verification uses Basalt's built-in ZkComplianceVerifier and SchemaRegistry to verify investor identity without exposing personal data on-chain. A buyer proves they have valid KYC credentials from an accredited issuer without revealing their name, address, or nationality to other market participants.
- **BST-VC Verifiable Credentials**: Property appraisals, title documents, and KYC attestations are represented as W3C Verifiable Credentials via BST-VC, providing standardized, tamper-proof documentation that can be verified by any participant.
- **Governance Integration**: Basalt's built-in Governance contract (quadratic voting, delegation, timelock) can be reused for property-level decisions, providing battle-tested governance primitives without reimplementation.
- **Escrow Integration**: Rental income and property sale proceeds can be held in the protocol-level Escrow contract, providing trustless settlement with time-locked release conditions.
- **AOT-Compiled Execution**: Rental income distribution and governance vote tallying involve iterative calculations over potentially hundreds of fractional owners; AOT compilation ensures deterministic, predictable gas costs for these operations.
- **Ed25519 Signatures**: All property transactions are signed with Ed25519, and property manager actions carry cryptographic accountability that can be audited.
- **BNS Integration**: Properties can be registered with human-readable names (e.g., "123-main-st.property.basalt") for discoverability in the explorer and API.

## Token Standards Used

- **BST-3525 (Semi-Fungible Token)**: Primary standard. Each property is a slot; fractional ownership is represented by token value in basis points.
- **BST-VC (Verifiable Credentials)**: Used for KYC attestations, property appraisals, title verification, and property manager certification.
- **BST-20 (Fungible Token)**: Rental income can be distributed in native BST or a BST-20 stablecoin.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines credential schemas for "KYCVerified" (investor identity), "PropertyAppraisal" (valuation attestation), and "PropertyManager" (manager certification).
- **IssuerRegistry (0x...1007)**: Verifies that property issuers, appraisers, and KYC providers are registered credential authorities.
- **Escrow (0x...1003)**: Holds rental income pending distribution and property sale proceeds pending owner claims. Also used for lockup enforcement on initial purchases.
- **Governance (0x...1002)**: Protocol-level governance can update global parameters (e.g., minimum ownership threshold, maximum properties per contract). Property-level governance is implemented within this contract.
- **BNS (0x...1001)**: Human-readable property identifiers for explorer and API discoverability.
- **StakingPool (0x...1005)**: Property manager bonding -- managers stake BST as a performance bond that can be slashed for mismanagement.
- **BridgeETH (0x...1008)**: Cross-chain property investment: investors bridge ETH to BST for purchasing fractional property tokens.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Real Estate Fractionalization contract built on BST-3525.
/// Slot = property ID, Value = ownership basis points (out of 10,000).
/// Type ID: 0x0109
/// </summary>
[BasaltContract]
public partial class RealEstateFractionalization : BST3525Token
{
    // --- Property state ---
    private readonly StorageMap<string, string> _propertyManagers;      // slot -> manager address hex
    private readonly StorageMap<string, string> _propertyIssuers;       // slot -> issuer address hex
    private readonly StorageMap<string, string> _propertyStatus;        // slot -> "active"/"sale"/"sold"/"frozen"
    private readonly StorageMap<string, UInt256> _propertyValuation;    // slot -> latest appraised value
    private readonly StorageMap<string, UInt256> _issuedBps;            // slot -> total bps issued (max 10000)

    // --- Rental income state ---
    private readonly StorageMap<string, UInt256> _rentalPool;           // slot -> undistributed rental income
    private readonly StorageMap<string, UInt256> _claimedRental;        // slot:tokenId -> claimed amount
    private readonly StorageMap<string, UInt256> _totalDistributed;     // slot -> cumulative rental distributed
    private readonly StorageMap<string, UInt256> _expenses;             // slot -> total expenses deducted

    // --- Governance state ---
    private readonly StorageMap<string, ulong> _nextPropertyProposal;  // slot -> next proposal ID
    private readonly StorageMap<string, string> _proposalDescriptions;  // slot:propId -> description
    private readonly StorageMap<string, UInt256> _proposalVotesFor;     // slot:propId -> votes for (bps)
    private readonly StorageMap<string, UInt256> _proposalVotesAgainst; // slot:propId -> votes against
    private readonly StorageMap<string, ulong> _proposalEndBlocks;     // slot:propId -> voting end block
    private readonly StorageMap<string, string> _proposalStatus;       // slot:propId -> "active"/"passed"/"rejected"
    private readonly StorageMap<string, string> _proposalVoted;        // slot:propId:tokenId -> "1"

    // --- Compliance state ---
    private readonly StorageMap<string, string> _kycVerified;          // address hex -> "1"
    private readonly StorageMap<string, ulong> _lockupExpiry;          // tokenId -> block after which transfer is allowed

    // --- Configuration ---
    private readonly StorageValue<ulong> _lockupPeriodBlocks;
    private readonly StorageValue<ulong> _managerFeeBps;               // property manager fee in bps

    // --- System contract addresses ---
    private readonly byte[] _schemaRegistryAddress;
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _escrowAddress;

    public RealEstateFractionalization(ulong lockupPeriodBlocks = 43200, ulong managerFeeBps = 200)
        : base("Basalt Real Estate", "bREAL", 0)
    {
        _propertyManagers = new StorageMap<string, string>("re_mgr");
        _propertyIssuers = new StorageMap<string, string>("re_issuer");
        _propertyStatus = new StorageMap<string, string>("re_status");
        _propertyValuation = new StorageMap<string, UInt256>("re_val");
        _issuedBps = new StorageMap<string, UInt256>("re_bps");
        _rentalPool = new StorageMap<string, UInt256>("re_rent");
        _claimedRental = new StorageMap<string, UInt256>("re_claimed");
        _totalDistributed = new StorageMap<string, UInt256>("re_tdist");
        _expenses = new StorageMap<string, UInt256>("re_exp");
        _nextPropertyProposal = new StorageMap<string, ulong>("re_pnext");
        _proposalDescriptions = new StorageMap<string, string>("re_pdesc");
        _proposalVotesFor = new StorageMap<string, UInt256>("re_pfor");
        _proposalVotesAgainst = new StorageMap<string, UInt256>("re_pagn");
        _proposalEndBlocks = new StorageMap<string, ulong>("re_pend");
        _proposalStatus = new StorageMap<string, string>("re_pstat");
        _proposalVoted = new StorageMap<string, string>("re_pvoted");
        _kycVerified = new StorageMap<string, string>("re_kyc");
        _lockupExpiry = new StorageMap<string, ulong>("re_lockup");
        _lockupPeriodBlocks = new StorageValue<ulong>("re_lockperiod");
        _managerFeeBps = new StorageValue<ulong>("re_mgrfee");

        _lockupPeriodBlocks.Set(lockupPeriodBlocks);
        _managerFeeBps.Set(managerFeeBps);

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _escrowAddress = new byte[20];
        _escrowAddress[18] = 0x10;
        _escrowAddress[19] = 0x03;
    }

    // ================================================================
    // Property Registration
    // ================================================================

    /// <summary>
    /// Register a new property for fractionalization. Only registered issuers.
    /// Property ID (slot) is caller-defined. Metadata URI points to legal docs.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterProperty(
        ulong propertyId,
        byte[] propertyManager,
        UInt256 initialValuation,
        string metadataUri)
    {
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "RE: caller not a registered issuer");

        var slotKey = propertyId.ToString();
        Context.Require(
            string.IsNullOrEmpty(_propertyIssuers.Get(slotKey)),
            "RE: property already registered");

        _propertyIssuers.Set(slotKey, Convert.ToHexString(Context.Caller));
        _propertyManagers.Set(slotKey, Convert.ToHexString(propertyManager));
        _propertyStatus.Set(slotKey, "active");
        _propertyValuation.Set(slotKey, initialValuation);

        SetSlotUri(propertyId, metadataUri);

        Context.Emit(new PropertyRegisteredEvent
        {
            PropertyId = propertyId,
            Issuer = Context.Caller,
            Manager = propertyManager,
            Valuation = initialValuation,
        });
    }

    /// <summary>
    /// Mint fractional ownership tokens for a property.
    /// Value = ownership basis points (1-10000). Total issued cannot exceed 10000.
    /// Buyer must be KYC-verified.
    /// </summary>
    [BasaltEntrypoint]
    public ulong MintFraction(ulong propertyId, byte[] to, UInt256 ownershipBps, byte[] kycProof)
    {
        var slotKey = propertyId.ToString();
        Context.Require(_propertyStatus.Get(slotKey) == "active", "RE: property not active");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _propertyIssuers.Get(slotKey),
            "RE: only issuer can mint");

        // Verify KYC
        VerifyAndRecordKyc(to, kycProof);

        // Check total does not exceed 10000 bps
        var currentBps = _issuedBps.Get(slotKey);
        Context.Require(currentBps + ownershipBps <= new UInt256(10000), "RE: exceeds 100% ownership");
        _issuedBps.Set(slotKey, currentBps + ownershipBps);

        var tokenId = Mint(to, propertyId, ownershipBps);

        // Set lockup period
        _lockupExpiry.Set(tokenId.ToString(), Context.BlockHeight + _lockupPeriodBlocks.Get());

        Context.Emit(new FractionMintedEvent
        {
            TokenId = tokenId,
            PropertyId = propertyId,
            Owner = to,
            OwnershipBps = ownershipBps,
        });

        return tokenId;
    }

    // ================================================================
    // Secondary Market Transfers
    // ================================================================

    /// <summary>
    /// Transfer fractional ownership to another address. Both parties must be KYC-verified.
    /// Lockup period must have expired for the token.
    /// </summary>
    [BasaltEntrypoint]
    public ulong TransferFraction(ulong fromTokenId, byte[] to, UInt256 bpsAmount)
    {
        // Check lockup
        Context.Require(
            Context.BlockHeight >= _lockupExpiry.Get(fromTokenId.ToString()),
            "RE: token in lockup period");

        // Verify receiver KYC
        Context.Require(
            _kycVerified.Get(Convert.ToHexString(to)) == "1",
            "RE: receiver not KYC-verified");

        return TransferValueToAddress(fromTokenId, to, bpsAmount);
    }

    // ================================================================
    // Rental Income
    // ================================================================

    /// <summary>
    /// Property manager deposits rental income for distribution.
    /// Manager fee is deducted automatically.
    /// </summary>
    [BasaltEntrypoint]
    public void DepositRentalIncome(ulong propertyId)
    {
        var slotKey = propertyId.ToString();
        Context.Require(
            Convert.ToHexString(Context.Caller) == _propertyManagers.Get(slotKey),
            "RE: only property manager");
        Context.Require(!Context.TxValue.IsZero, "RE: must send value");

        var mgrFeeBps = _managerFeeBps.Get();
        var fee = Context.TxValue * new UInt256(mgrFeeBps) / new UInt256(10000);
        var distributable = Context.TxValue - fee;

        // Pay manager fee
        var managerAddr = Convert.FromHexString(_propertyManagers.Get(slotKey));
        Context.TransferNative(managerAddr, fee);

        // Add to rental pool
        _rentalPool.Set(slotKey, _rentalPool.Get(slotKey) + distributable);

        Context.Emit(new RentalIncomeDepositedEvent
        {
            PropertyId = propertyId,
            GrossAmount = Context.TxValue,
            ManagerFee = fee,
            NetAmount = distributable,
        });
    }

    /// <summary>
    /// Claim proportional rental income for a specific ownership token.
    /// Amount = (tokenValue / 10000) * rentalPool
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimRentalIncome(ulong tokenId)
    {
        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "RE: not token owner");

        var slot = SlotOf(tokenId);
        var slotKey = slot.ToString();
        var pool = _rentalPool.Get(slotKey);
        Context.Require(!pool.IsZero, "RE: no rental income to claim");

        var ownershipBps = BalanceOf(tokenId);
        var share = pool * ownershipBps / new UInt256(10000);
        Context.Require(!share.IsZero, "RE: share too small");

        var claimKey = slotKey + ":" + tokenId.ToString();
        _claimedRental.Set(claimKey, _claimedRental.Get(claimKey) + share);
        _rentalPool.Set(slotKey, pool - share);

        Context.TransferNative(Context.Caller, share);

        Context.Emit(new RentalClaimedEvent
        {
            TokenId = tokenId,
            PropertyId = slot,
            Owner = Context.Caller,
            Amount = share,
        });
    }

    /// <summary>
    /// Record an expense against a property. Only property manager.
    /// Deducts from rental pool.
    /// </summary>
    [BasaltEntrypoint]
    public void RecordExpense(ulong propertyId, UInt256 amount, string description)
    {
        var slotKey = propertyId.ToString();
        Context.Require(
            Convert.ToHexString(Context.Caller) == _propertyManagers.Get(slotKey),
            "RE: only property manager");

        var pool = _rentalPool.Get(slotKey);
        Context.Require(pool >= amount, "RE: insufficient rental pool");

        _rentalPool.Set(slotKey, pool - amount);
        _expenses.Set(slotKey, _expenses.Get(slotKey) + amount);

        // Transfer expense amount to manager for payment
        Context.TransferNative(Context.Caller, amount);

        Context.Emit(new ExpenseRecordedEvent
        {
            PropertyId = propertyId,
            Amount = amount,
            Description = description,
        });
    }

    // ================================================================
    // Property Governance
    // ================================================================

    /// <summary>
    /// Create a governance proposal for a property. Only token holders.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreatePropertyProposal(
        ulong propertyId,
        ulong tokenId,
        string description,
        ulong votingPeriodBlocks)
    {
        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "RE: not token owner");
        Context.Require(SlotOf(tokenId) == propertyId, "RE: token not in property");

        var slotKey = propertyId.ToString();
        var propId = _nextPropertyProposal.Get(slotKey);
        _nextPropertyProposal.Set(slotKey, propId + 1);

        var propKey = slotKey + ":" + propId.ToString();
        _proposalDescriptions.Set(propKey, description);
        _proposalEndBlocks.Set(propKey, Context.BlockHeight + votingPeriodBlocks);
        _proposalStatus.Set(propKey, "active");

        Context.Emit(new PropertyProposalCreatedEvent
        {
            PropertyId = propertyId,
            ProposalId = propId,
            Description = description,
        });

        return propId;
    }

    /// <summary>
    /// Vote on a property proposal. Weight = ownership basis points held.
    /// </summary>
    [BasaltEntrypoint]
    public void VoteOnPropertyProposal(
        ulong propertyId,
        ulong proposalId,
        ulong tokenId,
        bool support)
    {
        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "RE: not token owner");
        Context.Require(SlotOf(tokenId) == propertyId, "RE: token not in property");

        var slotKey = propertyId.ToString();
        var propKey = slotKey + ":" + proposalId.ToString();
        Context.Require(_proposalStatus.Get(propKey) == "active", "RE: proposal not active");
        Context.Require(Context.BlockHeight <= _proposalEndBlocks.Get(propKey), "RE: voting ended");

        var voteKey = propKey + ":" + tokenId.ToString();
        Context.Require(_proposalVoted.Get(voteKey) != "1", "RE: already voted with this token");
        _proposalVoted.Set(voteKey, "1");

        var weight = BalanceOf(tokenId);
        if (support)
            _proposalVotesFor.Set(propKey, _proposalVotesFor.Get(propKey) + weight);
        else
            _proposalVotesAgainst.Set(propKey, _proposalVotesAgainst.Get(propKey) + weight);
    }

    /// <summary>
    /// Finalize a property proposal after voting ends.
    /// Passes if votesFor > votesAgainst and quorum >= 5000 bps.
    /// </summary>
    [BasaltEntrypoint]
    public void FinalizePropertyProposal(ulong propertyId, ulong proposalId)
    {
        var slotKey = propertyId.ToString();
        var propKey = slotKey + ":" + proposalId.ToString();
        Context.Require(_proposalStatus.Get(propKey) == "active", "RE: not active");
        Context.Require(Context.BlockHeight > _proposalEndBlocks.Get(propKey), "RE: voting ongoing");

        var votesFor = _proposalVotesFor.Get(propKey);
        var votesAgainst = _proposalVotesAgainst.Get(propKey);
        var totalVoted = votesFor + votesAgainst;
        var quorumMet = totalVoted >= new UInt256(5000); // 50% of total bps

        if (quorumMet && votesFor > votesAgainst)
            _proposalStatus.Set(propKey, "passed");
        else
            _proposalStatus.Set(propKey, "rejected");
    }

    /// <summary>
    /// Replace property manager. Requires a passed governance proposal.
    /// </summary>
    [BasaltEntrypoint]
    public void ReplaceManager(ulong propertyId, byte[] newManager, ulong proposalId)
    {
        var slotKey = propertyId.ToString();
        var propKey = slotKey + ":" + proposalId.ToString();
        Context.Require(_proposalStatus.Get(propKey) == "passed", "RE: proposal not passed");

        _propertyManagers.Set(slotKey, Convert.ToHexString(newManager));

        Context.Emit(new ManagerReplacedEvent
        {
            PropertyId = propertyId,
            NewManager = newManager,
        });
    }

    /// <summary>
    /// Update property appraisal. Only registered appraisers via IssuerRegistry.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateValuation(ulong propertyId, UInt256 newValuation)
    {
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "RE: caller not a registered appraiser");

        _propertyValuation.Set(propertyId.ToString(), newValuation);

        Context.Emit(new ValuationUpdatedEvent
        {
            PropertyId = propertyId,
            NewValuation = newValuation,
            Appraiser = Context.Caller,
        });
    }

    // ================================================================
    // Views
    // ================================================================

    [BasaltView]
    public string GetPropertyStatus(ulong propertyId)
        => _propertyStatus.Get(propertyId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetPropertyValuation(ulong propertyId)
        => _propertyValuation.Get(propertyId.ToString());

    [BasaltView]
    public UInt256 GetRentalPoolBalance(ulong propertyId)
        => _rentalPool.Get(propertyId.ToString());

    [BasaltView]
    public UInt256 GetIssuedBps(ulong propertyId)
        => _issuedBps.Get(propertyId.ToString());

    [BasaltView]
    public bool IsKycVerified(byte[] holder)
        => _kycVerified.Get(Convert.ToHexString(holder)) == "1";

    [BasaltView]
    public string GetPropertyProposalStatus(ulong propertyId, ulong proposalId)
        => _proposalStatus.Get(propertyId.ToString() + ":" + proposalId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetTotalExpenses(ulong propertyId)
        => _expenses.Get(propertyId.ToString());

    // ================================================================
    // Internal helpers
    // ================================================================

    private void VerifyAndRecordKyc(byte[] subject, byte[] kycProof)
    {
        var subjectHex = Convert.ToHexString(subject);
        if (_kycVerified.Get(subjectHex) == "1") return; // already verified

        var proofValid = Context.CallContract<bool>(
            _schemaRegistryAddress, "VerifyProof", "KYCVerified", subject, kycProof);
        Context.Require(proofValid, "RE: invalid KYC proof");

        _kycVerified.Set(subjectHex, "1");
    }
}

// ================================================================
// Events
// ================================================================

[BasaltEvent]
public sealed class PropertyRegisteredEvent
{
    [Indexed] public ulong PropertyId { get; init; }
    [Indexed] public byte[] Issuer { get; init; } = [];
    public byte[] Manager { get; init; } = [];
    public UInt256 Valuation { get; init; }
}

[BasaltEvent]
public sealed class FractionMintedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public ulong PropertyId { get; init; }
    public byte[] Owner { get; init; } = [];
    public UInt256 OwnershipBps { get; init; }
}

[BasaltEvent]
public sealed class RentalIncomeDepositedEvent
{
    [Indexed] public ulong PropertyId { get; init; }
    public UInt256 GrossAmount { get; init; }
    public UInt256 ManagerFee { get; init; }
    public UInt256 NetAmount { get; init; }
}

[BasaltEvent]
public sealed class RentalClaimedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public ulong PropertyId { get; init; }
    public byte[] Owner { get; init; } = [];
    public UInt256 Amount { get; init; }
}

[BasaltEvent]
public sealed class ExpenseRecordedEvent
{
    [Indexed] public ulong PropertyId { get; init; }
    public UInt256 Amount { get; init; }
    public string Description { get; init; } = "";
}

[BasaltEvent]
public sealed class PropertyProposalCreatedEvent
{
    [Indexed] public ulong PropertyId { get; init; }
    [Indexed] public ulong ProposalId { get; init; }
    public string Description { get; init; } = "";
}

[BasaltEvent]
public sealed class ManagerReplacedEvent
{
    [Indexed] public ulong PropertyId { get; init; }
    public byte[] NewManager { get; init; } = [];
}

[BasaltEvent]
public sealed class ValuationUpdatedEvent
{
    [Indexed] public ulong PropertyId { get; init; }
    public UInt256 NewValuation { get; init; }
    public byte[] Appraiser { get; init; } = [];
}
```

## Complexity

**High** -- This contract combines BST-3525 inheritance, KYC-gated transfers, proportional income distribution, multi-level governance (property-level proposals with weighted voting), property manager role management, expense tracking, and appraisal updates. Cross-contract calls to SchemaRegistry, IssuerRegistry, and Escrow add integration complexity. The governance subsystem alone (proposals, voting, finalization, manager replacement) is a substantial state machine.

## Priority

**P1** -- Real estate fractionalization is a high-demand RWA use case that directly demonstrates BST-3525's value proposition for institutional tokenization. It complements the P0 Tokenized Bonds contract and together they form the core of Basalt's RWA narrative. Slightly lower priority than bonds because the bond market is more liquid and more immediately tokenizable, while real estate requires more off-chain legal infrastructure.
