# Decentralized Messaging Registry

## Category

Communication Infrastructure / Privacy

## Summary

An on-chain registry where users register their encryption public keys linked to their BNS name or address, enabling end-to-end encrypted off-chain messaging between Basalt participants. The contract manages key lifecycle including registration, rotation, and revocation, supports group channel key distribution for multi-party encrypted communication, and provides the foundational key discovery layer for secure peer-to-peer messaging applications built on the Basalt network.

## Why It's Useful

- **Market need**: Encrypted messaging requires a trustless key distribution mechanism. Without a decentralized registry, users must exchange keys through out-of-band channels or trust centralized key servers (which can be compromised, subpoenaed, or taken offline).
- **User benefit**: Any Basalt user can discover another user's encryption key by looking up their BNS name or address, enabling spontaneous encrypted communication without prior key exchange. Key rotation is transparent and auditable.
- **Security foundation**: The registry is the foundation for secure communications in the Basalt ecosystem -- private trade negotiations, governance discussions, team coordination, and any scenario requiring confidential messaging.
- **Application enablement**: Messaging dApps, notification services, and private communication tools can all use the shared registry rather than maintaining separate key databases, ensuring interoperability across communication platforms.
- **Group communication**: The group channel key distribution mechanism enables encrypted multi-party chats, DAOs to have private internal discussions, and teams to coordinate securely.

## Key Features

- **Key registration**: Users register X25519 encryption public keys (for Diffie-Hellman key agreement) linked to their address. Multiple key types supported (current, backup).
- **BNS name linkage**: Keys are discoverable through BNS names. Looking up "alice.basalt" returns her encryption public key.
- **Key rotation**: Users can rotate their encryption key at any time. Previous keys are archived with expiry timestamps so existing conversations can still be decrypted.
- **Key revocation**: Users can explicitly revoke compromised keys. Revocation is recorded with a timestamp and reason.
- **Prekeys (one-time keys)**: Users upload batches of one-time prekeys (similar to the Signal protocol). Each prekey is used once for initial key establishment, then marked as consumed.
- **Signed prekeys**: Periodically rotating signed prekeys for the X3DH key agreement protocol.
- **Group channels**: Create encrypted group channels with member management. The channel creator distributes group keys encrypted to each member's individual key.
- **Key transparency**: All key operations (registration, rotation, revocation) are recorded on-chain, providing an auditable log that prevents silent key substitution attacks.
- **Device keys**: Support for per-device keys, enabling multi-device messaging where each device has its own encryption key pair.
- **Key backup**: Optional encrypted key backup stored on-chain for account recovery.
- **Identity binding**: Keys can be bound to BST-VC credentials, proving that the key owner has verified their identity.
- **Key expiry**: Keys have optional expiry timestamps. Expired keys are treated as inactive.

## Basalt-Specific Advantages

- **BNS native key discovery**: Basalt's Name Service provides intuitive key lookup. "Send encrypted message to alice.basalt" requires only a BNS resolution and key lookup, both on-chain operations.
- **Ed25519 identity binding**: Basalt's native Ed25519 signatures bind encryption keys to transaction signing keys, creating a strong identity chain. Users sign their encryption key registration with their Ed25519 transaction key, proving ownership.
- **ZK credential binding**: Keys can be bound to BST-VC credentials through the ZK compliance layer, enabling verified encrypted communication (e.g., a business can verify it is communicating with a KYC-verified counterparty).
- **BLAKE3 key fingerprints**: Key fingerprints are computed with BLAKE3 for fast verification. Users can compare key fingerprints out-of-band to detect man-in-the-middle attacks.
- **AOT-compiled key operations**: Key registration, lookup, and rotation execute at native speed, critical for messaging applications that perform many key lookups per session.
- **Flat state DB for key caching**: Frequent key lookups benefit from FlatStateDb's O(1) dictionary caches, avoiding Merkle trie traversals. Messaging applications may look up dozens of keys per conversation load.
- **Social profile integration**: Encryption keys are linked to social profiles (contract 57), enabling profile-to-profile encrypted messaging with verified identities.
- **Confidential transactions**: Pedersen commitment support enables the registry to store key material without revealing associated metadata in privacy-sensitive scenarios.

## Token Standards Used

- **BST-VC**: Optional verifiable credentials binding identity to encryption keys.

## Integration Points

- **BNS (0x...1002)**: Key discovery through BNS name resolution. Users register keys under their BNS name for easy lookup.
- **SchemaRegistry (0x...1006)**: Credential schemas for identity-bound key registration.
- **IssuerRegistry (0x...1007)**: Trusted issuers for identity verification in key binding.
- **Social Profile (contract 57)**: Encryption keys displayed on social profiles. Profile-to-profile messaging initiation.
- **Governance (0x...1003)**: Governance over registry parameters (prekey limits, rotation policies).
- **Freelance Platform (contract 64)**: Encrypted communication between freelancers and clients.
- **Data Marketplace (contract 63)**: Encrypted delivery of viewing keys for purchased datasets.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum KeyStatus : byte
{
    Active = 0,
    Rotated = 1,
    Revoked = 2,
    Expired = 3
}

