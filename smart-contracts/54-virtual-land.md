# Virtual Land Registry

## Category

Metaverse Infrastructure / Digital Real Estate

## Summary

A coordinate-based virtual land registry that represents parcels as BST-721 tokens in a two-dimensional grid world. The contract supports parcel ownership, merging adjacent parcels into larger plots, splitting plots back into individual parcels, building permits stored as on-chain metadata, a rental system for temporary use rights, neighbor discovery for social features, and a land value tax mechanism that discourages speculative hoarding by requiring periodic tax payments proportional to the self-assessed land value.

## Why It's Useful

- **Market need**: Virtual worlds and metaverse applications require trustless land ownership with clear property rights. Traditional centralized metaverse platforms can revoke land arbitrarily; blockchain-based registries provide permanent, censorship-resistant ownership.
- **User benefit**: Landowners have verifiable, transferable ownership. The rental system allows passive income from virtual property. The tax mechanism prevents large-scale land hoarding that makes parcels unavailable to new participants.
- **Economic design**: Harberger-style taxation creates a healthy land market where prices reflect actual utility rather than speculative holding. Merging and splitting enable flexible land management for varying project sizes.
- **Developer benefit**: Game and metaverse developers build on a shared land standard rather than creating proprietary systems, enabling cross-application interoperability (a building in one game visible in another).
- **Community value**: Neighbor discovery and social features create organic communities around geographic clusters in the virtual world.

## Key Features

- **Grid-based coordinate system**: Parcels identified by (x, y) coordinates in a bounded world grid. Each coordinate maps to exactly one BST-721 token.
- **Parcel minting**: Admin (world creator) mints parcels in batches, defining world boundaries and initial pricing.
- **Land merging**: Adjacent parcels owned by the same address can be merged into a single plot token. The plot stores the bounding rectangle of constituent parcels.
- **Land splitting**: Plots can be split back into individual parcels, restoring the original BST-721 tokens.
- **Building permits**: Landowners can set metadata (building type, height, description, content hash) on their parcels. Metadata is stored on-chain and queryable.
- **Rental system**: Landowners can list parcels for rent at a specified rate per epoch. Renters gain temporary use rights (building permits, content placement) without ownership transfer.
- **Neighbor discovery**: Query adjacent parcels to find neighboring landowners. Supports social features like neighborhood groups and community governance.
- **Land value tax (Harberger tax)**: Owners self-assess their land value and pay a periodic tax proportional to that value. Any buyer can purchase the land at the self-assessed price, incentivizing honest valuation. Tax revenue goes to the world treasury.
- **Zoning**: Admin can designate zones (residential, commercial, entertainment) with different tax rates and building restrictions.
- **Transfer restrictions**: Optional cooldown period after purchase before resale, preventing rapid speculative flipping.

## Basalt-Specific Advantages

- **BST-721 native tokens**: Each parcel is a first-class BST-721 token, immediately tradeable on the NFT Marketplace (contract 51) and recognizable by any Basalt wallet or explorer.
- **BNS land naming**: Parcels can be linked to BNS names (e.g., "downtown.basaltworld"), enabling human-readable land references and discovery.
- **Escrow-based rentals**: Rental payments leverage the Escrow system contract for trustless prepayment and automatic release to landowners at the end of each rental period.
- **ZK compliance for real-estate tokens**: If virtual land carries real-world regulatory implications (e.g., tokenized real estate), the ZK compliance layer can enforce KYC requirements on buyers without revealing personal data.
- **AOT performance for spatial queries**: Neighbor discovery and merge/split validation require coordinate arithmetic and adjacency checks that benefit from AOT-compiled native performance.
- **Governance-controlled world parameters**: Tax rates, world boundaries, and zoning rules can be controlled by Governance proposals, enabling community-driven world evolution.
- **BST-3525 for fractional land ownership**: Large plots can be represented as BST-3525 tokens where the slot represents the plot and the value represents ownership shares, enabling fractional real-estate investment.

## Token Standards Used

- **BST-721**: Primary standard for individual parcel and plot tokens.
- **BST-3525**: Optional fractional ownership of large plots.
- **BST-20**: Payment for purchases, rentals, and tax payments.

## Integration Points

