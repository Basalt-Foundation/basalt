# Decentralized Social Profile

## Category

Identity / Social Infrastructure

## Summary

An on-chain social profile contract that enables users to create rich, portable identity profiles linked to their BNS name. Profiles include avatar references (BST-721 NFTs), biographical information, social links, and a follow/follower graph stored entirely on-chain. Profiles can be verified through BST-VC credentials, creating a trustworthy, decentralized identity layer that travels with the user across all dApps in the Basalt ecosystem.

## Why It's Useful

- **Market need**: Decentralized applications lack a unified identity layer. Users must recreate their profiles on every platform, losing their social graph, reputation, and credentials each time. A shared on-chain profile solves this fragmentation.
- **User benefit**: Users maintain a single identity with their social graph, achievements, and verified credentials that any dApp can read. Changing platforms does not mean losing followers or reputation.
- **Developer benefit**: dApps can display rich user profiles (avatar, name, bio, verified credentials) without building their own profile infrastructure. Social features (follow recommendations, activity feeds) are built on shared data.
- **Trust and verification**: BST-VC credential integration enables verified profiles (real developer, verified business, KYC-passed) without centralized verification services. Users control what credentials they attach to their profile.
- **Ecosystem cohesion**: A shared social layer creates network effects across all Basalt dApps. Users who build social capital in one application carry it everywhere.

## Key Features

- **BNS-linked profile**: Each profile is associated with a BNS name, providing human-readable identity. Profile creation requires BNS name ownership.
- **Avatar**: Reference to a BST-721 NFT as the profile avatar. The contract verifies the user owns the referenced NFT. Avatar updates are reflected across all dApps reading the profile.
- **Bio and metadata**: On-chain storage for biography text, display name, website URL, and custom key-value metadata fields.
- **Social links**: Structured storage for social platform links (verified where possible through BST-VC credentials).
- **Follow/follower graph**: On-chain follow relationships. Users can follow any address. The graph is bidirectional (following and followers are both queryable).
- **BST-VC verification badges**: Attach verifiable credentials to the profile. Supported badge types include identity verification, professional credentials, developer certification, and business verification.
- **Profile privacy levels**: Public profiles visible to all, semi-private profiles visible only to followers, and private profiles visible only to the owner.
- **Delegation**: Authorize another address to manage your profile on your behalf (useful for teams managing organizational profiles).
- **Activity feed integration**: Profile stores a pointer to recent on-chain activities for aggregation by front-ends.
- **Profile migration**: Transfer a complete profile to a new address while preserving the social graph (useful when changing wallets).
- **Block list**: Users can block addresses, preventing them from following or interacting.

## Basalt-Specific Advantages

- **BNS native integration**: Basalt's Name Service (0x...1002) provides the foundational identity layer. Profiles are resolved by BNS names, enabling human-readable social interactions like "follow alice.basalt" rather than raw addresses.
- **BST-VC verifiable credentials**: Basalt's native verifiable credential standard (BST-VC) enables trustless profile verification. A developer can attach their GitHub-verified developer credential, a business can attach its registration credential -- all verifiable on-chain without trusting a centralized authority.
- **ZK privacy for credentials**: Users can prove credential properties (e.g., "I am over 18" or "I am a licensed professional") without revealing the full credential, using Basalt's ZK compliance layer. This enables verified profiles that preserve privacy.
- **SchemaRegistry and IssuerRegistry**: Credential schemas are registered in SchemaRegistry (0x...1006) and issuer trust is managed in IssuerRegistry (0x...1007), providing a standardized verification infrastructure.
- **Achievement system integration**: Profiles display soulbound achievement badges (contract 55), creating a rich reputation layer. Achievement points contribute to profile reputation scores.
- **Ed25519 delegation signatures**: Profile delegation uses Ed25519 signatures for authorization, which are native to Basalt and gas-efficient.
- **AOT-compiled graph queries**: Social graph traversal (followers, following, mutual connections) executes at native speed, important for contracts that make access-control decisions based on follow relationships.
- **Flat state DB for social data**: Profile lookups and social graph queries benefit from FlatStateDb's O(1) dictionary caches, avoiding expensive Merkle trie traversals for the frequent reads that social features require.

## Token Standards Used

- **BST-721**: Avatar NFT references. The contract verifies ownership before setting an NFT as the profile avatar.
- **BST-VC**: Verifiable credentials for profile verification badges (identity, professional, business).

## Integration Points