public enum KeyType : byte
{
    IdentityKey = 0,          // Long-term X25519 identity key
    SignedPrekey = 1,         // Periodically rotating signed prekey
    OneTimePrekey = 2,        // One-time use prekeys (Signal X3DH)
    DeviceKey = 3,            // Per-device encryption key
    BackupKey = 4             // Encrypted backup key
}

public struct EncryptionKeyRecord
{
    public Address Owner;
    public KeyType Type;
    public byte[] PublicKey;           // X25519 public key (32 bytes)
    public byte[] KeySignature;        // Ed25519 signature over the public key
    public ulong RegisteredAt;
    public ulong ExpiresAt;            // 0 = no expiry
    public KeyStatus Status;
    public string DeviceId;            // For device keys only
    public ulong SequenceNumber;       // Monotonic for ordering rotations
}

public struct KeyRotationLog
{
    public Address Owner;
    public byte[] OldKeyHash;          // BLAKE3 hash of rotated key
    public byte[] NewKeyHash;          // BLAKE3 hash of new key
    public ulong Timestamp;
    public ulong SequenceNumber;
}

public struct KeyRevocation
{
    public Address Owner;
    public byte[] RevokedKeyHash;
    public string Reason;
    public ulong Timestamp;
}

public struct PreKeyBundle
{
    public Address Owner;
    public byte[] IdentityKey;         // Long-term X25519 identity key
    public byte[] SignedPrekey;        // Current signed prekey
    public byte[] SignedPrekeySignature; // Ed25519 signature over signed prekey
    public byte[] OneTimePrekey;       // Next available one-time prekey (nullable)
}

public struct GroupChannel
{
    public ulong ChannelId;
    public string Name;
    public Address Creator;
    public ulong MemberCount;
    public ulong CreatedAt;
    public ulong KeyVersion;           // Incremented on member changes
    public bool Active;
}

public struct GroupMember
{
    public ulong ChannelId;
    public Address Member;
    public byte[] EncryptedGroupKey;   // Group key encrypted to member's public key
    public ulong JoinedAt;
    public bool IsAdmin;
}

public struct KeyFingerprint
{
    public Address Owner;
    public byte[] Fingerprint;         // BLAKE3(identityKey || owner)
    public ulong ComputedAt;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x020B)]
public partial class MessagingRegistry : SdkContractBase
{
    // Storage
    private StorageMap<Address, EncryptionKeyRecord> _identityKeys;
    private StorageMap<ulong, GroupChannel> _channels;
    private StorageValue<ulong> _nextChannelId;
    private StorageValue<ulong> _maxPrekeysPerUser;       // Default 100
    private StorageValue<ulong> _signedPrekeyMaxAge;      // Default 30 days

    // Composite key storage:
    // SignedPrekey keyed by owner address
    // OneTimePrekeys keyed by BLAKE3(ownerAddress || prekeyIndex)
    // DeviceKey keyed by BLAKE3(ownerAddress || deviceId)
    // GroupMember keyed by BLAKE3(channelId || memberAddress)
    // KeyRotationLog keyed by BLAKE3(ownerAddress || sequenceNumber)

    // --- Key Registration ---

    /// <summary>
    /// Register an X25519 identity encryption key. The key must be
    /// signed by the caller's Ed25519 transaction signing key to prove
    /// ownership. This is the long-term key used for key discovery.
    /// </summary>
    public void RegisterIdentityKey(
        byte[] x25519PublicKey,
        byte[] ed25519Signature,
        ulong expiresAt
    );

    /// <summary>
    /// Register a signed prekey (rotated periodically, e.g., every 7 days).
    /// Signed with the identity key for binding.
    /// </summary>
    public void RegisterSignedPrekey(
        byte[] signedPrekey,
        byte[] signedPrekeySignature
    );

    /// <summary>
    /// Upload a batch of one-time prekeys for the X3DH key agreement.
    /// Each prekey is used exactly once for initial key establishment.
    /// </summary>
    public void UploadOneTimePrekeys(byte[][] prekeys);

    /// <summary>
    /// Register a device-specific encryption key.
    /// Enables multi-device messaging.
    /// </summary>
    public void RegisterDeviceKey(
        string deviceId,
        byte[] devicePublicKey,
        byte[] ed25519Signature
    );

    // --- Key Rotation ---

    /// <summary>
    /// Rotate the identity key. The old key is archived with its
    /// expiry timestamp. The new key must be signed.
    /// </summary>
    public void RotateIdentityKey(
        byte[] newPublicKey,
        byte[] ed25519Signature,
        ulong expiresAt
    );

