using System.Security.Cryptography;
using System.Text;
using Basalt.Core;

namespace Basalt.Confidentiality.Crypto;

/// <summary>
/// Pedersen commitment scheme on BLS12-381 G1.
///
/// A Pedersen commitment to value <c>v</c> with blinding factor <c>r</c> is:
///   <c>C = v * G + r * H</c>
/// where G is the standard G1 generator and H is a nothing-up-my-sleeve
/// generator derived via hash-to-curve.
///
/// The scheme is perfectly hiding and computationally binding, and is
/// additively homomorphic: <c>Commit(v1, r1) + Commit(v2, r2) = Commit(v1+v2, r1+r2)</c>.
/// </summary>
public static class PedersenCommitment
{
    /// <summary>Domain separation tag used to derive the H generator.</summary>
    private const string PedersenDst = "BASALT_PEDERSEN_DST";

    /// <summary>Message hashed to produce the H generator.</summary>
    private static readonly byte[] s_hMessage = Encoding.UTF8.GetBytes("basalt_pedersen_h");

    /// <summary>
    /// Cached 48-byte compressed G1 point used as the second (blinding) generator H.
    /// Derived deterministically via hash-to-curve so that the discrete log
    /// relationship between G and H is unknown.
    /// </summary>
    private static readonly byte[] s_hGenerator;

    static PedersenCommitment()
    {
        s_hGenerator = PairingEngine.HashToG1(s_hMessage, PedersenDst);
    }

    /// <summary>
    /// The Pedersen H generator (48-byte compressed G1 point). Returns a defensive copy.
    /// </summary>
    public static byte[] HGenerator => (byte[])s_hGenerator.Clone();

    /// <summary>
    /// Compute a Pedersen commitment: <c>C = value * G + blindingFactor * H</c>.
    /// </summary>
    /// <param name="value">The value to commit to.</param>
    /// <param name="blindingFactor">32-byte big-endian blinding factor (random scalar).</param>
    /// <returns>48-byte compressed G1 point representing the commitment.</returns>
    public static byte[] Commit(UInt256 value, ReadOnlySpan<byte> blindingFactor)
    {
        if (blindingFactor.Length != PairingEngine.ScalarSize)
            throw new ArgumentException(
                $"Blinding factor must be {PairingEngine.ScalarSize} bytes.",
                nameof(blindingFactor));

        // F-13: Always perform scalar multiplication (constant-time, no branching on secret value).
        // The blst library handles zero scalars correctly, returning the identity point.
        Span<byte> scalar = stackalloc byte[PairingEngine.ScalarSize];
        value.WriteTo(scalar, isBigEndian: true);
        byte[] vG = PairingEngine.ScalarMultG1(PairingEngine.G1Generator, scalar);

        // Compute blindingFactor * H
        byte[] rH = PairingEngine.ScalarMultG1(s_hGenerator, blindingFactor);

        // C = vG + rH
        return PairingEngine.AddG1(vG, rH);
    }

    /// <summary>
    /// Verify that a commitment opens to the given value and blinding factor.
    /// Recomputes <c>C' = value * G + blindingFactor * H</c> and checks
    /// byte-equality with the provided commitment.
    /// </summary>
    /// <param name="commitment">48-byte compressed G1 commitment to verify.</param>
    /// <param name="value">The claimed committed value.</param>
    /// <param name="blindingFactor">32-byte big-endian blinding factor.</param>
    /// <returns><c>true</c> if the commitment is valid for the given opening.</returns>
    public static bool Open(ReadOnlySpan<byte> commitment, UInt256 value, ReadOnlySpan<byte> blindingFactor)
    {
        if (commitment.Length != PairingEngine.G1CompressedSize)
            return false;

        try
        {
            byte[] recomputed = Commit(value, blindingFactor);
            // M-01: Use constant-time comparison to prevent timing side-channels.
            // Variable-time SequenceEqual leaks information about matching prefix
            // length, which could help an attacker brute-force blinding factors.
            return CryptographicOperations.FixedTimeEquals(commitment, recomputed);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// F-10: Generate a Pedersen commitment with a securely random blinding factor.
    /// This prevents accidental reuse of blinding factors, which would leak
    /// the difference of committed values.
    /// </summary>
    /// <param name="value">The value to commit to.</param>
    /// <returns>
    /// A tuple of (commitment, blindingFactor) where the blinding factor was
    /// generated from a cryptographic random source.
    /// </returns>
    public static (byte[] Commitment, byte[] BlindingFactor) CommitRandom(UInt256 value)
    {
        var blindingFactor = new byte[PairingEngine.ScalarSize];
        RandomNumberGenerator.Fill(blindingFactor);
        try
        {
            var commitment = Commit(value, blindingFactor);
            return (commitment, blindingFactor);
        }
        catch
        {
            // L-03: Zero blinding factor if commitment fails to prevent
            // secret material from lingering in memory.
            CryptographicOperations.ZeroMemory(blindingFactor);
            throw;
        }
    }

    /// <summary>
    /// Homomorphically add multiple commitments:
    ///   <c>C_sum = C_1 + C_2 + ... + C_n</c>.
    ///
    /// Because Pedersen commitments are additively homomorphic, the result
    /// commits to the sum of the individual values with the sum of the
    /// individual blinding factors.
    /// </summary>
    /// <param name="commitments">Array of 48-byte compressed G1 commitments.</param>
    /// <returns>48-byte compressed G1 point representing the summed commitment.</returns>
    public static byte[] AddCommitments(ReadOnlySpan<byte[]> commitments)
    {
        if (commitments.Length == 0)
            throw new ArgumentException("At least one commitment is required.", nameof(commitments));

        if (commitments.Length == 1)
            return (byte[])commitments[0].Clone();

        byte[] accumulator = (byte[])commitments[0].Clone();
        for (int i = 1; i < commitments.Length; i++)
        {
            accumulator = PairingEngine.AddG1(accumulator, commitments[i]);
        }

        return accumulator;
    }

    /// <summary>
    /// Homomorphically subtract two commitments: <c>C_diff = A - B</c>.
    ///
    /// Equivalent to <c>A + (-B)</c>. The result commits to <c>v_a - v_b</c>
    /// with blinding factor <c>r_a - r_b</c>.
    /// </summary>
    /// <param name="a">48-byte compressed G1 commitment (minuend).</param>
    /// <param name="b">48-byte compressed G1 commitment (subtrahend).</param>
    /// <returns>48-byte compressed G1 point representing A - B.</returns>
    public static byte[] SubtractCommitments(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        byte[] negB = PairingEngine.NegG1(b);
        return PairingEngine.AddG1(a, negB);
    }

    /// <summary>
    /// Returns the 48-byte compressed G1 identity (point at infinity).
    /// </summary>
    private static byte[] IdentityG1()
    {
        // BLS12-381 compressed G1 identity: 0xC0 followed by 47 zero bytes.
        var identity = new byte[PairingEngine.G1CompressedSize];
        identity[0] = 0xC0;
        return identity;
    }
}
