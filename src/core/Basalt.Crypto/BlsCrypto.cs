using Nethermind.Crypto;

namespace Basalt.Crypto;

/// <summary>
/// Low-level BLS12-381 G1 point arithmetic for threshold encryption.
/// Wraps blst's P1 operations to provide scalar multiplication in G1.
/// </summary>
public static class BlsCrypto
{
    /// <summary>Compressed G1 point size in bytes.</summary>
    public const int G1CompressedSize = 48;

    /// <summary>Scalar size in bytes (big-endian).</summary>
    public const int ScalarSize = 32;

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

        // blst expects scalars in little-endian byte order; our API uses big-endian.
        Span<byte> leScalar = stackalloc byte[ScalarSize];
        for (int i = 0; i < ScalarSize; i++)
            leScalar[i] = scalar[ScalarSize - 1 - i];

        p.Mult(leScalar);
        return p.Compress();
    }

    /// <summary>
    /// G1 point addition: returns the compressed sum of two G1 points.
    /// </summary>
    /// <param name="a">48-byte compressed G1 point.</param>
    /// <param name="b">48-byte compressed G1 point.</param>
    /// <returns>48-byte compressed G1 result (a + b).</returns>
    public static byte[] AddG1(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != G1CompressedSize)
            throw new ArgumentException($"G1 point must be {G1CompressedSize} bytes.", nameof(a));
        if (b.Length != G1CompressedSize)
            throw new ArgumentException($"G1 point must be {G1CompressedSize} bytes.", nameof(b));

        var p1 = new Bls.P1();
        p1.Decode(a);
        var p2 = new Bls.P1();
        p2.Decode(b);
        p1.Add(p2);
        return p1.Compress();
    }

    /// <summary>
    /// Returns the compressed G1 generator point (48 bytes).
    /// </summary>
    public static byte[] G1Generator()
    {
        return Bls.P1Affine.Generator().Compress();
    }
}
