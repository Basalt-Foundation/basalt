using Basalt.Codec;
using Basalt.Core;
using Basalt.Execution.VM;
using Basalt.Sdk.Contracts;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class SdkContractExecutionTests
{
    private readonly InMemoryStateDb _stateDb = new();

    private static readonly Address ContractAddr = new(new byte[]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
        11, 12, 13, 14, 15, 16, 17, 18, 19, 20
    });

    private static readonly Address CallerAddr = new(new byte[]
    {
        0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x01, 0x02, 0x03, 0x04, 0x05,
        0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F
    });

    private VmExecutionContext CreateContext(ulong gasLimit = 50_000_000)
    {
        return new VmExecutionContext
        {
            Caller = CallerAddr,
            ContractAddress = ContractAddr,
            Value = UInt256.Zero,
            BlockTimestamp = 1000,
            BlockNumber = 42,
            BlockProposer = new Address(new byte[20]),
            ChainId = 4242,
            GasMeter = new GasMeter(gasLimit),
            StateDb = _stateDb,
            CallDepth = 0,
        };
    }

    private static byte[] BuildBst20Manifest(string name, string symbol, byte decimals)
    {
        var argsBuf = new byte[512];
        var writer = new BasaltWriter(argsBuf);
        writer.WriteString(name);
        writer.WriteString(symbol);
        writer.WriteByte(decimals);
        var ctorArgs = argsBuf[..writer.Position];
        return ContractRegistry.BuildManifest(0x0001, ctorArgs);
    }

    private static Hash256 GetCodeStorageKey()
    {
        var keyBytes = new byte[32];
        keyBytes[0] = 0xFF;
        keyBytes[1] = 0x01;
        return new Hash256(keyBytes);
    }

    // ---- Deploy ----

    [Fact]
    public void Deploy_BST20_Succeeds()
    {
        var ctx = CreateContext();
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);

        var runtime = new ManagedContractRuntime();
        var result = runtime.Deploy(manifest, [], ctx);

        result.Success.Should().BeTrue();
        result.Code.Should().Equal(manifest);
    }

    [Fact]
    public void Deploy_StoresCodeInStorage()
    {
        var ctx = CreateContext();
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);

        var runtime = new ManagedContractRuntime();
        runtime.Deploy(manifest, [], ctx);

        var codeKey = GetCodeStorageKey();
        var storedCode = _stateDb.GetStorage(ContractAddr, codeKey);
        storedCode.Should().NotBeNull();
        storedCode.Should().Equal(manifest);
    }

    [Fact]
    public void Deploy_ManifestIsRecognizedAsSdkContract()
    {
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);
        ContractRegistry.IsSdkContract(manifest).Should().BeTrue();
    }

    // ---- Execute: Name() ----

    [Fact]
    public void Execute_Name_ReturnsTokenName()
    {
        // Deploy
        var ctx1 = CreateContext();
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);
        var runtime = new ManagedContractRuntime();
        runtime.Deploy(manifest, [], ctx1);

        // Retrieve stored code
        var codeKey = GetCodeStorageKey();
        var storedCode = _stateDb.GetStorage(ContractAddr, codeKey)!;

        // Execute Name()
        var ctx2 = CreateContext();
        var selector = SelectorHelper.ComputeSelectorBytes("Name");
        var callResult = runtime.Execute(storedCode, selector, ctx2);

        callResult.Success.Should().BeTrue();
        callResult.ReturnData.Should().NotBeNull();

        var reader = new BasaltReader(callResult.ReturnData!);
        reader.ReadString().Should().Be("TestToken");
    }

    // ---- Execute: Symbol() ----

    [Fact]
    public void Execute_Symbol_ReturnsTokenSymbol()
    {
        var ctx1 = CreateContext();
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);
        var runtime = new ManagedContractRuntime();
        runtime.Deploy(manifest, [], ctx1);

        var storedCode = _stateDb.GetStorage(ContractAddr, GetCodeStorageKey())!;

        var ctx2 = CreateContext();
        var selector = SelectorHelper.ComputeSelectorBytes("Symbol");
        var callResult = runtime.Execute(storedCode, selector, ctx2);

        callResult.Success.Should().BeTrue();
        var reader = new BasaltReader(callResult.ReturnData!);
        reader.ReadString().Should().Be("TST");
    }

    // ---- Execute: Decimals() ----

    [Fact]
    public void Execute_Decimals_ReturnsConfiguredDecimals()
    {
        var ctx1 = CreateContext();
        var manifest = BuildBst20Manifest("TestToken", "TST", 8);
        var runtime = new ManagedContractRuntime();
        runtime.Deploy(manifest, [], ctx1);

        var storedCode = _stateDb.GetStorage(ContractAddr, GetCodeStorageKey())!;

        var ctx2 = CreateContext();
        var selector = SelectorHelper.ComputeSelectorBytes("Decimals");
        var callResult = runtime.Execute(storedCode, selector, ctx2);

        callResult.Success.Should().BeTrue();
        var reader = new BasaltReader(callResult.ReturnData!);
        reader.ReadByte().Should().Be(8);
    }

    // ---- Execute: TotalSupply() initially zero ----

    [Fact]
    public void Execute_TotalSupply_InitiallyZero()
    {
        var ctx1 = CreateContext();
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);
        var runtime = new ManagedContractRuntime();
        runtime.Deploy(manifest, [], ctx1);

        var storedCode = _stateDb.GetStorage(ContractAddr, GetCodeStorageKey())!;

        var ctx2 = CreateContext();
        var selector = SelectorHelper.ComputeSelectorBytes("TotalSupply");
        var callResult = runtime.Execute(storedCode, selector, ctx2);

        callResult.Success.Should().BeTrue();
        var reader = new BasaltReader(callResult.ReturnData!);
        reader.ReadUInt64().Should().Be(0);
    }

    // ---- Execute: BalanceOf() initially zero ----

    [Fact]
    public void Execute_BalanceOf_InitiallyZero()
    {
        var ctx1 = CreateContext();
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);
        var runtime = new ManagedContractRuntime();
        runtime.Deploy(manifest, [], ctx1);

        var storedCode = _stateDb.GetStorage(ContractAddr, GetCodeStorageKey())!;

        // Build calldata: selector + 20-byte address
        var ctx2 = CreateContext();
        var selector = SelectorHelper.ComputeSelectorBytes("BalanceOf");
        var callData = new byte[selector.Length + 1 + 20]; // selector + varint length prefix + address bytes
        selector.CopyTo(callData, 0);

        // BasaltWriter encodes byte[] with a varint length prefix
        var argsBuf = new byte[64];
        var writer = new BasaltWriter(argsBuf);
        writer.WriteBytes(CallerAddr.ToArray());
        var args = argsBuf[..writer.Position];

        var fullCallData = new byte[selector.Length + args.Length];
        selector.CopyTo(fullCallData, 0);
        args.CopyTo(fullCallData.AsSpan(selector.Length));

        var callResult = runtime.Execute(storedCode, fullCallData, ctx2);

        callResult.Success.Should().BeTrue();
        var reader = new BasaltReader(callResult.ReturnData!);
        reader.ReadUInt64().Should().Be(0);
    }

    // ---- Execute: unknown selector fails ----

    [Fact]
    public void Execute_UnknownSelector_Throws()
    {
        var ctx1 = CreateContext();
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);
        var runtime = new ManagedContractRuntime();
        runtime.Deploy(manifest, [], ctx1);

        var storedCode = _stateDb.GetStorage(ContractAddr, GetCodeStorageKey())!;

        var ctx2 = CreateContext();
        var unknownSelector = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC };

        // The generated Dispatch method throws InvalidOperationException
        // for unknown selectors, which propagates out of the runtime
        var act = () => runtime.Execute(storedCode, unknownSelector, ctx2);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unknown selector*");
    }

    // ---- Deploy different contract types ----

    [Fact]
    public void Deploy_BST721_Succeeds()
    {
        var ctx = CreateContext();
        var argsBuf = new byte[256];
        var writer = new BasaltWriter(argsBuf);
        writer.WriteString("TestNFT");
        writer.WriteString("TNFT");
        var ctorArgs = argsBuf[..writer.Position];
        var manifest = ContractRegistry.BuildManifest(0x0002, ctorArgs);

        var runtime = new ManagedContractRuntime();
        var result = runtime.Deploy(manifest, [], ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public void Deploy_BST1155_Succeeds()
    {
        var ctx = CreateContext();
        var argsBuf = new byte[256];
        var writer = new BasaltWriter(argsBuf);
        writer.WriteString("https://example.com/tokens/");
        var ctorArgs = argsBuf[..writer.Position];
        var manifest = ContractRegistry.BuildManifest(0x0003, ctorArgs);

        var runtime = new ManagedContractRuntime();
        var result = runtime.Deploy(manifest, [], ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public void Deploy_WBSLT_Succeeds()
    {
        var ctx = CreateContext();
        var manifest = ContractRegistry.BuildManifest(0x0100, []);

        var runtime = new ManagedContractRuntime();
        var result = runtime.Deploy(manifest, [], ctx);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public void Deploy_Escrow_Succeeds()
    {
        var ctx = CreateContext();
        var manifest = ContractRegistry.BuildManifest(0x0103, []);

        var runtime = new ManagedContractRuntime();
        var result = runtime.Deploy(manifest, [], ctx);

        result.Success.Should().BeTrue();
    }

    // ---- Gas consumed during deploy ----

    [Fact]
    public void Deploy_ConsumesGas()
    {
        var ctx = CreateContext();
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);

        var runtime = new ManagedContractRuntime();
        runtime.Deploy(manifest, [], ctx);

        ctx.GasMeter.GasUsed.Should().BeGreaterThan(0);
    }

    // ---- Deploy with insufficient gas fails ----

    [Fact]
    public void Deploy_InsufficientGas_Fails()
    {
        var ctx = CreateContext(gasLimit: 100); // very low gas
        var manifest = BuildBst20Manifest("TestToken", "TST", 18);

        var runtime = new ManagedContractRuntime();
        var result = runtime.Deploy(manifest, [], ctx);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("gas");
    }

    // ---- Execute with empty code fails ----

    [Fact]
    public void Execute_EmptyCode_Fails()
    {
        var ctx = CreateContext();
        var runtime = new ManagedContractRuntime();

        var result = runtime.Execute([], new byte[] { 0x01, 0x02, 0x03, 0x04 }, ctx);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no code");
    }

    // ---- Custom registry with ManagedContractRuntime ----

    [Fact]
    public void ManagedContractRuntime_WithCustomRegistry_Works()
    {
        var registry = new ContractRegistry();
        registry.Register(0x0001, "BST20Token", args =>
        {
            var reader = new BasaltReader(args);
            var name = reader.ReadString();
            var symbol = reader.ReadString();
            var decimals = reader.ReadByte();
            return new Basalt.Sdk.Contracts.Standards.BST20Token(name, symbol, decimals);
        });

        var runtime = new ManagedContractRuntime(registry);
        var ctx = CreateContext();
        var manifest = BuildBst20Manifest("Custom", "CUS", 6);

        var result = runtime.Deploy(manifest, [], ctx);
        result.Success.Should().BeTrue();

        // Query Name
        var storedCode = _stateDb.GetStorage(ContractAddr, GetCodeStorageKey())!;
        var ctx2 = CreateContext();
        var selector = SelectorHelper.ComputeSelectorBytes("Name");
        var callResult = runtime.Execute(storedCode, selector, ctx2);

        callResult.Success.Should().BeTrue();
        var nameReader = new BasaltReader(callResult.ReturnData!);
        nameReader.ReadString().Should().Be("Custom");
    }

    // ---- Multiple deploys to different addresses ----

    [Fact]
    public void Deploy_ThenExecute_MultipleContracts()
    {
        var runtime = new ManagedContractRuntime();

        // Deploy token A
        var ctxA = CreateContext();
        var manifestA = BuildBst20Manifest("TokenA", "TKA", 18);
        var resultA = runtime.Deploy(manifestA, [], ctxA);
        resultA.Success.Should().BeTrue();

        // Deploy token B to a different address
        var addrB = new Address(new byte[]
        {
            0xBB, 0xBB, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x10,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x20
        });
        var ctxB = new VmExecutionContext
        {
            Caller = CallerAddr,
            ContractAddress = addrB,
            Value = UInt256.Zero,
            BlockTimestamp = 1000,
            BlockNumber = 42,
            BlockProposer = new Address(new byte[20]),
            ChainId = 4242,
            GasMeter = new GasMeter(50_000_000),
            StateDb = _stateDb,
            CallDepth = 0,
        };
        var manifestB = BuildBst20Manifest("TokenB", "TKB", 8);
        var resultB = runtime.Deploy(manifestB, [], ctxB);
        resultB.Success.Should().BeTrue();

        // Query Name on token A
        var storedCodeA = _stateDb.GetStorage(ContractAddr, GetCodeStorageKey())!;
        var queryCtxA = CreateContext();
        var nameSelector = SelectorHelper.ComputeSelectorBytes("Name");
        var nameResultA = runtime.Execute(storedCodeA, nameSelector, queryCtxA);
        nameResultA.Success.Should().BeTrue();
        var readerA = new BasaltReader(nameResultA.ReturnData!);
        readerA.ReadString().Should().Be("TokenA");

        // Query Name on token B
        var storedCodeB = _stateDb.GetStorage(addrB, GetCodeStorageKey())!;
        var queryCtxB = new VmExecutionContext
        {
            Caller = CallerAddr,
            ContractAddress = addrB,
            Value = UInt256.Zero,
            BlockTimestamp = 1000,
            BlockNumber = 42,
            BlockProposer = new Address(new byte[20]),
            ChainId = 4242,
            GasMeter = new GasMeter(50_000_000),
            StateDb = _stateDb,
            CallDepth = 0,
        };
        var nameResultB = runtime.Execute(storedCodeB, nameSelector, queryCtxB);
        nameResultB.Success.Should().BeTrue();
        var readerB = new BasaltReader(nameResultB.ReturnData!);
        readerB.ReadString().Should().Be("TokenB");
    }
}
