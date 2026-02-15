using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Basalt.Core;

/// <summary>
/// Ed25519 signature (64 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct Signature : IEquatable<Signature>
{
    public const int Size = 64;

    private readonly ulong _v0, _v1, _v2, _v3, _v4, _v5, _v6, _v7;

    public static readonly Signature Empty = default;

    public Signature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"Signature requires exactly {Size} bytes, got {bytes.Length}.");

        var ulongs = MemoryMarshal.Cast<byte, ulong>(bytes);
        _v0 = ulongs[0]; _v1 = ulongs[1]; _v2 = ulongs[2]; _v3 = ulongs[3];
        _v4 = ulongs[4]; _v5 = ulongs[5]; _v6 = ulongs[6]; _v7 = ulongs[7];
    }

    public bool IsEmpty => _v0 == 0 && _v1 == 0 && _v2 == 0 && _v3 == 0 &&
                           _v4 == 0 && _v5 == 0 && _v6 == 0 && _v7 == 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination must be at least {Size} bytes.");

        var ulongs = MemoryMarshal.Cast<byte, ulong>(destination);
        ulongs[0] = _v0; ulongs[1] = _v1; ulongs[2] = _v2; ulongs[3] = _v3;
        ulongs[4] = _v4; ulongs[5] = _v5; ulongs[6] = _v6; ulongs[7] = _v7;
    }

    public byte[] ToArray()
    {
        var result = new byte[Size];
        WriteTo(result);
        return result;
    }

    public bool Equals(Signature other) =>
        _v0 == other._v0 && _v1 == other._v1 && _v2 == other._v2 && _v3 == other._v3 &&
        _v4 == other._v4 && _v5 == other._v5 && _v6 == other._v6 && _v7 == other._v7;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is Signature other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_v0, _v1, _v2, _v3);

    public static bool operator ==(Signature left, Signature right) => left.Equals(right);
    public static bool operator !=(Signature left, Signature right) => !left.Equals(right);

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);
        return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// Ed25519 public key (32 bytes).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct PublicKey : IEquatable<PublicKey>
{
    public const int Size = 32;

    private readonly ulong _v0, _v1, _v2, _v3;

    public static readonly PublicKey Empty = default;

    public PublicKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"PublicKey requires exactly {Size} bytes, got {bytes.Length}.");

        var ulongs = MemoryMarshal.Cast<byte, ulong>(bytes);
        _v0 = ulongs[0]; _v1 = ulongs[1]; _v2 = ulongs[2]; _v3 = ulongs[3];
    }

    public bool IsEmpty => _v0 == 0 && _v1 == 0 && _v2 == 0 && _v3 == 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination must be at least {Size} bytes.");

        var ulongs = MemoryMarshal.Cast<byte, ulong>(destination);
        ulongs[0] = _v0; ulongs[1] = _v1; ulongs[2] = _v2; ulongs[3] = _v3;
    }

    public byte[] ToArray()
    {
        var result = new byte[Size];
        WriteTo(result);
        return result;
    }

    public bool Equals(PublicKey other) =>
        _v0 == other._v0 && _v1 == other._v1 && _v2 == other._v2 && _v3 == other._v3;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is PublicKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_v0, _v1, _v2, _v3);

    public static bool operator ==(PublicKey left, PublicKey right) => left.Equals(right);
    public static bool operator !=(PublicKey left, PublicKey right) => !left.Equals(right);

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);
        return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// BLS12-381 signature (96 bytes, compressed G2 point).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BlsSignature : IEquatable<BlsSignature>
{
    public const int Size = 96;

    private readonly ulong _v0, _v1, _v2, _v3, _v4, _v5, _v6, _v7, _v8, _v9, _v10, _v11;

    public static readonly BlsSignature Empty = default;

    public BlsSignature(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"BlsSignature requires exactly {Size} bytes, got {bytes.Length}.");

        var ulongs = MemoryMarshal.Cast<byte, ulong>(bytes);
        _v0 = ulongs[0]; _v1 = ulongs[1]; _v2 = ulongs[2]; _v3 = ulongs[3];
        _v4 = ulongs[4]; _v5 = ulongs[5]; _v6 = ulongs[6]; _v7 = ulongs[7];
        _v8 = ulongs[8]; _v9 = ulongs[9]; _v10 = ulongs[10]; _v11 = ulongs[11];
    }

    public bool IsEmpty => _v0 == 0 && _v1 == 0 && _v2 == 0 && _v3 == 0 &&
                           _v4 == 0 && _v5 == 0 && _v6 == 0 && _v7 == 0 &&
                           _v8 == 0 && _v9 == 0 && _v10 == 0 && _v11 == 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination must be at least {Size} bytes.");

        var ulongs = MemoryMarshal.Cast<byte, ulong>(destination);
        ulongs[0] = _v0; ulongs[1] = _v1; ulongs[2] = _v2; ulongs[3] = _v3;
        ulongs[4] = _v4; ulongs[5] = _v5; ulongs[6] = _v6; ulongs[7] = _v7;
        ulongs[8] = _v8; ulongs[9] = _v9; ulongs[10] = _v10; ulongs[11] = _v11;
    }

    public byte[] ToArray()
    {
        var result = new byte[Size];
        WriteTo(result);
        return result;
    }

    public bool Equals(BlsSignature other) =>
        _v0 == other._v0 && _v1 == other._v1 && _v2 == other._v2 && _v3 == other._v3 &&
        _v4 == other._v4 && _v5 == other._v5 && _v6 == other._v6 && _v7 == other._v7 &&
        _v8 == other._v8 && _v9 == other._v9 && _v10 == other._v10 && _v11 == other._v11;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is BlsSignature other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_v0, _v1, _v2, _v3);

    public static bool operator ==(BlsSignature left, BlsSignature right) => left.Equals(right);
    public static bool operator !=(BlsSignature left, BlsSignature right) => !left.Equals(right);

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);
        return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

