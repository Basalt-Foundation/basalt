# Music Royalty Tokens

## Category

Creator Economy / Intellectual Property / Real-World Assets (RWA) / Revenue Sharing

## Summary

Music Royalty Tokens is a BST-3525 semi-fungible token contract that enables artists and rights holders to tokenize their music royalty streams and sell fractional shares to fans and investors. Each token uses the slot to represent a specific song, album, or catalog (identified by a unique content ID), and the value represents the royalty percentage share (in basis points out of 10,000). Revenue from streaming platforms, sync licensing, and other royalty sources flows into the contract via oracle-reported earnings, and is distributed proportionally to all share holders based on their ownership percentage.

The contract creates a direct connection between artists and their supporters: artists can raise capital by selling a portion of their future royalties without giving up creative control, while fans and investors gain exposure to music revenue streams as an alternative asset class. The secondary market allows royalty shares to be traded freely, with price discovery reflecting the market's assessment of the underlying music's earning potential.

## Why It's Useful

- The global music royalty market generates $40B+ annually, but artists typically receive only 12-20% of streaming revenue through traditional label and distributor arrangements; tokenization enables artists to retain more value by selling directly to fans.
- Music royalties are a proven, recession-resistant asset class (Hipgnosis Songs Fund, Round Hill Music) with predictable cash flows, but institutional-grade royalty investing requires $10M+ minimums; fractionalization opens this asset class to retail investors.
- Royalty accounting in the music industry is notoriously opaque, with artists waiting 6-18 months for payment and often receiving inaccurate statements; on-chain revenue distribution provides real-time, auditable payment flows.
- Fan engagement is increasingly important for artist monetization; owning a royalty share creates an economic alignment between artists and fans that deepens community loyalty beyond traditional merchandise or NFT collectibles.
- Secondary market trading of music royalties is currently limited to private transactions between institutional buyers; tokenization enables a liquid, 24/7 marketplace for royalty shares.
- Cross-border royalty collection is fragmented across dozens of collecting societies with different rules; on-chain distribution simplifies international payment routing.

## Key Features

- Catalog registration: artists or rights holders register songs/albums as unique slots with metadata (title, artist, ISRC/ISWC codes, release date, territory rights).
- Royalty share minting: rights holders mint BST-3525 tokens with value representing royalty basis points, distributing shares to themselves, investors, or fans.
- Revenue oracle integration: designated revenue reporters (oracle operators verified via IssuerRegistry) submit periodic royalty earnings data for each catalog entry.
- Proportional revenue distribution: accumulated royalties are distributed to share holders based on their ownership percentage, claimable at any time.
- Artist reserve: configurable minimum percentage that the original artist must retain (e.g., cannot sell more than 60% of royalties, always retaining at least 40%).
- Secondary market: built-in order book for trading royalty shares between any parties, with optional royalty on secondary sales paid back to the original artist.
- Revenue history: on-chain record of all revenue reports, enabling transparent valuation and due diligence for potential investors.
- Catalog verification: BST-VC credentials from recognized music rights organizations (PROs, CMOs) attest to the legitimacy of catalog ownership.
- Streaming revenue oracle: authorized oracle operators report streaming counts and revenue from platforms (Spotify, Apple Music, YouTube) at configurable intervals.
- Advance mechanism: investors can provide upfront capital to artists in exchange for future royalty shares, functioning as a decentralized music advance.
- Sync licensing revenue: separate revenue channel for synchronization licensing (TV, film, advertising) with independent reporting and distribution.
- Territory-based splits: optional per-territory royalty splits where different shareholders receive revenue from different geographic regions.

## Basalt-Specific Advantages

