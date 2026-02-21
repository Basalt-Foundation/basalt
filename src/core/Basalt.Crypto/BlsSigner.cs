using System.Security.Cryptography;
using System.Text;
using Basalt.Core;
using Nethermind.Crypto;

namespace Basalt.Crypto;

/// <summary>
/// Real BLS12-381 implementation using Nethermind's blst bindings.
///
/// Key sizes:
/// - Private key: 32 bytes
/// - Public key: 48 bytes (compressed G1 point)
/// - Signature: 96 bytes (compressed G2 point)
/// - Aggregated signature: 96 bytes (compressed G2 point)
///
/// Uses the Ethereum "BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_" domain separation tag.
/// </summary>
public sealed class BlsSigner : IBlsSigner
{
    private static readonly byte[] Dst = Encoding.UTF8.GetBytes("BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_");

    /// <summary>
    /// Sign a message with a BLS private key (32 bytes).
    /// Returns a 96-byte compressed G2 signature.
    /// </summary>
    /// <remarks>
    /// The first byte is masked with 0x3F to ensure the scalar is below the BLS12-381
    /// field modulus (which starts with 0x73...). This wastes ~2 bits of key space
    /// (reducing from 256 to ~254 effective bits) but guarantees validity without
    /// rejection sampling. The security loss is negligible (254 bits >> 128-bit target).
    /// </remarks>
    public byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message)
    {
        Span<byte> masked = stackalloc byte[32];
        privateKey[..32].CopyTo(masked);
        masked[0] &= 0x3F;

        var sk = new Bls.SecretKey();
        sk.FromBendian(masked);

        // AUDIT H-06: Zero masked key material immediately after use
        CryptographicOperations.ZeroMemory(masked);

        var sig = new Bls.P2();
        sig.HashTo(message, Dst, ReadOnlySpan<byte>.Empty);
        sig = sig.SignWith(sk);

        return sig.Compress();
    }

    /// <summary>
    /// Verify a BLS signature using pairing check: e(pk, H(m)) == e(G1, sig).
    /// Public key: 48-byte compressed G1 point.
    /// Signature: 96-byte compressed G2 point.
    /// </summary>
    /// <remarks>
    /// Returns false for any exception during verification (malformed points,
    /// invalid encodings, etc.). This is intentional — verification failure
    /// should never crash the node. The blst native library may throw various
    /// exceptions for malformed inputs that cannot be narrowed safely.
    /// </remarks>
    public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        try
        {
            var pkAff = new Bls.P1Affine();
            pkAff.Decode(publicKey);

            var sigAff = new Bls.P2Affine();
            sigAff.Decode(signature);

            // Hash message to G2 point
            var h = new Bls.P2();
            h.HashTo(message, Dst, ReadOnlySpan<byte>.Empty);

            // Compute e(pk, H(m))
            var gt1 = new Bls.PT();
            gt1.MillerLoop(h.ToAffine(), pkAff);

            // Compute e(G1, sig)
            var gt2 = new Bls.PT();
            gt2.MillerLoop(sigAff, Bls.P1Affine.Generator());

            // Check equality after final exponentiation
            return gt1.FinalExp().IsEqual(gt2.FinalExp());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Aggregate multiple BLS signatures into a single 96-byte signature.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when signatures array is empty.</exception>
    public byte[] AggregateSignatures(ReadOnlySpan<byte[]> signatures)
    {
        if (signatures.Length == 0)
            throw new ArgumentException("Cannot aggregate zero signatures.", nameof(signatures));

        if (signatures.Length == 1)
            return (byte[])signatures[0].Clone();

        var agg = new Bls.P2();
        agg.Decode(signatures[0]);

        for (int i = 1; i < signatures.Length; i++)
        {
            var sig = new Bls.P2Affine();
            sig.Decode(signatures[i]);
            agg.Aggregate(sig);
        }

        return agg.Compress();
    }

    /// <summary>
    /// Verify an aggregated BLS signature against multiple public keys and a shared message.
    /// Aggregates public keys, then verifies: e(aggPk, H(m)) == e(G1, aggSig).
    /// </summary>
    public bool VerifyAggregate(ReadOnlySpan<byte[]> publicKeys, ReadOnlySpan<byte> message, ReadOnlySpan<byte> aggregateSignature)
    {
        if (publicKeys.Length == 0)
            return false;

        try
        {
            var sigAff = new Bls.P2Affine();
            sigAff.Decode(aggregateSignature);

            // Aggregate all public keys
            var aggPk = new Bls.P1();
            aggPk.Decode(publicKeys[0]);

            for (int i = 1; i < publicKeys.Length; i++)
            {
                var pk = new Bls.P1Affine();
                pk.Decode(publicKeys[i]);
                aggPk.Aggregate(pk);
            }

            var aggPkAff = aggPk.ToAffine();

            // Hash message to G2
            var h = new Bls.P2();
            h.HashTo(message, Dst, ReadOnlySpan<byte>.Empty);

            // e(aggPk, H(m))
            var gt1 = new Bls.PT();
            gt1.MillerLoop(h.ToAffine(), aggPkAff);

            // e(G1, aggSig)
            var gt2 = new Bls.PT();
            gt2.MillerLoop(sigAff, Bls.P1Affine.Generator());

            return gt1.FinalExp().IsEqual(gt2.FinalExp());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Derive a BLS public key (48-byte compressed G1) from a 32-byte private key.
    /// </summary>
    public static byte[] GetPublicKeyStatic(ReadOnlySpan<byte> privateKey)
    {
        Span<byte> masked = stackalloc byte[32];
        privateKey[..32].CopyTo(masked);
        masked[0] &= 0x3F;

        var sk = new Bls.SecretKey();
        sk.FromBendian(masked);

        // AUDIT H-06: Zero masked key material immediately after use
        CryptographicOperations.ZeroMemory(masked);

        var pk = new Bls.P1();
        pk.FromSk(sk);

        return pk.Compress();
    }

    /// <summary>
    /// CORE-03: Generate a Proof of Possession for a BLS public key.
    /// PoP = Sign(sk, pk_bytes) — proves the holder knows the secret key corresponding to pk.
    /// This prevents rogue key attacks in aggregate signature schemes.
    /// Returns a 96-byte compressed G2 signature.
    /// </summary>
    public byte[] GenerateProofOfPossession(ReadOnlySpan<byte> privateKey)
    {
        var publicKey = GetPublicKeyStatic(privateKey);
        return Sign(privateKey, publicKey);
    }

    /// <summary>
    /// CORE-03: Verify a Proof of Possession for a BLS public key.
    /// Verifies that PoP is a valid signature of the public key bytes under the corresponding secret key.
    /// Must be enforced at validator registration to prevent rogue key attacks.
    /// </summary>
    public bool VerifyProofOfPossession(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> proofOfPossession)
    {
        // PoP = Sign(sk, pk), so we verify: Verify(pk, pk, pop)
        return Verify(publicKey, publicKey, proofOfPossession);
    }

    /// <inheritdoc />
    byte[] IBlsSigner.GetPublicKey(ReadOnlySpan<byte> privateKey)
        => GetPublicKeyStatic(privateKey);
}
