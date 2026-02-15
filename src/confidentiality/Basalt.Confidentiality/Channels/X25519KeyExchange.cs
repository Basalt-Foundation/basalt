using System.Security.Cryptography;
using NSec.Cryptography;
using NSecPublicKey = NSec.Cryptography.PublicKey;

namespace Basalt.Confidentiality.Channels;

/// <summary>
/// X25519 Diffie-Hellman key exchange for establishing shared secrets between
/// two parties in a private channel.
/// </summary>
public static class X25519KeyExchange
{
    private static readonly KeyAgreementAlgorithm Algorithm = KeyAgreementAlgorithm.X25519;

    /// <summary>X25519 key size in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>
    /// Generate a new X25519 key pair.
    /// </summary>
    /// <returns>A tuple of (privateKey, publicKey) as 32-byte arrays.</returns>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        using var key = Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });
        var privateKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKeyBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return (privateKeyBytes, publicKeyBytes);
    }

    /// <summary>
    /// Derive a 32-byte shared secret from a private key and the other party's public key
    /// using X25519 Diffie-Hellman followed by HKDF-SHA256.
    /// </summary>
    /// <param name="myPrivateKey">32-byte X25519 private key.</param>
    /// <param name="theirPublicKey">32-byte X25519 public key of the other party.</param>
    /// <returns>32-byte derived shared secret.</returns>
    /// <exception cref="CryptographicException">If key agreement fails.</exception>
    public static byte[] DeriveSharedSecret(ReadOnlySpan<byte> myPrivateKey, ReadOnlySpan<byte> theirPublicKey)
    {
        if (myPrivateKey.Length != KeySize)
            throw new ArgumentException($"Private key must be {KeySize} bytes.", nameof(myPrivateKey));
        if (theirPublicKey.Length != KeySize)
            throw new ArgumentException($"Public key must be {KeySize} bytes.", nameof(theirPublicKey));

        using var key = Key.Import(Algorithm, myPrivateKey, KeyBlobFormat.RawPrivateKey);
        var pubKey = NSecPublicKey.Import(Algorithm, theirPublicKey, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = Algorithm.Agree(key, pubKey);
        if (sharedSecret == null)
            throw new CryptographicException("X25519 key agreement failed.");

        // Derive a symmetric key using HKDF-SHA256
        var hkdf = KeyDerivationAlgorithm.HkdfSha256;
        using var derivedKey = hkdf.DeriveKey(
            sharedSecret,
            ReadOnlySpan<byte>.Empty,  // salt
            "basalt-channel-v1"u8,      // info/context
            AeadAlgorithm.Aes256Gcm,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

        return derivedKey.Export(KeyBlobFormat.RawSymmetricKey);
    }
}
