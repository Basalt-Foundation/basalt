using System.Globalization;
using System.Numerics;
using Basalt.Core;

namespace Basalt.Execution.Dex.Math;

/// <summary>
/// Concentrated liquidity tick math — converts between ticks and Q64.96 sqrt price ratios.
/// Each tick represents a 0.01% (1 basis point) price change: price(tick) = 1.0001^tick.
/// Sqrt prices are stored as Q64.96 fixed-point: sqrtPriceX96 = sqrt(1.0001^tick) * 2^96.
/// </summary>
/// <remarks>
/// Algorithm uses precomputed Q128.128 reciprocal constants for powers of sqrt(1.0001).
/// For |tick|, the constants give 1/sqrt(1.0001)^|tick| in Q128.128.
/// Positive ticks are then inverted to get sqrt(1.0001)^tick.
/// Ported from Uniswap v3 TickMath.sol to UInt256/BigInteger arithmetic.
/// </remarks>
public static class TickMath
{
    /// <summary>Minimum supported tick value.</summary>
    public const int MinTick = -887272;

    /// <summary>Maximum supported tick value.</summary>
    public const int MaxTick = 887272;

    /// <summary>Minimum sqrt price ratio (Q64.96) — corresponds to MinTick.</summary>
    public static readonly UInt256 MinSqrtRatio;

    /// <summary>Maximum sqrt price ratio (Q64.96) — corresponds to MaxTick.</summary>
    public static readonly UInt256 MaxSqrtRatio;

    /// <summary>Q96 = 2^96 — the fixed-point scaling factor for sqrt prices.</summary>
    public static readonly UInt256 Q96 = UInt256.One << 96;

    // Precomputed Q128.128 constants: each is 2^128 / sqrt(1.0001)^(2^i).
    // These exact hex values come from the Uniswap v3 TickMath.sol reference implementation.
    // The algorithm multiplies these together for each set bit of |tick| to get
    // ratio = 2^128 / sqrt(1.0001)^|tick| in Q128.128 format.
    private static readonly BigInteger[] MagicConstants;

    static TickMath()
    {
        // Parse constants from hex (Uniswap v3 TickMath.sol values)
        MagicConstants =
        [
            BigInteger.Parse("0fffcb933bd6fad37aa2d162d1a594001", NumberStyles.HexNumber), // bit 0:  1/sqrt(1.0001)^1
            BigInteger.Parse("0fff97272373d413259a46990580e213a", NumberStyles.HexNumber), // bit 1:  1/sqrt(1.0001)^2
            BigInteger.Parse("0fff2e50f5f656932ef12357cf3c7fdcc", NumberStyles.HexNumber), // bit 2:  1/sqrt(1.0001)^4
            BigInteger.Parse("0ffe5caca7e10e4e61c3624eaa0941cd0", NumberStyles.HexNumber), // bit 3:  1/sqrt(1.0001)^8
            BigInteger.Parse("0ffcb9843d60f6159c9db58835c926644", NumberStyles.HexNumber), // bit 4:  1/sqrt(1.0001)^16
            BigInteger.Parse("0ff973b41fa98c081472e6896dfb254c0", NumberStyles.HexNumber), // bit 5:  1/sqrt(1.0001)^32
            BigInteger.Parse("0ff2ea16466c96a3843ec78b326b52861", NumberStyles.HexNumber), // bit 6:  1/sqrt(1.0001)^64
            BigInteger.Parse("0fe5dee046a99a2a811c461f1969c3053", NumberStyles.HexNumber), // bit 7:  1/sqrt(1.0001)^128
            BigInteger.Parse("0fcbe86c7900a88aedcffc83b479aa3a4", NumberStyles.HexNumber), // bit 8:  1/sqrt(1.0001)^256
            BigInteger.Parse("0f987a7253ac413176f2b074cf7815e54", NumberStyles.HexNumber), // bit 9:  1/sqrt(1.0001)^512
            BigInteger.Parse("0f3392b0822b70005940c7a398e4b70f3", NumberStyles.HexNumber), // bit 10: 1/sqrt(1.0001)^1024
            BigInteger.Parse("0e7159475a2c29b7443b29c7fa6e889d9", NumberStyles.HexNumber), // bit 11: 1/sqrt(1.0001)^2048
            BigInteger.Parse("0d097f3bdfd2022b8845ad8f792aa5825", NumberStyles.HexNumber), // bit 12: 1/sqrt(1.0001)^4096
            BigInteger.Parse("0a9f746462d870fdf8a65dc1f90e061e5", NumberStyles.HexNumber), // bit 13: 1/sqrt(1.0001)^8192
            BigInteger.Parse("070d869a156d2a1b890bb3df62baf32f7", NumberStyles.HexNumber), // bit 14: 1/sqrt(1.0001)^16384
            BigInteger.Parse("031be135f97d08fd981231505542fcfa6", NumberStyles.HexNumber), // bit 15: 1/sqrt(1.0001)^32768
            BigInteger.Parse("009aa508b5b7a84e1c677de54f3e99bc9", NumberStyles.HexNumber), // bit 16: 1/sqrt(1.0001)^65536
            BigInteger.Parse("0005d6af8dedb81196699c329225ee604", NumberStyles.HexNumber), // bit 17: 1/sqrt(1.0001)^131072
            BigInteger.Parse("000002216e584f5fa1ea926041bedfe98", NumberStyles.HexNumber), // bit 18: 1/sqrt(1.0001)^262144
            BigInteger.Parse("00000000048a170391f7dc42444e8fa2", NumberStyles.HexNumber), // bit 19: 1/sqrt(1.0001)^524288
        ];

        // Compute min/max sqrt ratios from the tick boundaries
        MinSqrtRatio = GetSqrtRatioAtTickUnchecked(MinTick);
        MaxSqrtRatio = GetSqrtRatioAtTickUnchecked(MaxTick);
    }

