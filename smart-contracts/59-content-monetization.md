# Content Monetization / Patreon-style

## Category

Creator Economy / Subscription Infrastructure

## Summary

A decentralized content monetization platform that enables creators to publish content behind paywalls, manage subscriber tiers with streaming payments, and issue access tokens as BST-721 NFTs. The contract supports revenue sharing among collaborators, exclusive content gating verified through on-chain access tokens, and flexible subscription models ranging from one-time purchases to monthly streaming payments, creating a censorship-resistant alternative to centralized platforms like Patreon.

## Why It's Useful

- **Market need**: Content creators on centralized platforms face deplatforming risk, opaque algorithm changes affecting visibility, high platform fees (often 10-20%), and delayed payment processing. A decentralized alternative eliminates these risks.
- **User benefit**: Creators receive payments directly with minimal fees and no risk of account suspension. Subscribers get verifiable access rights as on-chain tokens that cannot be arbitrarily revoked.
- **Economic empowerment**: Revenue sharing is enforced by smart contract logic, not by platform goodwill. Collaborators receive their agreed-upon share automatically and immediately.
- **Content permanence**: Content hashes stored on-chain create a permanent, verifiable record of publication. Even if the content host disappears, the proof of creation persists.
- **Portability**: Access tokens are standard BST-721 NFTs. If a creator migrates platforms, subscribers' access rights travel with them.

## Key Features

- **Creator profiles**: Creators register with content categories, description, and payout address. Profile linked to BNS name for discoverability.
- **Subscription tiers**: Multiple tiers per creator (e.g., Free, Basic, Premium, VIP) with different price points and content access levels.
- **Streaming payments**: Subscribers lock funds that stream to the creator in real-time (per-block or per-epoch distribution). No more monthly billing cycles -- payment flows continuously.
- **One-time purchases**: Individual content pieces can be sold as one-time purchases without requiring a subscription.
- **Access tokens (BST-721)**: Subscriptions mint BST-721 tokens that serve as access credentials. dApps and content hosts check token ownership to gate content.
- **Content publishing**: Creators publish content hashes (BLAKE3 of the actual content stored on IPFS/Arweave) with metadata (title, description, tier requirement, publication date).
- **Revenue sharing**: Creators can add collaborators with percentage-based revenue splits. Payments are automatically divided on receipt.
- **Exclusive content gating**: Content is tagged with a minimum tier requirement. Access verification checks that the requesting user holds an access token at or above the required tier.
- **Refund policy**: Configurable per-creator refund window. Subscribers can cancel and receive a pro-rata refund for unused time within the window.
- **Creator analytics**: On-chain metrics for subscriber count, revenue per tier, churn rate (cancellations), and content engagement (number of access token verifications).
- **Tip integration**: Direct integration with the Tipping contract (contract 58) for ad-hoc supporter payments.

## Basalt-Specific Advantages

- **BST-721 access tokens**: Subscription access tokens are standard BST-721 NFTs on Basalt, immediately recognizable by wallets, explorers, and other contracts. Unlike centralized API keys, these tokens are self-sovereign and can be verified by any party without a central authority.
- **BNS discoverability**: Creators are discoverable through their BNS names. Subscribing to "alice.basalt" is more intuitive than interacting with a raw contract address.
- **Streaming payments via block-level accounting**: Basalt's deterministic block times enable precise per-block streaming payments. Creators earn continuously rather than in monthly lump sums.
- **ZK compliance for regulated content**: Content platforms in regulated jurisdictions can require BST-VC age verification credentials before issuing subscription tokens, using the ZK compliance layer to verify without exposing personal data.
- **Confidential subscription amounts**: Pedersen commitments can hide subscription payment amounts, allowing creators to offer private pricing tiers where the subscription cost is not publicly visible.
- **AOT-compiled streaming calculations**: Per-block payment streaming requires efficient arithmetic for computing accrued payments, remaining balances, and refund amounts. AOT compilation ensures these calculations are gas-efficient.
- **Escrow for prepaid subscriptions**: Subscriber funds are held in the Escrow system contract, providing trustless prepayment that releases to creators proportionally over the subscription period.
- **Ed25519 content signing**: Creators can sign content hashes with their Ed25519 key to prove authorship, creating a verifiable publication record.

## Token Standards Used

- **BST-721**: Access tokens representing active subscriptions. Each token encodes the tier level and expiration in its metadata.
- **BST-20**: Payment tokens for subscriptions and one-time purchases.
- **BST-VC**: Age verification and jurisdiction compliance credentials for regulated content access.

## Integration Points