/// <summary>
/// BLS12-381 public key (48 bytes, compressed G1 point).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct BlsPublicKey : IEquatable<BlsPublicKey>
{
    public const int Size = 48;

    private readonly ulong _v0, _v1, _v2, _v3, _v4, _v5;

    public static readonly BlsPublicKey Empty = default;

    public BlsPublicKey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
            throw new ArgumentException($"BlsPublicKey requires exactly {Size} bytes, got {bytes.Length}.");

        var ulongs = MemoryMarshal.Cast<byte, ulong>(bytes);
        _v0 = ulongs[0]; _v1 = ulongs[1]; _v2 = ulongs[2];
        _v3 = ulongs[3]; _v4 = ulongs[4]; _v5 = ulongs[5];
    }

    public bool IsEmpty => _v0 == 0 && _v1 == 0 && _v2 == 0 &&
                           _v3 == 0 && _v4 == 0 && _v5 == 0;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination must be at least {Size} bytes.");

        var ulongs = MemoryMarshal.Cast<byte, ulong>(destination);
        ulongs[0] = _v0; ulongs[1] = _v1; ulongs[2] = _v2;
        ulongs[3] = _v3; ulongs[4] = _v4; ulongs[5] = _v5;
    }

    public byte[] ToArray()
    {
        var result = new byte[Size];
        WriteTo(result);
        return result;
    }

    public bool Equals(BlsPublicKey other) =>
        _v0 == other._v0 && _v1 == other._v1 && _v2 == other._v2 &&
        _v3 == other._v3 && _v4 == other._v4 && _v5 == other._v5;

    public override bool Equals([NotNullWhen(true)] object? obj) =>
        obj is BlsPublicKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_v0, _v1, _v2);

    public static bool operator ==(BlsPublicKey left, BlsPublicKey right) => left.Equals(right);
    public static bool operator !=(BlsPublicKey left, BlsPublicKey right) => !left.Equals(right);

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);
        return "0x" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
