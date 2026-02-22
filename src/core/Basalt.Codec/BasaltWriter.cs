using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Basalt.Core;

namespace Basalt.Codec;

/// <summary>
/// High-performance binary writer operating on Span&lt;byte&gt;.
/// Provides deterministic serialization for all Basalt types.
/// </summary>
public ref struct BasaltWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    public BasaltWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public int Position => _position;
    public int Remaining => _buffer.Length - _position;
    public ReadOnlySpan<byte> WrittenSpan => _buffer[.._position];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        _buffer[_position++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16(ushort value)
    {
        EnsureCapacity(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer[_position..], value);
        _position += 2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32(uint value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer[_position..], value);
        _position += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32(int value)
    {
        EnsureCapacity(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer[_position..], value);
        _position += 4;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt64(ulong value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer[_position..], value);
        _position += 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt64(long value)
    {
        EnsureCapacity(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer[_position..], value);
        _position += 8;
    }

    /// <summary>
    /// Write a variable-length encoded unsigned integer (LEB128).
    /// </summary>
    public void WriteVarInt(ulong value)
    {
        while (value >= 0x80)
        {
            EnsureCapacity(1);
            _buffer[_position++] = (byte)(value | 0x80);
            value >>= 7;
        }
        EnsureCapacity(1);
        _buffer[_position++] = (byte)value;
    }

    /// <summary>
    /// Write a length-prefixed byte array.
    /// </summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        WriteVarInt((ulong)data.Length);
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer[_position..]);
        _position += data.Length;
    }

    /// <summary>
    /// Write raw bytes without length prefix.
    /// </summary>
    public void WriteRawBytes(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer[_position..]);
        _position += data.Length;
    }

    /// <summary>
    /// Write a length-prefixed UTF-8 string.
    /// </summary>
    public void WriteString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(value);
        WriteVarInt((ulong)byteCount);
        EnsureCapacity(byteCount);
        System.Text.Encoding.UTF8.GetBytes(value, _buffer[_position..]);
        _position += byteCount;
    }

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteHash256(Hash256 hash)
    {
        EnsureCapacity(Hash256.Size);
        hash.WriteTo(_buffer[_position..]);
        _position += Hash256.Size;
    }

    public void WriteAddress(Address address)
    {
        EnsureCapacity(Address.Size);
        address.WriteTo(_buffer[_position..]);
        _position += Address.Size;
    }

    public void WriteUInt256(UInt256 value)
    {
        EnsureCapacity(32);
        value.WriteTo(_buffer[_position..]);
        _position += 32;
    }

    public void WriteSignature(Signature sig)
    {
        EnsureCapacity(Signature.Size);
        sig.WriteTo(_buffer[_position..]);
        _position += Signature.Size;
    }

    public void WritePublicKey(PublicKey key)
    {
        EnsureCapacity(PublicKey.Size);
        key.WriteTo(_buffer[_position..]);
        _position += PublicKey.Size;
    }

    public void WriteBlsSignature(BlsSignature sig)
    {
        EnsureCapacity(BlsSignature.Size);
        sig.WriteTo(_buffer[_position..]);
        _position += BlsSignature.Size;
    }

    public void WriteBlsPublicKey(BlsPublicKey key)
    {
        EnsureCapacity(BlsPublicKey.Size);
        key.WriteTo(_buffer[_position..]);
        _position += BlsPublicKey.Size;
    }

    /// <summary>
    /// LOW-04: Uses subtraction to avoid integer overflow when _position + bytes
    /// could exceed int.MaxValue for extreme values.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureCapacity(int bytes)
    {
        if (bytes > _buffer.Length - _position)
            ThrowBufferTooSmall();
    }

    private static void ThrowBufferTooSmall() =>
        throw new InvalidOperationException("Buffer is too small for write operation.");
}
