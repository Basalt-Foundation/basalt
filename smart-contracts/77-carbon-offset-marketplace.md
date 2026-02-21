# Carbon Offset Marketplace

## Category

Sustainability and ESG -- Environmental Asset Management

## Summary

A marketplace for buying, selling, and retiring carbon credits on Basalt, integrated with corporate compliance reporting. Companies and individuals can purchase tokenized carbon credits, retire them against their emissions, and receive BST-VC sustainability certificates as proof. The contract supports automated carbon offset on every transaction (green fee), corporate carbon accounting dashboards, and integration with verified carbon credit registries through bridge attestations.

## Why It's Useful

- **Corporate ESG Compliance**: Companies face increasing regulatory pressure (EU CSRD, SEC climate disclosure, CBAM) to report and offset their carbon footprint. An on-chain marketplace provides transparent, auditable offset mechanisms.
- **Voluntary Carbon Markets**: Individuals and organizations seeking to voluntarily offset their emissions can purchase and retire credits with full provenance tracking on-chain.
- **Carbon Credit Integrity**: On-chain tracking of credit lifecycle (issuance, transfer, retirement) prevents the double-counting problem that plagues traditional carbon markets.
- **Automated Green Fees**: Novel mechanism where a small fee on every Basalt transaction is automatically used to purchase and retire carbon credits, making the entire network carbon-negative.
- **Transparent Pricing**: On-chain price discovery for carbon credits, creating efficient markets that reflect true environmental value.
- **Compliance Certificates**: BST-VC certificates for carbon offset provide machine-verifiable proof of environmental compliance usable in regulatory filings.
- **DeFi Composability**: Carbon credits as tokens can be used in DeFi (lending collateral, yield farming, index funds), attracting capital to environmental markets.

## Key Features

- **Credit Listing**: Verified carbon credit issuers list tokenized credits with metadata (project type, vintage year, registry, methodology, location, additional certifications).
- **Credit Retirement**: Buyers permanently retire credits by burning them. Retirement generates an immutable on-chain certificate.
- **BST-VC Sustainability Certificates**: Upon retirement, the buyer receives a BST-VC verifiable credential attesting to the offset amount, project, and date. Machine-verifiable by regulators and auditors.
- **Corporate Programs**: Companies register corporate accounts with annual offset targets. Dashboard tracks cumulative offsets against targets with periodic reporting.
- **Automated Green Fee**: Configurable per-transaction fee (basis points) automatically converted to carbon credit purchases and retirements. Governance-controlled rate.
- **Credit Quality Tiers**: Credits classified by quality (Gold Standard, Verra VCS, CDM, voluntary) with different pricing tiers.
- **Vintage Tracking**: Credits tagged by vintage year. Older vintages may be discounted; newest vintages command premiums.
- **Project Registry**: On-chain registry of verified carbon offset projects with metadata, verification status, and credit issuance history.
- **Batch Operations**: Retire multiple credits of different types in a single transaction for corporate bulk offsets.
- **Price Oracle**: On-chain price feed for carbon credits by quality tier and vintage, enabling automated purchasing at market rates.
- **Retirement Dashboard**: On-chain analytics for total retired credits, credits by project type, corporate offset leaderboard, and network-level environmental impact.
- **Marketplace Orders**: Limit orders and market orders for carbon credit trading. Order book or AMM-based price discovery.
- **Credit Verification**: Bridge attestations from real-world carbon registries (Verra, Gold Standard, ACR) verify credit authenticity.
- **Surplus Trading**: Companies that exceed their offset targets can sell surplus offset certificates to others.

## Basalt-Specific Advantages

