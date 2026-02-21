using System.Buffers.Binary;
using System.Security.Cryptography;
using Basalt.Confidentiality.Channels;
using Basalt.Core;

namespace Basalt.Confidentiality.Disclosure;

/// <summary>
/// Viewing keys enable selective disclosure of confidential transaction data.
/// An auditor or recipient generates a viewing key pair. The sender encrypts
/// the committed value and blinding factor to the viewer's public key, allowing
/// the viewer to open the Pedersen commitment and verify the amount.
///
/// Uses X25519 key exchange to derive a shared secret, then AES-256-GCM
/// to encrypt the disclosure payload.
/// </summary>
public static class ViewingKey
{
    /// <summary>
    /// Size of the encrypted disclosure payload:
    /// 32 bytes (UInt256 value, big-endian) + 32 bytes (blinding factor) = 64 bytes plaintext,
    /// encrypted to 64 bytes ciphertext + 16 bytes GCM tag = 80 bytes,
    /// plus 32 bytes ephemeral public key and 12 bytes nonce = 124 bytes total.
    /// </summary>
    public const int EnclosedSize = X25519KeyExchange.KeySize + ChannelEncryption.NonceSize +
                                     64 + ChannelEncryption.TagSize;
    // 32 + 12 + 64 + 16 = 124

    /// <summary>
    /// Generate a new viewing key pair (X25519).
    /// </summary>
    /// <returns>A tuple of (privateKey, publicKey) as 32-byte arrays.</returns>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateViewingKeyPair()
    {
        return X25519KeyExchange.GenerateKeyPair();
    }

    /// <summary>
    /// Encrypt a committed value and blinding factor for a specific viewer.
    /// Uses an ephemeral X25519 key pair so the sender doesn't need a persistent key.
    /// </summary>
    /// <param name="viewerPublicKey">32-byte X25519 public key of the viewer/auditor.</param>
    /// <param name="value">The committed value to disclose.</param>
    /// <param name="blindingFactor">32-byte blinding factor used in the Pedersen commitment.</param>
    /// <returns>
    /// Encrypted envelope: [ephemeralPubKey:32][nonce:12][ciphertext+tag:80].
    /// </returns>
    public static byte[] EncryptForViewer(
        ReadOnlySpan<byte> viewerPublicKey,
        UInt256 value,
        ReadOnlySpan<byte> blindingFactor)
    {
        if (viewerPublicKey.Length != X25519KeyExchange.KeySize)
            throw new ArgumentException($"Viewer public key must be {X25519KeyExchange.KeySize} bytes.", nameof(viewerPublicKey));
        if (blindingFactor.Length != 32)
            throw new ArgumentException("Blinding factor must be 32 bytes.", nameof(blindingFactor));

        // Generate ephemeral key pair for this encryption
        var (ephPrivKey, ephPubKey) = X25519KeyExchange.GenerateKeyPair();

        byte[]? sharedSecret = null;
        try
        {
            // Derive shared secret
            sharedSecret = X25519KeyExchange.DeriveSharedSecret(ephPrivKey, viewerPublicKey);

            // Build plaintext: value (32 bytes BE) || blindingFactor (32 bytes)
            var plaintext = new byte[64];
            value.WriteTo(plaintext.AsSpan(0, 32), isBigEndian: true);
            blindingFactor.CopyTo(plaintext.AsSpan(32));

            // Generate random nonce
            var nonce = new byte[ChannelEncryption.NonceSize];
            RandomNumberGenerator.Fill(nonce);

            // Encrypt
            var encrypted = ChannelEncryption.Encrypt(sharedSecret, nonce, plaintext);

            // Pack: ephemeralPubKey || nonce || encrypted(ciphertext + tag)
            var result = new byte[EnclosedSize];
            Buffer.BlockCopy(ephPubKey, 0, result, 0, X25519KeyExchange.KeySize);
            Buffer.BlockCopy(nonce, 0, result, X25519KeyExchange.KeySize, ChannelEncryption.NonceSize);
            Buffer.BlockCopy(encrypted, 0, result, X25519KeyExchange.KeySize + ChannelEncryption.NonceSize, encrypted.Length);

            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephPrivKey);
            // F-14: Zero shared secret after use
            if (sharedSecret != null)
                CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    /// <summary>
    /// Decrypt a disclosure payload using the viewer's private key.
    /// </summary>
    /// <param name="viewerPrivateKey">32-byte X25519 private key of the viewer.</param>
    /// <param name="encrypted">The encrypted envelope from <see cref="EncryptForViewer"/>.</param>
    /// <returns>The disclosed (value, blindingFactor) tuple.</returns>
    /// <exception cref="CryptographicException">If decryption fails.</exception>
    public static (UInt256 Value, byte[] BlindingFactor) DecryptWithViewingKey(
        ReadOnlySpan<byte> viewerPrivateKey,
        ReadOnlySpan<byte> encrypted)
    {
        if (viewerPrivateKey.Length != X25519KeyExchange.KeySize)
            throw new ArgumentException($"Viewer private key must be {X25519KeyExchange.KeySize} bytes.", nameof(viewerPrivateKey));
        if (encrypted.Length != EnclosedSize)
            throw new ArgumentException($"Encrypted data must be {EnclosedSize} bytes.", nameof(encrypted));

        // Unpack
        var ephPubKey = encrypted[..X25519KeyExchange.KeySize];
        var nonce = encrypted.Slice(X25519KeyExchange.KeySize, ChannelEncryption.NonceSize);
        var ciphertextWithTag = encrypted[(X25519KeyExchange.KeySize + ChannelEncryption.NonceSize)..];

        // F-14: Zero shared secret after use
        byte[]? sharedSecret = null;
        byte[]? plaintext = null;
        try
        {
            // Derive shared secret
            sharedSecret = X25519KeyExchange.DeriveSharedSecret(viewerPrivateKey, ephPubKey);

            // Decrypt
            plaintext = ChannelEncryption.Decrypt(sharedSecret, nonce, ciphertextWithTag);

            // Parse: value (32 bytes BE) || blindingFactor (32 bytes)
            var value = new UInt256(plaintext.AsSpan(0, 32), isBigEndian: true);
            var blindingFactor = plaintext.AsSpan(32, 32).ToArray();

            return (value, blindingFactor);
        }
        finally
        {
            if (sharedSecret != null)
                CryptographicOperations.ZeroMemory(sharedSecret);
            // L-05: Zero plaintext after parsing to prevent secret data from
            // lingering in memory (value + blinding factor are sensitive).
            if (plaintext != null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>
/// F-16: Viewing key with time-based validity window.
/// Prevents indefinite access to confidential transaction data by
/// binding the viewing key to a specific time range.
/// </summary>
public sealed class TimeBoundViewingKey
{
    /// <summary>32-byte X25519 public key of the viewer.</summary>
    public required byte[] PublicKey { get; init; }

    /// <summary>Start of validity window (Unix timestamp in milliseconds).</summary>
    public required ulong ValidFrom { get; init; }

    /// <summary>End of validity window (Unix timestamp in milliseconds).</summary>
    public required ulong ValidUntil { get; init; }

    /// <summary>
    /// Check whether this viewing key is valid at the given timestamp.
    /// </summary>
    /// <param name="currentTimestamp">Current Unix timestamp in milliseconds.</param>
    /// <returns><c>true</c> if the current time falls within [ValidFrom, ValidUntil].</returns>
    public bool IsValid(ulong currentTimestamp) =>
        currentTimestamp >= ValidFrom && currentTimestamp <= ValidUntil;
}
