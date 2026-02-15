using System.Buffers.Binary;
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
    /// Create an encrypted channel message.
    /// </summary>
    /// <param name="sharedSecret">32-byte shared secret derived via X25519.</param>
    /// <param name="payload">The plaintext message payload.</param>
    /// <param name="senderPrivateKey">Ed25519 private key of the sender for signing.</param>
    /// <returns>An encrypted and signed <see cref="ChannelMessage"/>.</returns>
    /// <exception cref="InvalidOperationException">If the channel is not active.</exception>
    public ChannelMessage CreateMessage(byte[] sharedSecret, byte[] payload, byte[] senderPrivateKey)
    {
        if (Status != ChannelStatus.Active)
            throw new InvalidOperationException($"Cannot send messages on a channel with status {Status}.");

        var currentNonce = Nonce;
        var nonce = ChannelEncryption.BuildNonce(currentNonce);
        var encrypted = ChannelEncryption.Encrypt(sharedSecret, nonce, payload);

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
    /// Verify and decrypt a received channel message.
    /// </summary>
    /// <param name="message">The received message to verify.</param>
    /// <param name="sharedSecret">32-byte shared secret.</param>
    /// <param name="senderPublicKey">Ed25519 public key of the expected sender.</param>
    /// <returns>Decrypted payload bytes.</returns>
    /// <exception cref="InvalidOperationException">If verification or decryption fails.</exception>
    public byte[] VerifyAndDecrypt(ChannelMessage message, byte[] sharedSecret, PublicKey senderPublicKey)
    {
        if (message.ChannelId != ChannelId)
            throw new InvalidOperationException("Message channel ID does not match.");

        // Verify signature
        var signData = BuildSignData(message.ChannelId, message.Nonce, message.EncryptedPayload);
        if (!Ed25519Signer.Verify(senderPublicKey, signData, message.SenderSignature))
            throw new InvalidOperationException("Message signature verification failed.");

        // Decrypt
        var nonce = ChannelEncryption.BuildNonce(message.Nonce);
        return ChannelEncryption.Decrypt(sharedSecret, nonce, message.EncryptedPayload);
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
