using System.Reflection;
using Basalt.Core;
using Basalt.Execution.VM;
using Basalt.Execution.VM.Sandbox;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Sandbox;

public class SandboxedRuntimeTests
{
    // =========================================================================
    // Helpers
    // =========================================================================

    private static readonly Address CallerAddress =
        Address.FromHexString("0x" + new string('1', 40));

    private static readonly Address ContractAddr =
        Address.FromHexString("0x" + new string('2', 40));

    /// <summary>
    /// Builds a <see cref="VmExecutionContext"/> backed by a fresh
    /// <see cref="InMemoryStateDb"/> with a contract account already set up.
    /// </summary>
    private static (VmExecutionContext Ctx, InMemoryStateDb Db) CreateContext(
        ulong gasLimit = 1_000_000)
    {
        var db = new InMemoryStateDb();

        // Ensure the contract address is registered as a Contract account
        // so that storage operations resolve correctly.
        db.SetAccount(ContractAddr, new AccountState
        {
            Nonce = 0,
            Balance = UInt256.Zero,
            StorageRoot = Hash256.Zero,
            CodeHash = Hash256.Zero,
            AccountType = AccountType.Contract,
            ComplianceHash = Hash256.Zero,
        });

        var ctx = new VmExecutionContext
        {
            Caller = CallerAddress,
            ContractAddress = ContractAddr,
            Value = UInt256.Zero,
            BlockTimestamp = 1_700_000_000,
            BlockNumber = 42,
            BlockProposer = Address.Zero,
            ChainId = 1,
            GasMeter = new GasMeter(gasLimit),
            StateDb = db,
            CallDepth = 0,
        };

        return (ctx, db);
    }

    /// <summary>
    /// Build call data by concatenating a 4-byte hex selector with arbitrary
    /// argument bytes.
    /// </summary>
    private static byte[] BuildCallData(string selectorHex, byte[] args)
    {
        var selector = Convert.FromHexString(selectorHex);
        var callData = new byte[selector.Length + args.Length];
        selector.CopyTo(callData, 0);
        args.CopyTo(callData, selector.Length);
        return callData;
    }

    /// <summary>A minimal non-empty contract code blob.</summary>
    private static readonly byte[] DummyCode = [0xCA, 0xFE, 0xBA, 0xBE];

    /// <summary>Well-known storage key for contract code (0xFF, 0x01, 0x00...).</summary>
    private static Hash256 CodeStorageKey
    {
        get
        {
            Span<byte> key = stackalloc byte[32];
            key.Clear();
            key[0] = 0xFF;
            key[1] = 0x01;
            return new Hash256(key);
        }
    }

    // Selectors: BLAKE3(method_name)[0:4] — must match ManagedContractRuntime dispatch table
    private static readonly string StorageSetSelector = Convert.ToHexString(ManagedContractRuntime.ComputeSelector("storage_set")).ToLowerInvariant();
    private static readonly string StorageGetSelector = Convert.ToHexString(ManagedContractRuntime.ComputeSelector("storage_get")).ToLowerInvariant();

    // =========================================================================
    // ResourceLimiter tests
    // =========================================================================

    [Fact]
    public void ResourceLimiter_Allocate_TrackMemory()
    {
        var limiter = new ResourceLimiter(1024);

        limiter.Allocate(256);

        limiter.CurrentUsage.Should().Be(256);
    }

    [Fact]
    public void ResourceLimiter_Allocate_ThrowsOnExceedLimit()
    {
        var limiter = new ResourceLimiter(100);

        var act = () => limiter.Allocate(101);

        act.Should().Throw<BasaltException>()
            .Where(e => e.ErrorCode == BasaltErrorCode.MemoryLimitExceeded);

        // Usage should remain 0 because the allocation was rolled back.
        limiter.CurrentUsage.Should().Be(0);
    }

    [Fact]
    public void ResourceLimiter_Free_DecrementsUsage()
    {
        var limiter = new ResourceLimiter(1024);

        limiter.Allocate(512);
        limiter.Free(200);

        limiter.CurrentUsage.Should().Be(312);
    }

    [Fact]
    public void ResourceLimiter_Reset_ClearsUsage()
    {
        var limiter = new ResourceLimiter(1024);

        limiter.Allocate(800);
        limiter.CurrentUsage.Should().Be(800);

        limiter.Reset();

        limiter.CurrentUsage.Should().Be(0);
    }

    // =========================================================================
    // ContractAssemblyContext tests
    // =========================================================================

