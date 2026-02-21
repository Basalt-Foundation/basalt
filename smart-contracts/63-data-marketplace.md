# Data Marketplace

## Category

Data Economy / Information Exchange

## Summary

A decentralized marketplace for buying and selling access to datasets where data hashes are stored on-chain for integrity verification while actual data is hosted off-chain on decentralized storage (IPFS, Arweave). Purchases trigger delivery of encrypted viewing keys using Basalt's ViewingKey primitive, enabling buyers to decrypt the dataset. The platform supports ZK proofs of data quality (statistical properties verified without revealing data), a review and reputation system, and configurable licensing terms for different usage rights.

## Why It's Useful

- **Market need**: Data is a critical economic asset, but data markets suffer from the "inspection paradox" -- buyers cannot verify data quality before purchase. ZK proofs solve this by proving data properties without revealing the data itself.
- **User benefit**: Data sellers monetize their datasets with guaranteed payment through escrow. Data buyers verify data quality through ZK proofs before committing funds. Both parties benefit from on-chain reputation and review systems.
- **Economic value**: Data marketplaces unlock value from datasets that are currently siloed. Machine learning training data, market research, IoT sensor data, and analytics datasets can all be traded trustlessly.
- **Privacy preservation**: The combination of off-chain storage with on-chain viewing keys ensures data is only accessible to authorized buyers. ZK quality proofs enable verification without exposure.
- **Dispute resolution**: On-chain reviews and quality proofs provide evidence for dispute resolution. If data does not match its advertised properties, buyers have verifiable grounds for refunds.

## Key Features

- **Dataset listing**: Sellers list datasets with metadata (title, description, category, schema description, record count, sample hash) and pricing.
- **Data integrity verification**: BLAKE3 hash of the complete dataset stored on-chain. Buyers can verify downloaded data matches the listed hash.
- **Viewing key delivery**: Upon purchase, the seller's encrypted viewing key is delivered to the buyer. The viewing key decrypts the dataset stored on IPFS/Arweave.
- **ZK quality proofs**: Sellers submit ZK proofs attesting to data properties (e.g., "this dataset contains at least 10,000 records", "all records have valid email format", "statistical mean of column X is within range Y"). Groth16 verification on Basalt validates these proofs.
- **Licensing tiers**: Multiple licensing options per dataset (personal use, commercial use, redistribution rights) with different pricing.
- **Subscription access**: Recurring access for continuously updated datasets (e.g., real-time market data, daily sensor readings).
- **Review system**: Buyers rate datasets on quality, accuracy, and description accuracy. Reviews are weighted by purchase amount.
- **Dispute resolution**: Buyers can dispute purchases within a configurable window. Disputes are resolved by staked arbitrators or escalated to governance.
- **Data provider reputation**: On-chain reputation scores based on sales volume, review ratings, and dispute outcomes.
- **Sample data**: Sellers can provide a free sample (separate hash and viewing key) for preview before purchase.
- **Bundled datasets**: Sellers can bundle multiple datasets at a discounted price.
- **Update notifications**: Sellers can publish dataset updates with new hashes. Subscribers with active access receive the updated viewing key.

## Basalt-Specific Advantages

- **ViewingKey primitive**: Basalt's native ViewingKey mechanism provides a standardized way to deliver decryption keys to authorized parties. The key is encrypted to the buyer's encryption public key (from the Messaging Registry, contract 62) and stored on-chain for retrieval.
- **Groth16 ZK verification**: Basalt's ZK compliance layer includes Groth16 proof verification, enabling data quality proofs to be verified on-chain efficiently. Sellers can prove statistical properties of their data without revealing the data itself.
- **BLAKE3 data integrity**: Dataset integrity hashes use BLAKE3, Basalt's native hash function, for fast computation and verification. Buyers can verify multi-gigabyte datasets efficiently.
- **Escrow-backed purchases**: Payment is held in the Escrow system contract (0x...1004) during the dispute window, protecting both buyers and sellers.
- **BNS data provider identity**: Data providers are identified by BNS names, building branded data businesses (e.g., "marketdata.basalt", "weatherapi.basalt").
- **BST-VC provider credentials**: Data providers can attach BST-VC verifiable credentials proving their data sourcing methodology, certification, or professional qualifications, verified through SchemaRegistry and IssuerRegistry.
- **Confidential pricing via Pedersen commitments**: For sensitive data categories, pricing can be kept confidential using Pedersen commitments. The price is revealed only to the buyer during the purchase flow.
- **AOT-compiled proof verification**: Groth16 proof verification executes at native speed, critical for a marketplace that may process many quality proof verifications per block.

