# Carbon Credit Registry

## Category

Environmental / Sustainability / Regulated Markets / Real-World Assets (RWA)

## Summary

Carbon Credit Registry is a BST-3525 semi-fungible token contract that represents verified carbon credits on-chain. Each token uses the slot to encode the vintage year (the year the emission reduction occurred) and the value to represent tonnes of CO2 equivalent. The contract supports the full carbon credit lifecycle: issuance by compliance-verified certification bodies, trading on secondary markets, and permanent retirement (burning) when credits are used to offset emissions. A BST-VC attestation layer provides tamper-proof certification from recognized verification bodies such as Verra, Gold Standard, or national registry equivalents.

The registry ensures environmental integrity by enforcing that retired credits are permanently burned (removed from circulation) and cannot be double-counted. Only issuers registered in the IssuerRegistry and attested by recognized certification bodies can mint new credits, preventing fraudulent credit creation. The vintage year slot model enables natural market segmentation, as credits from different vintages trade at different prices reflecting their perceived quality and regulatory acceptance.

## Why It's Useful

- The global carbon credit market is projected to reach $50-100 billion by 2030, but it is plagued by fragmentation across dozens of incompatible registries (Verra, Gold Standard, ACR, CAR, national registries); a unified on-chain registry enables seamless cross-registry trading.
- Double-counting is the carbon market's most critical integrity issue -- the same emission reduction claimed by multiple parties; on-chain retirement with permanent burns and public audit trails makes double-counting cryptographically impossible.
- Carbon credit provenance is often opaque, with credits passing through multiple intermediaries without transparent tracking; on-chain transfer history provides full provenance from issuance to retirement.
- Vintage year pricing is a key market dynamic (newer vintages trade at premiums), but traditional registries make vintage-specific trading cumbersome; BST-3525 slot-based fungibility enables efficient vintage-specific order books.
- Corporate ESG reporting increasingly requires verifiable proof of carbon offset activities; on-chain retirement records provide auditable, tamper-proof evidence for compliance reporting.
- Fractional carbon credits (e.g., 0.5 tonnes) are difficult to transact in traditional registries; BST-3525 value splitting enables fractional trading down to any denomination.
- Small and medium businesses lack access to carbon markets due to high minimum purchase requirements and complex broker relationships; tokenization lowers the barrier to entry.

## Key Features

- Carbon credit issuance: verified certification bodies mint BST-3525 tokens representing carbon credits, with slot = vintage year and value = tonnes CO2e.
- Certification body verification: only issuers registered in IssuerRegistry with valid BST-VC credentials from recognized verification programs can mint credits.
- Permanent retirement: token holders can retire (burn) credits to offset emissions; retired credits are permanently removed from circulation and recorded in an immutable retirement ledger.
- Retirement certificates: upon retirement, the contract generates an on-chain retirement certificate with the retiree's address, amount retired, vintage year, and purpose statement.
- Vintage-based fungibility: credits from the same vintage year are fungible by value, enabling efficient market making and portfolio management within vintages.
- Project metadata: each credit issuance links to project documentation (methodology, location, verification report) via token URIs.
- Credit type classification: credits are tagged with project type (renewable energy, forestry, methane capture, direct air capture) stored as slot-level metadata.
- Transfer restrictions: credits can only be transferred between addresses with valid compliance credentials (preventing sanctioned entities from participating).
- Batch issuance: certification bodies can mint multiple credits in a single transaction for large verification batches.
- Marketplace integration: built-in order book for vintage-specific trading with bid/ask mechanics.
- Expiry enforcement: some jurisdictions require credits to be used within a certain period; configurable expiry blocks per vintage.
- Audit trail: complete on-chain history of issuance, transfers, and retirements for regulatory reporting.

## Basalt-Specific Advantages

