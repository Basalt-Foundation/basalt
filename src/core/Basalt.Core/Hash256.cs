using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Basalt.Core;

/// <summary>
/// 32-byte hash value. Immutable value type used for block hashes, state roots, transaction hashes, etc.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Hash256 : IEquatable<Hash256>, IComparable<Hash256>
{
    public const int Size = 32;

    private readonly ulong _v0;
    private readonly ulong _v1;
    private readonly ulong _v2;
    private readonly ulong _v3;

    public static readonly Hash256 Zero = default;

    public Hash256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            ThrowInvalidLength(bytes.Length);

        ref var src = ref MemoryMarshal.GetReference(bytes);
        _v0 = Unsafe.ReadUnaligned<ulong>(ref src);
        _v1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 8));
        _v2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 16));
        _v3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 24));
    }

    public Hash256(ulong v0, ulong v1, ulong v2, ulong v3)
    {
        _v0 = v0;
        _v1 = v1;
        _v2 = v2;
        _v3 = v3;
    }

    public bool IsZero => _v0 == 0 && _v1 == 0 && _v2 == 0 && _v3 == 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            ThrowDestinationTooSmall();

        ref var dst = ref MemoryMarshal.GetReference(destination);
        Unsafe.WriteUnaligned(ref dst, _v0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 8), _v1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 16), _v2);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 24), _v3);
    }

    public byte[] ToArray()
    {
        var result = new byte[Size];
        WriteTo(result);
        return result;
    }

    public bool Equals(Hash256 other) =>
        _v0 == other._v0 && _v1 == other._v1 && _v2 == other._v2 && _v3 == other._v3;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is Hash256 other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_v0, _v1, _v2, _v3);

    public int CompareTo(Hash256 other)
    {
        Span<byte> a = stackalloc byte[Size];
        Span<byte> b = stackalloc byte[Size];
        WriteTo(a);
        other.WriteTo(b);
        return a.SequenceCompareTo(b);
    }

    public static bool operator ==(Hash256 left, Hash256 right) => left.Equals(right);
    public static bool operator !=(Hash256 left, Hash256 right) => !left.Equals(right);

    public override string ToString() => ToHexString();

    public string ToHexString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);
        return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static Hash256 FromHexString(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];

        if (hex.Length != Size * 2)
            ThrowInvalidHex();

        Span<byte> bytes = stackalloc byte[Size];
        Convert.FromHexString(hex, bytes, out _, out _);
        return new Hash256(bytes);
    }

    public static bool TryFromHexString(string hex, out Hash256 result)
    {
        try
        {
            result = FromHexString(hex);
            return true;
        }
        catch
        {
            result = Zero;
            return false;
        }
    }

    [DoesNotReturn]
    private static void ThrowInvalidLength(int length) =>
        throw new ArgumentException($"Hash256 requires exactly {Size} bytes, got {length}.");

    [DoesNotReturn]
    private static void ThrowDestinationTooSmall() =>
        throw new ArgumentException($"Destination must be at least {Size} bytes.");

    [DoesNotReturn]
    private static void ThrowInvalidHex() =>
        throw new FormatException($"Invalid hex string for Hash256. Expected {Size * 2} hex characters.");
}