## Token Standards Used

- **BST-20**: Payment for dataset purchases and subscriptions.
- **BST-721**: Optional access tokens representing data licenses (tradeable or non-transferable depending on licensing terms).
- **BST-VC**: Data provider professional credentials and data certification attestations.

## Integration Points

- **Escrow (0x...1004)**: Purchase funds held in escrow during dispute windows. Subscription prepayments managed through escrow.
- **BNS (0x...1002)**: Data provider branding and discovery through BNS names.
- **Governance (0x...1003)**: Final dispute resolution for contested data quality claims. Platform parameter governance.
- **SchemaRegistry (0x...1006)**: Data quality proof schemas and provider credential schemas.
- **IssuerRegistry (0x...1007)**: Trusted issuers for data provider credentials.
- **Messaging Registry (contract 62)**: Buyer encryption public key discovery for viewing key delivery.
- **Social Profile (contract 57)**: Data provider reputation displayed on social profiles.
- **StakingPool (0x...1005)**: Arbitrator staking for dispute resolution.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum LicenseType : byte
{
    PersonalUse = 0,
    CommercialUse = 1,
    Redistribution = 2,
    Exclusive = 3
}

public enum DataCategory : byte
{
    Finance = 0,
    Healthcare = 1,
    IoT = 2,
    Social = 3,
    Research = 4,
    Government = 5,
    Commerce = 6,
    Other = 255
}

public struct DatasetListing
{
    public ulong DatasetId;
    public Address Seller;
    public string Title;
    public string Description;
    public DataCategory Category;
    public byte[] DataHash;                  // BLAKE3 hash of complete dataset
    public string StorageUri;                // IPFS/Arweave URI
    public ulong RecordCount;
    public string SchemaDescription;
    public ulong CreatedAt;
    public ulong UpdatedAt;
    public ulong TotalSales;
    public UInt256 TotalRevenue;
    public bool Active;
}

public struct LicenseTier
{
    public ulong TierId;
    public ulong DatasetId;
    public LicenseType Type;
    public UInt256 Price;
    public ulong ValidityDays;              // 0 = perpetual
    public bool Transferable;
}

public struct DataPurchase
{
    public ulong PurchaseId;
    public ulong DatasetId;
    public ulong LicenseTierId;
    public Address Buyer;
    public UInt256 Price;
    public byte[] EncryptedViewingKey;       // Viewing key encrypted to buyer's public key
    public ulong PurchasedAt;
    public ulong ExpiresAt;
    public bool DisputeOpen;
    public bool Refunded;
}

public struct QualityProof
{
    public ulong ProofId;
    public ulong DatasetId;
    public string PropertyDescription;      // Human-readable description of proven property
    public byte[] Proof;                     // Groth16 proof bytes
    public byte[] PublicInputs;             // Public inputs for proof verification
    public ulong VerifiedAt;
    public bool Valid;
}

public struct DataReview
{
    public ulong ReviewId;
    public ulong DatasetId;
    public Address Reviewer;
    public ulong Rating;                    // 1-5
    public string Comment;
    public UInt256 PurchaseAmount;          // Weight for review aggregation
    public ulong Timestamp;
}

public struct SellerReputation
{
    public Address Seller;
    public ulong TotalSales;
    public UInt256 TotalRevenue;
    public ulong AverageRating;             // Scaled by 100 (e.g., 450 = 4.50)
    public ulong ReviewCount;
    public ulong DisputeCount;
    public ulong DisputesLost;
    public ulong ReputationScore;
}

