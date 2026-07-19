using Basalt.Api.Rest;
using Basalt.Codec;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Execution.VM;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Api.Tests;

/// <summary>
/// End-to-end checks for the Trilith read surface (SystemContractReader, behind GET /v1/names and
/// /v1/anchors): deploy the system contracts at genesis, write real state through the contract runtime
/// (register a name, set a content record, anchor a digest), then read it back through the reader. This
/// exercises the FNV-1a selector, the argument encoding, and the return decoding against the actual
/// source-generated contract dispatchers.
/// </summary>
public class TrilithReadSurfaceTests
{
    private readonly InMemoryStateDb _stateDb = new();
    private readonly ManagedContractRuntime _runtime = new();

    private const ulong BlockTsMs = 1_000_000; // 1000 unix seconds
    private const long NowSeconds = 1000;
    private const long RegistrationPeriod = 31_536_000;
    private static readonly UInt256 Fee = new(1_000_000_000);

    public TrilithReadSurfaceTests()
    {
        GenesisContractDeployer.DeployAll(_stateDb, 4242);
    }

    private static byte[] Call(string method, params byte[][] args)
    {
        uint hash = 2166136261;
        foreach (char c in method) { hash ^= (byte)c; hash *= 16777619; }
        var selector = new byte[4];
        selector[0] = (byte)(hash & 0xFF);
        selector[1] = (byte)((hash >> 8) & 0xFF);
        selector[2] = (byte)((hash >> 16) & 0xFF);
        selector[3] = (byte)((hash >> 24) & 0xFF);
        var result = new List<byte>(selector);
        foreach (var a in args) result.AddRange(a);
        return result.ToArray();
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

    private static byte[] EncByte(byte value) => [value];

    private byte[] CodeOf(Address addr)
    {
        var key = new byte[32];
        key[0] = 0xFF;
        key[1] = 0x01;
        return _stateDb.GetStorage(addr, new Hash256(key))!;
    }

    private void Exec(Address contract, byte[] caller, UInt256 value, byte[] callData)
    {
        var ctx = new VmExecutionContext
        {
            Caller = new Address(caller),
            ContractAddress = contract,
            Value = value,
            BlockTimestamp = BlockTsMs,
            BlockNumber = 7,
            BlockProposer = Address.Zero,
            ChainId = 4242,
            GasMeter = new GasMeter(50_000_000),
            StateDb = _stateDb,
            CallDepth = 0,
        };
        var r = _runtime.Execute(CodeOf(contract), callData, ctx);
        r.Success.Should().BeTrue(r.ErrorMessage);
    }

    private SystemContractReader Reader() =>
        new(_stateDb, _runtime, () => new ViewBlockContext(BlockTsMs, 7, Address.Zero, 4242));

    private static byte[] Address20(byte seed)
    {
        var a = new byte[20];
        a[19] = seed;
        return a;
    }

    [Fact]
    public void ReadName_Returns_The_Content_Record_Expiry_And_Owner()
    {
        var alice = Address20(1);
        var nameAddr = GenesisContractDeployer.Addresses.NameService;
        Exec(nameAddr, alice, Fee, Call("Register", EncString("alice")));
        Exec(nameAddr, alice, UInt256.Zero, Call("SetContentRecord", EncString("alice"), EncString("trilith://alicekey/blog")));

        var record = Reader().ReadName("alice");

        record.Should().NotBeNull();
        record!.Label.Should().Be("alice");
        record.ContentUri.Should().Be("trilith://alicekey/blog");
        record.Expiry.Should().Be(NowSeconds + RegistrationPeriod);
        record.Owner.Should().Be(Convert.ToHexString(alice).ToLowerInvariant());
    }

    [Fact]
    public void ReadName_Returns_Null_For_An_Unregistered_Name()
    {
        Reader().ReadName("ghost").Should().BeNull();
    }

    [Fact]
    public void ReadAnchor_Returns_The_Record()
    {
        var alice = Address20(1);
        var digest = new byte[32];
        Array.Fill(digest, (byte)0xAB);
        var anchorAddr = GenesisContractDeployer.Addresses.TrilithAnchor;

        // Anchor(digest, kind=1 destruction set, label)
        Exec(anchorAddr, alice, UInt256.Zero, Call("Anchor", EncBytes(digest), EncByte(1), EncString("cert-1")));

        var record = Reader().ReadAnchor(digest);

        record.Should().NotBeNull();
        record!.Digest.Should().Be(Convert.ToHexString(digest).ToLowerInvariant());
        record.Kind.Should().Be(1);
        record.Timestamp.Should().Be((ulong)NowSeconds);
        record.BlockNumber.Should().Be(7);
        record.Label.Should().Be("cert-1");
        record.Submitter.Should().Be(Convert.ToHexString(alice).ToLowerInvariant());
    }

    [Fact]
    public void ReadAnchor_Returns_Null_For_An_Unanchored_Digest()
    {
        Reader().ReadAnchor(new byte[32]).Should().BeNull();
    }
}
