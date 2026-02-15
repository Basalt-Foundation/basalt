using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Basalt.Confidentiality.Channels;

/// <summary>
/// AES-256-GCM authenticated encryption for private channel messages.
/// </summary>
public static class ChannelEncryption
{
    /// <summary>AES-256-GCM nonce size in bytes.</summary>
    public const int NonceSize = 12;

    /// <summary>AES-256-GCM authentication tag size in bytes.</summary>
    public const int TagSize = 16;

    /// <summary>AES-256 key size in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>
    /// Encrypt plaintext using AES-256-GCM.
    /// </summary>
    /// <param name="key">32-byte symmetric key.</param>
    /// <param name="nonce">12-byte nonce (must be unique per message with the same key).</param>
    /// <param name="plaintext">The data to encrypt.</param>
    /// <returns>Ciphertext with appended 16-byte authentication tag.</returns>
    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
        if (nonce.Length != NonceSize)
            throw new ArgumentException($"Nonce must be {NonceSize} bytes.", nameof(nonce));

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // Return ciphertext || tag
        var result = new byte[ciphertext.Length + TagSize];
        Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, ciphertext.Length, TagSize);
        return result;
    }

    /// <summary>
    /// Decrypt ciphertext using AES-256-GCM.
    /// </summary>
    /// <param name="key">32-byte symmetric key.</param>
    /// <param name="nonce">12-byte nonce used during encryption.</param>
    /// <param name="ciphertextWithTag">Ciphertext with appended 16-byte authentication tag.</param>
    /// <returns>Decrypted plaintext.</returns>
    /// <exception cref="CryptographicException">If authentication fails (tampered data).</exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertextWithTag)
    {
        if (key.Length != KeySize)
            throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
        if (nonce.Length != NonceSize)
            throw new ArgumentException($"Nonce must be {NonceSize} bytes.", nameof(nonce));
        if (ciphertextWithTag.Length < TagSize)
            throw new ArgumentException("Ciphertext too short (must include auth tag).", nameof(ciphertextWithTag));

        var ciphertext = ciphertextWithTag[..^TagSize];
        var tag = ciphertextWithTag[^TagSize..];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// Build a 12-byte nonce from a 64-bit message sequence number.
    /// The first 4 bytes are zero; the last 8 bytes are the big-endian sequence number.
    /// </summary>
    /// <param name="sequenceNumber">Monotonically increasing message counter.</param>
    /// <returns>12-byte nonce.</returns>
    public static byte[] BuildNonce(ulong sequenceNumber)
    {
        var nonce = new byte[NonceSize];
        BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), sequenceNumber);
        return nonce;
    }
}