- **BNS (0x...1002)**: Profile identity layer. BNS name ownership is required for profile creation. Reverse resolution enables address-to-profile lookups.
- **SchemaRegistry (0x...1006)**: Credential schema definitions for verification badge types.
- **IssuerRegistry (0x...1007)**: Trusted credential issuers for profile verification.
- **Achievement System (contract 55)**: Display soulbound achievement badges on profiles. Achievement points contribute to reputation scores.
- **Governance (0x...1003)**: Community governance over profile metadata schemas, verification badge standards, and moderation policies.
- **NFT Marketplace (contract 51)**: Avatar NFTs can link back to marketplace listings.
- **Messaging Registry (contract 62)**: Encryption keys for private messaging linked to social profiles.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum PrivacyLevel : byte
{
    Public = 0,
    FollowersOnly = 1,
    Private = 2
}

public enum BadgeType : byte
{
    IdentityVerified = 0,
    DeveloperCertified = 1,
    BusinessVerified = 2,
    ProfessionalLicense = 3,
    EarlyAdopter = 4,
    Custom = 255
}

public struct Profile
{
    public Address Owner;
    public string BnsName;
    public string DisplayName;
    public string Bio;
    public string Website;
    public Address AvatarContract;         // BST-721 contract holding avatar
    public ulong AvatarTokenId;            // Token ID of the avatar NFT
    public PrivacyLevel Privacy;
    public ulong CreatedAt;
    public ulong UpdatedAt;
    public ulong FollowerCount;
    public ulong FollowingCount;
}

public struct SocialLink
{
    public string Platform;                // e.g., "github", "twitter", "website"
    public string Url;
    public bool Verified;                  // True if backed by BST-VC credential
    public byte[] CredentialHash;          // Hash of the verifying credential
}

public struct VerificationBadge
{
    public BadgeType Type;
    public Address Issuer;
    public byte[] CredentialProof;         // ZK proof or credential reference
    public ulong IssuedAt;
    public ulong ExpiresAt;               // 0 = no expiry
}

public struct Delegation
{
    public Address Delegate;
    public ulong GrantedAt;
    public ulong ExpiresAt;               // 0 = no expiry
    public bool CanUpdateProfile;
    public bool CanManageFollows;
    public bool CanPostActivity;
}

public struct FollowRelation
{
    public Address Follower;
    public Address Followed;
    public ulong FollowedAt;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0206)]
public partial class SocialProfile : SdkContractBase
{
    // Storage
    private StorageMap<Address, Profile> _profiles;
    private StorageValue<ulong> _totalProfiles;

    // Composite key storage:
    // SocialLink keyed by BLAKE3(ownerAddress || platform)
    // VerificationBadge keyed by BLAKE3(ownerAddress || badgeType)
    // FollowRelation keyed by BLAKE3(follower || followed)
    // Delegation keyed by BLAKE3(ownerAddress || delegateAddress)
    // BlockList keyed by BLAKE3(ownerAddress || blockedAddress) -> bool

    // --- Profile Management ---

    /// <summary>
    /// Create a new profile linked to the caller's BNS name.
    /// Caller must own the specified BNS name.
    /// </summary>
    public void CreateProfile(
        string bnsName,
        string displayName,
        string bio,
        string website,
        PrivacyLevel privacy
    );

    /// <summary>
    /// Update profile metadata (display name, bio, website, privacy).
    /// Only callable by owner or authorized delegate.
    /// </summary>
    public void UpdateProfile(
        string displayName,
        string bio,
        string website,
        PrivacyLevel privacy
    );

    /// <summary>
    /// Set the profile avatar to a BST-721 NFT.
    /// Verifies that the caller owns the specified token.
    /// </summary>
    public void SetAvatar(Address nftContract, ulong tokenId);

    /// <summary>
    /// Clear the profile avatar.
    /// </summary>
    public void ClearAvatar();

    /// <summary>
    /// Migrate the profile to a new address. Transfers the entire
    /// profile, social graph, and credentials. The old address's
    /// profile is deleted. Requires a signed migration authorization
    /// from the new address.
    /// </summary>
    public void MigrateProfile(Address newAddress, byte[] newAddressSignature);

    // --- Social Links ---

    /// <summary>
    /// Add or update a social link on the profile.
    /// </summary>
    public void SetSocialLink(string platform, string url);

    /// <summary>
    /// Verify a social link using a BST-VC credential.
    /// The credential must attest to ownership of the linked account.
    /// </summary>
    public void VerifySocialLink(
        string platform,
        byte[] credentialProof
    );

