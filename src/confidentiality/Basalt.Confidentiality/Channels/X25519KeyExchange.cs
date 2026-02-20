using System.Security.Cryptography;
using Basalt.Core;
using Basalt.Crypto;
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
    /// Derive the X25519 public key from a private key.
    /// </summary>
    /// <param name="privateKey">32-byte X25519 private key.</param>
    /// <returns>32-byte X25519 public key.</returns>
    public static byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeySize)
            throw new ArgumentException($"Private key must be {KeySize} bytes.", nameof(privateKey));

        using var key = Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    /// <summary>
    /// Derive a 32-byte shared secret from a private key and the other party's public key
    /// using X25519 Diffie-Hellman followed by HKDF-SHA256.
    /// F-06: Both parties' public keys are bound into the HKDF info parameter.
    /// F-14: Intermediate shared secret is zeroed after use.
    /// </summary>
    /// <param name="myPrivateKey">32-byte X25519 private key.</param>
    /// <param name="theirPublicKey">32-byte X25519 public key of the other party.</param>
    /// <param name="myPublicKey">32-byte X25519 public key of the caller (for identity binding).
    /// If null, the public key is derived from the private key.</param>
    /// <returns>32-byte derived shared secret.</returns>
    /// <exception cref="CryptographicException">If key agreement fails.</exception>
    public static byte[] DeriveSharedSecret(ReadOnlySpan<byte> myPrivateKey, ReadOnlySpan<byte> theirPublicKey,
        ReadOnlySpan<byte> myPublicKey = default)
    {
        if (myPrivateKey.Length != KeySize)
            throw new ArgumentException($"Private key must be {KeySize} bytes.", nameof(myPrivateKey));
        if (theirPublicKey.Length != KeySize)
            throw new ArgumentException($"Public key must be {KeySize} bytes.", nameof(theirPublicKey));

        // F-06: If myPublicKey not provided, derive it from private key
        byte[]? derivedPubKey = null;
        ReadOnlySpan<byte> myPub;
        if (myPublicKey.IsEmpty)
        {
            derivedPubKey = GetPublicKey(myPrivateKey);
            myPub = derivedPubKey;
        }
        else
        {
            if (myPublicKey.Length != KeySize)
                throw new ArgumentException($"Public key must be {KeySize} bytes.", nameof(myPublicKey));
            myPub = myPublicKey;
        }

        byte[]? intermediateSecret = null;
        try
        {
            using var key = Key.Import(Algorithm, myPrivateKey, KeyBlobFormat.RawPrivateKey);
            var pubKey = NSecPublicKey.Import(Algorithm, theirPublicKey, KeyBlobFormat.RawPublicKey);

            using var sharedSecret = Algorithm.Agree(key, pubKey);
            if (sharedSecret == null)
                throw new CryptographicException("X25519 key agreement failed.");

            // F-14: Export intermediate secret for zeroing after HKDF
            // We use System.Security.Cryptography.HKDF to bind party identities.
            // Extract the raw shared secret bytes via NSec's export
            var hkdfAlg = KeyDerivationAlgorithm.HkdfSha256;
            // Use a temporary key export to get raw bytes for zeroing
            using var tempKey = hkdfAlg.DeriveKey(
                sharedSecret,
                ReadOnlySpan<byte>.Empty,
                ReadOnlySpan<byte>.Empty,
                AeadAlgorithm.Aes256Gcm,
                new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
            intermediateSecret = tempKey.Export(KeyBlobFormat.RawSymmetricKey);

            // F-06: Build HKDF info with sorted public keys bound to context
            // info = "basalt-channel-v1" || sortedKeyA || sortedKeyB
            var prefix = "basalt-channel-v1"u8;
            var info = new byte[prefix.Length + KeySize + KeySize];
            prefix.CopyTo(info);

            if (myPub.SequenceCompareTo(theirPublicKey) <= 0)
            {
                myPub.CopyTo(info.AsSpan(prefix.Length));
                theirPublicKey.CopyTo(info.AsSpan(prefix.Length + KeySize));
            }
            else
            {
                theirPublicKey.CopyTo(info.AsSpan(prefix.Length));
                myPub.CopyTo(info.AsSpan(prefix.Length + KeySize));
            }

            // Derive final key using System.Security.Cryptography.HKDF with identity binding
            var derivedKey = new byte[32];
            HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                intermediateSecret,
                derivedKey,
                ReadOnlySpan<byte>.Empty, // salt
                info);

            return derivedKey;
        }
        finally
        {
            // F-14: Zero intermediate shared secret material
            if (intermediateSecret != null)
                CryptographicOperations.ZeroMemory(intermediateSecret);
        }
    }

    /// <summary>
    /// F-07: Sign an X25519 public key with an Ed25519 identity key to authenticate the key exchange.
    /// Prevents MITM attacks by binding the ephemeral X25519 key to a known Ed25519 identity.
    /// </summary>
    /// <param name="x25519PubKey">32-byte X25519 public key to sign.</param>
    /// <param name="ed25519PrivKey">Ed25519 private key of the signer.</param>
    /// <returns>Ed25519 signature bytes.</returns>
    public static byte[] SignKeyExchange(byte[] x25519PubKey, byte[] ed25519PrivKey)
    {
        if (x25519PubKey.Length != KeySize)
            throw new ArgumentException($"X25519 public key must be {KeySize} bytes.", nameof(x25519PubKey));

        // "basalt-key-exchange-v1" is 22 bytes; pad to 24 for alignment
        var prefix = "basalt-key-exchange-v1\0\0"u8;
        var msg = new byte[prefix.Length + KeySize];
        prefix.CopyTo(msg);
        x25519PubKey.CopyTo(msg.AsSpan(prefix.Length));

        return Ed25519Signer.Sign(ed25519PrivKey, msg).ToArray();
    }

    /// <summary>
    /// F-07: Verify that an X25519 public key was signed by the claimed Ed25519 identity.
    /// </summary>
    /// <param name="x25519PubKey">32-byte X25519 public key that was signed.</param>
    /// <param name="ed25519PubKey">Ed25519 public key of the expected signer.</param>
    /// <param name="signature">Ed25519 signature to verify.</param>
    /// <returns>True if the signature is valid.</returns>
    public static bool VerifyKeyExchange(byte[] x25519PubKey, Core.PublicKey ed25519PubKey, Core.Signature signature)
    {
        if (x25519PubKey.Length != KeySize)
            throw new ArgumentException($"X25519 public key must be {KeySize} bytes.", nameof(x25519PubKey));

        var prefix = "basalt-key-exchange-v1\0\0"u8;
        var msg = new byte[prefix.Length + KeySize];
        prefix.CopyTo(msg);
        x25519PubKey.CopyTo(msg.AsSpan(prefix.Length));

        return Ed25519Signer.Verify(ed25519PubKey, msg, signature);
    }
}
