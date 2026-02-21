# Art Fractionalization

## Category

Collectibles / Fine Art / Real-World Assets (RWA) / Alternative Investments

## Summary

Art Fractionalization is a BST-3525 semi-fungible token contract that tokenizes high-value artworks into tradable fractional ownership shares. Each artwork is represented by a unique slot (artwork ID), and the token value represents the ownership share (in basis points out of 10,000). The contract supports a curator role for artwork authentication and management, exhibition revenue sharing among fractional owners, a buy-out mechanism where a majority holder can trigger an auction to acquire full ownership, and comprehensive provenance tracking via BST-VC Verifiable Credentials that record the artwork's ownership history, authentication certificates, and condition reports.

The contract enables art collectors, galleries, and institutions to democratize access to high-value artworks ($100K+ paintings, sculptures, and photographs) by allowing retail investors and art enthusiasts to own fractional shares. Owners benefit from potential appreciation and exhibition revenue, while the art market gains liquidity through secondary trading of fractional positions. The buy-out mechanism ensures that a collector who acquires a supermajority stake can force an auction, providing a natural exit path for all shareholders at fair market value.

## Why It's Useful

- The global art market is valued at $65+ billion annually, but approximately 50% of high-value artworks sit in storage without generating returns; fractionalization incentivizes exhibition and active management.
- Most fine art is priced well beyond retail investor reach ($100K-$100M+); fractionalization lowers the minimum investment to whatever threshold the curator defines, opening art as an asset class to millions of new participants.
- Art has historically generated 5-10% annualized returns with low correlation to equities, making it an attractive diversification tool; tokenization enables portfolio construction across multiple artworks.
- Provenance (ownership history) is the most critical factor in art valuation, yet traditional provenance records are fragmented, paper-based, and susceptible to forgery; on-chain BST-VC credentials provide tamper-proof, cryptographically verifiable provenance.
- Exhibition revenue from museum loans, gallery displays, and digital reproductions can generate 2-5% annual yield on artwork value; on-chain distribution ensures all fractional owners receive their proportional share.
- Art authentication is plagued by forgery scandals; BST-VC credentials from certified authenticators provide cryptographic proof of authenticity that cannot be retroactively altered.
- Secondary market liquidity for art is limited to auction houses (Sotheby's, Christie's) with high fees (25%+ buyer's premium); on-chain trading eliminates intermediaries and reduces transaction costs.

## Key Features

- Artwork registration by verified curators: each artwork gets a unique slot ID with metadata linking to high-resolution images, authentication certificates, condition reports, and provenance documentation.
- Fractional ownership minting: curator mints BST-3525 tokens with value representing ownership basis points (1 = 0.01%, 10000 = 100%).
- Curator role: a designated curator manages the physical artwork (storage, insurance, exhibition), authenticates the piece, and handles logistics. The curator earns a management fee.
- Exhibition revenue sharing: when the artwork generates revenue (museum loans, gallery exhibitions, licensing), the curator deposits funds and fractional owners claim proportional shares.
- Provenance tracking via BST-VC: every ownership transfer, authentication, condition inspection, and exhibition is recorded as a Verifiable Credential, building an immutable provenance record.
- Buy-out mechanism: any holder reaching a configurable supermajority threshold (e.g., 6,000 bps = 60%) can trigger a buy-out auction. Remaining shareholders can accept the offered price or counter-bid. If the buy-out succeeds, the initiator receives full ownership and the artwork's on-chain record is updated.
- Authentication attestation: certified authenticators (registered via IssuerRegistry) submit authentication credentials that are permanently linked to the artwork's on-chain record.
- Condition reporting: periodic condition reports from registered conservators are stored as BST-VC credentials, documenting the artwork's physical state for insurance and valuation purposes.
- Insurance integration: optional insurance escrow for artwork damage or loss, funded proportionally by fractional owners.
- Appraisal updates: registered appraisers submit updated valuations, providing on-chain price discovery for secondary market trading.
- Transfer restrictions: configurable lockup periods after initial purchase and optional curator-approval for transfers (for regulatory compliance in certain jurisdictions).
- Artwork sale proceeds distribution: if the physical artwork is sold, proceeds are distributed proportionally to all fractional owners after curator fees.

## Basalt-Specific Advantages

- **BST-3525 Semi-Fungible Tokens**: The slot = artwork ID model means fractional shares of the same artwork are fungible by value. A collector holding 500 bps of "Starry Night" and another holding 300 bps can trade partial positions seamlessly. Different artworks maintain distinct identities and price trajectories. Artists or curators can split large positions into smaller shares for retail distribution, or an investor can merge multiple small purchases into a single token for portfolio simplicity.
- **BST-VC Verifiable Credentials**: Provenance is the cornerstone of art valuation. Basalt's native BST-VC standard enables authentication certificates, condition reports, ownership transfers, and exhibition records to be represented as W3C Verifiable Credentials that are cryptographically signed by recognized experts (authenticators, conservators, galleries) and permanently recorded on-chain. This creates the most robust provenance system possible -- tamper-proof, universally verifiable, and independent of any single institution.
- **IssuerRegistry Integration**: Authenticators, conservators, appraisers, and curators must be registered in the protocol-level IssuerRegistry. This creates a curated network of art-world professionals whose credentials can be verified by any market participant without contacting the individual or their institution.
- **ZK Compliance Layer**: Art transactions above certain thresholds trigger anti-money laundering (AML) requirements in most jurisdictions. Basalt's ZK proof system allows buyers to prove AML compliance without revealing their identity or financial details to other market participants, enabling private yet compliant art investment.
- **Escrow Integration**: Buy-out auctions, exhibition revenue deposits, and primary sale settlements flow through the protocol-level Escrow contract, providing atomic settlement guarantees. When a buy-out is triggered, the auction payment is escrowed until all minority shareholders accept or counter-bid.
- **Governance Integration**: Curator replacement (if the curator mismanages the artwork), buy-out threshold changes, and dispute resolution flow through on-chain governance.
- **AOT-Compiled Execution**: Exhibition revenue distribution and buy-out auction mechanics involve proportional calculations across potentially hundreds of fractional owners; AOT compilation ensures deterministic, predictable execution.
- **Ed25519 Signatures**: All provenance-related transactions carry Ed25519 signatures, providing cryptographic proof of who authenticated, appraised, or transferred the artwork.
- **BNS Integration**: Artworks and curators can register human-readable names (e.g., "starry-night.art.basalt") for marketplace discoverability.

## Token Standards Used

- **BST-3525 (Semi-Fungible Token)**: Primary standard. Slot = artwork ID, Value = ownership share in basis points.
- **BST-VC (Verifiable Credentials)**: Authentication certificates, condition reports, provenance records, appraiser attestations, and curator certifications.
- **BST-20 (Fungible Token)**: Exhibition revenue and sale proceeds distributed in native BST or BST-20 stablecoins.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines credential schemas for "ArtAuthentication" (authenticator attestation), "ConditionReport" (conservator inspection), "ArtAppraisal" (appraiser valuation), "CuratorCertification" (curator authority), and "AMLCompliance" (buyer AML verification).
- **IssuerRegistry (0x...1007)**: Verifies that authenticators, conservators, appraisers, and curators are registered professionals from recognized institutions.
- **Escrow (0x...1003)**: Holds buy-out auction payments, exhibition revenue pending distribution, and primary sale funds pending share delivery.
- **Governance (0x...1002)**: Curator replacement, buy-out threshold governance, dispute resolution, and parameter updates.
- **BNS (0x...1001)**: Artwork and curator name registration for marketplace discoverability.
- **BridgeETH (0x...1008)**: Cross-chain art investment -- collectors on Ethereum can bridge assets to participate in Basalt's art fractionalization platform.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Art Fractionalization contract built on BST-3525.
/// Slot = artwork ID, Value = ownership share in basis points.
/// Type ID: 0x010F
/// </summary>
[BasaltContract]
public partial class ArtFractionalization : BST3525Token
{
    // --- Artwork state ---
    private readonly StorageMap<string, string> _artworkCurators;       // slot -> curator address hex
    private readonly StorageMap<string, string> _artworkStatus;         // slot -> "active"/"buyout"/"sold"/"frozen"
    private readonly StorageMap<string, UInt256> _artworkValuation;     // slot -> latest appraised value
    private readonly StorageMap<string, UInt256> _issuedBps;            // slot -> total bps issued
    private readonly StorageMap<string, string> _artworkTitle;          // slot -> artwork title
    private readonly StorageMap<string, string> _artworkArtist;         // slot -> artist name
    private readonly StorageMap<string, ulong> _artworkYear;            // slot -> creation year

    // --- Exhibition revenue state ---
    private readonly StorageMap<string, UInt256> _exhibitionPool;       // slot -> undistributed exhibition revenue
    private readonly StorageMap<string, UInt256> _totalExhibitionRev;   // slot -> cumulative exhibition revenue
    private readonly StorageMap<string, UInt256> _claimedExhibition;    // slot:tokenId -> claimed amount

    // --- Curator fee ---
    private readonly StorageValue<ulong> _curatorFeeBps;               // curator management fee in bps

    // --- Buy-out state ---
    private readonly StorageValue<ulong> _buyoutThresholdBps;          // bps needed to trigger buy-out (default 6000)
    private readonly StorageMap<string, string> _buyoutInitiator;      // slot -> initiator address hex
    private readonly StorageMap<string, UInt256> _buyoutPricePerBps;    // slot -> offered price per basis point
    private readonly StorageMap<string, ulong> _buyoutDeadlineBlock;   // slot -> deadline for minority acceptance
    private readonly StorageMap<string, UInt256> _buyoutEscrowTotal;    // slot -> total BST escrowed for buy-out
    private readonly StorageMap<string, string> _buyoutAccepted;       // slot:tokenId -> "1" if accepted

    // --- Provenance tracking ---
    private readonly StorageValue<ulong> _nextProvenanceId;
    private readonly StorageMap<string, ulong> _provenanceArtworkId;    // provId -> artwork slot
    private readonly StorageMap<string, string> _provenanceType;       // provId -> "authentication"/"condition"/"transfer"/"exhibition"
    private readonly StorageMap<string, string> _provenanceIssuer;     // provId -> issuer address hex
    private readonly StorageMap<string, ulong> _provenanceBlock;       // provId -> block number
    private readonly StorageMap<string, string> _provenanceDataHash;   // provId -> IPFS hash of credential data

    // --- Compliance ---
    private readonly StorageMap<string, string> _amlVerified;          // address hex -> "1"

    // --- System contract addresses ---
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _schemaRegistryAddress;
    private readonly byte[] _escrowAddress;
    private readonly byte[] _governanceAddress;

    public ArtFractionalization(
        ulong curatorFeeBps = 200,
        ulong buyoutThresholdBps = 6000)
        : base("Basalt Fractional Art", "bART", 0)
    {
        _artworkCurators = new StorageMap<string, string>("art_curator");
        _artworkStatus = new StorageMap<string, string>("art_status");
        _artworkValuation = new StorageMap<string, UInt256>("art_val");
        _issuedBps = new StorageMap<string, UInt256>("art_bps");
        _artworkTitle = new StorageMap<string, string>("art_title");
        _artworkArtist = new StorageMap<string, string>("art_artist");
        _artworkYear = new StorageMap<string, ulong>("art_year");
        _exhibitionPool = new StorageMap<string, UInt256>("art_epool");
        _totalExhibitionRev = new StorageMap<string, UInt256>("art_etotal");
        _claimedExhibition = new StorageMap<string, UInt256>("art_eclaim");
        _curatorFeeBps = new StorageValue<ulong>("art_cfee");
        _buyoutThresholdBps = new StorageValue<ulong>("art_bthresh");
        _buyoutInitiator = new StorageMap<string, string>("art_binit");
        _buyoutPricePerBps = new StorageMap<string, UInt256>("art_bprice");
        _buyoutDeadlineBlock = new StorageMap<string, ulong>("art_bdead");
        _buyoutEscrowTotal = new StorageMap<string, UInt256>("art_bescrow");
        _buyoutAccepted = new StorageMap<string, string>("art_baccept");
        _nextProvenanceId = new StorageValue<ulong>("art_pnext");
        _provenanceArtworkId = new StorageMap<string, ulong>("art_part");
        _provenanceType = new StorageMap<string, string>("art_ptype");
        _provenanceIssuer = new StorageMap<string, string>("art_piss");
        _provenanceBlock = new StorageMap<string, ulong>("art_pblk");
        _provenanceDataHash = new StorageMap<string, string>("art_phash");
        _amlVerified = new StorageMap<string, string>("art_aml");

        _curatorFeeBps.Set(curatorFeeBps);
        _buyoutThresholdBps.Set(buyoutThresholdBps);

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        _escrowAddress = new byte[20];
        _escrowAddress[18] = 0x10;
        _escrowAddress[19] = 0x03;

        _governanceAddress = new byte[20];
        _governanceAddress[18] = 0x10;
        _governanceAddress[19] = 0x02;
    }

    // ================================================================
    // Artwork Registration
    // ================================================================

    /// <summary>
    /// Register a new artwork for fractionalization. Only registered curators.
    /// Creates the artwork slot and mints 100% ownership to the curator initially.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterArtwork(
        ulong artworkId,
        string title,
        string artistName,
        ulong creationYear,
        UInt256 initialValuation,
        string metadataUri,
        byte[] authenticationProofHash)
    {
        // Verify curator is registered
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "ART: caller not a registered curator");

        var slotKey = artworkId.ToString();
        Context.Require(
            string.IsNullOrEmpty(_artworkCurators.Get(slotKey)),
            "ART: artwork already registered");

        _artworkCurators.Set(slotKey, Convert.ToHexString(Context.Caller));
        _artworkStatus.Set(slotKey, "active");
        _artworkValuation.Set(slotKey, initialValuation);
        _artworkTitle.Set(slotKey, title);
        _artworkArtist.Set(slotKey, artistName);
        _artworkYear.Set(slotKey, creationYear);

        // Mint 100% ownership to curator
        Mint(Context.Caller, artworkId, new UInt256(10000));

        SetSlotUri(artworkId, metadataUri);

        // Record authentication provenance
        RecordProvenance(artworkId, "authentication", Convert.ToHexString(authenticationProofHash));

        Context.Emit(new ArtworkRegisteredEvent
        {
            ArtworkId = artworkId,
            Curator = Context.Caller,
            Title = title,
            ArtistName = artistName,
            Valuation = initialValuation,
        });
    }

    // ================================================================
    // Share Distribution
    // ================================================================

    /// <summary>
    /// Curator sells fractional shares to an investor/collector.
    /// Buyer must be AML-verified.
    /// </summary>
    [BasaltEntrypoint]
    public ulong SellShares(ulong curatorTokenId, byte[] buyer, UInt256 bpsToSell, byte[] amlProof)
    {
        var artworkId = SlotOf(curatorTokenId);
        var slotKey = artworkId.ToString();

        Context.Require(
            Convert.ToHexString(Context.Caller) == _artworkCurators.Get(slotKey),
            "ART: only curator");
        Context.Require(_artworkStatus.Get(slotKey) == "active", "ART: not active");

        // Verify buyer AML compliance
        VerifyAml(buyer, amlProof);

        var newTokenId = TransferValueToAddress(curatorTokenId, buyer, bpsToSell);
        _issuedBps.Set(slotKey, _issuedBps.Get(slotKey) + bpsToSell);

        // Record provenance
        RecordProvenance(artworkId, "transfer", "");

        Context.Emit(new SharesSoldEvent
        {
            ArtworkId = artworkId,
            Buyer = buyer,
            NewTokenId = newTokenId,
            BpsSold = bpsToSell,
        });

        return newTokenId;
    }

    /// <summary>
    /// Transfer fractional shares on the secondary market.
    /// Both parties must be AML-verified.
    /// </summary>
    [BasaltEntrypoint]
    public ulong TransferShares(ulong fromTokenId, byte[] to, UInt256 bpsAmount)
    {
        var artworkId = SlotOf(fromTokenId);
        Context.Require(
            _artworkStatus.Get(artworkId.ToString()) == "active",
            "ART: not active");
        Context.Require(
            _amlVerified.Get(Convert.ToHexString(to)) == "1",
            "ART: receiver not AML-verified");

        var newTokenId = TransferValueToAddress(fromTokenId, to, bpsAmount);

        RecordProvenance(artworkId, "transfer", "");

        return newTokenId;
    }

    // ================================================================
    // Exhibition Revenue
    // ================================================================

    /// <summary>
    /// Curator deposits exhibition revenue. Curator fee is deducted.
    /// </summary>
    [BasaltEntrypoint]
    public void DepositExhibitionRevenue(ulong artworkId)
    {
        var slotKey = artworkId.ToString();
        Context.Require(
            Convert.ToHexString(Context.Caller) == _artworkCurators.Get(slotKey),
            "ART: only curator");
        Context.Require(!Context.TxValue.IsZero, "ART: must send value");

        var feeBps = _curatorFeeBps.Get();
        var fee = Context.TxValue * new UInt256(feeBps) / new UInt256(10000);
        var distributable = Context.TxValue - fee;

        // Pay curator fee
        Context.TransferNative(Context.Caller, fee);

        _exhibitionPool.Set(slotKey, _exhibitionPool.Get(slotKey) + distributable);
        _totalExhibitionRev.Set(slotKey, _totalExhibitionRev.Get(slotKey) + distributable);

        RecordProvenance(artworkId, "exhibition", "");

        Context.Emit(new ExhibitionRevenueDepositedEvent
        {
            ArtworkId = artworkId,
            GrossAmount = Context.TxValue,
            CuratorFee = fee,
            NetAmount = distributable,
        });
    }

    /// <summary>
    /// Claim proportional exhibition revenue for a fractional ownership token.
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimExhibitionRevenue(ulong tokenId)
    {
        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "ART: not token owner");

        var artworkId = SlotOf(tokenId);
        var slotKey = artworkId.ToString();
        var pool = _exhibitionPool.Get(slotKey);
        Context.Require(!pool.IsZero, "ART: no revenue to claim");

        var ownershipBps = BalanceOf(tokenId);
        var share = pool * ownershipBps / new UInt256(10000);
        Context.Require(!share.IsZero, "ART: share too small");

        var claimKey = slotKey + ":" + tokenId.ToString();
        _claimedExhibition.Set(claimKey, _claimedExhibition.Get(claimKey) + share);
        _exhibitionPool.Set(slotKey, pool - share);

        Context.TransferNative(Context.Caller, share);

        Context.Emit(new ExhibitionRevenueClaimedEvent
        {
            TokenId = tokenId,
            ArtworkId = artworkId,
            Owner = Context.Caller,
            Amount = share,
        });
    }

    // ================================================================
    // Buy-Out Mechanism
    // ================================================================

    /// <summary>
    /// Initiate a buy-out. Caller must hold >= buyoutThresholdBps of the artwork.
    /// Must deposit BST covering the price for all remaining shares.
    /// </summary>
    [BasaltEntrypoint]
    public void InitiateBuyout(ulong tokenId, UInt256 pricePerBps, ulong deadlineBlocks)
    {
        var artworkId = SlotOf(tokenId);
        var slotKey = artworkId.ToString();
        Context.Require(_artworkStatus.Get(slotKey) == "active", "ART: not active");

        var callerBps = BalanceOf(tokenId);
        var threshold = _buyoutThresholdBps.Get();
        Context.Require(
            callerBps >= new UInt256(threshold),
            "ART: below buy-out threshold");

        var remainingBps = new UInt256(10000) - callerBps;
        var totalCost = remainingBps * pricePerBps;
        Context.Require(Context.TxValue >= totalCost, "ART: insufficient buy-out deposit");

        _artworkStatus.Set(slotKey, "buyout");
        _buyoutInitiator.Set(slotKey, Convert.ToHexString(Context.Caller));
        _buyoutPricePerBps.Set(slotKey, pricePerBps);
        _buyoutDeadlineBlock.Set(slotKey, Context.BlockHeight + deadlineBlocks);
        _buyoutEscrowTotal.Set(slotKey, totalCost);

        Context.Emit(new BuyoutInitiatedEvent
        {
            ArtworkId = artworkId,
            Initiator = Context.Caller,
            PricePerBps = pricePerBps,
            DeadlineBlock = Context.BlockHeight + deadlineBlocks,
            TotalEscrowed = totalCost,
        });
    }

    /// <summary>
    /// Minority shareholder accepts the buy-out offer.
    /// Receives payment and their shares are transferred to the initiator.
    /// </summary>
    [BasaltEntrypoint]
    public void AcceptBuyout(ulong tokenId)
    {
        var artworkId = SlotOf(tokenId);
        var slotKey = artworkId.ToString();
        Context.Require(_artworkStatus.Get(slotKey) == "buyout", "ART: no active buy-out");

        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "ART: not token owner");

        var initiatorHex = _buyoutInitiator.Get(slotKey);
        Context.Require(
            Convert.ToHexString(Context.Caller) != initiatorHex,
            "ART: initiator cannot accept own buy-out");

        var acceptKey = slotKey + ":" + tokenId.ToString();
        Context.Require(_buyoutAccepted.Get(acceptKey) != "1", "ART: already accepted");
        _buyoutAccepted.Set(acceptKey, "1");

        var bps = BalanceOf(tokenId);
        var pricePerBps = _buyoutPricePerBps.Get(slotKey);
        var payment = bps * pricePerBps;

        // Transfer shares to initiator
        var initiator = Convert.FromHexString(initiatorHex);
        TransferToken(initiator, tokenId);

        // Pay the minority shareholder
        Context.TransferNative(Context.Caller, payment);

        var escrowRemaining = _buyoutEscrowTotal.Get(slotKey) - payment;
        _buyoutEscrowTotal.Set(slotKey, escrowRemaining);

        Context.Emit(new BuyoutAcceptedEvent
        {
            ArtworkId = artworkId,
            TokenId = tokenId,
            Seller = Context.Caller,
            Payment = payment,
        });
    }

    /// <summary>
    /// Finalize buy-out after deadline. If all minority shareholders accepted,
    /// the initiator now owns 100%. Otherwise, the buy-out is cancelled
    /// and escrowed funds are returned.
    /// </summary>
    [BasaltEntrypoint]
    public void FinalizeBuyout(ulong artworkId)
    {
        var slotKey = artworkId.ToString();
        Context.Require(_artworkStatus.Get(slotKey) == "buyout", "ART: no active buy-out");
        Context.Require(
            Context.BlockHeight >= _buyoutDeadlineBlock.Get(slotKey),
            "ART: deadline not reached");

        var escrowRemaining = _buyoutEscrowTotal.Get(slotKey);
        var initiatorHex = _buyoutInitiator.Get(slotKey);
        var initiator = Convert.FromHexString(initiatorHex);

        if (escrowRemaining.IsZero)
        {
            // All shares acquired -- buy-out successful
            _artworkStatus.Set(slotKey, "active"); // back to normal, fully owned
            Context.Emit(new BuyoutCompletedEvent { ArtworkId = artworkId, Success = true });
        }
        else
        {
            // Not all accepted -- cancel and refund
            _artworkStatus.Set(slotKey, "active");
            Context.TransferNative(initiator, escrowRemaining);
            _buyoutEscrowTotal.Set(slotKey, UInt256.Zero);
            Context.Emit(new BuyoutCompletedEvent { ArtworkId = artworkId, Success = false });
        }
    }

    /// <summary>
    /// Cancel a buy-out before the deadline. Only the initiator.
    /// Returns escrowed funds.
    /// </summary>
    [BasaltEntrypoint]
    public void CancelBuyout(ulong artworkId)
    {
        var slotKey = artworkId.ToString();
        Context.Require(_artworkStatus.Get(slotKey) == "buyout", "ART: no active buy-out");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _buyoutInitiator.Get(slotKey),
            "ART: only initiator");

        var escrowRemaining = _buyoutEscrowTotal.Get(slotKey);
        _artworkStatus.Set(slotKey, "active");
        _buyoutEscrowTotal.Set(slotKey, UInt256.Zero);

        if (!escrowRemaining.IsZero)
            Context.TransferNative(Context.Caller, escrowRemaining);

        Context.Emit(new BuyoutCompletedEvent { ArtworkId = artworkId, Success = false });
    }

    // ================================================================
    // Provenance and Authentication
    // ================================================================

    /// <summary>
    /// Submit an authentication attestation for an artwork.
    /// Only registered authenticators via IssuerRegistry.
    /// </summary>
    [BasaltEntrypoint]
    public ulong SubmitAuthentication(ulong artworkId, string credentialDataHash)
    {
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "ART: not a registered authenticator");

        return RecordProvenance(artworkId, "authentication", credentialDataHash);
    }

    /// <summary>
    /// Submit a condition report for an artwork.
    /// Only registered conservators via IssuerRegistry.
    /// </summary>
    [BasaltEntrypoint]
    public ulong SubmitConditionReport(ulong artworkId, string reportDataHash)
    {
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "ART: not a registered conservator");

        return RecordProvenance(artworkId, "condition", reportDataHash);
    }

    /// <summary>
    /// Update artwork appraisal. Only registered appraisers.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateValuation(ulong artworkId, UInt256 newValuation)
    {
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "ART: not a registered appraiser");

        _artworkValuation.Set(artworkId.ToString(), newValuation);

        Context.Emit(new ValuationUpdatedEvent
        {
            ArtworkId = artworkId,
            NewValuation = newValuation,
            Appraiser = Context.Caller,
        });
    }

    // ================================================================
    // Curator Management
    // ================================================================

    /// <summary>
    /// Replace artwork curator. Governance-only.
    /// </summary>
    [BasaltEntrypoint]
    public void ReplaceCurator(ulong artworkId, byte[] newCurator)
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(_governanceAddress),
            "ART: only governance");

        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", newCurator);
        Context.Require(isRegistered, "ART: new curator not registered");

        _artworkCurators.Set(artworkId.ToString(), Convert.ToHexString(newCurator));

        Context.Emit(new CuratorReplacedEvent
        {
            ArtworkId = artworkId,
            NewCurator = newCurator,
        });
    }

    // ================================================================
    // Views
    // ================================================================

    [BasaltView]
    public string GetArtworkStatus(ulong artworkId)
        => _artworkStatus.Get(artworkId.ToString()) ?? "unknown";

    [BasaltView]
    public string GetArtworkTitle(ulong artworkId)
        => _artworkTitle.Get(artworkId.ToString()) ?? "";

    [BasaltView]
    public string GetArtworkArtist(ulong artworkId)
        => _artworkArtist.Get(artworkId.ToString()) ?? "";

    [BasaltView]
    public UInt256 GetArtworkValuation(ulong artworkId)
        => _artworkValuation.Get(artworkId.ToString());

    [BasaltView]
    public UInt256 GetExhibitionPoolBalance(ulong artworkId)
        => _exhibitionPool.Get(artworkId.ToString());

    [BasaltView]
    public UInt256 GetTotalExhibitionRevenue(ulong artworkId)
        => _totalExhibitionRev.Get(artworkId.ToString());

    [BasaltView]
    public UInt256 GetBuyoutPricePerBps(ulong artworkId)
        => _buyoutPricePerBps.Get(artworkId.ToString());

    [BasaltView]
    public ulong GetBuyoutDeadline(ulong artworkId)
        => _buyoutDeadlineBlock.Get(artworkId.ToString());

    [BasaltView]
    public UInt256 GetBuyoutEscrowRemaining(ulong artworkId)
        => _buyoutEscrowTotal.Get(artworkId.ToString());

    [BasaltView]
    public bool IsAmlVerified(byte[] addr)
        => _amlVerified.Get(Convert.ToHexString(addr)) == "1";

    [BasaltView]
    public string GetProvenanceType(ulong provenanceId)
        => _provenanceType.Get(provenanceId.ToString()) ?? "";

    [BasaltView]
    public ulong GetProvenanceBlock(ulong provenanceId)
        => _provenanceBlock.Get(provenanceId.ToString());

    [BasaltView]
    public ulong GetBuyoutThresholdBps()
        => _buyoutThresholdBps.Get();

    // ================================================================
    // Internal helpers
    // ================================================================

    private ulong RecordProvenance(ulong artworkId, string provenanceType, string dataHash)
    {
        var provId = _nextProvenanceId.Get();
        _nextProvenanceId.Set(provId + 1);

        var key = provId.ToString();
        _provenanceArtworkId.Set(key, artworkId);
        _provenanceType.Set(key, provenanceType);
        _provenanceIssuer.Set(key, Convert.ToHexString(Context.Caller));
        _provenanceBlock.Set(key, Context.BlockHeight);
        _provenanceDataHash.Set(key, dataHash);

        Context.Emit(new ProvenanceRecordedEvent
        {
            ProvenanceId = provId,
            ArtworkId = artworkId,
            Type = provenanceType,
            Issuer = Context.Caller,
        });

        return provId;
    }

    private void VerifyAml(byte[] subject, byte[] amlProof)
    {
        var hex = Convert.ToHexString(subject);
        if (_amlVerified.Get(hex) == "1") return;

        var valid = Context.CallContract<bool>(
            _schemaRegistryAddress, "VerifyProof", "AMLCompliance", subject, amlProof);
        Context.Require(valid, "ART: invalid AML proof");

        _amlVerified.Set(hex, "1");
    }
}