- **BNS (0x...1002)**: Human-readable names for parcels and plots. Reverse lookup from BNS name to parcel coordinates.
- **Escrow (0x...1004)**: Rental payment escrow with automatic release schedule. Purchase escrow for Harberger tax forced sales.
- **Governance (0x...1003)**: Community governance over world parameters (tax rates, zone definitions, world boundary expansion).
- **SchemaRegistry (0x...1006)**: Credential schemas for KYC-gated land zones.
- **IssuerRegistry (0x...1007)**: Trusted issuers for land-zone compliance credentials.
- **NFT Marketplace (contract 51)**: Direct trading of land parcels and plots.

## Technical Sketch

```csharp
// ---- Data Structures ----

public struct Coordinate
{
    public int X;
    public int Y;
}

public enum ZoneType : byte
{
    Unzoned = 0,
    Residential = 1,
    Commercial = 2,
    Entertainment = 3,
    Industrial = 4,
    Protected = 5              // Cannot be built on
}

public struct Parcel
{
    public ulong TokenId;
    public int X;
    public int Y;
    public Address Owner;
    public ulong PlotId;              // 0 if standalone, otherwise merged plot ID
    public ZoneType Zone;
}

public struct Plot
{
    public ulong PlotId;
    public Address Owner;
    public int MinX;
    public int MinY;
    public int MaxX;
    public int MaxY;
    public ulong ParcelCount;
}

public struct BuildingPermit
{
    public ulong TokenId;             // Parcel or Plot token ID
    public string BuildingType;
    public ulong Height;
    public string Description;
    public byte[] ContentHash;        // BLAKE3 hash of off-chain content
    public ulong IssuedAt;
}

public struct RentalListing
{
    public ulong TokenId;
    public Address Landlord;
    public UInt256 RatePerEpoch;
    public ulong MinEpochs;
    public ulong MaxEpochs;
    public bool Active;
}

public struct RentalAgreement
{
    public ulong RentalId;
    public ulong TokenId;
    public Address Tenant;
    public Address Landlord;
    public UInt256 RatePerEpoch;
    public ulong StartEpoch;
    public ulong EndEpoch;
}

public struct TaxAssessment
{
    public ulong TokenId;
    public UInt256 SelfAssessedValue;
    public ulong LastTaxPaidEpoch;
    public UInt256 AccruedTaxOwed;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0203)]
public partial class VirtualLandRegistry : SdkContractBase
{
    // Storage
    private StorageMap<ulong, Parcel> _parcels;            // tokenId -> Parcel
    private StorageMap<ulong, Plot> _plots;                // plotId -> Plot
    private StorageMap<ulong, BuildingPermit> _permits;    // tokenId -> BuildingPermit
    private StorageMap<ulong, RentalListing> _rentalListings;
    private StorageMap<ulong, RentalAgreement> _rentals;
    private StorageMap<ulong, TaxAssessment> _taxAssessments;
    private StorageValue<ulong> _nextTokenId;
    private StorageValue<ulong> _nextPlotId;
    private StorageValue<ulong> _nextRentalId;
    private StorageValue<Address> _worldAdmin;
    private StorageValue<Address> _treasury;
    private StorageValue<int> _worldMinX;
    private StorageValue<int> _worldMaxX;
    private StorageValue<int> _worldMinY;
    private StorageValue<int> _worldMaxY;
    private StorageValue<ulong> _taxRateBps;               // Basis points per epoch

    // --- World Management (Admin) ---

    /// <summary>
    /// Set the world boundaries. Only callable by world admin.
    /// </summary>
    public void SetWorldBounds(int minX, int maxX, int minY, int maxY);

    /// <summary>
    /// Mint a batch of parcels within the world bounds.
    /// Each coordinate becomes a BST-721 token.
    /// </summary>
    public ulong[] MintParcels(int[] xCoords, int[] yCoords);

    /// <summary>
    /// Set the zone type for a coordinate range.
    /// </summary>
    public void SetZone(int minX, int maxX, int minY, int maxY, ZoneType zone);

    /// <summary>
    /// Update the tax rate (basis points per epoch).
    /// </summary>
    public void SetTaxRate(ulong basisPoints);

    // --- Ownership and Transfer ---

    /// <summary>
    /// Purchase an unowned parcel at the initial minting price.
    /// Caller sends payment as tx value.
    /// </summary>
    public void PurchaseParcel(ulong tokenId);

    /// <summary>
    /// Force-purchase a parcel at its self-assessed value (Harberger tax).
    /// Caller must send the self-assessed value. Current owner receives payment.
    /// Only works if the owner has a tax assessment on file.
    /// </summary>
    public void HarbergerPurchase(ulong tokenId);

    // --- Merging and Splitting ---

    /// <summary>
    /// Merge adjacent parcels into a single plot. All parcels must be
    /// owned by the caller and form a contiguous rectangular region.
    /// Returns the new plot token ID.
    /// </summary>
    public ulong MergeParcels(ulong[] parcelTokenIds);

    /// <summary>
    /// Split a plot back into individual parcels.
    /// Restores all constituent parcel tokens to the caller.
    /// </summary>
    public void SplitPlot(ulong plotId);

    // --- Building Permits ---

    /// <summary>
    /// Set or update the building permit for a parcel or plot.
    /// Only callable by owner or current renter.
    /// Building type must comply with zone restrictions.
    /// </summary>
    public void SetBuildingPermit(
        ulong tokenId,
        string buildingType,
        ulong height,
        string description,
        byte[] contentHash
    );

    /// <summary>
    /// Clear the building permit for a parcel.
    /// </summary>
    public void ClearBuildingPermit(ulong tokenId);

    // --- Rental System ---

    /// <summary>
    /// List a parcel or plot for rent.
    /// </summary>
    public void ListForRent(
        ulong tokenId,
        UInt256 ratePerEpoch,
        ulong minEpochs,
        ulong maxEpochs
    );

    /// <summary>
    /// Rent a listed parcel. Caller must send the full prepayment
    /// (rate * epochs) which is held in escrow.
    /// </summary>
    public ulong RentParcel(ulong tokenId, ulong epochs);

    /// <summary>
    /// End a rental agreement (callable after endEpoch).
    /// Returns use rights to the landlord.
    /// </summary>
    public void EndRental(ulong rentalId);

    /// <summary>
    /// Evict a tenant who has violated building restrictions.
    /// Only callable by world admin. Remaining prepayment refunded.
    /// </summary>
    public void Evict(ulong rentalId);

    // --- Tax System ---

    /// <summary>
    /// Set or update the self-assessed value for a parcel.
    /// Higher values mean higher tax but protection from Harberger purchase.
    /// Lower values mean lower tax but vulnerability to forced purchase.
    /// </summary>
    public void AssessValue(ulong tokenId, UInt256 selfAssessedValue);

    /// <summary>
    /// Pay accrued tax on a parcel. Caller sends tax payment as tx value.
    /// </summary>
    public void PayTax(ulong tokenId);

    /// <summary>
    /// Seize a parcel with delinquent tax (unpaid for > threshold epochs).
    /// Parcel is returned to the world treasury for resale.
    /// </summary>
    public void SeizeDelinquent(ulong tokenId);

    // --- Queries ---

    public Parcel GetParcel(ulong tokenId);
    public Plot GetPlot(ulong plotId);
    public BuildingPermit GetBuildingPermit(ulong tokenId);
    public RentalListing GetRentalListing(ulong tokenId);
    public RentalAgreement GetRental(ulong rentalId);
    public TaxAssessment GetTaxAssessment(ulong tokenId);

    /// <summary>
    /// Get the parcel token ID at a specific coordinate.
    /// Returns 0 if no parcel exists at that coordinate.
    /// </summary>
    public ulong GetParcelAt(int x, int y);

    /// <summary>
    /// Get the owners of all four neighboring parcels
    /// (north, south, east, west).
    /// </summary>
    public Address[] GetNeighbors(int x, int y);

    /// <summary>
    /// Get all parcels owned by an address within a bounding box.
    /// </summary>
    public ulong[] GetOwnedParcelsInArea(
        Address owner,
        int minX, int maxX,
        int minY, int maxY
    );

    /// <summary>
    /// Calculate the current tax owed on a parcel.
    /// </summary>
    public UInt256 CalculateTaxOwed(ulong tokenId);
}
```

## Complexity

**High** -- The contract combines several complex subsystems: coordinate-based spatial logic (adjacency validation for merging, rectangular contiguity checks), Harberger taxation with continuous accrual calculations, a rental system with escrow integration, zoning enforcement, and neighbor discovery. Merge validation must ensure all parcels form a valid rectangle with no gaps. Tax seizure and forced purchase create edge cases around concurrent rentals and building permits that must be handled gracefully.

## Priority

**P2** -- Virtual land registries become important once the metaverse/gaming ecosystem matures on Basalt. The contract is not needed for core DeFi or infrastructure but is a significant differentiator for attracting metaverse developers and establishing Basalt as a platform for virtual worlds.