    [Fact]
    public void ContractAssemblyContext_IsCollectible()
    {
        var alc = new ContractAssemblyContext("test-context");

        try
        {
            alc.IsCollectible.Should().BeTrue();
        }
        finally
        {
            alc.Unload();
        }
    }

    [Fact]
    public void ContractAssemblyContext_IsAssemblyAllowed()
    {
        // Allowed assemblies
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName("Basalt.Core"))
            .Should().BeTrue();
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName("System.Runtime"))
            .Should().BeTrue();
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName("Basalt.Sdk.Contracts"))
            .Should().BeTrue();
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName("Basalt.Codec"))
            .Should().BeTrue();
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName("System.Private.CoreLib"))
            .Should().BeTrue();
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName("netstandard"))
            .Should().BeTrue();

        // Disallowed assemblies
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName("System.IO.FileSystem"))
            .Should().BeFalse();
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName("System.Net.Http"))
            .Should().BeFalse();
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName("Newtonsoft.Json"))
            .Should().BeFalse();

        // Empty / null name
        ContractAssemblyContext.IsAssemblyAllowed(new AssemblyName())
            .Should().BeFalse();
    }

    // =========================================================================
    // SandboxedContractRuntime.Deploy tests
    // =========================================================================

    [Fact]
    public void SandboxedRuntime_Deploy_StoresCode()
    {
        var config = new SandboxConfiguration();
        var runtime = new SandboxedContractRuntime(config);
        var (ctx, db) = CreateContext();

        var result = runtime.Deploy(DummyCode, [], ctx);

        result.Success.Should().BeTrue();
        result.Code.Should().Equal(DummyCode);

        // The code should be persisted in contract storage under the well-known
        // code key (0xFF01 + 30 zero bytes).
        var storedCode = db.GetStorage(ContractAddr, CodeStorageKey);
        storedCode.Should().NotBeNull();
        storedCode.Should().Equal(DummyCode);
    }

    [Fact]
    public void SandboxedRuntime_Deploy_OutOfGas()
    {
        var config = new SandboxConfiguration();
        var runtime = new SandboxedContractRuntime(config);

        // Use an extremely low gas limit that cannot cover ContractCreation (32 000)
        // plus per-byte cost.
        var (ctx, _) = CreateContext(gasLimit: 100);

        var result = runtime.Deploy(DummyCode, [], ctx);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("gas");
    }

    // =========================================================================
    // SandboxedContractRuntime.Execute tests
    // =========================================================================

    [Fact]
    public void SandboxedRuntime_Execute_StorageSet()
    {
        var config = new SandboxConfiguration();
        var runtime = new SandboxedContractRuntime(config);

        // Deploy first to store code
        var (deployCtx, db) = CreateContext();
        runtime.Deploy(DummyCode, [], deployCtx);

        // Build call data: storage_set(key, value)
        var storageKey = new byte[32];
        storageKey[0] = 0xAA;
        var storageValue = new byte[] { 0x01, 0x02, 0x03 };

        var callData = BuildCallData(StorageSetSelector, [.. storageKey, .. storageValue]);

        // Execute storage_set
        var (execCtx, _) = CreateContextWithDb(db);
        var result = runtime.Execute(DummyCode, callData, execCtx);

        result.Success.Should().BeTrue();

        // The value should be written in the state DB.
        var stored = db.GetStorage(ContractAddr, new Hash256(storageKey));
        stored.Should().NotBeNull();
        stored.Should().Equal(storageValue);
    }

    [Fact]
    public void SandboxedRuntime_Execute_StorageGet()
    {
        var config = new SandboxConfiguration();
        var runtime = new SandboxedContractRuntime(config);

        var (_, db) = CreateContext();

        // Pre-populate a storage value.
        var storageKey = new byte[32];
        storageKey[0] = 0xBB;
        var storageValue = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        db.SetStorage(ContractAddr, new Hash256(storageKey), storageValue);

        // Build call data: storage_get(key)
        var callData = BuildCallData(StorageGetSelector, storageKey);

        var (execCtx, _) = CreateContextWithDb(db);
        var result = runtime.Execute(DummyCode, callData, execCtx);

        result.Success.Should().BeTrue();
        result.ReturnData.Should().Equal(storageValue);
    }

    [Fact]
    public void SandboxedRuntime_Execute_EmptyCode_ReturnsError()
    {
        var config = new SandboxConfiguration();
        var runtime = new SandboxedContractRuntime(config);
        var (ctx, _) = CreateContext();

        var result = runtime.Execute([], [0x00, 0x00, 0x00, 0x00], ctx);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("no code");
    }

    // =========================================================================
    // SandboxedRuntime vs ManagedRuntime parity test
    // =========================================================================

    [Fact]
    public void SandboxedRuntime_SameResultsAsManagedRuntime()
    {
        // Both runtimes should produce identical storage side-effects and return
        // data for the same deploy + storage_set + storage_get sequence.

        var sandboxConfig = new SandboxConfiguration();
        var sandboxRuntime = new SandboxedContractRuntime(sandboxConfig);
        var managedRuntime = new ManagedContractRuntime();

        // -- Managed Runtime --
        var (managedDeployCtx, managedDb) = CreateContext();
        var managedDeployResult = managedRuntime.Deploy(DummyCode, [], managedDeployCtx);

        var managedStorageKey = new byte[32];
        managedStorageKey[0] = 0xCC;
        var managedStorageValue = new byte[] { 0x10, 0x20, 0x30 };

        var setCallData = BuildCallData(StorageSetSelector, [.. managedStorageKey, .. managedStorageValue]);
        var (managedSetCtx, _) = CreateContextWithDb(managedDb);
        var managedSetResult = managedRuntime.Execute(DummyCode, setCallData, managedSetCtx);

        var getCallData = BuildCallData(StorageGetSelector, managedStorageKey);
        var (managedGetCtx, _) = CreateContextWithDb(managedDb);
        var managedGetResult = managedRuntime.Execute(DummyCode, getCallData, managedGetCtx);

        // -- Sandboxed Runtime --
        var (sandboxDeployCtx, sandboxDb) = CreateContext();
        var sandboxDeployResult = sandboxRuntime.Deploy(DummyCode, [], sandboxDeployCtx);

        var sandboxStorageKey = new byte[32];
        sandboxStorageKey[0] = 0xCC;
        var sandboxStorageValue = new byte[] { 0x10, 0x20, 0x30 };

        var setCallData2 = BuildCallData(StorageSetSelector, [.. sandboxStorageKey, .. sandboxStorageValue]);
        var (sandboxSetCtx, _) = CreateContextWithDb(sandboxDb);
        var sandboxSetResult = sandboxRuntime.Execute(DummyCode, setCallData2, sandboxSetCtx);

        var getCallData2 = BuildCallData(StorageGetSelector, sandboxStorageKey);
        var (sandboxGetCtx, _) = CreateContextWithDb(sandboxDb);
        var sandboxGetResult = sandboxRuntime.Execute(DummyCode, getCallData2, sandboxGetCtx);

        // -- Compare results --
        managedDeployResult.Success.Should().Be(sandboxDeployResult.Success);
        managedDeployResult.Code.Should().Equal(sandboxDeployResult.Code);

        managedSetResult.Success.Should().Be(sandboxSetResult.Success);

        managedGetResult.Success.Should().Be(sandboxGetResult.Success);
        managedGetResult.ReturnData.Should().Equal(sandboxGetResult.ReturnData);

        // Verify both databases have the same stored value.
        var managedStored = managedDb.GetStorage(ContractAddr, new Hash256(managedStorageKey));
        var sandboxStored = sandboxDb.GetStorage(ContractAddr, new Hash256(sandboxStorageKey));
        managedStored.Should().Equal(sandboxStored);
    }

    // =========================================================================
    // Additional helper for tests that share a DB across multiple contexts
    // =========================================================================

    /// <summary>
    /// Create a fresh <see cref="VmExecutionContext"/> that reuses an existing
    /// <see cref="InMemoryStateDb"/> (useful for multi-step workflows where
    /// deploy and execute share the same storage).
    /// </summary>
    private static (VmExecutionContext Ctx, InMemoryStateDb Db) CreateContextWithDb(
        InMemoryStateDb db, ulong gasLimit = 1_000_000)
    {
        var ctx = new VmExecutionContext
        {
            Caller = CallerAddress,
            ContractAddress = ContractAddr,
            Value = UInt256.Zero,
            BlockTimestamp = 1_700_000_000,
            BlockNumber = 42,
            BlockProposer = Address.Zero,
            ChainId = 1,
            GasMeter = new GasMeter(gasLimit),
            StateDb = db,
            CallDepth = 0,
        };

        return (ctx, db);
    }
}