    /// <summary>
    /// Computes the sqrt price ratio at the given tick as a Q64.96 fixed-point number.
    /// </summary>
    /// <param name="tick">The tick index. Must be in [MinTick, MaxTick].</param>
    /// <returns>The sqrt price ratio as a Q64.96 UInt256.</returns>
    public static UInt256 GetSqrtRatioAtTick(int tick)
    {
        if (tick < MinTick || tick > MaxTick)
            throw new ArgumentOutOfRangeException(nameof(tick), $"Tick {tick} out of range [{MinTick}, {MaxTick}]");

        return GetSqrtRatioAtTickUnchecked(tick);
    }

    /// <summary>
    /// Internal implementation without range checks — used by static constructor.
    /// </summary>
    private static UInt256 GetSqrtRatioAtTickUnchecked(int tick)
    {
        uint absTick = tick < 0 ? (uint)(-tick) : (uint)tick;

        // Start with 1.0 in Q128.128
        var ratio = BigInteger.One << 128;

        // Multiply by reciprocal constants for each set bit of |tick|.
        // After this loop: ratio ≈ 2^128 / sqrt(1.0001)^|tick|
        for (int i = 0; i < MagicConstants.Length; i++)
        {
            if ((absTick & (1u << i)) != 0)
            {
                ratio = ratio * MagicConstants[i] >> 128;
            }
        }

        // For positive ticks, we want sqrt(1.0001)^tick, but we have 1/sqrt(1.0001)^tick.
        // Invert: ratio = 2^256 / ratio ≈ 2^128 * sqrt(1.0001)^tick
        if (tick > 0)
        {
            ratio = ((BigInteger.One << 256) - 1) / ratio;
        }

        // Convert from Q128.128 to Q64.96 by right-shifting 32 bits.
        // Round up if there's a remainder.
        var shifted = ratio >> 32;
        if ((ratio & ((BigInteger.One << 32) - 1)) != BigInteger.Zero)
            shifted += 1;

        return FromBig(shifted);
    }

    /// <summary>
    /// Computes the tick at the given sqrt price ratio (floor).
    /// The result satisfies: GetSqrtRatioAtTick(result) &lt;= sqrtPriceX96 &lt; GetSqrtRatioAtTick(result + 1).
    /// </summary>
    /// <param name="sqrtPriceX96">The sqrt price as a Q64.96 value. Must be in [MinSqrtRatio, MaxSqrtRatio].</param>
    /// <returns>The greatest tick such that GetSqrtRatioAtTick(tick) &lt;= sqrtPriceX96.</returns>
    public static int GetTickAtSqrtRatio(UInt256 sqrtPriceX96)
    {
        if (sqrtPriceX96 < MinSqrtRatio || sqrtPriceX96 > MaxSqrtRatio)
            throw new ArgumentOutOfRangeException(nameof(sqrtPriceX96), "Sqrt ratio out of range");

        // Binary search for the greatest tick where GetSqrtRatioAtTick(tick) <= sqrtPriceX96.
        int lo = MinTick;
        int hi = MaxTick;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            var sqrtAtMid = GetSqrtRatioAtTick(mid);
            if (sqrtAtMid <= sqrtPriceX96)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    private static BigInteger ToBig(UInt256 value)
    {
        return new BigInteger(value.ToArray(isBigEndian: false), isUnsigned: true);
    }

    private static UInt256 FromBig(BigInteger value)
    {
        if (value.Sign < 0)
            throw new OverflowException("TickMath: result is negative");

        var bytes = value.ToByteArray(isUnsigned: true);
        if (bytes.Length > 32)
            throw new OverflowException("TickMath: result exceeds UInt256 range");

        Span<byte> padded = stackalloc byte[32];
        padded.Clear();
        bytes.CopyTo(padded);
        return new UInt256(padded);
    }
}