- **BST-VC Compliance Certificates (Native)**: Basalt's W3C-compatible BST-VC standard provides the exact format needed for sustainability certificates. These credentials are machine-verifiable, interoperable with regulatory systems, and natively understood by all Basalt protocols. No other chain has built-in verifiable credential infrastructure for environmental compliance.
- **ZK Compliance for Corporate Reporting**: Companies can prove they meet offset targets via ZK proofs without revealing their exact emissions data, purchase history, or offset amounts. This protects commercially sensitive environmental data while satisfying regulatory requirements.
- **BST-3525 SFT Carbon Credits**: Individual carbon credits represented as BST-3525 semi-fungible tokens with rich slot metadata (project name, vintage year, methodology, registry, issuance date, quality tier, geographic origin). Enables granular trading, filtering, and retirement with full provenance.
- **BST-4626 Vault for Carbon Pools**: Pooled carbon credits of the same quality tier can be held in BST-4626 vaults, providing fungible shares for easier trading while maintaining per-credit metadata. Yield from credit price appreciation accrues to vault holders.
- **Confidential Purchases via Pedersen Commitments**: Corporate carbon credit purchases can be made confidentially using Pedersen commitments, preventing competitors from monitoring environmental spending and strategy through on-chain analysis.
- **AOT-Compiled Fee Automation**: The automated green fee mechanism (calculating, purchasing, and retiring credits on every transaction) executes in AOT-compiled native code, ensuring negligible overhead on transaction processing.
- **Ed25519 Registry Attestations**: Carbon registry attestations signed with Ed25519 benefit from fast verification, enabling efficient on-chain verification of credit authenticity.
- **BLS Aggregate Verification**: Batch credit verifications from multiple registry sources can be aggregated into a single BLS signature, reducing costs for large-scale credit imports.

## Token Standards Used

- **BST-3525 (SFT)**: Carbon credits as semi-fungible tokens with project metadata (vintage, methodology, registry, quality tier, location, additional certifications) in slot fields.
- **BST-4626 (Vault)**: Pooled carbon credit vaults for fungible trading of grouped credits by quality tier.
- **BST-VC (Verifiable Credentials)**: Sustainability certificates issued upon credit retirement. Corporate compliance attestations. Registry verification credentials.
- **BST-20**: Payment token for credit purchases. Green fee denomination. Trading pair token.
- **BST-721**: Non-transferable retirement receipts with unique project and offset details.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines credential schemas for sustainability certificates ("CarbonOffsetCertificateV1", "CorporateComplianceReportV1", "CreditVerificationV1").
- **IssuerRegistry (0x...1007)**: Registers verified carbon credit issuers and registry attestation providers. Only credits from registered issuers are accepted.
- **Governance (0x0102)**: Governs green fee rate, approved credit quality tiers, issuer registration, corporate program parameters, and marketplace rules.
- **BridgeETH (0x...1008)**: Cross-chain credit imports from Ethereum-based carbon credit protocols (Toucan, KlimaDAO) via bridge attestations.
- **BNS (0x0101)**: Marketplace and corporate programs registered under BNS names (e.g., `carbon.basalt`, `company.carbon.basalt`).
- **Escrow (0x0103)**: Large corporate offset purchases held in escrow until credit verification is complete.
- **StakingPool (0x0105)**: Green fee revenue can partially fund staking rewards, incentivizing network participation alongside environmental impact.
- **WBSLT (0x0100)**: Default payment currency for carbon credit purchases.

## Technical Sketch

