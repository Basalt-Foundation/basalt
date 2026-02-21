# NFT Marketplace

## Category

Digital Asset Exchange / NFT Infrastructure

## Summary

A fully on-chain NFT marketplace contract for listing, bidding, and purchasing BST-721 and BST-1155 tokens on the Basalt network. The contract supports English auctions, Dutch auctions, fixed-price listings, and collection-wide offers, with escrow-based settlement and automatic royalty enforcement on every secondary sale. A configurable protocol fee is directed to the network treasury.

## Why It's Useful

- **Market need**: NFT marketplaces are the primary infrastructure layer for any blockchain ecosystem with non-fungible token standards. Without a native, trustless marketplace, users must rely on off-chain order books or centralized platforms that introduce counterparty risk.
- **User benefit**: Creators receive guaranteed royalty payments on every resale without needing to trust the marketplace operator. Buyers and sellers benefit from escrow-based settlement that eliminates rug-pull risk during high-value trades.
- **Ecosystem growth**: A native marketplace drives adoption of BST-721 and BST-1155 standards, attracts creators and collectors, and generates protocol revenue through trading fees.
- **Composability**: Other contracts (gaming, virtual land, trading cards) can integrate marketplace functionality directly, enabling in-app trading experiences.

## Key Features

- **Fixed-price listings**: Sellers list NFTs at a set price; first buyer to pay claims the token.
- **English auctions**: Time-bound ascending-price auctions with minimum bid increments and reserve prices. Automatic extension on last-minute bids (anti-sniping).
- **Dutch auctions**: Price decreases linearly from a starting price to a floor price over a defined duration. First buyer to accept claims the token.
- **Collection offers**: Buyers place standing offers on any token in a collection, specifying the collection address and maximum price. Sellers can accept collection offers for any token they hold in that collection.
- **Royalty enforcement**: Reads royalty configuration from the NFT contract (creator address and basis points). Royalties are distributed before seller proceeds on every sale.
- **Escrow-based settlement**: Buyer funds are held in the contract until the trade is finalized. No direct peer-to-peer transfers that could be front-run.
- **Protocol fee**: Configurable fee (default 2.5%) sent to the protocol treasury on every completed sale.
- **Batch operations**: List multiple tokens in a single transaction. Cancel multiple listings atomically.
- **Activity tracking**: On-chain events for listings, sales, bids, cancellations, and offers. Enables indexing by the Explorer or external services.
- **Admin controls**: Governance-controlled fee adjustment, treasury address updates, and emergency pause functionality.

## Basalt-Specific Advantages

- **ZK compliance integration**: The marketplace can enforce compliance checks on buyers and sellers via the ComplianceEngine. Regulated collections (e.g., real-world asset NFTs) can require BST-VC credential verification before allowing trades, ensuring only KYC-verified participants transact in regulated asset classes.
- **BST-3525 SFT support**: Beyond standard BST-721 and BST-1155 tokens, the marketplace can natively handle BST-3525 semi-fungible tokens, enabling fractional NFT trading where a single token ID can have multiple units with differing values in the same slot.
- **Escrow system contract**: Settlement leverages Basalt's built-in Escrow system contract (0x...1004) for atomic fund locking, eliminating the need to reimplement escrow logic and inheriting battle-tested security.
- **BNS name resolution**: Sellers and buyers can be identified by their BNS names rather than raw addresses, improving the user experience in listing displays and activity feeds.
- **AOT-compiled performance**: As a native AOT-compiled SDK contract, the marketplace executes with near-native performance. Complex auction logic (bid validation, Dutch price calculation, royalty splits) runs without JIT overhead, critical for a high-throughput trading venue.
- **Ed25519 signature efficiency**: Off-chain order signing (for gasless listings) uses Ed25519 signatures, which are faster to verify than ECDSA, enabling lower gas costs for order validation.
- **EIP-1559 fee model**: Basalt's dynamic base fee ensures marketplace transactions are priced fairly even during high-activity periods (popular drops, auction endings), preventing fee spikes from making small trades uneconomical.

## Token Standards Used