    /// <summary>
    /// Rotate a device key.
    /// </summary>
    public void RotateDeviceKey(
        string deviceId,
        byte[] newPublicKey,
        byte[] ed25519Signature
    );

    // --- Key Revocation ---

    /// <summary>
    /// Revoke a compromised key. The key is marked as revoked
    /// with a timestamp and reason. Contacts should re-establish
    /// sessions using the new key.
    /// </summary>
    public void RevokeKey(byte[] keyHash, string reason);

    /// <summary>
    /// Revoke a device key by device ID.
    /// </summary>
    public void RevokeDeviceKey(string deviceId, string reason);

    // --- Key Discovery ---

    /// <summary>
    /// Get the current identity key for an address.
    /// Returns null if no key is registered.
    /// </summary>
    public EncryptionKeyRecord GetIdentityKey(Address owner);

    /// <summary>
    /// Get the identity key for a BNS name by resolving through
    /// the BNS contract.
    /// </summary>
    public EncryptionKeyRecord GetIdentityKeyByName(string bnsName);

    /// <summary>
    /// Get a full prekey bundle for initiating an encrypted session
    /// (X3DH key agreement). Consumes one one-time prekey.
    /// Returns identity key + signed prekey + one-time prekey.
    /// </summary>
    public PreKeyBundle GetPreKeyBundle(Address owner);

    /// <summary>
    /// Get the key fingerprint for verification.
    /// Fingerprint = BLAKE3(identityKey || ownerAddress)
    /// </summary>
    public KeyFingerprint GetFingerprint(Address owner);

    /// <summary>
    /// Get a device-specific key.
    /// </summary>
    public EncryptionKeyRecord GetDeviceKey(Address owner, string deviceId);

    /// <summary>
    /// Get the remaining one-time prekey count for a user.
    /// Clients should upload more prekeys when this drops below a threshold.
    /// </summary>
    public ulong GetRemainingPrekeyCount(Address owner);

    /// <summary>
    /// Get the key rotation history for an address.
    /// Returns the last N rotation events.
    /// </summary>
    public KeyRotationLog GetRotationLog(Address owner, ulong sequenceNumber);

    // --- Group Channels ---

    /// <summary>
    /// Create an encrypted group channel. The creator generates a
    /// symmetric group key and encrypts it to each initial member's
    /// identity key.
    /// </summary>
    public ulong CreateGroupChannel(
        string name,
        Address[] initialMembers,
        byte[][] encryptedGroupKeys
    );

    /// <summary>
    /// Add a member to a group channel. The caller (must be admin)
    /// provides the group key encrypted to the new member's identity key.
    /// Key version is incremented.
    /// </summary>
    public void AddGroupMember(
        ulong channelId,
        Address newMember,
        byte[] encryptedGroupKey
    );

    /// <summary>
    /// Remove a member from a group channel. All remaining members
    /// must receive a new group key (re-keying). Admin provides
    /// new encrypted group keys for all remaining members.
    /// </summary>
    public void RemoveGroupMember(
        ulong channelId,
        Address member,
        byte[][] newEncryptedGroupKeys
    );

    /// <summary>
    /// Leave a group channel voluntarily.
    /// Triggers re-keying for remaining members.
    /// </summary>
    public void LeaveGroupChannel(ulong channelId);

    /// <summary>
    /// Get the group channel details.
    /// </summary>
    public GroupChannel GetGroupChannel(ulong channelId);

    /// <summary>
    /// Get the encrypted group key for a specific member.
    /// Only the member themselves should call this.
    /// </summary>
    public byte[] GetGroupKey(ulong channelId, Address member);

    /// <summary>
    /// Promote a member to admin.
    /// </summary>
    public void PromoteToAdmin(ulong channelId, Address member);

    // --- Admin (Governance) ---

    public void SetMaxPrekeys(ulong maxPerUser);
    public void SetSignedPrekeyMaxAge(ulong maxAgeSeconds);
}
```

## Complexity

**Medium** -- The core key registration and lookup logic is straightforward CRUD operations on key records. Complexity increases with the X3DH prekey management (one-time prekey consumption, signed prekey rotation), group channel key distribution (re-keying on member changes requires updating all remaining members), and multi-device support (key per device with individual rotation). The Ed25519 signature verification on key registration adds a security-critical validation step. Group re-keying on member removal is the most complex operation, requiring atomic updates for all remaining members.

## Priority

**P2** -- While encrypted communication is important, the messaging registry is infrastructure that benefits from a mature ecosystem. It becomes more valuable once social profiles (contract 57), freelance platforms (contract 64), and data marketplaces (contract 63) are deployed and create demand for secure peer-to-peer communication. It is a prerequisite for any messaging dApp built on Basalt.
