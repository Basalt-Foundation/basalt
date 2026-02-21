using Basalt.Core;

namespace Basalt.Crypto;

/// <summary>
/// BLS12-381 signature operations for signing, verification, and aggregation.
/// </summary>
public interface IBlsSigner
{
    /// <summary>
    /// Sign a message with a BLS private key.
    /// </summary>
    byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message);

    /// <summary>
    /// Verify a BLS signature.
    /// </summary>
    bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature);

    /// <summary>
    /// Aggregate multiple BLS signatures into one.
    /// </summary>
    byte[] AggregateSignatures(ReadOnlySpan<byte[]> signatures);

    /// <summary>
    /// Verify an aggregated BLS signature against multiple public keys and a shared message.
    /// </summary>
    bool VerifyAggregate(ReadOnlySpan<byte[]> publicKeys, ReadOnlySpan<byte> message, ReadOnlySpan<byte> aggregateSignature);

    /// <summary>
    /// Derive the public key from a private key.
    /// </summary>
    byte[] GetPublicKey(ReadOnlySpan<byte> privateKey);

    /// <summary>
    /// Generate a Proof of Possession for a BLS public key.
    /// PoP = Sign(sk, pk_bytes) — proves the holder knows the secret key.
    /// Prevents rogue key attacks in aggregate signature schemes.
    /// </summary>
    byte[] GenerateProofOfPossession(ReadOnlySpan<byte> privateKey);

    /// <summary>
    /// Verify a Proof of Possession for a BLS public key.
    /// Must be enforced at validator registration to prevent rogue key attacks.
    /// </summary>
    bool VerifyProofOfPossession(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> proofOfPossession);
}

/// <summary>
/// Stub BLS implementation retained for backward compatibility.
/// Use <see cref="BlsSigner"/> for real BLS12-381 operations.
/// </summary>
[Obsolete("Use BlsSigner instead. StubBlsSigner is retained only for backward compatibility.")]
public sealed class StubBlsSigner : IBlsSigner
{
    public byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        var sig = Ed25519Signer.Sign(privateKey, message);
        // Pad Ed25519 signature (64 bytes) to BLS size (96 bytes)
        var result = new byte[BlsSignature.Size];
        sig.WriteTo(result);
        return result;
    }

    public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        // Extract Ed25519 key (first 32 bytes) and signature (first 64 bytes) from BLS-padded data
        var pk = new PublicKey(publicKey[..PublicKey.Size]);
        var sig = new Signature(signature[..Signature.Size]);
        return Ed25519Signer.Verify(pk, message, sig);
    }

    public byte[] AggregateSignatures(ReadOnlySpan<byte[]> signatures)
    {
        // Stub: concatenate all signatures
        using var ms = new MemoryStream();
        foreach (var sig in signatures)
            ms.Write(sig);
        return ms.ToArray();
    }

    public bool VerifyAggregate(ReadOnlySpan<byte[]> publicKeys, ReadOnlySpan<byte> message, ReadOnlySpan<byte> aggregateSignature)
    {
        // Stub: split concatenated signatures and verify each
        if (publicKeys.Length == 0 || aggregateSignature.Length == 0)
            return false;

        var sigSize = aggregateSignature.Length / publicKeys.Length;
        if (sigSize * publicKeys.Length != aggregateSignature.Length)
            return false;

        for (int i = 0; i < publicKeys.Length; i++)
        {
            var sigSlice = aggregateSignature.Slice(i * sigSize, sigSize);
            if (!Verify(publicKeys[i], message, sigSlice))
                return false;
        }
        return true;
    }

    public byte[] GetPublicKey(ReadOnlySpan<byte> privateKey)
    {
        var pk = Ed25519Signer.GetPublicKey(privateKey);
        // Pad Ed25519 public key (32 bytes) to BLS size (48 bytes)
        var result = new byte[BlsPublicKey.Size];
        pk.WriteTo(result);
        return result;
    }

    public byte[] GenerateProofOfPossession(ReadOnlySpan<byte> privateKey)
    {
        var publicKey = GetPublicKey(privateKey);
        return Sign(privateKey, publicKey);
    }

    public bool VerifyProofOfPossession(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> proofOfPossession)
    {
        return Verify(publicKey, publicKey.ToArray(), proofOfPossession);
    }
}
