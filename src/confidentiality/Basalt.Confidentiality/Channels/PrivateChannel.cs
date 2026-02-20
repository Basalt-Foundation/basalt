using System.Buffers.Binary;
using System.Security.Cryptography;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Confidentiality.Channels;

/// <summary>
/// Status of a private channel.
/// </summary>
public enum ChannelStatus
{
    /// <summary>Channel proposed but not yet accepted by both parties.</summary>
    Open,

    /// <summary>Both parties have agreed; messages can be exchanged.</summary>
    Active,

    /// <summary>One party has initiated channel closure.</summary>
    Closing,

    /// <summary>Channel is fully closed.</summary>
    Closed,
}

/// <summary>
/// A bilateral private communication channel between two parties.
/// Uses X25519 for key exchange and AES-256-GCM for message encryption.
/// </summary>
public sealed class PrivateChannel
{
    /// <summary>F-18: Maximum payload size in bytes (1 MB).</summary>
    public const int MaxPayloadSize = 1024 * 1024;

    /// <summary>Unique channel identifier (derived from both parties' public keys).</summary>
    public Hash256 ChannelId { get; init; }

    /// <summary>Address of the first party.</summary>
    public Address PartyA { get; init; }

    /// <summary>Address of the second party.</summary>
    public Address PartyB { get; init; }

    /// <summary>X25519 public key of party A (32 bytes).</summary>
    public required byte[] PartyAPublicKey { get; init; }

    /// <summary>X25519 public key of party B (32 bytes).</summary>
    public required byte[] PartyBPublicKey { get; init; }

    /// <summary>Current message sequence number (incremented per message).</summary>
    public ulong Nonce { get; private set; }

    /// <summary>Current channel status.</summary>
    public ChannelStatus Status { get; set; } = ChannelStatus.Open;

    /// <summary>
    /// F-05: Last received nonce for replay protection.
    /// Initialized to ulong.MaxValue as sentinel meaning no message received yet.
    /// </summary>
    private ulong _lastReceivedNonce = ulong.MaxValue;

    /// <summary>
    /// Derive the channel ID from both parties' X25519 public keys.
    /// The keys are sorted lexicographically before hashing to ensure
    /// both parties derive the same channel ID regardless of order.
    /// </summary>
    public static Hash256 DeriveChannelId(ReadOnlySpan<byte> pubKeyA, ReadOnlySpan<byte> pubKeyB)
    {
        // Sort keys to ensure deterministic ordering
        Span<byte> combined = stackalloc byte[X25519KeyExchange.KeySize * 2];
        if (pubKeyA.SequenceCompareTo(pubKeyB) <= 0)
        {
            pubKeyA.CopyTo(combined);
            pubKeyB.CopyTo(combined[X25519KeyExchange.KeySize..]);
        }
        else
        {
            pubKeyB.CopyTo(combined);
            pubKeyA.CopyTo(combined[X25519KeyExchange.KeySize..]);
        }

        return Blake3Hasher.Hash(combined);
    }