- **BNS (0x...1002)**: Creator discoverability and subscriber-facing identity. BNS name resolution for subscription commands.
- **Escrow (0x...1004)**: Prepaid subscription funds held in escrow with time-based release to creators.
- **Governance (0x...1003)**: Community governance over platform fee rates and content moderation policies.
- **SchemaRegistry (0x...1006)**: Credential schemas for content access requirements (age verification, jurisdiction compliance).
- **IssuerRegistry (0x...1007)**: Trusted issuers for access-gating credentials.
- **Tipping (contract 58)**: Complementary ad-hoc payments to creators beyond subscription fees.
- **Social Profile (contract 57)**: Creator profiles linked to social identity. Subscriber counts displayed on profiles.

## Technical Sketch

```csharp
// ---- Data Structures ----

public struct CreatorProfile
{
    public Address Creator;
    public string BnsName;
    public string DisplayName;
    public string Description;
    public string Category;
    public ulong TierCount;
    public ulong SubscriberCount;
    public UInt256 TotalRevenue;
    public ulong CreatedAt;
    public bool Active;
}

public struct SubscriptionTier
{
    public ulong TierId;
    public Address Creator;
    public string Name;
    public string Description;
    public UInt256 PricePerEpoch;         // Cost per epoch (e.g., per month)
    public ulong EpochDurationSeconds;    // Length of one billing epoch
    public ulong AccessLevel;             // Higher = more content access
    public ulong MaxSubscribers;          // 0 = unlimited
    public ulong CurrentSubscribers;
    public bool Active;
}

public struct Subscription
{
    public ulong SubscriptionId;
    public Address Subscriber;
    public Address Creator;
    public ulong TierId;
    public ulong AccessTokenId;           // BST-721 token ID
    public ulong StartTime;
    public ulong PaidUntil;               // Timestamp up to which payment covers
    public UInt256 DepositedBalance;       // Remaining prepaid balance
    public UInt256 StreamedAmount;         // Amount already streamed to creator
    public bool Active;
}

public struct ContentEntry
{
    public ulong ContentId;
    public Address Creator;
    public string Title;
    public string Description;
    public byte[] ContentHash;             // BLAKE3 hash of content
    public ulong MinAccessLevel;           // Minimum tier access level
    public UInt256 OneTimePrice;           // 0 = subscription only
    public ulong PublishedAt;
}

public struct Collaborator
{
    public Address Creator;
    public Address Collaborator_;
    public ulong ShareBps;                 // Basis points share of revenue
}

public struct OneTimePurchase
{
    public ulong PurchaseId;
    public Address Buyer;
    public ulong ContentId;
    public UInt256 Price;
    public ulong PurchasedAt;
    public ulong AccessTokenId;            // BST-721 token for access
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0208)]
public partial class ContentMonetization : SdkContractBase
{
    // Storage
    private StorageMap<Address, CreatorProfile> _creators;
    private StorageMap<ulong, SubscriptionTier> _tiers;
    private StorageMap<ulong, Subscription> _subscriptions;
    private StorageMap<ulong, ContentEntry> _content;
    private StorageMap<ulong, OneTimePurchase> _purchases;
    private StorageValue<ulong> _nextTierId;
    private StorageValue<ulong> _nextSubscriptionId;
    private StorageValue<ulong> _nextContentId;
    private StorageValue<ulong> _nextPurchaseId;
    private StorageValue<ulong> _nextTokenId;
    private StorageValue<ulong> _platformFeeBps;          // Default 200 = 2%
    private StorageValue<Address> _treasury;

    // Composite key storage:
    // Collaborator keyed by BLAKE3(creatorAddress || collaboratorAddress)
    // Access verification keyed by BLAKE3(userAddress || contentId) -> bool

    // --- Creator Management ---

    /// <summary>
    /// Register as a content creator. Caller must own the specified BNS name.
    /// </summary>
    public void RegisterCreator(
        string bnsName,
        string displayName,
        string description,
        string category
    );

    /// <summary>
    /// Update creator profile metadata.
    /// </summary>
    public void UpdateCreatorProfile(
        string displayName,
        string description,
        string category
    );

    /// <summary>
    /// Deactivate a creator profile. Existing subscriptions continue
    /// until expiry but no new subscriptions are accepted.
    /// </summary>
    public void DeactivateCreator();

    // --- Tier Management ---

    /// <summary>
    /// Create a subscription tier with pricing and access level.
    /// </summary>
    public ulong CreateTier(
        string name,
        string description,
        UInt256 pricePerEpoch,
        ulong epochDurationSeconds,
        ulong accessLevel,
        ulong maxSubscribers
    );

    /// <summary>
    /// Update tier pricing. Changes apply only to new subscriptions
    /// and renewals, not to active prepaid subscriptions.
    /// </summary>
    public void UpdateTierPrice(ulong tierId, UInt256 newPricePerEpoch);

    /// <summary>
    /// Deactivate a tier. Existing subscriptions continue.
    /// </summary>
    public void DeactivateTier(ulong tierId);

    // --- Revenue Sharing ---

    /// <summary>
    /// Add a collaborator with a revenue share.
    /// Total collaborator shares must not exceed 90% (creator retains minimum 10%).
    /// </summary>
    public void AddCollaborator(Address collaborator, ulong shareBps);

    /// <summary>
    /// Remove a collaborator. Their share returns to the creator.
    /// </summary>
    public void RemoveCollaborator(Address collaborator);

    // --- Subscriptions ---

    /// <summary>
    /// Subscribe to a creator's tier. Caller deposits prepaid funds
    /// as tx value. A BST-721 access token is minted to the subscriber.
    /// Streaming begins immediately.
    /// </summary>
    public ulong Subscribe(ulong tierId, ulong epochCount);

    /// <summary>
    /// Renew a subscription by depositing additional prepaid funds.
    /// Extends the paidUntil timestamp.
    /// </summary>
    public void RenewSubscription(ulong subscriptionId, ulong additionalEpochs);

    /// <summary>
    /// Cancel a subscription. Pro-rata refund of unused prepaid balance.
    /// Access token remains valid until paidUntil timestamp.
    /// </summary>
    public void CancelSubscription(ulong subscriptionId);

    /// <summary>
    /// Upgrade a subscription to a higher tier. Price difference
    /// is charged pro-rata for the remaining period.
    /// </summary>
    public void UpgradeTier(ulong subscriptionId, ulong newTierId);

    // --- Streaming Payment Settlement ---

    /// <summary>
    /// Settle streamed payments for a subscription. Callable by anyone.
    /// Calculates the accrued amount since last settlement and transfers
    /// it to the creator (minus platform fee), split among collaborators.
    /// </summary>
    public UInt256 SettlePayment(ulong subscriptionId);

    /// <summary>
    /// Batch settle payments for all active subscriptions of a creator.
    /// </summary>
    public UInt256 BatchSettlePayments(Address creator);

    // --- Content Publishing ---

    /// <summary>
    /// Publish a content entry with access level gating.
    /// Content hash is the BLAKE3 hash of the off-chain content.
    /// </summary>
    public ulong PublishContent(
        string title,
        string description,
        byte[] contentHash,
        ulong minAccessLevel,
        UInt256 oneTimePrice
    );

    /// <summary>
    /// Purchase one-time access to a specific content piece.
    /// Mints a BST-721 access token for the content.
    /// </summary>
    public ulong PurchaseContent(ulong contentId);

    // --- Access Verification ---

    /// <summary>
    /// Check if a user has access to specific content.
    /// Verifies subscription tier access level or one-time purchase.
    /// </summary>
    public bool HasAccess(Address user, ulong contentId);

    /// <summary>
    /// Verify that a BST-721 token grants access to specific content.
    /// Used by off-chain content hosts to gate access.
    /// </summary>
    public bool VerifyAccessToken(ulong tokenId, ulong contentId);

    // --- View Functions ---

    public CreatorProfile GetCreatorProfile(Address creator);
    public SubscriptionTier GetTier(ulong tierId);
    public Subscription GetSubscription(ulong subscriptionId);
    public ContentEntry GetContent(ulong contentId);
    public ulong GetSubscriberCount(Address creator);
    public UInt256 GetPendingSettlement(ulong subscriptionId);
    public UInt256 GetCreatorRevenue(Address creator);

    /// <summary>
    /// Calculate the pro-rata refund amount for a cancellation.
    /// </summary>
    public UInt256 CalculateRefund(ulong subscriptionId);

    // --- Admin (Governance) ---

    public void SetPlatformFee(ulong basisPoints);
    public void SetTreasury(Address newTreasury);
}
```

## Complexity

**High** -- The contract manages multiple interacting systems: creator profiles with tier configurations, streaming payment calculations requiring precise time-based arithmetic, revenue splitting among collaborators with rounding considerations, access token lifecycle management (mint on subscribe, verify on access, handle on cancellation), one-time purchase parallel access path, and subscription upgrades with pro-rata pricing. The streaming settlement mechanism must handle edge cases around partial epochs, cancellations mid-stream, and tier price changes.

## Priority

**P1** -- Content monetization is a major use case for blockchain technology, and the creator economy is a significant market. The contract provides immediate utility for any content creator in the Basalt ecosystem and drives regular economic activity through subscription payments. It also demonstrates Basalt's capability for real-world applications beyond DeFi.