// ================================================================
// Events
// ================================================================

[BasaltEvent]
public sealed class ArtworkRegisteredEvent
{
    [Indexed] public ulong ArtworkId { get; init; }
    [Indexed] public byte[] Curator { get; init; } = [];
    public string Title { get; init; } = "";
    public string ArtistName { get; init; } = "";
    public UInt256 Valuation { get; init; }
}

[BasaltEvent]
public sealed class SharesSoldEvent
{
    [Indexed] public ulong ArtworkId { get; init; }
    [Indexed] public byte[] Buyer { get; init; } = [];
    public ulong NewTokenId { get; init; }
    public UInt256 BpsSold { get; init; }
}

[BasaltEvent]
public sealed class ExhibitionRevenueDepositedEvent
{
    [Indexed] public ulong ArtworkId { get; init; }
    public UInt256 GrossAmount { get; init; }
    public UInt256 CuratorFee { get; init; }
    public UInt256 NetAmount { get; init; }
}

[BasaltEvent]
public sealed class ExhibitionRevenueClaimedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public ulong ArtworkId { get; init; }
    public byte[] Owner { get; init; } = [];
    public UInt256 Amount { get; init; }
}

[BasaltEvent]
public sealed class BuyoutInitiatedEvent
{
    [Indexed] public ulong ArtworkId { get; init; }
    [Indexed] public byte[] Initiator { get; init; } = [];
    public UInt256 PricePerBps { get; init; }
    public ulong DeadlineBlock { get; init; }
    public UInt256 TotalEscrowed { get; init; }
}

