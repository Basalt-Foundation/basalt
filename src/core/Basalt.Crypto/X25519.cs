using System.Security.Cryptography;
using Basalt.Core;
using NSec.Cryptography;
using NSecPublicKey = NSec.Cryptography.PublicKey;

namespace Basalt.Crypto;

/// <summary>
/// X25519 Diffie-Hellman key exchange for transport encryption.
/// Generates ephemeral key pairs and derives shared secrets.
/// NET-C02: Used during handshake to establish AES-256-GCM transport keys.
/// </summary>
public static class X25519
{
    private static readonly KeyAgreementAlgorithm Algorithm = KeyAgreementAlgorithm.X25519;

    /// <summary>X25519 key size in bytes.</summary>
    public const int KeySize = 32;

    /// <summary>NET-C02: Domain separation prefix for transport key signatures.</summary>
    private static readonly byte[] TransportKeyDomain = "basalt-transport-key-v1\0"u8.ToArray();

    /// <summary>
    /// Generate a new ephemeral X25519 key pair for transport encryption.
    /// </summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        using var key = Key.Create(Algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var privateKey = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return (privateKey, publicKey);
    }

    /// <summary>
    /// Perform X25519 key agreement and derive a 32-byte shared key via HKDF-SHA256.
    /// TransportEncryption applies HKDF again with directional info strings to derive
    /// separate initiator-to-responder and responder-to-initiator encryption keys.
    /// </summary>
    public static byte[] DeriveSharedSecret(ReadOnlySpan<byte> myPrivateKey, ReadOnlySpan<byte> theirPublicKey)
    {
        if (myPrivateKey.Length != KeySize)
            throw new ArgumentException($"Private key must be {KeySize} bytes.", nameof(myPrivateKey));
        if (theirPublicKey.Length != KeySize)
            throw new ArgumentException($"Public key must be {KeySize} bytes.", nameof(theirPublicKey));

        using var key = Key.Import(Algorithm, myPrivateKey, KeyBlobFormat.RawPrivateKey);
        var pubKey = NSecPublicKey.Import(Algorithm, theirPublicKey, KeyBlobFormat.RawPublicKey);

        using var sharedSecret = Algorithm.Agree(key, pubKey)
            ?? throw new CryptographicException("X25519 key agreement failed.");

        // Export raw shared secret via HKDF extract (no expand) — just get 32 raw bytes
        var hkdfAlg = KeyDerivationAlgorithm.HkdfSha256;
        using var derivedKey = hkdfAlg.DeriveKey(
            sharedSecret,
            ReadOnlySpan<byte>.Empty,
            ReadOnlySpan<byte>.Empty,
            AeadAlgorithm.Aes256Gcm,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return derivedKey.Export(KeyBlobFormat.RawSymmetricKey);
    }

    /// <summary>
    /// NET-C02: Sign an X25519 public key with Ed25519 identity key for MITM protection.
    /// </summary>
    public static Core.Signature SignPublicKey(byte[] x25519PubKey, byte[] ed25519PrivKey)
    {
        if (x25519PubKey.Length != KeySize)
            throw new ArgumentException($"X25519 public key must be {KeySize} bytes.", nameof(x25519PubKey));

        var msg = new byte[TransportKeyDomain.Length + KeySize];
        TransportKeyDomain.CopyTo(msg, 0);
        x25519PubKey.CopyTo(msg.AsSpan(TransportKeyDomain.Length));

        return Ed25519Signer.Sign(ed25519PrivKey, msg);
    }

    /// <summary>
    /// NET-C02: Verify that an X25519 public key was signed by the claimed Ed25519 identity.
    /// </summary>
    public static bool VerifyPublicKey(byte[] x25519PubKey, Core.PublicKey ed25519PubKey, Core.Signature signature)
    {
        if (x25519PubKey.Length != KeySize)
            throw new ArgumentException($"X25519 public key must be {KeySize} bytes.", nameof(x25519PubKey));

        var msg = new byte[TransportKeyDomain.Length + KeySize];
        TransportKeyDomain.CopyTo(msg, 0);
        x25519PubKey.CopyTo(msg.AsSpan(TransportKeyDomain.Length));

        return Ed25519Signer.Verify(ed25519PubKey, msg, signature);
    }
}
