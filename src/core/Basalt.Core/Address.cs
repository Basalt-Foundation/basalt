using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Basalt.Core;

/// <summary>
/// 20-byte account address derived from the public key via Keccak-256.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Address : IEquatable<Address>, IComparable<Address>
{
    public const int Size = 20;

    private readonly ulong _v0;
    private readonly ulong _v1;
    private readonly uint _v2;

    public static readonly Address Zero = default;

    public Address(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            ThrowInvalidLength(bytes.Length);

        ref var src = ref MemoryMarshal.GetReference(bytes);
        _v0 = Unsafe.ReadUnaligned<ulong>(ref src);
        _v1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 8));
        _v2 = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, 16));
    }

    public bool IsZero => _v0 == 0 && _v1 == 0 && _v2 == 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            ThrowDestinationTooSmall();

        ref var dst = ref MemoryMarshal.GetReference(destination);
        Unsafe.WriteUnaligned(ref dst, _v0);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 8), _v1);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 16), _v2);
    }

    public byte[] ToArray()
    {
        var result = new byte[Size];
        WriteTo(result);
        return result;
    }

    public bool Equals(Address other) =>
        _v0 == other._v0 && _v1 == other._v1 && _v2 == other._v2;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is Address other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_v0, _v1, _v2);

    public int CompareTo(Address other)
    {
        Span<byte> a = stackalloc byte[Size];
        Span<byte> b = stackalloc byte[Size];
        WriteTo(a);
        other.WriteTo(b);
        return a.SequenceCompareTo(b);
    }

    public static bool operator ==(Address left, Address right) => left.Equals(right);
    public static bool operator !=(Address left, Address right) => !left.Equals(right);

    public override string ToString() => ToHexString();

    public string ToHexString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);
        return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static Address FromHexString(string hex)
    {
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex[2..];

        if (hex.Length != Size * 2)
            ThrowInvalidHex();

        Span<byte> bytes = stackalloc byte[Size];
        Convert.FromHexString(hex, bytes, out _, out _);
        return new Address(bytes);
    }

    public static bool TryFromHexString(string hex, out Address result)
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

    /// <summary>
    /// Check if this address is a system contract address (0x0...0001 through 0x0...1FFF).
    /// Uses big-endian comparison over the raw 20-byte address.
    /// </summary>
    public bool IsSystemContract
    {
        get
        {
            // First 16 bytes must be zero
            if (_v0 != 0 || _v1 != 0) return false;
            // Last 4 bytes (_v2) hold the value in native endianness.
            // Convert to big-endian for range check.
            Span<byte> tail = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tail, _v2);
            uint valueBE = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(tail);
            return valueBE >= 1 && valueBE <= 0x1FFF;
        }
    }

    [DoesNotReturn]
    private static void ThrowInvalidLength(int length) =>
        throw new ArgumentException($"Address requires exactly {Size} bytes, got {length}.");

    [DoesNotReturn]
    private static void ThrowDestinationTooSmall() =>
        throw new ArgumentException($"Destination must be at least {Size} bytes.");

    [DoesNotReturn]
    private static void ThrowInvalidHex() =>
        throw new FormatException($"Invalid hex string for Address. Expected {Size * 2} hex characters.");
}
