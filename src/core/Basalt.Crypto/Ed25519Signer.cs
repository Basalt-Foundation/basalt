using System.Security.Cryptography;
using Basalt.Core;
using NSec.Cryptography;
using NSecPublicKey = NSec.Cryptography.PublicKey;

namespace Basalt.Crypto;

/// <summary>
/// Ed25519 digital signature operations using NSec (libsodium).
/// </summary>
public static class Ed25519Signer
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    /// <summary>
    /// Generate a new Ed25519 key pair.
    /// </summary>
    /// <remarks>
    /// SECURITY: The returned PrivateKey byte[] is unprotected in managed memory.
    /// Callers MUST zero the private key with <see cref="ZeroPrivateKey"/> or
    /// <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/>
    /// as soon as it is no longer needed, to minimize exposure in memory.
    /// </remarks>
    public static (byte[] PrivateKey, Core.PublicKey PublicKey) GenerateKeyPair()
    {
        using var key = Key.Create(Algorithm, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        var privateKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKeyBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return (privateKeyBytes, new Core.PublicKey(publicKeyBytes));
    }

    /// <summary>
    /// Sign a message with the given private key.
    /// </summary>
    public static Core.Signature Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        using var key = Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey);
        var sigBytes = Algorithm.Sign(key, message);
        return new Core.Signature(sigBytes);
    }

    /// <summary>
    /// Verify a signature against a message and public key.
    /// </summary>
    public static bool Verify(Core.PublicKey publicKey, ReadOnlySpan<byte> message, Core.Signature signature)
    {
        var pubKeyBytes = publicKey.ToArray();
        var nsecPubKey = NSecPublicKey.Import(Algorithm, pubKeyBytes, KeyBlobFormat.RawPublicKey);
        var sigBytes = signature.ToArray();
        return Algorithm.Verify(nsecPubKey, message, sigBytes);
    }

    /// <summary>
    /// Batch verify multiple signatures (validates all independently, returns false if any fail).
    /// </summary>
    /// <remarks>
    /// NOTE: This is a sequential implementation — each signature is verified independently.
    /// It does NOT use multi-scalar multiplication or other batch verification optimizations.
    /// Performance is O(n) individual verifications. A true batch verification scheme
    /// (e.g., Bos-Coster) would provide ~2x speedup but is not yet implemented.
    /// </remarks>
    public static bool BatchVerify(
        ReadOnlySpan<Core.PublicKey> publicKeys,
        ReadOnlySpan<byte[]> messages,
        ReadOnlySpan<Core.Signature> signatures)
    {
        if (publicKeys.Length != messages.Length || publicKeys.Length != signatures.Length)
            throw new ArgumentException("All arrays must have the same length.");

        for (int i = 0; i < publicKeys.Length; i++)
        {
            if (!Verify(publicKeys[i], messages[i], signatures[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Derive public key from private key.
    /// </summary>
    public static Core.PublicKey GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        using var key = Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey);
        var pubKeyBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return new Core.PublicKey(pubKeyBytes);
    }

    /// <summary>
    /// Derive an Address from a public key using Keccak-256 (last 20 bytes).
    /// </summary>
    public static Address DeriveAddress(Core.PublicKey publicKey)
    {
        return KeccakHasher.DeriveAddress(publicKey);
    }

    /// <summary>
    /// Securely zero a private key buffer using CryptographicOperations.ZeroMemory.
    /// </summary>
    public static void ZeroPrivateKey(Span<byte> privateKey)
    {
        CryptographicOperations.ZeroMemory(privateKey);
    }
}