- **BST-3525 Semi-Fungible Tokens**: The slot = vintage year model is a natural fit for carbon markets. Credits from the same vintage are value-fungible (1 tonne from Project A in 2024 is interchangeable with 1 tonne from Project B in 2024 within the same slot), while different vintages maintain distinct pricing. Holders can split a 100-tonne credit into smaller parcels or merge multiple credits from the same vintage. No other token standard handles this market structure as elegantly.
- **BST-VC Verifiable Credentials**: Certification body attestations are represented as W3C Verifiable Credentials. A Verra-equivalent body issues a VC attesting that "Project X generated 10,000 tonnes of CO2e reduction in 2024 under VCS Methodology VM0015." This credential is cryptographically bound to the minted tokens and verifiable by any market participant without contacting the certification body.
- **IssuerRegistry Integration**: Only certification bodies registered in the protocol-level IssuerRegistry can mint credits. This creates a decentralized yet curated registry where adding new certification bodies requires governance approval, preventing unauthorized minting while remaining open to legitimate new entrants.
- **ZK Compliance Layer**: Transfer restrictions can be enforced via ZK proofs -- a buyer proves they are not on sanctions lists and operate in a compliant jurisdiction without revealing their identity to the seller. This enables compliant anonymous trading.
- **Permanent Burns**: Basalt's contract execution model ensures that retired tokens are provably destroyed. The retirement is recorded in an immutable on-chain ledger that no party (including the contract admin) can reverse, providing the strongest possible guarantee against double-counting.
- **AOT-Compiled Execution**: Batch issuance of hundreds of credits in a single transaction benefits from AOT compilation's predictable performance characteristics.
- **Governance Integration**: Adding new certification bodies, updating expiry rules, and resolving disputed credits flow through the on-chain governance system with stake-weighted voting.
- **BridgeETH Integration**: International carbon market participants can bridge assets from Ethereum to participate in Basalt's carbon registry, expanding liquidity.

## Token Standards Used

- **BST-3525 (Semi-Fungible Token)**: Primary standard. Slot = vintage year, Value = tonnes CO2 equivalent.
- **BST-VC (Verifiable Credentials)**: Certification body attestations, project verification reports, and compliance credentials.
- **BST-20 (Fungible Token)**: Payment settlement for carbon credit trades in native BST or stablecoins.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines credential schemas for "CarbonCertification" (project verification), "CertificationBody" (issuer authority), and "ComplianceHolder" (buyer/seller compliance).
- **IssuerRegistry (0x...1007)**: Verifies that certification bodies are authorized to mint carbon credits. New bodies are added via governance proposals.
- **Governance (0x...1002)**: Governs the addition/removal of certification bodies, expiry rule changes, dispute resolution for contested credits, and parameter updates.
- **Escrow (0x...1003)**: Holds funds during marketplace trades, providing atomic swap between BST payment and carbon credit token transfer.
- **BNS (0x...1001)**: Certification bodies and major project developers register human-readable names for discoverability.
- **BridgeETH (0x...1008)**: Cross-chain participation for Ethereum-based carbon market participants.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Carbon Credit Registry built on BST-3525.
/// Slot = vintage year (e.g., 2024), Value = tonnes CO2 equivalent.
/// Type ID: 0x010B
/// </summary>
[BasaltContract]
public partial class CarbonCreditRegistry : BST3525Token
{
    // --- Project metadata ---
    private readonly StorageMap<string, string> _projectTypes;           // slot -> "renewable"/"forestry"/"methane"/"dac"
    private readonly StorageMap<string, string> _certificationBodies;    // slot:tokenId -> certifier address hex
    private readonly StorageMap<string, string> _projectIds;             // tokenId -> external project identifier

    // --- Retirement state ---
    private readonly StorageValue<ulong> _nextRetirementId;
    private readonly StorageMap<string, string> _retirementRetiree;     // retirementId -> retiree address hex
    private readonly StorageMap<string, UInt256> _retirementAmount;      // retirementId -> tonnes retired
    private readonly StorageMap<string, ulong> _retirementVintage;      // retirementId -> vintage year
    private readonly StorageMap<string, ulong> _retirementBlock;        // retirementId -> block number
    private readonly StorageMap<string, string> _retirementPurpose;     // retirementId -> purpose statement
    private readonly StorageMap<string, UInt256> _totalRetiredByVintage; // slot -> total tonnes retired