    /// <summary>
    /// F-01: Derive a directional encryption key from the shared secret.
    /// Each direction (A-to-B, B-to-A) gets a unique key, preventing nonce reuse
    /// when both parties start their nonce counters at 0.
    /// </summary>
    /// <param name="sharedSecret">32-byte shared secret derived via X25519.</param>
    /// <param name="senderPubKey">X25519 public key of the sender.</param>
    /// <param name="receiverPubKey">X25519 public key of the receiver.</param>
    /// <returns>32-byte directional encryption key.</returns>
    private static byte[] DeriveDirectionalKey(byte[] sharedSecret, byte[] senderPubKey, byte[] receiverPubKey)
    {
        // info = "basalt-dir-v1\0\0\0" (16 bytes) || senderPubKey (32 bytes) || receiverPubKey (32 bytes)
        var prefix = "basalt-dir-v1\0\0\0"u8;
        var info = new byte[prefix.Length + X25519KeyExchange.KeySize + X25519KeyExchange.KeySize];
        prefix.CopyTo(info);
        senderPubKey.AsSpan().CopyTo(info.AsSpan(prefix.Length));
        receiverPubKey.AsSpan().CopyTo(info.AsSpan(prefix.Length + X25519KeyExchange.KeySize));

        var derivedKey = new byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            sharedSecret, derivedKey, ReadOnlySpan<byte>.Empty, info);
        return derivedKey;
    }

    /// <summary>
    /// Determine which X25519 public key belongs to the sender based on their Ed25519 private key.
    /// The sender's X25519 public key is identified by matching against PartyAPublicKey/PartyBPublicKey.
    /// </summary>
    /// <param name="senderX25519PubKey">The sender's X25519 public key.</param>
    /// <returns>A tuple of (senderX25519PubKey, receiverX25519PubKey).</returns>
    private (byte[] SenderPub, byte[] ReceiverPub) ResolveSenderReceiver(byte[] senderX25519PubKey)
    {
        if (senderX25519PubKey.AsSpan().SequenceEqual(PartyAPublicKey))
            return (PartyAPublicKey, PartyBPublicKey);
        if (senderX25519PubKey.AsSpan().SequenceEqual(PartyBPublicKey))
            return (PartyBPublicKey, PartyAPublicKey);

        throw new InvalidOperationException("Sender X25519 public key does not match either party in this channel.");
    }

    /// <summary>
    /// Create an encrypted channel message.
    /// F-01: Uses directional encryption keys derived from the shared secret and party public keys.
    /// F-18: Enforces maximum payload size.
    /// </summary>
    /// <param name="sharedSecret">32-byte shared secret derived via X25519.</param>
    /// <param name="payload">The plaintext message payload.</param>
    /// <param name="senderPrivateKey">Ed25519 private key of the sender for signing.</param>
    /// <param name="senderX25519PublicKey">X25519 public key of the sender (to determine direction).</param>
    /// <returns>An encrypted and signed <see cref="ChannelMessage"/>.</returns>
    /// <exception cref="InvalidOperationException">If the channel is not active.</exception>
    /// <exception cref="ArgumentException">If payload exceeds MaxPayloadSize.</exception>
    public ChannelMessage CreateMessage(byte[] sharedSecret, byte[] payload, byte[] senderPrivateKey,
        byte[] senderX25519PublicKey)
    {
        if (Status != ChannelStatus.Active)
            throw new InvalidOperationException($"Cannot send messages on a channel with status {Status}.");

        // F-18: Enforce maximum payload size
        if (payload.Length > MaxPayloadSize)
            throw new ArgumentException($"F-18: Payload exceeds maximum size of {MaxPayloadSize} bytes.");

        // F-01: Derive directional key
        var (senderPub, receiverPub) = ResolveSenderReceiver(senderX25519PublicKey);
        var directionalKey = DeriveDirectionalKey(sharedSecret, senderPub, receiverPub);

        var currentNonce = Nonce;
        var nonce = ChannelEncryption.BuildNonce(currentNonce);
        var encrypted = ChannelEncryption.Encrypt(directionalKey, nonce, payload);

        // Sign: ChannelId || Nonce (8 bytes BE) || EncryptedPayload
        var signData = BuildSignData(ChannelId, currentNonce, encrypted);
        var signature = Ed25519Signer.Sign(senderPrivateKey, signData);

        Nonce = currentNonce + 1;

        return new ChannelMessage
        {
            ChannelId = ChannelId,
            Nonce = currentNonce,
            EncryptedPayload = encrypted,
            SenderSignature = signature,
        };
    }

    /// <summary>
    /// Backward-compatible overload that infers sender identity from the channel's party keys.
    /// Uses PartyAPublicKey as the sender direction (legacy behavior for single-direction channels).
    /// Callers should prefer the 4-parameter overload with explicit senderX25519PublicKey.
    /// </summary>
    public ChannelMessage CreateMessage(byte[] sharedSecret, byte[] payload, byte[] senderPrivateKey)
    {
        // Legacy callers don't specify direction; default to PartyA as sender
        return CreateMessage(sharedSecret, payload, senderPrivateKey, PartyAPublicKey);
    }

    /// <summary>
    /// Verify and decrypt a received channel message.
    /// F-01: Uses directional encryption keys derived from the shared secret and party public keys.
    /// F-05: Enforces strictly increasing nonce to prevent replay attacks.
    /// </summary>
    /// <param name="message">The received message to verify.</param>
    /// <param name="sharedSecret">32-byte shared secret.</param>
    /// <param name="senderPublicKey">Ed25519 public key of the expected sender.</param>
    /// <param name="senderX25519PublicKey">X25519 public key of the message sender (to determine direction).</param>
    /// <returns>Decrypted payload bytes.</returns>
    /// <exception cref="InvalidOperationException">If verification, decryption, or replay check fails.</exception>
    public byte[] VerifyAndDecrypt(ChannelMessage message, byte[] sharedSecret, PublicKey senderPublicKey,
        byte[] senderX25519PublicKey)
    {
        if (message.ChannelId != ChannelId)
            throw new InvalidOperationException("Message channel ID does not match.");

        // Verify signature
        var signData = BuildSignData(message.ChannelId, message.Nonce, message.EncryptedPayload);
        if (!Ed25519Signer.Verify(senderPublicKey, signData, message.SenderSignature))
            throw new InvalidOperationException("Message signature verification failed.");

        // F-05: Nonce replay protection — enforce strictly increasing nonces
        if (_lastReceivedNonce != ulong.MaxValue && message.Nonce <= _lastReceivedNonce)
            throw new InvalidOperationException("F-05: Message nonce is not strictly increasing (replay detected).");
        _lastReceivedNonce = message.Nonce;

        // F-01: Derive directional key (sender→receiver direction)
        var (senderPub, receiverPub) = ResolveSenderReceiver(senderX25519PublicKey);
        var directionalKey = DeriveDirectionalKey(sharedSecret, senderPub, receiverPub);

        // Decrypt
        var nonce = ChannelEncryption.BuildNonce(message.Nonce);
        return ChannelEncryption.Decrypt(directionalKey, nonce, message.EncryptedPayload);
    }

    /// <summary>
    /// Backward-compatible overload that infers sender identity from the channel's party keys.
    /// Uses PartyAPublicKey as the sender direction (legacy behavior for single-direction channels).
    /// Callers should prefer the 4-parameter overload with explicit senderX25519PublicKey.
    /// </summary>
    public byte[] VerifyAndDecrypt(ChannelMessage message, byte[] sharedSecret, PublicKey senderPublicKey)
    {
        // Legacy callers don't specify direction; default to PartyA as sender
        return VerifyAndDecrypt(message, sharedSecret, senderPublicKey, PartyAPublicKey);
    }

    /// <summary>
    /// F-08: Ratchet the shared secret forward after each message for forward secrecy.
    /// Callers should apply this after each CreateMessage / VerifyAndDecrypt to ensure
    /// compromise of the current key does not reveal past messages.
    /// </summary>
    /// <param name="currentKey">The current 32-byte shared secret.</param>
    /// <param name="nonce">The nonce of the message just sent/received.</param>
    /// <returns>A new 32-byte key to use for subsequent messages.</returns>
    public static byte[] RatchetKey(byte[] currentKey, ulong nonce)
    {
        var derived = new byte[32];
        Span<byte> salt = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(salt, nonce);
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            currentKey, derived, salt, "basalt-ratchet-v1"u8);
        return derived;
    }

    /// <summary>
    /// Build the data buffer that is signed for a channel message:
    /// ChannelId (32 bytes) || Nonce (8 bytes BE) || EncryptedPayload.
    /// </summary>
    private static byte[] BuildSignData(Hash256 channelId, ulong nonce, byte[] encryptedPayload)
    {
        var channelIdBytes = channelId.ToArray();
        var data = new byte[Hash256.Size + sizeof(ulong) + encryptedPayload.Length];
        Buffer.BlockCopy(channelIdBytes, 0, data, 0, Hash256.Size);
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(Hash256.Size), nonce);
        Buffer.BlockCopy(encryptedPayload, 0, data, Hash256.Size + sizeof(ulong), encryptedPayload.Length);
        return data;
    }
}