- **BST-3525 Semi-Fungible Tokens**: The slot = song/album ID model means royalty shares for the same song are fungible by value. An investor holding 500 bps of "Song A" and another investor holding 300 bps of "Song A" can trade partial positions seamlessly. Shares across different songs remain distinct. Artists can split a 2,000 bps share into ten 200 bps shares for fan sales, or an investor can merge multiple small purchases into a single token. This granularity is impossible with simple NFTs (ERC-721) or generic fungible tokens (ERC-20 per song).
- **BST-VC Verifiable Credentials**: Catalog ownership is verified through W3C Verifiable Credentials issued by recognized performing rights organizations (PROs) or collecting management organizations (CMOs). These credentials cryptographically attest that "Artist X is the rights holder for ISRC US-ABC-12-34567" without requiring trust in a centralized platform.
- **IssuerRegistry Integration**: Revenue oracles, catalog verifiers, and rights organizations must be registered in the protocol-level IssuerRegistry, creating a curated trust network for music industry participants.
- **ZK Compliance Layer**: Revenue amounts can be reported with ZK proofs that verify "total streaming revenue for Q1 exceeded $X" without revealing the exact figure to competitors. Artists can prove royalty stream quality to potential investors without disclosing sensitive earnings data.
- **AOT-Compiled Execution**: Revenue distribution across potentially thousands of micro-shareholders (fans holding small positions) requires iterative calculation. AOT compilation ensures this scales predictably without gas estimation issues.
- **Governance Integration**: Adding new revenue oracle operators, updating artist reserve minimums, and resolving rights disputes flow through on-chain governance.
- **Escrow Integration**: Advance mechanisms use the protocol-level Escrow to hold investor funds until royalty share tokens are transferred, providing atomic settlement.
- **BNS Integration**: Artists register human-readable names (e.g., "artist-name.music.basalt") for discoverability in the marketplace.

## Token Standards Used

- **BST-3525 (Semi-Fungible Token)**: Primary standard. Slot = content ID (song/album/catalog), Value = royalty share in basis points.
- **BST-VC (Verifiable Credentials)**: Catalog ownership attestations from performing rights organizations, revenue oracle authorization.
- **BST-20 (Fungible Token)**: Revenue distribution in native BST or BST-20 stablecoins.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines credential schemas for "CatalogOwnership" (rights holder attestation), "RevenueOracle" (oracle operator authorization), and "MusicRightsOrg" (PRO/CMO verification).
- **IssuerRegistry (0x...1007)**: Verifies that catalog verifiers, revenue oracles, and rights organizations are registered and authorized.
- **Escrow (0x...1003)**: Holds advance payments and secondary market transaction funds for atomic settlement.
- **Governance (0x...1002)**: Governs oracle registration, artist reserve parameters, dispute resolution, and platform fee structures.
- **BNS (0x...1001)**: Artist and catalog name registration for marketplace discoverability.
- **BridgeETH (0x...1008)**: Cross-chain investment -- music fans and investors on Ethereum can bridge assets to purchase royalty shares on Basalt.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Music Royalty Tokens built on BST-3525.
/// Slot = content ID (song/album/catalog), Value = royalty share basis points.
/// Type ID: 0x010D
/// </summary>
[BasaltContract]
public partial class MusicRoyalties : BST3525Token
{
    // --- Catalog state ---
    private readonly StorageMap<string, string> _catalogOwners;        // slot -> original rights holder hex
    private readonly StorageMap<string, string> _catalogTitles;        // slot -> song/album title
    private readonly StorageMap<string, string> _catalogArtists;       // slot -> artist name
    private readonly StorageMap<string, string> _catalogIsrcCodes;     // slot -> ISRC/ISWC code
    private readonly StorageMap<string, UInt256> _issuedSharesBps;     // slot -> total bps issued to external holders
    private readonly StorageMap<string, ulong> _artistReserveMinBps;   // slot -> min bps artist must retain

    // --- Revenue state ---
    private readonly StorageMap<string, UInt256> _revenuePool;         // slot -> undistributed revenue
    private readonly StorageMap<string, UInt256> _totalRevenue;        // slot -> cumulative revenue lifetime
    private readonly StorageMap<string, UInt256> _claimedRevenue;      // slot:tokenId -> claimed amount
    private readonly StorageValue<ulong> _nextRevenueReportId;
    private readonly StorageMap<string, UInt256> _reportAmounts;       // reportId -> amount
    private readonly StorageMap<string, ulong> _reportSlots;           // reportId -> slot (content ID)
    private readonly StorageMap<string, ulong> _reportBlocks;          // reportId -> block
    private readonly StorageMap<string, string> _reportSources;        // reportId -> "streaming"/"sync"/"mechanical"/"performance"