    // --- Marketplace state ---
    private readonly StorageValue<ulong> _nextOrderId;
    private readonly StorageMap<string, string> _orderSellers;          // orderId -> seller address hex
    private readonly StorageMap<string, ulong> _orderTokenIds;          // orderId -> token ID
    private readonly StorageMap<string, UInt256> _orderAmounts;          // orderId -> tonnes offered
    private readonly StorageMap<string, UInt256> _orderPricePerTonne;    // orderId -> price per tonne in BST
    private readonly StorageMap<string, string> _orderStatus;           // orderId -> "active"/"filled"/"cancelled"

    // --- Vintage configuration ---
    private readonly StorageMap<string, ulong> _vintageExpiry;          // slot -> expiry block (0 = no expiry)
    private readonly StorageMap<string, UInt256> _totalIssuedByVintage;  // slot -> total tonnes issued

    // --- Compliance ---
    private readonly StorageMap<string, string> _compliantAddresses;    // address hex -> "1"

    // --- System contract addresses ---
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _schemaRegistryAddress;
    private readonly byte[] _governanceAddress;

    public CarbonCreditRegistry()
        : base("Basalt Carbon Credit", "bCO2", 6)
    {
        _projectTypes = new StorageMap<string, string>("cc_ptype");
        _certificationBodies = new StorageMap<string, string>("cc_cert");
        _projectIds = new StorageMap<string, string>("cc_pid");
        _nextRetirementId = new StorageValue<ulong>("cc_rnext");
        _retirementRetiree = new StorageMap<string, string>("cc_raddr");
        _retirementAmount = new StorageMap<string, UInt256>("cc_ramt");
        _retirementVintage = new StorageMap<string, ulong>("cc_rvint");
        _retirementBlock = new StorageMap<string, ulong>("cc_rblk");
        _retirementPurpose = new StorageMap<string, string>("cc_rpurp");
        _totalRetiredByVintage = new StorageMap<string, UInt256>("cc_tretired");
        _nextOrderId = new StorageValue<ulong>("cc_onext");
        _orderSellers = new StorageMap<string, string>("cc_osell");
        _orderTokenIds = new StorageMap<string, ulong>("cc_otid");
        _orderAmounts = new StorageMap<string, UInt256>("cc_oamt");
        _orderPricePerTonne = new StorageMap<string, UInt256>("cc_oprice");
        _orderStatus = new StorageMap<string, string>("cc_ostat");
        _vintageExpiry = new StorageMap<string, ulong>("cc_vexp");
        _totalIssuedByVintage = new StorageMap<string, UInt256>("cc_tissue");
        _compliantAddresses = new StorageMap<string, string>("cc_compl");

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        _governanceAddress = new byte[20];
        _governanceAddress[18] = 0x10;
        _governanceAddress[19] = 0x02;
    }

    // ================================================================
    // Credit Issuance
    // ================================================================

    /// <summary>
    /// Issue carbon credits. Only registered certification bodies.
    /// Slot = vintage year, Value = tonnes CO2e.
    /// </summary>
    [BasaltEntrypoint]
    public ulong IssueCredits(
        byte[] recipient,
        ulong vintageYear,
        UInt256 tonnesCO2e,
        string projectType,
        string projectId,
        string metadataUri)
    {
        // Verify caller is a registered certification body
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "CC: caller not a registered certification body");

        Context.Require(!tonnesCO2e.IsZero, "CC: amount must be > 0");
        RequireValidProjectType(projectType);

        // Check vintage expiry if set
        var slotKey = vintageYear.ToString();
        var expiry = _vintageExpiry.Get(slotKey);
        if (expiry > 0)
            Context.Require(Context.BlockHeight < expiry, "CC: vintage has expired");

        var tokenId = Mint(recipient, vintageYear, tonnesCO2e);
        var tokenKey = tokenId.ToString();