- **BST-721**: Primary NFT standard for unique digital assets (art, collectibles, PFPs).
- **BST-1155**: Multi-token standard for semi-fungible assets (game items, editions, bundles).
- **BST-3525**: Semi-fungible token standard for fractional/structured NFTs.
- **BST-20**: Native fungible token standard for payment (BST and wrapped tokens).

## Integration Points

- **Escrow (0x...1004)**: Holds buyer funds during auction periods and fixed-price settlement windows. Releases funds to seller (minus royalties and fees) upon successful trade completion.
- **BNS (0x...1002)**: Resolves human-readable names for display in listings and activity feeds. Sellers can list NFTs under their BNS identity.
- **Governance (0x...1003)**: Protocol fee rates and treasury address are governed by on-chain proposals. Emergency pause can be triggered through governance action.
- **SchemaRegistry (0x...1006)**: Validates credential schemas when compliance-gated collections require buyer/seller verification.
- **IssuerRegistry (0x...1007)**: Verifies that credentials presented for compliance checks were issued by trusted authorities.
- **BridgeETH (0x...1008)**: Enables cross-chain NFT listings where payment can originate from bridged assets.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum ListingType : byte
{
    FixedPrice = 0,
    EnglishAuction = 1,
    DutchAuction = 2
}

public enum TokenStandard : byte
{
    BST721 = 0,
    BST1155 = 1,
    BST3525 = 2
}

// Stored per listing ID
public struct Listing
{
    public ulong ListingId;
    public Address Seller;
    public Address TokenContract;
    public ulong TokenId;
    public ulong Amount;             // 1 for BST-721, N for BST-1155/BST-3525
    public TokenStandard Standard;
    public ListingType Type;
    public UInt256 Price;            // Fixed price or starting price (English) or start price (Dutch)
    public UInt256 ReservePrice;     // Minimum price for English auctions
    public UInt256 FloorPrice;       // End price for Dutch auctions
    public ulong StartTime;
    public ulong EndTime;
    public bool Active;
}

public struct Bid
{
    public Address Bidder;
    public UInt256 Amount;
    public ulong Timestamp;
}

public struct CollectionOffer
{
    public ulong OfferId;
    public Address Buyer;
    public Address TokenContract;
    public UInt256 Price;
    public ulong Expiry;
    public bool Active;
}

public struct RoyaltyInfo
{
    public Address Recipient;
    public ulong BasisPoints;        // e.g., 500 = 5%
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0200)]
public partial class NftMarketplace : SdkContractBase
{
    // Storage maps
    private StorageMap<ulong, Listing> _listings;
    private StorageMap<ulong, Bid> _highestBids;
    private StorageMap<ulong, CollectionOffer> _collectionOffers;
    private StorageValue<ulong> _nextListingId;
    private StorageValue<ulong> _nextOfferId;
    private StorageValue<Address> _treasury;
    private StorageValue<ulong> _protocolFeeBps;      // Default 250 = 2.5%
    private StorageValue<bool> _paused;

    // --- Listing Management ---

    /// <summary>
    /// Create a fixed-price listing for a BST-721 token.
    /// Transfers the token into the marketplace contract for escrow.
    /// </summary>
    public ulong ListFixedPrice(
        Address tokenContract,
        ulong tokenId,
        UInt256 price,
        ulong durationSeconds
    );

    /// <summary>
    /// Create a fixed-price listing for BST-1155 tokens (supports quantity > 1).
    /// </summary>
    public ulong ListFixedPrice1155(
        Address tokenContract,
        ulong tokenId,
        ulong amount,
        UInt256 price,
        ulong durationSeconds
    );

    /// <summary>
    /// Create an English auction listing with reserve price and anti-sniping extension.
    /// </summary>
    public ulong ListEnglishAuction(
        Address tokenContract,
        ulong tokenId,
        UInt256 startingPrice,
        UInt256 reservePrice,
        ulong durationSeconds,
        ulong antiSnipeSeconds       // Extend auction if bid in last N seconds
    );

    /// <summary>
    /// Create a Dutch auction listing with linearly decreasing price.
    /// </summary>
    public ulong ListDutchAuction(
        Address tokenContract,
        ulong tokenId,
        UInt256 startPrice,
        UInt256 floorPrice,
        ulong durationSeconds
    );