public struct SubscriptionPlan
{
    public ulong PlanId;
    public ulong DatasetId;
    public UInt256 PricePerEpoch;
    public ulong EpochDuration;
    public LicenseType License;
    public bool Active;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x020C)]
public partial class DataMarketplace : SdkContractBase
{
    // Storage
    private StorageMap<ulong, DatasetListing> _datasets;
    private StorageMap<ulong, LicenseTier> _licenseTiers;
    private StorageMap<ulong, DataPurchase> _purchases;
    private StorageMap<ulong, QualityProof> _qualityProofs;
    private StorageMap<ulong, DataReview> _reviews;
    private StorageMap<Address, SellerReputation> _reputations;
    private StorageMap<ulong, SubscriptionPlan> _subscriptions;
    private StorageValue<ulong> _nextDatasetId;
    private StorageValue<ulong> _nextTierId;
    private StorageValue<ulong> _nextPurchaseId;
    private StorageValue<ulong> _nextProofId;
    private StorageValue<ulong> _nextReviewId;
    private StorageValue<ulong> _nextPlanId;
    private StorageValue<ulong> _platformFeeBps;          // Default 300 = 3%
    private StorageValue<ulong> _disputeWindowSeconds;    // Default 7 days
    private StorageValue<Address> _treasury;

    // --- Dataset Management ---

    /// <summary>
    /// List a dataset for sale. Seller provides metadata, data hash,
    /// and storage URI.
    /// </summary>
    public ulong ListDataset(
        string title,
        string description,
        DataCategory category,
        byte[] dataHash,
        string storageUri,
        ulong recordCount,
        string schemaDescription
    );

    /// <summary>
    /// Add a license tier to a dataset with pricing and terms.
    /// </summary>
    public ulong AddLicenseTier(
        ulong datasetId,
        LicenseType licenseType,
        UInt256 price,
        ulong validityDays,
        bool transferable
    );

    /// <summary>
    /// Update the dataset storage URI (e.g., after re-uploading).
    /// Data hash must remain the same unless explicitly updating.
    /// </summary>
    public void UpdateStorageUri(ulong datasetId, string newUri);

    /// <summary>
    /// Update a dataset with new data. Changes the data hash and
    /// storage URI. Existing subscribers receive updated access.
    /// </summary>
    public void UpdateDataset(
        ulong datasetId,
        byte[] newDataHash,
        string newStorageUri,
        ulong newRecordCount
    );

    /// <summary>
    /// Deactivate a dataset listing. Existing purchases remain valid.
    /// </summary>
    public void DeactivateDataset(ulong datasetId);

    // --- Quality Proofs ---

    /// <summary>
    /// Submit a ZK quality proof for a dataset. The proof attests
    /// to a specific property of the data (e.g., minimum record count,
    /// statistical distribution, format compliance).
    /// Proof is verified on-chain using Groth16 verification.
    /// </summary>
    public ulong SubmitQualityProof(
        ulong datasetId,
        string propertyDescription,
        byte[] proof,
        byte[] publicInputs
    );

    // --- Purchasing ---

    /// <summary>
    /// Purchase access to a dataset at a specific license tier.
    /// Caller sends payment as tx value. Funds are held in Escrow
    /// during the dispute window.
    ///
    /// The seller must deliver the viewing key within the delivery window.
    /// </summary>
    public ulong PurchaseDataset(ulong datasetId, ulong licenseTierId);

    /// <summary>
    /// Deliver the viewing key to a buyer. The viewing key is encrypted
    /// to the buyer's X25519 encryption public key (from Messaging Registry).
    /// Only callable by the dataset seller.
    /// </summary>
    public void DeliverViewingKey(ulong purchaseId, byte[] encryptedViewingKey);

    /// <summary>
    /// Confirm receipt of a valid dataset. Releases funds from escrow
    /// to the seller (minus platform fee). Only callable by buyer.
    /// </summary>
    public void ConfirmReceipt(ulong purchaseId);

    /// <summary>
    /// Auto-release funds after the dispute window expires without
    /// a dispute being filed. Callable by anyone.
    /// </summary>
    public void AutoRelease(ulong purchaseId);

