using System.Text;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Sdk.Contracts;

namespace Basalt.Execution.VM;

/// <summary>
/// IStorageProvider implementation that bridges SDK contract storage
/// to the on-chain state database via HostInterface.
///
/// Key mapping: BLAKE3(UTF-8(key)) -> Hash256
/// Value serialization: [1-byte type tag][payload]
/// </summary>
public sealed class HostStorageProvider : IStorageProvider
{
    private readonly HostInterface _host;

    // Type tags for serialization
    private const byte TagULong = 0x01;
    private const byte TagLong = 0x02;
    private const byte TagInt = 0x03;
    private const byte TagUInt = 0x04;
    private const byte TagBool = 0x05;
    private const byte TagByte = 0x06;
    private const byte TagString = 0x07;
    private const byte TagByteArray = 0x08;
    private const byte TagUShort = 0x09;
    private const byte TagUInt256 = 0x0A;

    public HostStorageProvider(HostInterface host)
    {
        _host = host;
    }

    public void Set(string key, object? value)
    {
        var storageKey = ComputeStorageKey(key);
        var data = SerializeValue(value);
        _host.StorageWrite(storageKey, data);
    }

    public T Get<T>(string key)
    {
        var storageKey = ComputeStorageKey(key);
        var data = _host.StorageRead(storageKey);

        if (data is null || data.Length == 0)
            return default!;

        return DeserializeValue<T>(data);
    }

    public bool ContainsKey(string key)
    {
        var storageKey = ComputeStorageKey(key);
        var data = _host.StorageRead(storageKey);
        return data is not null && data.Length > 0;
    }

    public void Delete(string key)
    {
        var storageKey = ComputeStorageKey(key);
        _host.StorageDelete(storageKey);
    }

    /// <summary>
    /// L-12: Uses stackalloc for small keys to avoid heap allocation on every access.
    /// Falls back to heap for keys exceeding the stack threshold.
    /// </summary>
    private static Hash256 ComputeStorageKey(string key)
    {
        var byteCount = Encoding.UTF8.GetByteCount(key);
        if (byteCount <= 256)
        {
            Span<byte> buffer = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(key, buffer);
            return Blake3Hasher.Hash(buffer);
        }
        return Blake3Hasher.Hash(Encoding.UTF8.GetBytes(key));
    }

    private static byte[] SerializeValue(object? value)
    {
        if (value is null) return [];

        return value switch
        {
            ulong v => SerializeTagged(TagULong, v, 8, (buf, val) =>
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buf, val)),
            long v => SerializeTagged(TagLong, v, 8, (buf, val) =>
                System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf, val)),
            int v => SerializeTagged(TagInt, v, 4, (buf, val) =>
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf, val)),
            uint v => SerializeTagged(TagUInt, v, 4, (buf, val) =>
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, val)),
            ushort v => SerializeTagged(TagUShort, v, 2, (buf, val) =>
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf, val)),
            bool v => [TagBool, v ? (byte)1 : (byte)0],
            byte v => [TagByte, v],
            string v => SerializeString(v),
            byte[] v => SerializeByteArray(v),
            UInt256 v => SerializeUInt256(v),
            _ => throw new NotSupportedException($"Cannot serialize type {value.GetType().Name} to storage"),
        };
    }

    private static byte[] SerializeTagged<T>(byte tag, T value, int size, Action<Span<byte>, T> write)
    {
        var result = new byte[1 + size];
        result[0] = tag;
        write(result.AsSpan(1), value);
        return result;
    }

    private static byte[] SerializeString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = new byte[1 + bytes.Length];
        result[0] = TagString;
        bytes.CopyTo(result.AsSpan(1));
        return result;
    }

    private static byte[] SerializeByteArray(byte[] value)
    {
        var result = new byte[1 + value.Length];
        result[0] = TagByteArray;
        value.CopyTo(result.AsSpan(1));
        return result;
    }

    private static byte[] SerializeUInt256(UInt256 value)
    {
        var result = new byte[1 + 32];
        result[0] = TagUInt256;
        value.WriteTo(result.AsSpan(1));
        return result;
    }

    private static T DeserializeValue<T>(byte[] data)
    {
        if (data.Length == 0) return default!;

        var tag = data[0];
        var payload = data.AsSpan(1);

        // M-14: Validate payload length for fixed-size types before reading
        object result = tag switch
        {
            TagULong when payload.Length >= 8 => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(payload),
            TagLong when payload.Length >= 8 => System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(payload),
            TagInt when payload.Length >= 4 => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload),
            TagUInt when payload.Length >= 4 => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload),
            TagUShort when payload.Length >= 2 => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload),
            TagBool when payload.Length >= 1 => payload[0] != 0,
            TagByte when payload.Length >= 1 => payload[0],
            TagString => Encoding.UTF8.GetString(payload),
            TagByteArray => payload.ToArray(),
            TagUInt256 when payload.Length >= 32 => new UInt256(payload),
            TagULong or TagLong or TagInt or TagUInt or TagUShort or TagBool or TagByte or TagUInt256 =>
                throw new InvalidOperationException($"Corrupted storage: tag 0x{tag:X2} with insufficient payload length {payload.Length}"),
            _ => throw new NotSupportedException($"Unknown storage type tag: 0x{tag:X2}"),
        };

        return (T)result;
    }
}