    /// <summary>
    /// Remove a social link from the profile.
    /// </summary>
    public void RemoveSocialLink(string platform);

    // --- Verification Badges ---

    /// <summary>
    /// Attach a verification badge to the profile.
    /// The badge is validated against SchemaRegistry and IssuerRegistry.
    /// For ZK badges, only the proof is stored (not the full credential).
    /// </summary>
    public void AddVerificationBadge(
        BadgeType type,
        Address issuer,
        byte[] credentialProof,
        ulong expiresAt
    );

    /// <summary>
    /// Remove a verification badge from the profile.
    /// </summary>
    public void RemoveVerificationBadge(BadgeType type);

    // --- Follow/Follower Graph ---

    /// <summary>
    /// Follow an address. Creates a bidirectional relationship
    /// (following for caller, follower for target).
    /// Cannot follow blocked addresses or private profiles.
    /// </summary>
    public void Follow(Address target);

    /// <summary>
    /// Unfollow an address. Removes the bidirectional relationship.
    /// </summary>
    public void Unfollow(Address target);

    /// <summary>
    /// Block an address. Removes any existing follow relationship
    /// and prevents future follows.
    /// </summary>
    public void BlockAddress(Address target);

    /// <summary>
    /// Unblock an address.
    /// </summary>
    public void UnblockAddress(Address target);

    // --- Delegation ---

    /// <summary>
    /// Grant delegation to another address with specified permissions.
    /// </summary>
    public void GrantDelegation(
        Address delegate_,
        ulong expiresAt,
        bool canUpdateProfile,
        bool canManageFollows,
        bool canPostActivity
    );

    /// <summary>
    /// Revoke a delegation.
    /// </summary>
    public void RevokeDelegation(Address delegate_);

    // --- View Functions ---

    public Profile GetProfile(Address owner);

    /// <summary>
    /// Resolve a BNS name to a full profile.
    /// </summary>
    public Profile GetProfileByName(string bnsName);

    public SocialLink GetSocialLink(Address owner, string platform);
    public VerificationBadge GetVerificationBadge(Address owner, BadgeType type);
    public bool IsFollowing(Address follower, Address followed);
    public bool IsBlocked(Address blocker, Address blocked);
    public ulong GetFollowerCount(Address owner);
    public ulong GetFollowingCount(Address owner);
    public Delegation GetDelegation(Address owner, Address delegate_);

    /// <summary>
    /// Check if two addresses follow each other (mutual connection).
    /// </summary>
    public bool IsMutualConnection(Address addr1, Address addr2);

    /// <summary>
    /// Get the total reputation score for a profile.
    /// Combines achievement points, verification badge count,
    /// follower count, and on-chain activity metrics.
    /// </summary>
    public ulong GetReputationScore(Address owner);

    /// <summary>
    /// Check if a caller has permission to view a profile
    /// based on the profile's privacy level and follow relationship.
    /// </summary>
    public bool CanView(Address viewer, Address profileOwner);

    // --- Internal ---

    /// <summary>
    /// Verify that the caller owns a BNS name by querying the
    /// BNS contract (0x...1002).
    /// </summary>
    private bool VerifyBnsOwnership(Address caller, string bnsName);

    /// <summary>
    /// Validate a BST-VC credential proof against SchemaRegistry
    /// and IssuerRegistry.
    /// </summary>
    private bool ValidateCredential(
        byte[] credentialProof,
        Address expectedIssuer,
        BadgeType expectedType
    );

    /// <summary>
    /// Check if the caller is authorized to act on behalf of a profile
    /// owner (either is the owner or has active delegation with the
    /// required permission).
    /// </summary>
    private bool IsAuthorized(Address profileOwner, bool requireProfileUpdate);
}
```

## Complexity

**Medium** -- The core profile CRUD operations are straightforward. The social graph (follow/unfollow/block) requires bidirectional relationship management and privacy-aware access control. BST-VC credential validation adds complexity through cross-contract calls to SchemaRegistry and IssuerRegistry. Profile migration with social graph transfer is the most complex operation, requiring atomic updates across multiple storage maps. Delegation adds another layer of authorization checks.

## Priority

**P1** -- A decentralized social profile is a foundational building block for the Basalt ecosystem's user experience. Every dApp benefits from being able to display rich user identities instead of raw addresses. The profile system creates network effects that increase ecosystem stickiness and is a prerequisite for social features in the marketplace, gaming, and governance layers.