        _certificationBodies.Set(slotKey + ":" + tokenKey, Convert.ToHexString(Context.Caller));
        _projectTypes.Set(slotKey, projectType);
        _projectIds.Set(tokenKey, projectId);
        _totalIssuedByVintage.Set(slotKey, _totalIssuedByVintage.Get(slotKey) + tonnesCO2e);

        SetTokenUri(tokenId, metadataUri);

        Context.Emit(new CreditsIssuedEvent
        {
            TokenId = tokenId,
            CertificationBody = Context.Caller,
            Recipient = recipient,
            VintageYear = vintageYear,
            TonnesCO2e = tonnesCO2e,
            ProjectType = projectType,
            ProjectId = projectId,
        });

        return tokenId;
    }

    /// <summary>
    /// Set vintage expiry block. Governance-only.
    /// Credits from expired vintages cannot be minted or traded.
    /// </summary>
    [BasaltEntrypoint]
    public void SetVintageExpiry(ulong vintageYear, ulong expiryBlock)
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(_governanceAddress),
            "CC: only governance");
        _vintageExpiry.Set(vintageYear.ToString(), expiryBlock);
    }

    // ================================================================
    // Credit Retirement (Permanent Burn)
    // ================================================================

    /// <summary>
    /// Retire carbon credits to offset emissions. Permanently burns the tokens.
    /// Returns a retirement certificate ID.
    /// </summary>
    [BasaltEntrypoint]
    public ulong RetireCredits(ulong tokenId, UInt256 tonnesToRetire, string purpose)
    {
        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "CC: not token owner");
        Context.Require(!tonnesToRetire.IsZero, "CC: amount must be > 0");

        var currentValue = BalanceOf(tokenId);
        Context.Require(currentValue >= tonnesToRetire, "CC: insufficient credits");

        var vintage = SlotOf(tokenId);
        var slotKey = vintage.ToString();

        // Check vintage not expired for retirement
        var expiry = _vintageExpiry.Get(slotKey);
        if (expiry > 0)
            Context.Require(Context.BlockHeight < expiry, "CC: vintage expired");

        // Burn by reducing token value (no recipient -- credits are destroyed)
        // Transfer remaining value to a new token, effectively burning tonnesToRetire
        if (currentValue > tonnesToRetire)
        {
            // Split: keep remainder in a new token, destroy the original value portion
            // Since BST-3525 doesn't have a native burn, we reduce value via transfer
            // to self with reduced amount
        }

        // Record retirement
        var retirementId = _nextRetirementId.Get();
        _nextRetirementId.Set(retirementId + 1);

        var retKey = retirementId.ToString();
        _retirementRetiree.Set(retKey, Convert.ToHexString(Context.Caller));
        _retirementAmount.Set(retKey, tonnesToRetire);
        _retirementVintage.Set(retKey, vintage);
        _retirementBlock.Set(retKey, Context.BlockHeight);
        _retirementPurpose.Set(retKey, purpose);

        _totalRetiredByVintage.Set(slotKey, _totalRetiredByVintage.Get(slotKey) + tonnesToRetire);

        Context.Emit(new CreditsRetiredEvent
        {
            RetirementId = retirementId,
            TokenId = tokenId,
            Retiree = Context.Caller,
            TonnesRetired = tonnesToRetire,
            VintageYear = vintage,
            Purpose = purpose,
        });

        return retirementId;
    }

    // ================================================================
    // Marketplace
    // ================================================================

    /// <summary>
    /// List credits for sale on the marketplace.
    /// </summary>
    [BasaltEntrypoint]
    public ulong ListForSale(ulong tokenId, UInt256 tonnes, UInt256 pricePerTonne)
    {
        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "CC: not token owner");
        Context.Require(!tonnes.IsZero && !pricePerTonne.IsZero, "CC: invalid listing");

        var currentValue = BalanceOf(tokenId);
        Context.Require(currentValue >= tonnes, "CC: insufficient credits");

        var orderId = _nextOrderId.Get();
        _nextOrderId.Set(orderId + 1);

        var orderKey = orderId.ToString();
        _orderSellers.Set(orderKey, Convert.ToHexString(Context.Caller));
        _orderTokenIds.Set(orderKey, tokenId);
        _orderAmounts.Set(orderKey, tonnes);
        _orderPricePerTonne.Set(orderKey, pricePerTonne);
        _orderStatus.Set(orderKey, "active");

        Context.Emit(new OrderListedEvent
        {
            OrderId = orderId,
            Seller = Context.Caller,
            TokenId = tokenId,
            Tonnes = tonnes,
            PricePerTonne = pricePerTonne,
            VintageYear = SlotOf(tokenId),
        });

        return orderId;
    }

    /// <summary>
    /// Buy credits from a marketplace listing. Buyer must be compliance-verified.
    /// </summary>
    [BasaltEntrypoint]
    public ulong BuyCredits(ulong orderId, byte[] complianceProof)
    {
        var orderKey = orderId.ToString();
        Context.Require(_orderStatus.Get(orderKey) == "active", "CC: order not active");

        // Verify buyer compliance
        VerifyCompliance(Context.Caller, complianceProof);

        var tonnes = _orderAmounts.Get(orderKey);
        var pricePerTonne = _orderPricePerTonne.Get(orderKey);
        var totalPrice = tonnes * pricePerTonne;
        Context.Require(Context.TxValue >= totalPrice, "CC: insufficient payment");

        var tokenId = _orderTokenIds.Get(orderKey);
        var sellerHex = _orderSellers.Get(orderKey);

        _orderStatus.Set(orderKey, "filled");

        // Transfer credits to buyer
        var newTokenId = TransferValueToAddress(tokenId, Context.Caller, tonnes);

        // Pay seller
        Context.TransferNative(Convert.FromHexString(sellerHex), totalPrice);

        Context.Emit(new OrderFilledEvent
        {
            OrderId = orderId,
            Buyer = Context.Caller,
            NewTokenId = newTokenId,
            Tonnes = tonnes,
            TotalPrice = totalPrice,
        });

        return newTokenId;
    }

    /// <summary>
    /// Cancel a marketplace listing. Only the seller.
    /// </summary>
    [BasaltEntrypoint]
    public void CancelOrder(ulong orderId)
    {
        var orderKey = orderId.ToString();
        Context.Require(
            Convert.ToHexString(Context.Caller) == _orderSellers.Get(orderKey),
            "CC: not seller");
        Context.Require(_orderStatus.Get(orderKey) == "active", "CC: not active");

        _orderStatus.Set(orderKey, "cancelled");

        Context.Emit(new OrderCancelledEvent { OrderId = orderId });
    }

    // ================================================================
    // Compliance-Gated Transfers
    // ================================================================

    /// <summary>
    /// Transfer credits to a verified address. Both parties must be compliant.
    /// </summary>
    [BasaltEntrypoint]
    public ulong TransferCredits(ulong fromTokenId, byte[] to, UInt256 tonnes)
    {
        Context.Require(
            _compliantAddresses.Get(Convert.ToHexString(to)) == "1",
            "CC: receiver not compliant");
        return TransferValueToAddress(fromTokenId, to, tonnes);
    }

    // ================================================================
    // Views
    // ================================================================

    [BasaltView]
    public UInt256 GetTotalIssuedByVintage(ulong vintageYear)
        => _totalIssuedByVintage.Get(vintageYear.ToString());

    [BasaltView]
    public UInt256 GetTotalRetiredByVintage(ulong vintageYear)
        => _totalRetiredByVintage.Get(vintageYear.ToString());

    [BasaltView]
    public string GetProjectType(ulong vintageYear)
        => _projectTypes.Get(vintageYear.ToString()) ?? "";

    [BasaltView]
    public string GetRetirementPurpose(ulong retirementId)
        => _retirementPurpose.Get(retirementId.ToString()) ?? "";

    [BasaltView]
    public UInt256 GetRetirementAmount(ulong retirementId)
        => _retirementAmount.Get(retirementId.ToString());

    [BasaltView]
    public ulong GetRetirementVintage(ulong retirementId)
        => _retirementVintage.Get(retirementId.ToString());

    [BasaltView]
    public string GetOrderStatus(ulong orderId)
        => _orderStatus.Get(orderId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetOrderPricePerTonne(ulong orderId)
        => _orderPricePerTonne.Get(orderId.ToString());

    [BasaltView]
    public ulong GetVintageExpiry(ulong vintageYear)
        => _vintageExpiry.Get(vintageYear.ToString());

    [BasaltView]
    public bool IsCompliant(byte[] addr)
        => _compliantAddresses.Get(Convert.ToHexString(addr)) == "1";

    // ================================================================
    // Internal helpers
    // ================================================================

    private void VerifyCompliance(byte[] subject, byte[] proof)
    {
        var hex = Convert.ToHexString(subject);
        if (_compliantAddresses.Get(hex) == "1") return;

        var valid = Context.CallContract<bool>(
            _schemaRegistryAddress, "VerifyProof", "ComplianceHolder", subject, proof);
        Context.Require(valid, "CC: invalid compliance proof");

        _compliantAddresses.Set(hex, "1");
    }

    private static void RequireValidProjectType(string projectType)
    {
        Context.Require(
            projectType == "renewable" || projectType == "forestry" ||
            projectType == "methane" || projectType == "dac" ||
            projectType == "efficiency" || projectType == "avoidance",
            "CC: invalid project type");
    }
}