```csharp
// ============================================================
// CarbonMarketplace -- Carbon offset trading and retirement
// ============================================================

[BasaltContract(TypeId = 0x030D)]
public partial class CarbonMarketplace : SdkContract
{
    // --- Storage ---

    // Carbon credits
    private StorageValue<ulong> _nextCreditId;
    private StorageMap<ulong, CarbonCredit> _credits;
    private StorageMap<ulong, Address> _creditOwners;
    private StorageMap<ulong, bool> _retired;

    // Listings
    private StorageValue<ulong> _nextListingId;
    private StorageMap<ulong, Listing> _listings;
    private StorageMap<ulong, bool> _listingActive;

    // Project registry
    private StorageValue<ulong> _nextProjectId;
    private StorageMap<ulong, CarbonProject> _projects;
    private StorageMap<ulong, bool> _verifiedProjects;

    // Corporate programs
    private StorageMap<Address, CorporateProgram> _corporatePrograms;
    private StorageMap<Address, bool> _hasProgram;

    // Retirement records
    private StorageValue<ulong> _nextRetirementId;
    private StorageMap<ulong, RetirementRecord> _retirements;
    private StorageMap<Address, ulong> _retirementCount;
    private StorageMap<Address, StorageMap<ulong, ulong>> _userRetirements;

    // Green fee parameters
    private StorageValue<uint> _greenFeeBps;
    private StorageValue<UInt256> _greenFeePool;
    private StorageValue<UInt256> _totalGreenFeeCollected;

    // Price oracle per quality tier
    private StorageMap<byte, UInt256> _tierPrices;

    // Global statistics
    private StorageValue<UInt256> _totalCreditsRetired;
    private StorageValue<UInt256> _totalCO2Offset;      // In tonnes (scaled by 1e18)
    private StorageValue<UInt256> _totalMarketVolume;

    // Approved credit issuers
    private StorageMap<Address, bool> _approvedIssuers;

    // Admin
    private StorageValue<Address> _admin;

    // --- Data Structures ---

    public struct CarbonCredit
    {
        public ulong CreditId;
        public ulong ProjectId;
        public Address Issuer;
        public ushort VintageYear;
        public byte QualityTier;         // 0=voluntary, 1=CDM, 2=VCS, 3=GoldStandard
        public UInt256 TonnesCO2;        // CO2 equivalent in tonnes (scaled 1e18)
        public string Registry;          // "Verra", "GoldStandard", "ACR"
        public string Methodology;
        public string Location;          // Country/region
        public byte[] RegistrySerialNumber; // External registry reference
        public ulong IssuedAtBlock;
    }

    public struct Listing
    {
        public ulong ListingId;
        public ulong CreditId;
        public Address Seller;
        public UInt256 PricePerTonne;    // In BST per tonne CO2
        public UInt256 Quantity;         // Tonnes available
        public ulong ListedAtBlock;
        public ulong ExpiryBlock;
    }

    public struct CarbonProject
    {
        public ulong ProjectId;
        public string Name;
        public string ProjectType;      // "reforestation", "renewable_energy", "methane_capture"
        public string Country;
        public string Methodology;
        public Address Developer;
        public UInt256 TotalCreditsIssued;
        public UInt256 TotalCreditsRetired;
        public byte VerificationStatus; // 0=pending, 1=verified, 2=suspended
    }

    public struct CorporateProgram
    {
        public Address Company;
        public string CompanyName;
        public UInt256 AnnualTargetTonnes;
        public UInt256 CurrentYearOffset;
        public ulong ProgramStartBlock;
        public ulong CurrentPeriodStart;
        public ulong CurrentPeriodEnd;
        public byte ComplianceStatus;   // 0=on_track, 1=behind, 2=compliant, 3=exceeded
    }

    public struct RetirementRecord
    {
        public ulong RetirementId;
        public ulong CreditId;
        public Address Retiree;
        public UInt256 TonnesCO2;
        public ulong RetiredAtBlock;
        public string Purpose;          // "corporate_offset", "voluntary", "green_fee"
        public Hash256 CertificateHash; // BST-VC credential hash
    }

    // --- Credit Issuance ---

    /// <summary>
    /// Issue new carbon credits. Approved issuer only.
    /// </summary>
    public ulong IssueCredits(
        ulong projectId,
        ushort vintageYear,
        byte qualityTier,
        UInt256 tonnesCO2,
        string registry,
        string methodology,
        string location,
        byte[] registrySerialNumber)
    {
        Require(_approvedIssuers.Get(Context.Sender), "NOT_APPROVED_ISSUER");
        Require(_verifiedProjects.Get(projectId), "PROJECT_NOT_VERIFIED");
        Require(qualityTier <= 3, "INVALID_TIER");
        Require(tonnesCO2 > UInt256.Zero, "ZERO_CREDITS");

        ulong creditId = _nextCreditId.Get();
        _nextCreditId.Set(creditId + 1);

        _credits.Set(creditId, new CarbonCredit
        {
            CreditId = creditId,
            ProjectId = projectId,
            Issuer = Context.Sender,
            VintageYear = vintageYear,
            QualityTier = qualityTier,
            TonnesCO2 = tonnesCO2,
            Registry = registry,
            Methodology = methodology,
            Location = location,
            RegistrySerialNumber = registrySerialNumber,
            IssuedAtBlock = Context.BlockNumber
        });

        _creditOwners.Set(creditId, Context.Sender);

        // Update project stats
        var project = _projects.Get(projectId);
        project.TotalCreditsIssued = project.TotalCreditsIssued + tonnesCO2;
        _projects.Set(projectId, project);

        EmitEvent("CreditsIssued", creditId, projectId, tonnesCO2, qualityTier);
        return creditId;
    }

    // --- Marketplace ---

    /// <summary>
    /// List carbon credits for sale.
    /// </summary>
    public ulong ListCredits(ulong creditId, UInt256 pricePerTonne, ulong durationBlocks)
    {
        Require(_creditOwners.Get(creditId) == Context.Sender, "NOT_OWNER");
        Require(!_retired.Get(creditId), "ALREADY_RETIRED");
        Require(pricePerTonne > UInt256.Zero, "ZERO_PRICE");

        ulong listingId = _nextListingId.Get();
        _nextListingId.Set(listingId + 1);

        var credit = _credits.Get(creditId);

        _listings.Set(listingId, new Listing
        {
            ListingId = listingId,
            CreditId = creditId,
            Seller = Context.Sender,
            PricePerTonne = pricePerTonne,
            Quantity = credit.TonnesCO2,
            ListedAtBlock = Context.BlockNumber,
            ExpiryBlock = Context.BlockNumber + durationBlocks
        });
        _listingActive.Set(listingId, true);

        EmitEvent("CreditsListed", listingId, creditId, pricePerTonne);
        return listingId;
    }

    /// <summary>
    /// Purchase listed carbon credits.
    /// </summary>
    public void PurchaseCredits(ulong listingId)
    {
        Require(_listingActive.Get(listingId), "LISTING_NOT_ACTIVE");
        var listing = _listings.Get(listingId);
        Require(Context.BlockNumber <= listing.ExpiryBlock, "LISTING_EXPIRED");

        UInt256 totalPrice = listing.PricePerTonne * listing.Quantity /
                             1_000_000_000_000_000_000; // Adjust for scaling
        Require(Context.TxValue >= totalPrice, "INSUFFICIENT_PAYMENT");

        // Transfer credits to buyer
        _creditOwners.Set(listing.CreditId, Context.Sender);

        // Pay seller
        Context.TransferNative(listing.Seller, totalPrice);

        // Deactivate listing
        _listingActive.Set(listingId, false);

        // Update stats
        _totalMarketVolume.Set(_totalMarketVolume.Get() + totalPrice);

        EmitEvent("CreditsPurchased", listingId, Context.Sender, totalPrice);
    }

    // --- Retirement ---

    /// <summary>
    /// Retire carbon credits and receive a BST-VC sustainability certificate.
    /// </summary>
    public ulong RetireCredits(ulong creditId, string purpose)
    {
        Require(_creditOwners.Get(creditId) == Context.Sender, "NOT_OWNER");
        Require(!_retired.Get(creditId), "ALREADY_RETIRED");

        var credit = _credits.Get(creditId);

        // Mark as retired (burned)
        _retired.Set(creditId, true);
        _creditOwners.Set(creditId, Address.Zero);

        // Create retirement record
        ulong retirementId = _nextRetirementId.Get();
        _nextRetirementId.Set(retirementId + 1);

        Hash256 certHash = ComputeCertificateHash(retirementId, creditId, Context.Sender);

        _retirements.Set(retirementId, new RetirementRecord
        {
            RetirementId = retirementId,
            CreditId = creditId,
            Retiree = Context.Sender,
            TonnesCO2 = credit.TonnesCO2,
            RetiredAtBlock = Context.BlockNumber,
            Purpose = purpose,
            CertificateHash = certHash
        });

        ulong userRetCount = _retirementCount.Get(Context.Sender);
        _userRetirements.Get(Context.Sender).Set(userRetCount, retirementId);
        _retirementCount.Set(Context.Sender, userRetCount + 1);

        // Update global stats
        _totalCreditsRetired.Set(_totalCreditsRetired.Get() + credit.TonnesCO2);
        _totalCO2Offset.Set(_totalCO2Offset.Get() + credit.TonnesCO2);

        // Update project stats
        var project = _projects.Get(credit.ProjectId);
        project.TotalCreditsRetired = project.TotalCreditsRetired + credit.TonnesCO2;
        _projects.Set(credit.ProjectId, project);

        // Update corporate program if applicable
        if (_hasProgram.Get(Context.Sender))
        {
            var program = _corporatePrograms.Get(Context.Sender);
            program.CurrentYearOffset = program.CurrentYearOffset + credit.TonnesCO2;
            UpdateComplianceStatus(ref program);
            _corporatePrograms.Set(Context.Sender, program);
        }

        // Issue BST-VC sustainability certificate
        IssueSustainabilityCertificate(Context.Sender, retirementId, credit, certHash);

        EmitEvent("CreditsRetired", retirementId, creditId, credit.TonnesCO2, purpose);
        return retirementId;
    }

    /// <summary>
    /// Batch retire multiple credits.
    /// </summary>
    public ulong[] RetireBatch(ulong[] creditIds, string purpose)
    {
        ulong[] retirementIds = new ulong[creditIds.Length];
        for (int i = 0; i < creditIds.Length; i++)
        {
            retirementIds[i] = RetireCredits(creditIds[i], purpose);
        }
        return retirementIds;
    }

    // --- Corporate Programs ---

    /// <summary>
    /// Register a corporate offset program.
    /// </summary>
    public void RegisterCorporateProgram(
        string companyName,
        UInt256 annualTargetTonnes,
        ulong periodLengthBlocks)
    {
        Require(!_hasProgram.Get(Context.Sender), "PROGRAM_EXISTS");

        _corporatePrograms.Set(Context.Sender, new CorporateProgram
        {
            Company = Context.Sender,
            CompanyName = companyName,
            AnnualTargetTonnes = annualTargetTonnes,
            CurrentYearOffset = UInt256.Zero,
            ProgramStartBlock = Context.BlockNumber,
            CurrentPeriodStart = Context.BlockNumber,
            CurrentPeriodEnd = Context.BlockNumber + periodLengthBlocks,
            ComplianceStatus = 0
        });
        _hasProgram.Set(Context.Sender, true);

        EmitEvent("CorporateProgramRegistered", Context.Sender, companyName,
                  annualTargetTonnes);
    }

    /// <summary>
    /// Generate a corporate compliance report as a BST-VC credential.
    /// </summary>
    public byte[] GenerateComplianceReport(Address company)
    {
        Require(_hasProgram.Get(company), "NO_PROGRAM");
        var program = _corporatePrograms.Get(company);

        // Issue BST-VC compliance report credential
        byte[] report = IssueComplianceCredential(company, program);

        EmitEvent("ComplianceReportGenerated", company, program.ComplianceStatus);
        return report;
    }

    // --- Green Fee ---

    /// <summary>
    /// Process green fee from transaction. Called by the network.
    /// </summary>
    public void ProcessGreenFee(UInt256 feeAmount)
    {
        _greenFeePool.Set(_greenFeePool.Get() + feeAmount);
        _totalGreenFeeCollected.Set(_totalGreenFeeCollected.Get() + feeAmount);
    }

    /// <summary>
    /// Execute green fee offset: purchase and retire credits from the pool.
    /// Can be called by anyone (keeper).
    /// </summary>
    public void ExecuteGreenFeeOffset()
    {
        UInt256 pool = _greenFeePool.Get();
        Require(pool > UInt256.Zero, "EMPTY_POOL");

        // Find cheapest available credits and purchase
        // (simplified: in practice, would use order book)
        UInt256 spent = PurchaseCheapestCredits(pool);
        _greenFeePool.Set(pool - spent);

        EmitEvent("GreenFeeOffsetExecuted", spent);
    }

    // --- Project Registry ---

    /// <summary>
    /// Register a carbon offset project. Governance verification required.
    /// </summary>
    public ulong RegisterProject(
        string name,
        string projectType,
        string country,
        string methodology)
    {
        ulong projectId = _nextProjectId.Get();
        _nextProjectId.Set(projectId + 1);

        _projects.Set(projectId, new CarbonProject
        {
            ProjectId = projectId,
            Name = name,
            ProjectType = projectType,
            Country = country,
            Methodology = methodology,
            Developer = Context.Sender,
            TotalCreditsIssued = UInt256.Zero,
            TotalCreditsRetired = UInt256.Zero,
            VerificationStatus = 0 // pending
        });

        EmitEvent("ProjectRegistered", projectId, name, projectType);
        return projectId;
    }

    /// <summary>
    /// Verify a project. Governance-only.
    /// </summary>
    public void VerifyProject(ulong projectId)
    {
        RequireGovernance();
        var project = _projects.Get(projectId);
        project.VerificationStatus = 1;
        _projects.Set(projectId, project);
        _verifiedProjects.Set(projectId, true);
        EmitEvent("ProjectVerified", projectId);
    }

    // --- Query Methods ---

    public CarbonCredit GetCredit(ulong creditId) => _credits.Get(creditId);
    public Address GetCreditOwner(ulong creditId) => _creditOwners.Get(creditId);
    public bool IsCreditRetired(ulong creditId) => _retired.Get(creditId);
    public Listing GetListing(ulong listingId) => _listings.Get(listingId);
    public CarbonProject GetProject(ulong projectId) => _projects.Get(projectId);
    public CorporateProgram GetCorporateProgram(Address company)
        => _corporatePrograms.Get(company);
    public RetirementRecord GetRetirement(ulong retirementId)
        => _retirements.Get(retirementId);
    public UInt256 GetTotalCreditsRetired() => _totalCreditsRetired.Get();
    public UInt256 GetTotalCO2Offset() => _totalCO2Offset.Get();
    public UInt256 GetTotalMarketVolume() => _totalMarketVolume.Get();
    public UInt256 GetGreenFeePool() => _greenFeePool.Get();
    public uint GetGreenFeeBps() => _greenFeeBps.Get();
    public UInt256 GetTierPrice(byte tier) => _tierPrices.Get(tier);
    public ulong GetRetirementCount(Address user) => _retirementCount.Get(user);

    // --- Internal Helpers ---

    private void UpdateComplianceStatus(ref CorporateProgram program)
    {
        UInt256 progress = (program.CurrentYearOffset * 10000) /
                           program.AnnualTargetTonnes;
        if (progress >= 10000) program.ComplianceStatus = 3;      // exceeded
        else if (progress >= 10000) program.ComplianceStatus = 2; // compliant
        else if (progress >= 5000) program.ComplianceStatus = 0;  // on track
        else program.ComplianceStatus = 1;                        // behind
    }

    private Hash256 ComputeCertificateHash(ulong retId, ulong creditId,
        Address retiree) { /* BLAKE3 */ }
    private void IssueSustainabilityCertificate(Address retiree, ulong retId,
        CarbonCredit credit, Hash256 certHash) { /* ... */ }
    private byte[] IssueComplianceCredential(Address company,
        CorporateProgram program) { /* ... */ return new byte[0]; }
    private UInt256 PurchaseCheapestCredits(UInt256 budget) { /* ... */ return UInt256.Zero; }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**Medium** -- The core marketplace mechanics (listing, purchasing, retirement) are straightforward. Credit metadata management with quality tiers, vintage tracking, and project registry adds moderate data management complexity. The automated green fee mechanism requires integration with the transaction processing pipeline. Corporate compliance tracking and BST-VC certificate issuance add identity and credential complexity. The primary challenges are: ensuring credit integrity (preventing double-counting and counterfeit credits), integrating with real-world carbon registries via bridge attestations, and accurate green fee pricing that reflects current carbon credit market rates.

## Priority

**P3** -- Carbon offset functionality is a differentiating feature that positions Basalt as a sustainability-conscious blockchain, which is increasingly important for institutional adoption and regulatory compliance. However, it depends on real-world carbon credit partnerships and regulatory clarity around digital carbon markets. It should be developed after core DeFi infrastructure, governance, and cross-chain bridge capabilities are mature. The automated green fee mechanism could be a unique selling point for the network.
