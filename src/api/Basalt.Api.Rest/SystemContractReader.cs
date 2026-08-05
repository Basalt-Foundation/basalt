using Basalt.Codec;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Execution.VM;
using Basalt.Storage;

namespace Basalt.Api.Rest;

/// <summary>The block context a read-only view call runs against.</summary>
public readonly record struct ViewBlockContext(ulong Timestamp, ulong Number, Address Proposer, uint ChainId);

/// <summary>
/// Reads the BNS v2 and TrilithAnchor system contracts through read-only view calls and decodes the
/// results into plain DTOs. This is the server side of the Trilith read surface: it lets the (Basalt-free)
/// Trilith.Naming.Basalt adapter consume names and anchors over plain HTTP + JSON without encoding any
/// contract calls itself. Every call runs against a fork of the state, so it can never mutate the chain.
/// </summary>
public sealed class SystemContractReader
{
    private readonly IStateDatabase _stateDb;
    private readonly IContractRuntime _runtime;
    private readonly Func<ViewBlockContext> _block;
    private readonly ulong _gasLimit;

    public SystemContractReader(
        IStateDatabase stateDb, IContractRuntime runtime, Func<ViewBlockContext> block, ulong gasLimit = 5_000_000)
    {
        _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _block = block ?? throw new ArgumentNullException(nameof(block));
        _gasLimit = gasLimit;
    }

    /// <summary>Reads a BNS v2 name record, or null if the name has never been registered.</summary>
    public NameRecordResponse? ReadName(string label)
    {
        if (string.IsNullOrEmpty(label) || label.Length > 64)
            return null;

        var addr = GenesisContractDeployer.Addresses.NameService;
        long expiry = DecI64(CallView(addr, SdkCall("ExpiryOf", EncString(label))));
        if (expiry == 0)
            return null; // never registered

        // ResolveContent and OwnerOf already return empty/zero for a name past its grace period.
        return new NameRecordResponse
        {
            Label = label,
            ContentUri = DecStr(CallView(addr, SdkCall("ResolveContent", EncString(label)))),
            Expiry = expiry,
            Owner = DecAddrHex(CallView(addr, SdkCall("OwnerOf", EncString(label)))),
        };
    }

    /// <summary>Reads a TrilithAnchor record for a 32-byte digest, or null if it is not anchored.</summary>
    public AnchorResponse? ReadAnchor(byte[] digest)
    {
        if (digest is not { Length: 32 })
            return null;

        var addr = GenesisContractDeployer.Addresses.TrilithAnchor;
        if (!DecBool(CallView(addr, SdkCall("IsAnchored", EncBytes(digest)))))
            return null;

        return new AnchorResponse
        {
            Digest = Convert.ToHexString(digest).ToLowerInvariant(),
            Timestamp = DecU64(CallView(addr, SdkCall("GetAnchor", EncBytes(digest)))),
            Kind = DecByte(CallView(addr, SdkCall("GetAnchorKind", EncBytes(digest)))),
            BlockNumber = DecU64(CallView(addr, SdkCall("GetAnchorBlock", EncBytes(digest)))),
            Submitter = DecAddrHex(CallView(addr, SdkCall("GetAnchorSubmitter", EncBytes(digest)))),
            Label = DecStr(CallView(addr, SdkCall("GetAnchorLabel", EncBytes(digest)))),
        };
    }

    private byte[]? CallView(Address contractAddr, byte[] callData)
    {
        var acct = _stateDb.GetAccount(contractAddr);
        if (acct == null || acct.Value.AccountType is not (AccountType.Contract or AccountType.SystemContract))
            return null;

        Span<byte> codeKey = stackalloc byte[32];
        codeKey.Clear();
        codeKey[0] = 0xFF;
        codeKey[1] = 0x01;
        var code = _stateDb.GetStorage(contractAddr, new Hash256(codeKey)) ?? [];
        if (code.Length == 0)
            return null;

        var block = _block();
        var ctx = new VmExecutionContext
        {
            Caller = Address.Zero,
            ContractAddress = contractAddr,
            Value = UInt256.Zero,
            BlockTimestamp = block.Timestamp,
            BlockNumber = block.Number,
            BlockProposer = block.Proposer,
            ChainId = block.ChainId,
            GasMeter = new GasMeter(_gasLimit),
            StateDb = _stateDb.Fork(), // read-only: never mutate canonical state
            CallDepth = 0,
        };
        var result = _runtime.Execute(code, callData, ctx);
        return result.Success ? result.ReturnData : null;
    }

    // FNV-1a 4-byte little-endian method selector + concatenated args (matches Basalt.Sdk.Contracts.SelectorHelper
    // and the source-generated dispatcher).
    private static byte[] SdkCall(string method, params byte[][] args)
    {
        uint hash = 2166136261;
        foreach (char c in method) { hash ^= (byte)c; hash *= 16777619; }
        int total = 4;
        foreach (var a in args) total += a.Length;
        var data = new byte[total];
        data[0] = (byte)(hash & 0xFF);
        data[1] = (byte)((hash >> 8) & 0xFF);
        data[2] = (byte)((hash >> 16) & 0xFF);
        data[3] = (byte)((hash >> 24) & 0xFF);
        int off = 4;
        foreach (var a in args) { Array.Copy(a, 0, data, off, a.Length); off += a.Length; }
        return data;
    }

    private static byte[] EncString(string value)
    {
        var buf = new byte[10 + System.Text.Encoding.UTF8.GetByteCount(value)];
        var w = new BasaltWriter(buf);
        w.WriteString(value);
        return buf[..w.Position];
    }

    private static byte[] EncBytes(byte[] value)
    {
        var buf = new byte[10 + value.Length];
        var w = new BasaltWriter(buf);
        w.WriteBytes(value);
        return buf[..w.Position];
    }

    private static long DecI64(byte[]? d) => d != null ? new BasaltReader(d).ReadInt64() : 0L;
    private static ulong DecU64(byte[]? d) => d != null ? new BasaltReader(d).ReadUInt64() : 0UL;
    private static byte DecByte(byte[]? d) => d is { Length: > 0 } ? new BasaltReader(d).ReadByte() : (byte)0;
    private static bool DecBool(byte[]? d) => d is { Length: > 0 } && new BasaltReader(d).ReadBool();
    private static string DecStr(byte[]? d) => d != null ? new BasaltReader(d).ReadString() : "";
    private static string DecAddrHex(byte[]? d) => d != null ? Convert.ToHexString(new BasaltReader(d).ReadBytes()).ToLowerInvariant() : "";
}