    // --- Marketplace state ---
    private readonly StorageValue<ulong> _nextListingId;
    private readonly StorageMap<string, string> _listingSellers;       // listingId -> seller hex
    private readonly StorageMap<string, ulong> _listingTokenIds;       // listingId -> token ID
    private readonly StorageMap<string, UInt256> _listingAmountsBps;    // listingId -> bps for sale
    private readonly StorageMap<string, UInt256> _listingPrices;        // listingId -> asking price in BST
    private readonly StorageMap<string, string> _listingStatus;        // listingId -> "active"/"sold"/"cancelled"

    // --- Secondary sale royalty ---
    private readonly StorageMap<string, ulong> _secondarySaleFeeBps;   // slot -> fee to original artist on secondary sales

    // --- Oracle operators ---
    private readonly StorageMap<string, string> _authorizedOracles;    // oracle hex -> "1"

    // --- System contract addresses ---
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _schemaRegistryAddress;
    private readonly byte[] _escrowAddress;

    public MusicRoyalties()
        : base("Basalt Music Royalty", "bROYAL", 0)
    {
        _catalogOwners = new StorageMap<string, string>("mr_owner");
        _catalogTitles = new StorageMap<string, string>("mr_title");
        _catalogArtists = new StorageMap<string, string>("mr_artist");
        _catalogIsrcCodes = new StorageMap<string, string>("mr_isrc");
        _issuedSharesBps = new StorageMap<string, UInt256>("mr_issued");
        _artistReserveMinBps = new StorageMap<string, ulong>("mr_reserve");
        _revenuePool = new StorageMap<string, UInt256>("mr_pool");
        _totalRevenue = new StorageMap<string, UInt256>("mr_trev");
        _claimedRevenue = new StorageMap<string, UInt256>("mr_claimed");
        _nextRevenueReportId = new StorageValue<ulong>("mr_rnext");
        _reportAmounts = new StorageMap<string, UInt256>("mr_ramt");
        _reportSlots = new StorageMap<string, ulong>("mr_rslot");
        _reportBlocks = new StorageMap<string, ulong>("mr_rblk");
        _reportSources = new StorageMap<string, string>("mr_rsrc");
        _nextListingId = new StorageValue<ulong>("mr_lnext");
        _listingSellers = new StorageMap<string, string>("mr_lsell");
        _listingTokenIds = new StorageMap<string, ulong>("mr_ltid");
        _listingAmountsBps = new StorageMap<string, UInt256>("mr_lbps");
        _listingPrices = new StorageMap<string, UInt256>("mr_lprice");
        _listingStatus = new StorageMap<string, string>("mr_lstat");
        _secondarySaleFeeBps = new StorageMap<string, ulong>("mr_sfee");
        _authorizedOracles = new StorageMap<string, string>("mr_oracle");

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        _escrowAddress = new byte[20];
        _escrowAddress[18] = 0x10;
        _escrowAddress[19] = 0x03;
    }

    // ================================================================
    // Catalog Registration
    // ================================================================