    // --- Subscriptions ---

    /// <summary>
    /// Create a subscription plan for a dataset.
    /// </summary>
    public ulong CreateSubscriptionPlan(
        ulong datasetId,
        UInt256 pricePerEpoch,
        ulong epochDuration,
        LicenseType license
    );

    /// <summary>
    /// Subscribe to a dataset plan. Caller deposits prepaid funds.
    /// </summary>
    public ulong SubscribeToDataset(ulong planId, ulong epochs);

    /// <summary>
    /// Cancel a subscription. Pro-rata refund for unused epochs.
    /// </summary>
    public void CancelSubscription(ulong purchaseId);

    // --- Reviews ---

    /// <summary>
    /// Submit a review for a purchased dataset.
    /// Rating is 1-5. Only buyers can review. One review per purchase.
    /// Review weight = purchase amount.
    /// </summary>
    public ulong SubmitReview(
        ulong purchaseId,
        ulong rating,
        string comment
    );

    // --- Disputes ---

    /// <summary>
    /// Open a dispute for a purchase within the dispute window.
    /// Buyer provides evidence that the data does not match the listing.
    /// </summary>
    public void OpenDispute(ulong purchaseId, string reason, byte[] evidenceHash);

    /// <summary>
    /// Resolve a dispute as an arbitrator. Staked arbitrator reviews
    /// evidence and makes a binding decision.
    /// </summary>
    public void ResolveDispute(ulong purchaseId, bool refundBuyer, string reasoning);

    /// <summary>
    /// Escalate a dispute to governance.
    /// </summary>
    public void EscalateDispute(ulong purchaseId);

    // --- Sample Data ---

    /// <summary>
    /// Set a free sample for a dataset. Sample has its own hash
    /// and viewing key, available without purchase.
    /// </summary>
    public void SetSampleData(
        ulong datasetId,
        byte[] sampleHash,
        byte[] sampleViewingKey
    );

    // --- View Functions ---

    public DatasetListing GetDataset(ulong datasetId);
    public LicenseTier GetLicenseTier(ulong tierId);
    public DataPurchase GetPurchase(ulong purchaseId);
    public QualityProof GetQualityProof(ulong proofId);
    public DataReview GetReview(ulong reviewId);
    public SellerReputation GetSellerReputation(Address seller);
    public bool HasAccess(Address buyer, ulong datasetId);

    /// <summary>
    /// Get the weighted average rating for a dataset.
    /// Returns rating scaled by 100 (e.g., 385 = 3.85 stars).
    /// </summary>
    public ulong GetAverageRating(ulong datasetId);

    /// <summary>
    /// Verify a quality proof for a dataset.
    /// Returns true if the Groth16 proof is valid for the given public inputs.
    /// </summary>
    public bool VerifyQualityProof(ulong proofId);

    // --- Admin (Governance) ---

    public void SetPlatformFee(ulong basisPoints);
    public void SetDisputeWindow(ulong seconds);
    public void SetTreasury(Address newTreasury);

    /// <summary>
    /// Governance resolution of an escalated dispute.
    /// </summary>
    public void GovernanceResolveDispute(ulong purchaseId, bool refundBuyer);
}
```

## Complexity

**High** -- The contract combines multiple complex subsystems: dataset lifecycle management with multi-tier licensing, viewing key delivery requiring cross-contract encryption key lookup (Messaging Registry), Groth16 ZK proof verification for quality attestations, escrow-backed purchase flow with time-bounded dispute windows, subscription management with pro-rata refunds, weighted review aggregation, and multi-party dispute resolution. The viewing key delivery mechanism is particularly sensitive as it bridges on-chain payment with off-chain data access.

## Priority

**P2** -- Data marketplaces represent a significant long-term opportunity but require ecosystem maturity (established user base, messaging registry for key exchange, ZK proof infrastructure) to realize their full potential. The contract should be deployed once the foundational infrastructure (Messaging Registry, Escrow, ZK compliance) is well-established and data providers are ready to onboard.
