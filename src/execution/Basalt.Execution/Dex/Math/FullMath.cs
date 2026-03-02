using System.Numerics;
using Basalt.Core;

namespace Basalt.Execution.Dex.Math;

/// <summary>
/// Full-precision 256-bit math using 512-bit intermediates via <see cref="BigInteger"/>.
/// All multiply-then-divide operations go through BigInteger to prevent overflow.
/// This is critical for AMM calculations where intermediate products commonly exceed 256 bits.
/// </summary>
public static class FullMath
{
    /// <summary>
    /// Computes <c>(a * b) / denominator</c> with full 512-bit precision on the intermediate product.
    /// </summary>
    /// <param name="a">First multiplicand.</param>
    /// <param name="b">Second multiplicand.</param>
    /// <param name="denominator">The divisor. Must be non-zero.</param>
    /// <returns>The truncated quotient.</returns>
    /// <exception cref="DivideByZeroException">Thrown when <paramref name="denominator"/> is zero.</exception>
    /// <exception cref="OverflowException">Thrown when the result exceeds 256 bits.</exception>
    public static UInt256 MulDiv(UInt256 a, UInt256 b, UInt256 denominator)
    {
        if (denominator.IsZero)
            throw new DivideByZeroException("MulDiv: denominator is zero");

        var result = ToBig(a) * ToBig(b) / ToBig(denominator);
        return FromBig(result);
    }

    /// <summary>
    /// Computes <c>(a * b) / denominator</c>, rounded up (ceiling division).
    /// </summary>
    /// <param name="a">First multiplicand.</param>
    /// <param name="b">Second multiplicand.</param>
    /// <param name="denominator">The divisor. Must be non-zero.</param>
    /// <returns>The ceiling quotient.</returns>
    /// <exception cref="DivideByZeroException">Thrown when <paramref name="denominator"/> is zero.</exception>
    /// <exception cref="OverflowException">Thrown when the result exceeds 256 bits.</exception>
    public static UInt256 MulDivRoundingUp(UInt256 a, UInt256 b, UInt256 denominator)
    {
        if (denominator.IsZero)
            throw new DivideByZeroException("MulDivRoundingUp: denominator is zero");

        var bigD = ToBig(denominator);
        var product = ToBig(a) * ToBig(b);
        var (quotient, remainder) = BigInteger.DivRem(product, bigD);

        if (!remainder.IsZero)
            quotient += BigInteger.One;

        return FromBig(quotient);
    }

    /// <summary>
    /// Computes <c>(a * b) % modulus</c> with full precision.
    /// </summary>
    /// <param name="a">First multiplicand.</param>
    /// <param name="b">Second multiplicand.</param>
    /// <param name="modulus">The modulus. Must be non-zero.</param>
    /// <returns>The remainder.</returns>
    /// <exception cref="DivideByZeroException">Thrown when <paramref name="modulus"/> is zero.</exception>
    public static UInt256 MulMod(UInt256 a, UInt256 b, UInt256 modulus)
    {
        if (modulus.IsZero)
            throw new DivideByZeroException("MulMod: modulus is zero");

        var result = ToBig(a) * ToBig(b) % ToBig(modulus);
        return FromBig(result);
    }

    /// <summary>
    /// Integer square root (floor) via Newton's method.
    /// Returns the largest <c>x</c> such that <c>x * x &lt;= n</c>.
    /// </summary>
    /// <param name="n">The radicand.</param>
    /// <returns>Floor of the square root.</returns>
    public static UInt256 Sqrt(UInt256 n)
    {
        if (n.IsZero) return UInt256.Zero;
        if (n == UInt256.One) return UInt256.One;

        var two = new UInt256(2);
        var x = n;
        var y = (x + UInt256.One) / two;

        while (y < x)
        {
            x = y;
            y = (x + n / x) / two;
        }

        return x;
    }

    public static BigInteger ToBig(UInt256 value)
    {
        return new BigInteger(value.ToArray(isBigEndian: false), isUnsigned: true);
    }

    public static UInt256 FromBig(BigInteger value)
    {
        if (value.Sign < 0)
            throw new OverflowException("FullMath: result is negative");

        var bytes = value.ToByteArray(isUnsigned: true);
        if (bytes.Length > 32)
            throw new OverflowException("FullMath: result exceeds UInt256 range");

        Span<byte> padded = stackalloc byte[32];
        padded.Clear();
        bytes.CopyTo(padded);
        return new UInt256(padded);
    }
}