    /// <summary>
    /// Register a new song/album/catalog for royalty tokenization.
    /// Caller must be the verified rights holder.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterCatalog(
        ulong contentId,
        string title,
        string artistName,
        string isrcCode,
        ulong artistReserveMinBps,
        ulong secondarySaleFeeBps,
        string metadataUri)
    {
        // Verify caller is a registered rights holder
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "MR: caller not a registered rights holder");

        var slotKey = contentId.ToString();
        Context.Require(
            string.IsNullOrEmpty(_catalogOwners.Get(slotKey)),
            "MR: content ID already registered");
        Context.Require(artistReserveMinBps <= 10000, "MR: reserve exceeds 100%");
        Context.Require(secondarySaleFeeBps <= 1000, "MR: secondary fee max 10%");

        _catalogOwners.Set(slotKey, Convert.ToHexString(Context.Caller));
        _catalogTitles.Set(slotKey, title);
        _catalogArtists.Set(slotKey, artistName);
        _catalogIsrcCodes.Set(slotKey, isrcCode);
        _artistReserveMinBps.Set(slotKey, artistReserveMinBps);
        _secondarySaleFeeBps.Set(slotKey, secondarySaleFeeBps);

        // Mint 10000 bps to the artist (100% ownership initially)
        Mint(Context.Caller, contentId, new UInt256(10000));

        SetSlotUri(contentId, metadataUri);

        Context.Emit(new CatalogRegisteredEvent
        {
            ContentId = contentId,
            Artist = Context.Caller,
            Title = title,
            ArtistName = artistName,
            IsrcCode = isrcCode,
        });
    }

    // ================================================================
    // Share Distribution
    // ================================================================

    /// <summary>
    /// Artist sells royalty shares to an investor/fan.
    /// Enforces artist reserve minimum.
    /// </summary>
    [BasaltEntrypoint]
    public ulong SellShares(ulong artistTokenId, byte[] buyer, UInt256 bpsToSell)
    {
        var contentId = SlotOf(artistTokenId);
        var slotKey = contentId.ToString();

        // Only catalog owner can sell initial shares
        Context.Require(
            Convert.ToHexString(Context.Caller) == _catalogOwners.Get(slotKey),
            "MR: only catalog owner");

        // Enforce artist reserve
        var currentIssued = _issuedSharesBps.Get(slotKey);
        var maxSellable = new UInt256(10000) - new UInt256(_artistReserveMinBps.Get(slotKey));
        Context.Require(
            currentIssued + bpsToSell <= maxSellable,
            "MR: would breach artist reserve minimum");

        _issuedSharesBps.Set(slotKey, currentIssued + bpsToSell);

        // Transfer value (bps) from artist token to buyer
        var newTokenId = TransferValueToAddress(artistTokenId, buyer, bpsToSell);

        Context.Emit(new SharesSoldEvent
        {
            ContentId = contentId,
            Buyer = buyer,
            NewTokenId = newTokenId,
            BpsSold = bpsToSell,
        });

        return newTokenId;
    }

    // ================================================================
    // Revenue Reporting and Distribution
    // ================================================================

    /// <summary>
    /// Oracle reports revenue earned for a content ID.
    /// Only authorized revenue oracles can report.
    /// </summary>
    [BasaltEntrypoint]
    public ulong ReportRevenue(ulong contentId, UInt256 amount, string source)
    {
        Context.Require(
            _authorizedOracles.Get(Convert.ToHexString(Context.Caller)) == "1",
            "MR: not authorized oracle");
        Context.Require(!amount.IsZero, "MR: amount must be > 0");
        RequireValidSource(source);

        Context.Require(!Context.TxValue.IsZero, "MR: must send revenue as BST");
        Context.Require(Context.TxValue >= amount, "MR: sent value less than reported amount");

        var slotKey = contentId.ToString();
        _revenuePool.Set(slotKey, _revenuePool.Get(slotKey) + amount);
        _totalRevenue.Set(slotKey, _totalRevenue.Get(slotKey) + amount);

        var reportId = _nextRevenueReportId.Get();
        _nextRevenueReportId.Set(reportId + 1);

        var reportKey = reportId.ToString();
        _reportAmounts.Set(reportKey, amount);
        _reportSlots.Set(reportKey, contentId);
        _reportBlocks.Set(reportKey, Context.BlockHeight);
        _reportSources.Set(reportKey, source);

        Context.Emit(new RevenueReportedEvent
        {
            ReportId = reportId,
            ContentId = contentId,
            Amount = amount,
            Source = source,
            Oracle = Context.Caller,
        });

        return reportId;
    }

    /// <summary>
    /// Claim proportional revenue for a royalty share token.
    /// Amount = (tokenValue / 10000) * revenuePool
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimRevenue(ulong tokenId)
    {
        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "MR: not token owner");

        var contentId = SlotOf(tokenId);
        var slotKey = contentId.ToString();
        var pool = _revenuePool.Get(slotKey);
        Context.Require(!pool.IsZero, "MR: no revenue to claim");

        var sharesBps = BalanceOf(tokenId);
        var share = pool * sharesBps / new UInt256(10000);
        Context.Require(!share.IsZero, "MR: share too small");

        var claimKey = slotKey + ":" + tokenId.ToString();
        _claimedRevenue.Set(claimKey, _claimedRevenue.Get(claimKey) + share);
        _revenuePool.Set(slotKey, pool - share);

        Context.TransferNative(Context.Caller, share);

        Context.Emit(new RevenueClaimedEvent
        {
            TokenId = tokenId,
            ContentId = contentId,
            Recipient = Context.Caller,
            Amount = share,
        });
    }

    // ================================================================
    // Secondary Market
    // ================================================================

    /// <summary>
    /// List royalty shares for sale on the secondary market.
    /// </summary>
    [BasaltEntrypoint]
    public ulong ListShares(ulong tokenId, UInt256 bpsToSell, UInt256 askingPrice)
    {
        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "MR: not token owner");
        Context.Require(BalanceOf(tokenId) >= bpsToSell, "MR: insufficient shares");
        Context.Require(!askingPrice.IsZero, "MR: price must be > 0");

        var listingId = _nextListingId.Get();
        _nextListingId.Set(listingId + 1);

        var key = listingId.ToString();
        _listingSellers.Set(key, Convert.ToHexString(Context.Caller));
        _listingTokenIds.Set(key, tokenId);
        _listingAmountsBps.Set(key, bpsToSell);
        _listingPrices.Set(key, askingPrice);
        _listingStatus.Set(key, "active");

        Context.Emit(new SharesListedEvent
        {
            ListingId = listingId,
            Seller = Context.Caller,
            ContentId = SlotOf(tokenId),
            BpsForSale = bpsToSell,
            AskingPrice = askingPrice,
        });

        return listingId;
    }

    /// <summary>
    /// Buy royalty shares from a secondary market listing.
    /// A secondary sale fee goes to the original artist.
    /// </summary>
    [BasaltEntrypoint]
    public ulong BuyShares(ulong listingId)
    {
        var key = listingId.ToString();
        Context.Require(_listingStatus.Get(key) == "active", "MR: listing not active");

        var askingPrice = _listingPrices.Get(key);
        Context.Require(Context.TxValue >= askingPrice, "MR: insufficient payment");

        var tokenId = _listingTokenIds.Get(key);
        var bps = _listingAmountsBps.Get(key);
        var contentId = SlotOf(tokenId);
        var slotKey = contentId.ToString();
        var sellerHex = _listingSellers.Get(key);

        _listingStatus.Set(key, "sold");

        // Calculate secondary sale fee to original artist
        var feeBps = _secondarySaleFeeBps.Get(slotKey);
        var artistFee = askingPrice * new UInt256(feeBps) / new UInt256(10000);
        var sellerProceeds = askingPrice - artistFee;

        // Transfer shares to buyer
        var newTokenId = TransferValueToAddress(tokenId, Context.Caller, bps);

        // Pay seller and artist
        Context.TransferNative(Convert.FromHexString(sellerHex), sellerProceeds);
        if (!artistFee.IsZero)
        {
            var artistHex = _catalogOwners.Get(slotKey);
            Context.TransferNative(Convert.FromHexString(artistHex), artistFee);
        }

        Context.Emit(new SharesBoughtEvent
        {
            ListingId = listingId,
            Buyer = Context.Caller,
            NewTokenId = newTokenId,
            BpsBought = bps,
            Price = askingPrice,
            ArtistFee = artistFee,
        });

        return newTokenId;
    }

    /// <summary>
    /// Cancel a secondary market listing. Only the seller.
    /// </summary>
    [BasaltEntrypoint]
    public void CancelListing(ulong listingId)
    {
        var key = listingId.ToString();
        Context.Require(
            Convert.ToHexString(Context.Caller) == _listingSellers.Get(key),
            "MR: not seller");
        Context.Require(_listingStatus.Get(key) == "active", "MR: not active");
        _listingStatus.Set(key, "cancelled");
    }

    // ================================================================
    // Oracle Management
    // ================================================================

    /// <summary>
    /// Authorize a revenue oracle operator. Only governance.
    /// </summary>
    [BasaltEntrypoint]
    public void AuthorizeOracle(byte[] oracle)
    {
        var governanceAddress = new byte[20];
        governanceAddress[18] = 0x10;
        governanceAddress[19] = 0x02;
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(governanceAddress),
            "MR: only governance");
        _authorizedOracles.Set(Convert.ToHexString(oracle), "1");
    }

    // ================================================================
    // Views
    // ================================================================

    [BasaltView]
    public string GetCatalogTitle(ulong contentId)
        => _catalogTitles.Get(contentId.ToString()) ?? "";

    [BasaltView]
    public string GetCatalogArtist(ulong contentId)
        => _catalogArtists.Get(contentId.ToString()) ?? "";

    [BasaltView]
    public string GetIsrcCode(ulong contentId)
        => _catalogIsrcCodes.Get(contentId.ToString()) ?? "";

    [BasaltView]
    public UInt256 GetRevenuePoolBalance(ulong contentId)
        => _revenuePool.Get(contentId.ToString());

    [BasaltView]
    public UInt256 GetTotalRevenue(ulong contentId)
        => _totalRevenue.Get(contentId.ToString());

    [BasaltView]
    public UInt256 GetIssuedSharesBps(ulong contentId)
        => _issuedSharesBps.Get(contentId.ToString());

    [BasaltView]
    public ulong GetArtistReserveMinBps(ulong contentId)
        => _artistReserveMinBps.Get(contentId.ToString());

    [BasaltView]
    public string GetListingStatus(ulong listingId)
        => _listingStatus.Get(listingId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetListingPrice(ulong listingId)
        => _listingPrices.Get(listingId.ToString());

    [BasaltView]
    public UInt256 GetRevenueReportAmount(ulong reportId)
        => _reportAmounts.Get(reportId.ToString());

    [BasaltView]
    public string GetRevenueReportSource(ulong reportId)
        => _reportSources.Get(reportId.ToString()) ?? "";

    // ================================================================
    // Internal helpers
    // ================================================================

    private static void RequireValidSource(string source)
    {
        Context.Require(
            source == "streaming" || source == "sync" ||
            source == "mechanical" || source == "performance" ||
            source == "digital" || source == "other",
            "MR: invalid revenue source");
    }
}