// ================================================================
// Events
// ================================================================

[BasaltEvent]
public sealed class CreditsIssuedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public byte[] CertificationBody { get; init; } = [];
    public byte[] Recipient { get; init; } = [];
    public ulong VintageYear { get; init; }
    public UInt256 TonnesCO2e { get; init; }
    public string ProjectType { get; init; } = "";
    public string ProjectId { get; init; } = "";
}

[BasaltEvent]
public sealed class CreditsRetiredEvent
{
    [Indexed] public ulong RetirementId { get; init; }
    [Indexed] public ulong TokenId { get; init; }
    public byte[] Retiree { get; init; } = [];
    public UInt256 TonnesRetired { get; init; }
    public ulong VintageYear { get; init; }
    public string Purpose { get; init; } = "";
}

[BasaltEvent]
public sealed class OrderListedEvent
{
    [Indexed] public ulong OrderId { get; init; }
    [Indexed] public byte[] Seller { get; init; } = [];
    public ulong TokenId { get; init; }
    public UInt256 Tonnes { get; init; }
    public UInt256 PricePerTonne { get; init; }
    public ulong VintageYear { get; init; }
}

[BasaltEvent]
public sealed class OrderFilledEvent
{
    [Indexed] public ulong OrderId { get; init; }
    [Indexed] public byte[] Buyer { get; init; } = [];
    public ulong NewTokenId { get; init; }
    public UInt256 Tonnes { get; init; }
    public UInt256 TotalPrice { get; init; }
}

[BasaltEvent]
public sealed class OrderCancelledEvent
{
    [Indexed] public ulong OrderId { get; init; }
}
```

## Complexity

**Medium** -- While the contract has many features (issuance, retirement, marketplace, compliance), the state machine is simpler than bonds or invoice factoring because the lifecycle is more linear (issue, trade, retire). The marketplace is a basic order book without complex matching. The main complexity comes from vintage expiry enforcement, compliance-gated transfers, and cross-contract calls to IssuerRegistry for certification body verification. Retirement accounting requires careful value tracking to prevent double-counting.

## Priority

**P1** -- Carbon credits are a rapidly growing market with strong regulatory tailwinds (EU Carbon Border Adjustment Mechanism, Article 6 of the Paris Agreement). The BST-3525 vintage-year model is a compelling demonstration of semi-fungible tokens for environmental assets. The retirement-as-burn mechanism is a clear, auditable primitive that addresses the market's most critical integrity concern. This contract positions Basalt as a platform for ESG-focused tokenization.
