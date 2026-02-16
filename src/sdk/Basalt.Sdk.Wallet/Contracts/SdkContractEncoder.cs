using System.Buffers.Binary;
using System.Text;
using Basalt.Codec;
using Basalt.Core;
using Basalt.Execution.VM;
using Basalt.Sdk.Contracts;

namespace Basalt.Sdk.Wallet.Contracts;

/// <summary>
/// Encoder for SDK contract manifests (0xBA5A format) and FNV-1a method call data.
/// All methods are synchronous because <see cref="BasaltWriter"/> is a ref struct.
/// </summary>
/// <remarks>
/// SDK contracts use FNV-1a selectors and BasaltWriter encoding (VarInt length prefixes,
/// little-endian integers). This is distinct from <see cref="AbiEncoder"/>, which uses
/// BLAKE3 selectors and big-endian encoding for built-in contract methods.
/// </remarks>
public static class SdkContractEncoder
{
    // ── Manifest Builders ───────────────────────────────────────────────

    /// <summary>
    /// Build a BST-20 deployment manifest.
    /// </summary>
    public static byte[] BuildBST20Manifest(string name, string symbol, byte decimals)
    {
        Span<byte> buf = stackalloc byte[512];
        var writer = new BasaltWriter(buf);
        writer.WriteString(name);
        writer.WriteString(symbol);
        writer.WriteByte(decimals);
        return ContractRegistry.BuildManifest(0x0001, buf[..writer.Position].ToArray());
    }

    /// <summary>
    /// Build a BST-721 deployment manifest.
    /// </summary>
    public static byte[] BuildBST721Manifest(string name, string symbol)
    {
        Span<byte> buf = stackalloc byte[512];
        var writer = new BasaltWriter(buf);
        writer.WriteString(name);
        writer.WriteString(symbol);
        return ContractRegistry.BuildManifest(0x0002, buf[..writer.Position].ToArray());
    }

    /// <summary>
    /// Build a BST-1155 deployment manifest.
    /// </summary>
    public static byte[] BuildBST1155Manifest(string baseUri)
    {
        Span<byte> buf = stackalloc byte[512];
        var writer = new BasaltWriter(buf);
        writer.WriteString(baseUri);
        return ContractRegistry.BuildManifest(0x0003, buf[..writer.Position].ToArray());
    }

    /// <summary>
    /// Build a BSTDIDRegistry deployment manifest.
    /// </summary>
    public static byte[] BuildBSTDIDManifest(string? prefix = null)
    {
        if (prefix is null)
            return ContractRegistry.BuildManifest(0x0004, []);

        Span<byte> buf = stackalloc byte[512];
        var writer = new BasaltWriter(buf);
        writer.WriteString(prefix);
        return ContractRegistry.BuildManifest(0x0004, buf[..writer.Position].ToArray());
    }

    /// <summary>
    /// Build a raw manifest from a type ID and pre-encoded constructor args.
    /// </summary>
    public static byte[] BuildManifest(ushort typeId, byte[] constructorArgs)
        => ContractRegistry.BuildManifest(typeId, constructorArgs);

    // ── FNV-1a Call Encoding ────────────────────────────────────────────

    /// <summary>
    /// Compute a 4-byte FNV-1a selector (LE) for a method name.
    /// Method names are PascalCase, matching the C# method name exactly.
    /// </summary>
    public static byte[] ComputeFnvSelector(string methodName)
        => SelectorHelper.ComputeSelectorBytes(methodName);

    /// <summary>
    /// Encode a full SDK contract call: FNV-1a selector + concatenated args.
    /// Args should be pre-encoded using the Encode* methods.
    /// </summary>
    public static byte[] EncodeSdkCall(string methodName, params byte[][] args)
    {
        var selector = ComputeFnvSelector(methodName);

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

    // ── Typed Argument Encoders ─────────────────────────────────────────

    /// <summary>
    /// Encode a byte array with VarInt length prefix (BasaltWriter format).
    /// </summary>
    public static byte[] EncodeBytes(byte[] data)
    {
        Span<byte> buf = stackalloc byte[10 + data.Length];
        var writer = new BasaltWriter(buf);
        writer.WriteBytes(data);
        return buf[..writer.Position].ToArray();
    }

    /// <summary>
    /// Encode a UTF-8 string with VarInt length prefix (BasaltWriter format).
    /// </summary>
    public static byte[] EncodeString(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> buf = stackalloc byte[10 + byteCount];
        var writer = new BasaltWriter(buf);
        writer.WriteString(value);
        return buf[..writer.Position].ToArray();
    }

    /// <summary>
    /// Encode a ulong as 8 bytes little-endian (BasaltWriter format).
    /// </summary>
    public static byte[] EncodeUInt64(ulong value)
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        return buf;
    }

    /// <summary>
    /// Encode a single byte.
    /// </summary>
    public static byte[] EncodeByte(byte value) => [value];

    /// <summary>
    /// Encode a boolean as a single byte.
    /// </summary>
    public static byte[] EncodeBool(bool value) => [(byte)(value ? 1 : 0)];

    /// <summary>
    /// Encode an address as a VarInt-prefixed byte array.
    /// SDK contracts read addresses as byte[] parameters.
    /// </summary>
    public static byte[] EncodeAddress(Address addr) => EncodeBytes(addr.ToArray());

    // ── Return Value Decoders ───────────────────────────────────────────

    /// <summary>
    /// Decode a ulong return value (8 bytes LE).
    /// </summary>
    public static ulong DecodeUInt64(byte[] data)
    {
        var reader = new BasaltReader(data);
        return reader.ReadUInt64();
    }

    /// <summary>
    /// Decode a string return value (VarInt length + UTF-8).
    /// </summary>
    public static string DecodeString(byte[] data)
    {
        var reader = new BasaltReader(data);
        return reader.ReadString();
    }

    /// <summary>
    /// Decode a byte array return value (VarInt length + data).
    /// </summary>
    public static byte[] DecodeByteArray(byte[] data)
    {
        var reader = new BasaltReader(data);
        return reader.ReadBytes().ToArray();
    }

    /// <summary>
    /// Decode a byte return value.
    /// </summary>
    public static byte DecodeByte(byte[] data)
    {
        var reader = new BasaltReader(data);
        return reader.ReadByte();
    }

    /// <summary>
    /// Decode a boolean return value.
    /// </summary>
    public static bool DecodeBool(byte[] data)
    {
        var reader = new BasaltReader(data);
        return reader.ReadBool();
    }
}