// ================================================================
// Events
// ================================================================

[BasaltEvent]
public sealed class CatalogRegisteredEvent
{
    [Indexed] public ulong ContentId { get; init; }
    [Indexed] public byte[] Artist { get; init; } = [];
    public string Title { get; init; } = "";
    public string ArtistName { get; init; } = "";
    public string IsrcCode { get; init; } = "";
}

[BasaltEvent]
public sealed class SharesSoldEvent
{
    [Indexed] public ulong ContentId { get; init; }
    [Indexed] public byte[] Buyer { get; init; } = [];
    public ulong NewTokenId { get; init; }
    public UInt256 BpsSold { get; init; }
}

[BasaltEvent]
public sealed class RevenueReportedEvent
{
    [Indexed] public ulong ReportId { get; init; }
    [Indexed] public ulong ContentId { get; init; }
    public UInt256 Amount { get; init; }
    public string Source { get; init; } = "";
    public byte[] Oracle { get; init; } = [];
}

[BasaltEvent]
public sealed class RevenueClaimedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public ulong ContentId { get; init; }
    public byte[] Recipient { get; init; } = [];
    public UInt256 Amount { get; init; }
}

[BasaltEvent]
public sealed class SharesListedEvent
{
    [Indexed] public ulong ListingId { get; init; }
    [Indexed] public byte[] Seller { get; init; } = [];
    public ulong ContentId { get; init; }
    public UInt256 BpsForSale { get; init; }
    public UInt256 AskingPrice { get; init; }
}

