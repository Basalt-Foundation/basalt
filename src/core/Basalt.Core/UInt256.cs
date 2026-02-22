using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Basalt.Core;

/// <summary>
/// 256-bit unsigned integer for token balances, gas calculations, and cryptographic operations.
/// Stored as two UInt128 values in little-endian order (lo, hi).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct UInt256 : IEquatable<UInt256>, IComparable<UInt256>
{
    public static readonly UInt256 Zero = default;
    public static readonly UInt256 One = new(1, 0);
    public static readonly UInt256 MaxValue = new(UInt128.MaxValue, UInt128.MaxValue);

    public readonly UInt128 Lo;
    public readonly UInt128 Hi;

    public UInt256(UInt128 lo, UInt128 hi = default)
    {
        Lo = lo;
        Hi = hi;
    }

    public UInt256(ulong value)
    {
        Lo = value;
        Hi = 0;
    }

    public UInt256(ReadOnlySpan<byte> bytes, bool isBigEndian = false)
    {
        if (bytes.Length != 32)
            throw new ArgumentException("UInt256 requires exactly 32 bytes.");

        if (isBigEndian)
        {
            Hi = new UInt128(
                BinaryPrimitives.ReadUInt64BigEndian(bytes),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
            Lo = new UInt128(
                BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]),
                BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]));
        }
        else
        {
            Lo = new UInt128(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            Hi = new UInt128(
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]));
        }
    }

    public bool IsZero => Lo == 0 && Hi == 0;

    public void WriteTo(Span<byte> destination, bool isBigEndian = false)
    {
        if (destination.Length < 32)
            throw new ArgumentException("Destination must be at least 32 bytes.");

        if (isBigEndian)
        {
            BinaryPrimitives.WriteUInt64BigEndian(destination, (ulong)(Hi >> 64));
            BinaryPrimitives.WriteUInt64BigEndian(destination[8..], (ulong)Hi);
            BinaryPrimitives.WriteUInt64BigEndian(destination[16..], (ulong)(Lo >> 64));
            BinaryPrimitives.WriteUInt64BigEndian(destination[24..], (ulong)Lo);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)Lo);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], (ulong)(Lo >> 64));
            BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], (ulong)Hi);
            BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], (ulong)(Hi >> 64));
        }
    }

    public byte[] ToArray(bool isBigEndian = false)
    {
        var result = new byte[32];
        WriteTo(result, isBigEndian);
        return result;
    }

    // Arithmetic operators

    /// <summary>
    /// Unchecked addition. Wraps silently on overflow.
    /// For overflow-safe arithmetic, use <see cref="CheckedAdd"/> or <see cref="TryAdd"/>.
    /// </summary>
    public static UInt256 operator +(UInt256 a, UInt256 b)
    {
        var lo = a.Lo + b.Lo;
        var carry = lo < a.Lo ? (UInt128)1 : (UInt128)0;
        var hi = a.Hi + b.Hi + carry;
        return new UInt256(lo, hi);
    }

    /// <summary>
    /// Unchecked subtraction. Wraps silently on underflow.
    /// For underflow-safe arithmetic, use <see cref="CheckedSub"/> or <see cref="TrySub"/>.
    /// </summary>
    public static UInt256 operator -(UInt256 a, UInt256 b)
    {
        var lo = a.Lo - b.Lo;
        var borrow = lo > a.Lo ? (UInt128)1 : (UInt128)0;
        var hi = a.Hi - b.Hi - borrow;
        return new UInt256(lo, hi);
    }

    public static UInt256 operator *(UInt256 a, UInt256 b)
    {
        // Schoolbook multiplication with 64-bit limbs and proper carry propagation.
        // Each column accumulates UInt128 products; overflow is tracked in accHi.
        ulong a0 = (ulong)a.Lo;
        ulong a1 = (ulong)(a.Lo >> 64);
        ulong a2 = (ulong)a.Hi;
        ulong a3 = (ulong)(a.Hi >> 64);

        ulong b0 = (ulong)b.Lo;
        ulong b1 = (ulong)(b.Lo >> 64);
        ulong b2 = (ulong)b.Hi;
        ulong b3 = (ulong)(b.Hi >> 64);

        // Column 0: only a0*b0
        UInt128 acc = (UInt128)a0 * b0;
        ulong r0 = (ulong)acc;
        acc >>= 64;

        // Column 1: a0*b1 + a1*b0 + carry
        ulong accHi = 0;
        UInt128 term = (UInt128)a0 * b1;
        acc += term;
        if (acc < term) accHi++;
        term = (UInt128)a1 * b0;
        acc += term;
        if (acc < term) accHi++;
        ulong r1 = (ulong)acc;
        acc = (acc >> 64) | ((UInt128)accHi << 64);
        accHi = 0;

        // Column 2: a0*b2 + a1*b1 + a2*b0 + carry
        term = (UInt128)a0 * b2;
        acc += term;
        if (acc < term) accHi++;
        term = (UInt128)a1 * b1;
        acc += term;
        if (acc < term) accHi++;
        term = (UInt128)a2 * b0;
        acc += term;
        if (acc < term) accHi++;
        ulong r2 = (ulong)acc;
        acc = (acc >> 64) | ((UInt128)accHi << 64);

        // Column 3: a0*b3 + a1*b2 + a2*b1 + a3*b0 + carry (only low 64 bits needed)
        acc += (UInt128)a0 * b3;
        acc += (UInt128)a1 * b2;
        acc += (UInt128)a2 * b1;
        acc += (UInt128)a3 * b0;
        ulong r3 = (ulong)acc;

        return new UInt256(new UInt128(r1, r0), new UInt128(r3, r2));
    }

    public static UInt256 operator /(UInt256 a, UInt256 b)
    {
        if (b.IsZero)
            throw new DivideByZeroException();

        if (a < b)
            return Zero;

        if (b == One)
            return a;

        // Simple shift-subtract division
        DivRem(a, b, out var quotient, out _);
        return quotient;
    }

    public static UInt256 operator %(UInt256 a, UInt256 b)
    {
        if (b.IsZero)
            throw new DivideByZeroException();

        DivRem(a, b, out _, out var remainder);
        return remainder;
    }

    public static void DivRem(UInt256 dividend, UInt256 divisor, out UInt256 quotient, out UInt256 remainder)
    {
        if (divisor.IsZero)
            throw new DivideByZeroException();

        if (dividend < divisor)
        {
            quotient = Zero;
            remainder = dividend;
            return;
        }

        var q = Zero;
        var r = Zero;

        for (int i = 255; i >= 0; i--)
        {
            r = r << 1;
            if (GetBit(dividend, i))
                r = r + One;

            if (r >= divisor)
            {
                r = r - divisor;
                q = SetBit(q, i);
            }
        }

        quotient = q;
        remainder = r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool GetBit(UInt256 value, int bit)
    {
        if (bit < 128)
            return (value.Lo & ((UInt128)1 << bit)) != 0;
        return (value.Hi & ((UInt128)1 << (bit - 128))) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt256 SetBit(UInt256 value, int bit)
    {
        if (bit < 128)
            return new UInt256(value.Lo | ((UInt128)1 << bit), value.Hi);
        return new UInt256(value.Lo, value.Hi | ((UInt128)1 << (bit - 128)));
    }

    // Checked arithmetic (CORE-02: overflow-safe methods for balance/gas calculations)
    public static UInt256 CheckedAdd(UInt256 a, UInt256 b)
    {
        if (!TryAdd(a, b, out var result))
            throw new OverflowException("UInt256 addition overflow.");
        return result;
    }

    public static UInt256 CheckedSub(UInt256 a, UInt256 b)
    {
        if (a < b)
            throw new OverflowException("UInt256 subtraction underflow.");
        return a - b;
    }

    public static UInt256 CheckedMul(UInt256 a, UInt256 b)
    {
        if (a.IsZero || b.IsZero) return Zero;
        // Check for overflow: if a > MaxValue / b, then a * b overflows
        DivRem(MaxValue, a, out var maxB, out _);
        if (b > maxB)
            throw new OverflowException("UInt256 multiplication overflow.");
        return a * b;
    }

    public static bool TryAdd(UInt256 a, UInt256 b, out UInt256 result)
    {
        var lo = a.Lo + b.Lo;
        var carry = lo < a.Lo ? (UInt128)1 : (UInt128)0;
        // Two-stage hi overflow detection (AUDIT C-01):
        // Stage 1: check a.Hi + b.Hi for overflow
        var hiSum = a.Hi + b.Hi;
        if (hiSum < a.Hi)
        {
            result = Zero;
            return false;
        }
        // Stage 2: check hiSum + carry for overflow
        var hi = hiSum + carry;
        if (hi < hiSum)
        {
            result = Zero;
            return false;
        }
        result = new UInt256(lo, hi);
        return true;
    }

    public static bool TrySub(UInt256 a, UInt256 b, out UInt256 result)
    {
        if (a < b)
        {
            result = Zero;
            return false;
        }
        result = a - b;
        return true;
    }

    // Shift operators
    public static UInt256 operator <<(UInt256 value, int shift)
    {
        if (shift == 0) return value;
        if (shift >= 256) return Zero;
        if (shift >= 128)
            return new UInt256(0, value.Lo << (shift - 128));

        var lo = value.Lo << shift;
        var hi = (value.Hi << shift) | (value.Lo >> (128 - shift));
        return new UInt256(lo, hi);
    }

    public static UInt256 operator >>(UInt256 value, int shift)
    {
        if (shift == 0) return value;
        if (shift >= 256) return Zero;
        if (shift >= 128)
            return new UInt256(value.Hi >> (shift - 128), 0);

        var hi = value.Hi >> shift;
        var lo = (value.Lo >> shift) | (value.Hi << (128 - shift));
        return new UInt256(lo, hi);
    }

    // Bitwise operators
    public static UInt256 operator &(UInt256 a, UInt256 b) => new(a.Lo & b.Lo, a.Hi & b.Hi);
    public static UInt256 operator |(UInt256 a, UInt256 b) => new(a.Lo | b.Lo, a.Hi | b.Hi);
    public static UInt256 operator ^(UInt256 a, UInt256 b) => new(a.Lo ^ b.Lo, a.Hi ^ b.Hi);
    public static UInt256 operator ~(UInt256 a) => new(~a.Lo, ~a.Hi);

    // Comparison operators
    public static bool operator <(UInt256 a, UInt256 b) => a.CompareTo(b) < 0;
    public static bool operator >(UInt256 a, UInt256 b) => a.CompareTo(b) > 0;
    public static bool operator <=(UInt256 a, UInt256 b) => a.CompareTo(b) <= 0;
    public static bool operator >=(UInt256 a, UInt256 b) => a.CompareTo(b) >= 0;
    public static bool operator ==(UInt256 a, UInt256 b) => a.Equals(b);
    public static bool operator !=(UInt256 a, UInt256 b) => !a.Equals(b);

    // Implicit conversions
    public static implicit operator UInt256(ulong value) => new(value);
    public static implicit operator UInt256(uint value) => new(value);
    /// <summary>
    /// Implicit conversion from int. Negative values are rejected at runtime.
    /// This is intentionally implicit to allow convenient use of integer literals (e.g., <c>UInt256 x = 0</c>).
    /// LOW-05: Note that compile-time negative literals (e.g., <c>UInt256 x = -1</c>) are accepted
    /// by the compiler but will throw OverflowException at runtime. This is a .NET limitation
    /// with implicit conversions — the check cannot be enforced at compile time.
    /// </summary>
    public static implicit operator UInt256(int value)
    {
        if (value < 0) throw new OverflowException("Cannot convert negative value to UInt256.");
        return new((ulong)value);
    }

    public static explicit operator ulong(UInt256 value)
    {
        if (value.Hi != 0 || (ulong)(value.Lo >> 64) != 0)
            throw new OverflowException("UInt256 value is too large for ulong.");
        return (ulong)value.Lo;
    }

    // Equality
    public bool Equals(UInt256 other) => Lo == other.Lo && Hi == other.Hi;
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is UInt256 other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Lo, Hi);

    public int CompareTo(UInt256 other)
    {
        var hiCmp = Hi.CompareTo(other.Hi);
        return hiCmp != 0 ? hiCmp : Lo.CompareTo(other.Lo);
    }

    public override string ToString()
    {
        if (IsZero) return "0";
        if (Hi == 0) return Lo.ToString();

        // Consistent decimal format via BigInteger for all values
        var bytes = ToArray(isBigEndian: true);
        var big = new BigInteger(bytes, isUnsigned: true, isBigEndian: true);
        return big.ToString();
    }

    public string ToHexString()
    {
        if (IsZero) return "0";
        Span<byte> bytes = stackalloc byte[32];
        WriteTo(bytes, isBigEndian: true);
        return Convert.ToHexString(bytes).ToLowerInvariant().TrimStart('0');
    }

    public static bool TryParse(string? s, out UInt256 result)
    {
        result = Zero;
        if (string.IsNullOrEmpty(s)) return false;
        try
        {
            result = Parse(s);
            return true;
        }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    public static UInt256 Parse(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = s[2..].PadLeft(64, '0');
            Span<byte> bytes = stackalloc byte[32];
            Convert.FromHexString(hex, bytes, out _, out _);
            return new UInt256(bytes, isBigEndian: true);
        }

        // Decimal parsing via BigInteger
        if (!BigInteger.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var big) || big.Sign < 0)
            throw new FormatException($"Invalid UInt256 string: {s}");

        var beBytes = big.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (beBytes.Length > 32)
            throw new OverflowException("Value exceeds UInt256 range.");

        Span<byte> padded = stackalloc byte[32];
        padded.Clear();
        beBytes.AsSpan().CopyTo(padded[(32 - beBytes.Length)..]);
        return new UInt256(padded, isBigEndian: true);
    }
}

file static class BinaryPrimitives
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUInt64LittleEndian(ReadOnlySpan<byte> source) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUInt64BigEndian(ReadOnlySpan<byte> source) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(source);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt64LittleEndian(Span<byte> destination, ulong value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt64BigEndian(Span<byte> destination, ulong value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination, value);
}