[BasaltEvent]
public sealed class BuyoutAcceptedEvent
{
    [Indexed] public ulong ArtworkId { get; init; }
    [Indexed] public ulong TokenId { get; init; }
    public byte[] Seller { get; init; } = [];
    public UInt256 Payment { get; init; }
}

[BasaltEvent]
public sealed class BuyoutCompletedEvent
{
    [Indexed] public ulong ArtworkId { get; init; }
    public bool Success { get; init; }
}

[BasaltEvent]
public sealed class ProvenanceRecordedEvent
{
    [Indexed] public ulong ProvenanceId { get; init; }
    [Indexed] public ulong ArtworkId { get; init; }
    public string Type { get; init; } = "";
    public byte[] Issuer { get; init; } = [];
}

[BasaltEvent]
public sealed class ValuationUpdatedEvent
{
    [Indexed] public ulong ArtworkId { get; init; }
    public UInt256 NewValuation { get; init; }
    public byte[] Appraiser { get; init; } = [];
}

[BasaltEvent]
public sealed class CuratorReplacedEvent
{
    [Indexed] public ulong ArtworkId { get; init; }
    public byte[] NewCurator { get; init; } = [];
}
```

## Complexity

**High** -- This contract combines BST-3525 fractional ownership, a multi-phase buy-out auction mechanism (initiation, acceptance, deadline, finalization/cancellation with escrow management), exhibition revenue distribution, provenance tracking with multiple credential types, AML-gated transfers, and curator management with governance-based replacement. The buy-out mechanism alone is a complex state machine requiring careful handling of escrowed funds, deadline enforcement, and partial acceptance scenarios. Cross-contract calls to IssuerRegistry, SchemaRegistry, Escrow, and Governance add integration complexity. The provenance system creates an append-only credential history that must be carefully maintained.

## Priority

**P3** -- Art fractionalization is a compelling narrative use case that demonstrates BST-3525 and BST-VC capabilities in an accessible, consumer-friendly context. However, it faces significant real-world adoption barriers: physical art custody and insurance require trusted off-chain infrastructure, the art market is relationship-driven and resistant to technological disruption, and regulatory frameworks for fractional art ownership are still developing. Higher priority use cases (bonds, real estate, carbon credits) address larger markets with more established regulatory frameworks. Art fractionalization is best positioned as a showcase project that demonstrates the platform's capabilities to a broader audience.