    /// <summary>
    /// Cancel an active listing. Returns the token to the seller.
    /// English auctions cannot be cancelled if bids exist.
    /// </summary>
    public void CancelListing(ulong listingId);

    // --- Buying ---

    /// <summary>
    /// Purchase a fixed-price listing. Caller must send exact price as tx value.
    /// Distributes royalties, protocol fee, and seller proceeds atomically.
    /// </summary>
    public void BuyFixedPrice(ulong listingId);

    /// <summary>
    /// Purchase from a Dutch auction at the current price.
    /// Current price = startPrice - ((startPrice - floorPrice) * elapsed / duration).
    /// Caller must send at least the current price.
    /// </summary>
    public void BuyDutchAuction(ulong listingId);

    // --- Bidding (English Auctions) ---

    /// <summary>
    /// Place a bid on an English auction. Must exceed current highest bid
    /// by at least the minimum increment (1%). Previous highest bidder is refunded.
    /// </summary>
    public void PlaceBid(ulong listingId);

    /// <summary>
    /// Settle a completed English auction. Callable by anyone after end time.
    /// If reserve met: transfers token to winner, distributes funds.
    /// If reserve not met: returns token to seller, refunds highest bidder.
    /// </summary>
    public void SettleAuction(ulong listingId);

    // --- Collection Offers ---

    /// <summary>
    /// Place a standing offer for any token in a collection.
    /// Caller's funds are held in escrow until the offer is accepted or cancelled.
    /// </summary>
    public ulong PlaceCollectionOffer(
        Address tokenContract,
        UInt256 price,
        ulong expiryTimestamp
    );

    /// <summary>
    /// Accept a collection offer by transferring a specific token.
    /// Only the token owner can accept. Offer funds are released to seller.
    /// </summary>
    public void AcceptCollectionOffer(ulong offerId, ulong tokenId);

    /// <summary>
    /// Cancel a collection offer and reclaim escrowed funds.
    /// </summary>
    public void CancelCollectionOffer(ulong offerId);

    // --- Royalty Queries ---

    /// <summary>
    /// Query the royalty info for a given token contract.
    /// Reads from the token contract's on-chain royalty configuration.
    /// </summary>
    public RoyaltyInfo GetRoyaltyInfo(Address tokenContract, ulong tokenId);

    // --- View Functions ---

    public Listing GetListing(ulong listingId);
    public Bid GetHighestBid(ulong listingId);
    public CollectionOffer GetCollectionOffer(ulong offerId);
    public UInt256 GetCurrentDutchPrice(ulong listingId);
    public ulong GetProtocolFeeBps();
    public Address GetTreasury();

    // --- Admin (Governance-controlled) ---

    /// <summary>
    /// Update the protocol fee. Restricted to governance contract.
    /// Maximum 10% (1000 bps).
    /// </summary>
    public void SetProtocolFee(ulong basisPoints);

    /// <summary>
    /// Update the treasury address. Restricted to governance contract.
    /// </summary>
    public void SetTreasury(Address newTreasury);

    /// <summary>
    /// Emergency pause/unpause. Restricted to governance contract.
    /// When paused, no new listings or purchases can be made.
    /// Existing auctions can still be settled.
    /// </summary>
    public void SetPaused(bool paused);

    // --- Internal Helpers ---

    /// <summary>
    /// Distributes sale proceeds: royalties to creator, protocol fee to treasury,
    /// remainder to seller.
    /// </summary>
    private void DistributeProceeds(
        Address seller,
        Address tokenContract,
        ulong tokenId,
        UInt256 salePrice
    );
}
```

## Complexity

**High** -- The contract must handle three distinct auction mechanisms (fixed-price, English, Dutch), each with different state transitions and timing constraints. Royalty enforcement requires cross-contract calls to read token metadata. Escrow management for concurrent auctions and collection offers adds significant state management complexity. Anti-sniping logic, minimum bid increments, and reserve price handling require careful edge-case testing.

## Priority

**P0** -- An NFT marketplace is foundational infrastructure for any blockchain ecosystem. It is the primary venue for price discovery and liquidity for all non-fungible assets. Without it, BST-721, BST-1155, and BST-3525 tokens have no standardized trading mechanism, severely limiting adoption of the entire token standard stack.
