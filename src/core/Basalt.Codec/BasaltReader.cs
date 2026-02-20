using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Basalt.Core;

namespace Basalt.Codec;

/// <summary>
/// High-performance binary reader operating on ReadOnlySpan&lt;byte&gt;.
/// Provides deterministic deserialization for all Basalt types.
/// </summary>
public ref struct BasaltReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    // NET-M11: Maximum allowed string length in bytes to prevent oversized allocations
    public const int MaxStringLength = 4096;

    public BasaltReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public int Position => _position;
    public int Remaining => _buffer.Length - _position;
    public bool IsAtEnd => _position >= _buffer.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        EnsureAvailable(1);
        return _buffer[_position++];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16()
    {
        EnsureAvailable(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[_position..]);
        _position += 2;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[_position..]);
        _position += 4;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32()
    {
        EnsureAvailable(4);
        var value = BinaryPrimitives.ReadInt32LittleEndian(_buffer[_position..]);
        _position += 4;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64()
    {
        EnsureAvailable(8);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer[_position..]);
        _position += 8;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64()
    {
        EnsureAvailable(8);
        var value = BinaryPrimitives.ReadInt64LittleEndian(_buffer[_position..]);
        _position += 8;
        return value;
    }

    /// <summary>
    /// Read a variable-length encoded unsigned integer (LEB128).
    /// </summary>
    public ulong ReadVarInt()
    {
        ulong result = 0;
        int shift = 0;

        while (true)
        {
            EnsureAvailable(1);
            var b = _buffer[_position++];
            result |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return result;

            shift += 7;
            if (shift >= 64)
                throw new FormatException("VarInt is too long.");
        }
    }

    /// <summary>
    /// Read a length-prefixed byte array.
    /// </summary>
    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = (int)ReadVarInt();
        EnsureAvailable(length);
        var data = _buffer.Slice(_position, length);
        _position += length;
        return data;
    }

    /// <summary>
    /// Read a fixed number of raw bytes.
    /// </summary>
    public ReadOnlySpan<byte> ReadRawBytes(int count)
    {
        EnsureAvailable(count);
        var data = _buffer.Slice(_position, count);
        _position += count;
        return data;
    }

    /// <summary>
    /// Read a length-prefixed UTF-8 string.
    /// NET-M11: Enforces MaxStringLength to prevent oversized allocations.
    /// </summary>
    public string ReadString()
    {
        var length = (int)ReadVarInt();

        // NET-M11: Reject strings exceeding maximum allowed length
        if (length > MaxStringLength)
            throw new InvalidOperationException(
                $"String length {length} exceeds maximum of {MaxStringLength} bytes.");

        EnsureAvailable(length);
        var data = _buffer.Slice(_position, length);
        _position += length;
        return System.Text.Encoding.UTF8.GetString(data);
    }

    public bool ReadBool() => ReadByte() != 0;

    public Hash256 ReadHash256()
    {
        EnsureAvailable(Hash256.Size);
        var hash = new Hash256(_buffer.Slice(_position, Hash256.Size));
        _position += Hash256.Size;
        return hash;
    }

    public Address ReadAddress()
    {
        EnsureAvailable(Address.Size);
        var address = new Address(_buffer.Slice(_position, Address.Size));
        _position += Address.Size;
        return address;
    }

    public UInt256 ReadUInt256()
    {
        EnsureAvailable(32);
        var value = new UInt256(_buffer.Slice(_position, 32));
        _position += 32;
        return value;
    }

    public Signature ReadSignature()
    {
        EnsureAvailable(Signature.Size);
        var sig = new Signature(_buffer.Slice(_position, Signature.Size));
        _position += Signature.Size;
        return sig;
    }

    public PublicKey ReadPublicKey()
    {
        EnsureAvailable(PublicKey.Size);
        var key = new PublicKey(_buffer.Slice(_position, PublicKey.Size));
        _position += PublicKey.Size;
        return key;
    }

    public BlsSignature ReadBlsSignature()
    {
        EnsureAvailable(BlsSignature.Size);
        var sig = new BlsSignature(_buffer.Slice(_position, BlsSignature.Size));
        _position += BlsSignature.Size;
        return sig;
    }

    public BlsPublicKey ReadBlsPublicKey()
    {
        EnsureAvailable(BlsPublicKey.Size);
        var key = new BlsPublicKey(_buffer.Slice(_position, BlsPublicKey.Size));
        _position += BlsPublicKey.Size;
        return key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureAvailable(int bytes)
    {
        if (_position + bytes > _buffer.Length)
            ThrowUnexpectedEnd();
    }

    private static void ThrowUnexpectedEnd() =>
        throw new InvalidOperationException("Unexpected end of buffer during read operation.");
}