[BasaltEvent]
public sealed class SharesBoughtEvent
{
    [Indexed] public ulong ListingId { get; init; }
    [Indexed] public byte[] Buyer { get; init; } = [];
    public ulong NewTokenId { get; init; }
    public UInt256 BpsBought { get; init; }
    public UInt256 Price { get; init; }
    public UInt256 ArtistFee { get; init; }
}
```

## Complexity

**Medium** -- The contract has three main subsystems (catalog management, revenue distribution, secondary market) that are individually straightforward. Revenue distribution follows the same proportional pattern as the real estate contract. The secondary market is a basic listing/purchase model. The main complexity comes from the artist reserve enforcement, secondary sale fee routing, and oracle-based revenue reporting. Cross-contract calls to IssuerRegistry add integration complexity but the state machine is simpler than bonds or invoice factoring (no default states, no dispute resolution, no maturity mechanics).

## Priority

**P2** -- Music royalty tokenization is a compelling creator economy use case with growing market interest (Royal.io, Anotherblock). It effectively demonstrates BST-3525's value proposition in a consumer-friendly context that resonates with both artists and fans. Lower priority than the core RWA instruments (bonds, real estate, invoices, carbon credits) because the music industry's royalty infrastructure is complex to integrate with (collecting societies, DSPs), and the total addressable market for tokenized music royalties is smaller than fixed income or real estate.
