using System.Text;
using Nethermind.Crypto;

namespace Basalt.Confidentiality.Crypto;

/// <summary>
/// Thin wrapper around Nethermind's blst bindings for BLS12-381 pairing-based
/// cryptographic operations. Provides compressed G1/G2 point arithmetic and
/// pairing checks used by Pedersen commitments and Groth16 verification.
/// </summary>
public static class PairingEngine
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// <summary>Compressed G1 point size in bytes.</summary>
    public const int G1CompressedSize = 48;

    /// <summary>Compressed G2 point size in bytes.</summary>
    public const int G2CompressedSize = 96;

    /// <summary>Scalar field element size in bytes (big-endian).</summary>
    public const int ScalarSize = 32;

    // ── Cached generators ────────────────────────────────────────────────────

    private static readonly byte[] s_g1Generator;
    private static readonly byte[] s_g2Generator;

    static PairingEngine()
    {
        s_g1Generator = Bls.P1Affine.Generator().Compress();
        s_g2Generator = Bls.P2Affine.Generator().Compress();
    }

    /// <summary>Compressed BLS12-381 G1 generator (48 bytes). Returns a defensive copy.</summary>
    public static byte[] G1Generator => (byte[])s_g1Generator.Clone();

    /// <summary>Compressed BLS12-381 G2 generator (96 bytes). Returns a defensive copy.</summary>
    public static byte[] G2Generator => (byte[])s_g2Generator.Clone();

    // ── G1 Arithmetic ────────────────────────────────────────────────────────

    /// <summary>
    /// Scalar multiplication in G1: returns <paramref name="scalar"/> * P where P
    /// is a compressed G1 point.
    /// </summary>
    /// <param name="point">48-byte compressed G1 point.</param>
    /// <param name="scalar">32-byte big-endian scalar.</param>
    /// <returns>48-byte compressed G1 result.</returns>
    public static byte[] ScalarMultG1(ReadOnlySpan<byte> point, ReadOnlySpan<byte> scalar)
    {
        if (point.Length != G1CompressedSize)
            throw new ArgumentException($"G1 point must be {G1CompressedSize} bytes.", nameof(point));
        if (scalar.Length != ScalarSize)
            throw new ArgumentException($"Scalar must be {ScalarSize} bytes.", nameof(scalar));

        var p = new Bls.P1();
        p.Decode(point);

        // blst expects scalars in little-endian byte order, but our API
        // accepts big-endian (matching standard ECC conventions). Reverse.
        Span<byte> leScalar = stackalloc byte[ScalarSize];
        for (int i = 0; i < ScalarSize; i++)
            leScalar[i] = scalar[ScalarSize - 1 - i];

        p.Mult(leScalar);
        return p.Compress();
    }

    /// <summary>
    /// Point addition in G1: returns A + B where both are compressed G1 points.
    /// </summary>
    /// <param name="a">48-byte compressed G1 point.</param>
    /// <param name="b">48-byte compressed G1 point.</param>
    /// <returns>48-byte compressed G1 result.</returns>
    public static byte[] AddG1(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != G1CompressedSize)
            throw new ArgumentException($"G1 point must be {G1CompressedSize} bytes.", nameof(a));
        if (b.Length != G1CompressedSize)
            throw new ArgumentException($"G1 point must be {G1CompressedSize} bytes.", nameof(b));

        var pa = new Bls.P1();
        pa.Decode(a);

        var pb = new Bls.P1Affine();
        pb.Decode(b);

        pa.Add(pb);
        return pa.Compress();
    }

    /// <summary>
    /// Point negation in G1: returns -P where P is a compressed G1 point.
    /// </summary>
    /// <param name="point">48-byte compressed G1 point.</param>
    /// <returns>48-byte compressed negated G1 point.</returns>
    public static byte[] NegG1(ReadOnlySpan<byte> point)
    {
        if (point.Length != G1CompressedSize)
            throw new ArgumentException($"G1 point must be {G1CompressedSize} bytes.", nameof(point));

        var p = new Bls.P1();
        p.Decode(point);
        p.Neg();
        return p.Compress();
    }

    /// <summary>
    /// Hash an arbitrary message to a G1 point using the BLS12-381 hash-to-curve
    /// algorithm (RFC 9380).
    /// </summary>
    /// <param name="message">The message bytes to hash.</param>
    /// <param name="dst">Domain separation tag (ASCII string).</param>
    /// <returns>48-byte compressed G1 point.</returns>
    public static byte[] HashToG1(ReadOnlySpan<byte> message, string dst)
    {
        byte[] dstBytes = Encoding.UTF8.GetBytes(dst);
        var p = new Bls.P1();
        p.HashTo(message, dstBytes, ReadOnlySpan<byte>.Empty);
        return p.Compress();
    }

    // ── G2 Arithmetic ────────────────────────────────────────────────────────

    /// <summary>
    /// Point negation in G2: returns -Q where Q is a compressed G2 point.
    /// </summary>
    /// <param name="point">96-byte compressed G2 point.</param>
    /// <returns>96-byte compressed negated G2 point.</returns>
    public static byte[] NegG2(ReadOnlySpan<byte> point)
    {
        if (point.Length != G2CompressedSize)
            throw new ArgumentException($"G2 point must be {G2CompressedSize} bytes.", nameof(point));

        var p = new Bls.P2();
        p.Decode(point);
        p.Neg();
        return p.Compress();
    }

    // ── Pairing operations ───────────────────────────────────────────────────

    /// <summary>
    /// Compute a single Miller loop: ML(g1, g2). The result is a GT element
    /// (pairing target) that can be used with <see cref="FinalVerify"/>.
    /// </summary>
    /// <param name="g1">48-byte compressed G1 point.</param>
    /// <param name="g2">96-byte compressed G2 point.</param>
    /// <returns>A <see cref="Bls.PT"/> representing the Miller loop output.</returns>
    public static Bls.PT ComputeMillerLoop(ReadOnlySpan<byte> g1, ReadOnlySpan<byte> g2)
    {
        if (g1.Length != G1CompressedSize)
            throw new ArgumentException($"G1 point must be {G1CompressedSize} bytes.", nameof(g1));
        if (g2.Length != G2CompressedSize)
            throw new ArgumentException($"G2 point must be {G2CompressedSize} bytes.", nameof(g2));

        var g1Aff = new Bls.P1Affine();
        g1Aff.Decode(g1);

        var g2Aff = new Bls.P2Affine();
        g2Aff.Decode(g2);

        var pt = new Bls.PT();
        pt.MillerLoop(g2Aff, g1Aff);
        return pt;
    }

    /// <summary>
    /// Two-pairing equality check: verifies e(g1a, g2a) == e(g1b, g2b).
    /// Computes both Miller loops, applies the final exponentiation to each,
    /// and compares the resulting GT elements.
    /// </summary>
    /// <param name="g1a">First 48-byte compressed G1 point.</param>
    /// <param name="g2a">First 96-byte compressed G2 point.</param>
    /// <param name="g1b">Second 48-byte compressed G1 point.</param>
    /// <param name="g2b">Second 96-byte compressed G2 point.</param>
    /// <returns><c>true</c> if the pairings are equal; otherwise <c>false</c>.</returns>
    public static bool PairingCheck(
        ReadOnlySpan<byte> g1a, ReadOnlySpan<byte> g2a,
        ReadOnlySpan<byte> g1b, ReadOnlySpan<byte> g2b)
    {
        try
        {
            Bls.PT lhs = ComputeMillerLoop(g1a, g2a);
            Bls.PT rhs = ComputeMillerLoop(g1b, g2b);

            return lhs.FinalExp().IsEqual(rhs.FinalExp());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether a compressed G1 point is the identity (point at infinity).
    /// The BLS12-381 compressed identity point has the form 0xC0 followed by 47 zero bytes.
    /// </summary>
    /// <param name="point">48-byte compressed G1 point.</param>
    /// <returns><c>true</c> if the point is the identity element.</returns>
    public static bool IsG1Identity(ReadOnlySpan<byte> point)
    {
        if (point.Length != G1CompressedSize)
            throw new ArgumentException($"G1 point must be {G1CompressedSize} bytes.", nameof(point));

        // In compressed form, the G1 identity is 0xC0 followed by 47 zero bytes.
        // The high bit (0x80) signals compression; the second-highest bit (0x40)
        // signals the point at infinity.
        if (point[0] != 0xC0)
            return false;

        for (int i = 1; i < G1CompressedSize; i++)
        {
            if (point[i] != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether a compressed G2 point is the identity (point at infinity).
    /// The BLS12-381 compressed G2 identity has the form 0xC0 followed by 95 zero bytes.
    /// </summary>
    /// <param name="point">96-byte compressed G2 point.</param>
    /// <returns><c>true</c> if the point is the identity element.</returns>
    public static bool IsG2Identity(ReadOnlySpan<byte> point)
    {
        if (point.Length != G2CompressedSize)
            throw new ArgumentException($"G2 point must be {G2CompressedSize} bytes.", nameof(point));

        if (point[0] != 0xC0)
            return false;

        for (int i = 1; i < G2CompressedSize; i++)
        {
            if (point[i] != 0)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Validate that a compressed G1 point is on the curve AND in the G1 subgroup.
    /// Uses blst's native subgroup check.
    /// </summary>
    /// <param name="point">48-byte compressed G1 point.</param>
    /// <returns><c>true</c> if the point is a valid G1 subgroup element.</returns>
    public static bool IsValidG1(ReadOnlySpan<byte> point)
    {
        if (point.Length != G1CompressedSize)
            return false;

        try
        {
            var p = new Bls.P1Affine();
            p.Decode(point);
            return p.InGroup();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validate that a compressed G2 point is on the curve AND in the G2 subgroup.
    /// This is critical for security: G2 has a non-trivial cofactor, so points can
    /// be on the curve but NOT in the subgroup, enabling small-subgroup attacks
    /// that break Groth16 soundness.
    /// </summary>
    /// <param name="point">96-byte compressed G2 point.</param>
    /// <returns><c>true</c> if the point is a valid G2 subgroup element.</returns>
    public static bool IsValidG2(ReadOnlySpan<byte> point)
    {
        if (point.Length != G2CompressedSize)
            return false;

        try
        {
            var p = new Bls.P2Affine();
            p.Decode(point);
            return p.InGroup();
        }
        catch
        {
            return false;
        }
    }
}
