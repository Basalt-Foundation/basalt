using System.Buffers.Binary;
using System.Text;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Sdk.Wallet.Contracts;

/// <summary>
/// ABI encoder/decoder for Basalt smart contract call data.
/// Encodes method selectors and typed arguments into the binary format
/// expected by the Basalt VM, and decodes return values.
/// </summary>
/// <remarks>
/// Basalt uses a compact ABI format:
/// <list type="bullet">
///   <item>Method selector: first 4 bytes of BLAKE3(method_name)</item>
///   <item>Arguments: length-prefixed byte segments concatenated sequentially</item>
/// </list>
/// Fixed-size types (Address = 20 bytes, UInt256 = 32 bytes, UInt64 = 8 bytes, Bool = 1 byte)
/// are encoded inline without a length prefix. Variable-size types (Bytes, String)
/// are encoded as a 4-byte big-endian length prefix followed by the raw bytes.
/// </remarks>
public static class AbiEncoder
{
    /// <summary>
    /// Computes a 4-byte method selector from a method name using BLAKE3.
    /// </summary>
    /// <param name="methodName">The method name (e.g. "transfer", "storage_set").</param>
    /// <returns>A 4-byte selector matching the Basalt VM dispatch convention.</returns>
    public static byte[] ComputeSelector(string methodName)
    {
        ArgumentNullException.ThrowIfNull(methodName);

        var hash = Blake3Hasher.Hash(Encoding.UTF8.GetBytes(methodName));
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(hashBytes);
        return hashBytes[..4].ToArray();
    }

    /// <summary>
    /// Encodes a complete contract call data payload: 4-byte selector + encoded arguments.
    /// </summary>
    /// <param name="methodName">The method name to call.</param>
    /// <param name="args">Encoded argument segments (use the Encode* methods to produce these).</param>
    /// <returns>The full call data byte array.</returns>
    public static byte[] EncodeCall(string methodName, params byte[][] args)
    {
        var selector = ComputeSelector(methodName);

        var totalArgLen = 0;
        for (var i = 0; i < args.Length; i++)
            totalArgLen += args[i].Length;

        var result = new byte[4 + totalArgLen];
        selector.CopyTo(result, 0);

        var offset = 4;
        for (var i = 0; i < args.Length; i++)
        {
            args[i].CopyTo(result, offset);
            offset += args[i].Length;
        }

        return result;
    }

    /// <summary>
    /// Encodes a <see cref="UInt256"/> value as a fixed 32-byte big-endian array.
    /// </summary>
    public static byte[] EncodeUInt256(UInt256 value)
    {
        var bytes = new byte[32];
        value.WriteTo(bytes, isBigEndian: true);
        return bytes;
    }

    /// <summary>
    /// Encodes a <see cref="ulong"/> as a fixed 8-byte big-endian array.
    /// </summary>
    public static byte[] EncodeUInt64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>
    /// Encodes an <see cref="Address"/> as a fixed 20-byte array.
    /// </summary>
    public static byte[] EncodeAddress(Address address)
    {
        return address.ToArray();
    }

    /// <summary>
    /// Encodes a <see cref="Hash256"/> as a fixed 32-byte array.
    /// </summary>
    public static byte[] EncodeHash256(Hash256 hash)
    {
        var bytes = new byte[Hash256.Size];
        hash.WriteTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Encodes a boolean as a single byte (0x01 = true, 0x00 = false).
    /// </summary>
    public static byte[] EncodeBool(bool value)
    {
        return [(byte)(value ? 1 : 0)];
    }

    /// <summary>
    /// Encodes a variable-length byte array with a 4-byte big-endian length prefix.
    /// </summary>
    public static byte[] EncodeBytes(ReadOnlySpan<byte> data)
    {
        var result = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)data.Length);
        data.CopyTo(result.AsSpan(4));
        return result;
    }

    /// <summary>
    /// Encodes a UTF-8 string with a 4-byte big-endian length prefix.
    /// </summary>
    public static byte[] EncodeString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return EncodeBytes(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// Decodes a <see cref="UInt256"/> from a 32-byte big-endian span.
    /// </summary>
    /// <param name="data">The data to decode from.</param>
    /// <param name="offset">Byte offset to start reading from. Advanced by 32.</param>
    public static UInt256 DecodeUInt256(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = new UInt256(data.Slice(offset, 32), isBigEndian: true);
        offset += 32;
        return value;
    }

    /// <summary>
    /// Decodes a <see cref="ulong"/> from an 8-byte big-endian span.
    /// </summary>
    /// <param name="data">The data to decode from.</param>
    /// <param name="offset">Byte offset to start reading from. Advanced by 8.</param>
    public static ulong DecodeUInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));
        offset += 8;
        return value;
    }

    /// <summary>
    /// Decodes an <see cref="Address"/> from a 20-byte span.
    /// </summary>
    /// <param name="data">The data to decode from.</param>
    /// <param name="offset">Byte offset to start reading from. Advanced by 20.</param>
    public static Address DecodeAddress(ReadOnlySpan<byte> data, ref int offset)
    {
        var address = new Address(data.Slice(offset, Address.Size));
        offset += Address.Size;
        return address;
    }

    /// <summary>
    /// Decodes a boolean from a single byte.
    /// </summary>
    /// <param name="data">The data to decode from.</param>
    /// <param name="offset">Byte offset to start reading from. Advanced by 1.</param>
    public static bool DecodeBool(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = data[offset] != 0;
        offset += 1;
        return value;
    }

    /// <summary>
    /// Decodes a length-prefixed byte array (4-byte BE length + data).
    /// </summary>
    /// <param name="data">The data to decode from.</param>
    /// <param name="offset">Byte offset to start reading from. Advanced by 4 + length.</param>
    public static byte[] DecodeBytes(ReadOnlySpan<byte> data, ref int offset)
    {
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
        offset += 4;
        var result = data.Slice(offset, length).ToArray();
        offset += length;
        return result;
    }

    /// <summary>
    /// Decodes a length-prefixed UTF-8 string (4-byte BE length + data).
    /// </summary>
    /// <param name="data">The data to decode from.</param>
    /// <param name="offset">Byte offset to start reading from. Advanced by 4 + length.</param>
    public static string DecodeString(ReadOnlySpan<byte> data, ref int offset)
    {
        var bytes = DecodeBytes(data, ref offset);
        return Encoding.UTF8.GetString(bytes);
    }
}
