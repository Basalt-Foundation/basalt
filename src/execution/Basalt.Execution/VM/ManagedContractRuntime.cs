using Basalt.Core;
using Basalt.Crypto;
using Basalt.Sdk.Contracts;

namespace Basalt.Execution.VM;

/// <summary>
/// Phase 1 contract runtime using in-process managed execution.
///
/// Supports two code formats:
/// 1. Built-in methods: BLAKE3-based selectors (storage_set/get/del, emit_event)
/// 2. SDK contracts: Magic bytes [0xBA, 0x5A] + FNV-1a dispatch via IDispatchable
///
/// SDK contracts are instantiated via ContractRegistry factory delegates (AOT-safe).
/// </summary>
public sealed class ManagedContractRuntime : IContractRuntime
{
    private readonly ContractRegistry _registry;
    // Pre-computed BLAKE3-based method selectors (first 4 bytes of BLAKE3(method_name))
    private static readonly string SelectorStorageSet = Convert.ToHexString(ComputeSelector("storage_set")).ToLowerInvariant();
    private static readonly string SelectorStorageGet = Convert.ToHexString(ComputeSelector("storage_get")).ToLowerInvariant();
    private static readonly string SelectorStorageDel = Convert.ToHexString(ComputeSelector("storage_del")).ToLowerInvariant();
    private static readonly string SelectorEmitEvent = Convert.ToHexString(ComputeSelector("emit_event")).ToLowerInvariant();

    public ManagedContractRuntime()
    {
        _registry = ContractRegistry.CreateDefault();
    }

    public ManagedContractRuntime(ContractRegistry registry)
    {
        _registry = registry;
    }

    public ContractDeployResult Deploy(byte[] code, byte[] constructorArgs, VmExecutionContext ctx)
    {
        var host = new HostInterface(ctx);

        try
        {
            // Charge base deployment gas + gas for code storage
            ctx.GasMeter.Consume(GasTable.ContractCreation);
            ctx.GasMeter.Consume((ulong)code.Length * GasTable.TxDataNonZeroByte);

            // Store the code in contract storage under a well-known key
            var codeStorageKey = GetCodeStorageKey();
            host.StorageWrite(codeStorageKey, code);

            // For SDK contracts, run the constructor (wires storage + context)
            if (ContractRegistry.IsSdkContract(code))
            {
                var (typeId, ctorArgs) = ContractRegistry.ParseManifest(code);
                using var scope = ContractBridge.Setup(ctx, host);
                // Instantiate the contract — constructor runs and initializes storage
                _registry.CreateInstance(typeId, ctorArgs);
            }

            return new ContractDeployResult
            {
                Success = true,
                Code = code,
                Logs = [.. ctx.EmittedLogs],
            };
        }
        catch (OutOfGasException)
        {
            return new ContractDeployResult
            {
                Success = false,
                Code = [],
                ErrorMessage = "Out of gas during contract deployment",
            };
        }
        catch (ContractRevertException ex)
        {
            return new ContractDeployResult
            {
                Success = false,
                Code = [],
                ErrorMessage = ex.Message,
            };
        }
        catch (Basalt.Sdk.Contracts.ContractRevertException ex)
        {
            return new ContractDeployResult
            {
                Success = false,
                Code = [],
                ErrorMessage = ex.Message,
            };
        }
    }

    public ContractCallResult Execute(byte[] code, byte[] callData, VmExecutionContext ctx)
    {
        // C-6: Enforce maximum call depth to prevent stack overflow attacks
        if (ctx.CallDepth >= VmExecutionContext.MaxCallDepth)
        {
            return new ContractCallResult
            {
                Success = false,
                ErrorMessage = $"Maximum call depth ({VmExecutionContext.MaxCallDepth}) exceeded.",
            };
        }

        var host = new HostInterface(ctx);

        try
        {
            // Charge base call gas
            ctx.GasMeter.Consume(GasTable.Call);

            if (code.Length == 0)
            {
                return new ContractCallResult
                {
                    Success = false,
                    ErrorMessage = "Contract has no code",
                };
            }

            // SDK contracts: use ContractBridge + IDispatchable
            if (ContractRegistry.IsSdkContract(code))
                return ExecuteSdkContract(code, callData, ctx, host);

            // Built-in contracts: BLAKE3 selector dispatch
            if (callData.Length < 4)
            {
                // Fallback/receive function — just accept value
                return new ContractCallResult
                {
                    Success = true,
                    Logs = [.. ctx.EmittedLogs],
                };
            }

            var selector = callData[..4];
            var args = callData.Length > 4 ? callData[4..] : Array.Empty<byte>();

            return DispatchCall(host, ctx, selector, args);
        }
        catch (OutOfGasException)
        {
            return new ContractCallResult
            {
                Success = false,
                ErrorMessage = "Out of gas",
            };
        }
        catch (ContractRevertException ex)
        {
            return new ContractCallResult
            {
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
        catch (Basalt.Sdk.Contracts.ContractRevertException ex)
        {
            return new ContractCallResult
            {
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
    }

    private ContractCallResult ExecuteSdkContract(byte[] code, byte[] callData, VmExecutionContext ctx, HostInterface host)
    {
        var (typeId, ctorArgs) = ContractRegistry.ParseManifest(code);

        using var scope = ContractBridge.Setup(ctx, host);

        // Instantiate the contract (constructor populates in-memory fields + reads storage)
        var contract = _registry.CreateInstance(typeId, ctorArgs);

        if (callData.Length < 4)
        {
            return new ContractCallResult
            {
                Success = true,
                Logs = [.. ctx.EmittedLogs],
            };
        }

        var selector = callData[..4];
        var args = callData.Length > 4 ? callData[4..] : [];

        var result = contract.Dispatch(selector, args);

        return new ContractCallResult
        {
            Success = true,
            ReturnData = result,
            Logs = [.. ctx.EmittedLogs],
        };
    }

    private static ContractCallResult DispatchCall(HostInterface host, VmExecutionContext ctx, byte[] selector, byte[] args)
    {
        // Method selectors are BLAKE3(method_name)[0:4], matching AbiEncoder.ComputeSelector
        var selectorHex = Convert.ToHexString(selector).ToLowerInvariant();

        if (selectorHex == SelectorStorageSet)
            return ExecuteStorageSet(host, ctx, args);
        if (selectorHex == SelectorStorageGet)
            return ExecuteStorageGet(host, ctx, args);
        if (selectorHex == SelectorStorageDel)
            return ExecuteStorageDelete(host, ctx, args);
        if (selectorHex == SelectorEmitEvent)
            return ExecuteEmitEvent(host, ctx, args);

        return new ContractCallResult
        {
            Success = false,
            ErrorMessage = $"Unknown method selector: 0x{selectorHex}",
        };
    }

    private static ContractCallResult ExecuteStorageSet(HostInterface host, VmExecutionContext ctx, byte[] args)
    {
        if (args.Length < Hash256.Size)
            return new ContractCallResult { Success = false, ErrorMessage = "Invalid arguments for storage_set" };

        var key = new Hash256(args.AsSpan(0, Hash256.Size));
        var value = args[Hash256.Size..];
        host.StorageWrite(key, value);

        return new ContractCallResult
        {
            Success = true,
            Logs = [.. ctx.EmittedLogs],
        };
    }

    private static ContractCallResult ExecuteStorageGet(HostInterface host, VmExecutionContext ctx, byte[] args)
    {
        if (args.Length < Hash256.Size)
            return new ContractCallResult { Success = false, ErrorMessage = "Invalid arguments for storage_get" };

        var key = new Hash256(args.AsSpan(0, Hash256.Size));
        var value = host.StorageRead(key);

        return new ContractCallResult
        {
            Success = true,
            ReturnData = value ?? [],
            Logs = [.. ctx.EmittedLogs],
        };
    }

    private static ContractCallResult ExecuteStorageDelete(HostInterface host, VmExecutionContext ctx, byte[] args)
    {
        if (args.Length < Hash256.Size)
            return new ContractCallResult { Success = false, ErrorMessage = "Invalid arguments for storage_del" };

        var key = new Hash256(args.AsSpan(0, Hash256.Size));
        host.StorageDelete(key);

        return new ContractCallResult
        {
            Success = true,
            Logs = [.. ctx.EmittedLogs],
        };
    }

    private static ContractCallResult ExecuteEmitEvent(HostInterface host, VmExecutionContext ctx, byte[] args)
    {
        if (args.Length < Hash256.Size)
            return new ContractCallResult { Success = false, ErrorMessage = "Invalid arguments for emit_event" };

        var eventSig = new Hash256(args.AsSpan(0, Hash256.Size));
        // Remaining data is the event payload
        var data = args[Hash256.Size..];
        host.EmitEvent(eventSig, [], data);

        return new ContractCallResult
        {
            Success = true,
            Logs = [.. ctx.EmittedLogs],
        };
    }

    private static Hash256 GetCodeStorageKey()
    {
        // Well-known storage key for contract code
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0xFF; // Reserved prefix for system storage
        key[1] = 0x01; // Code slot
        return new Hash256(key);
    }

    /// <summary>
    /// Compute a 4-byte method selector from a method signature string.
    /// </summary>
    public static byte[] ComputeSelector(string methodSignature)
    {
        var hash = Blake3Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(methodSignature));
        var selector = new byte[4];
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(hashBytes);
        hashBytes[..4].CopyTo(selector);
        return selector;
    }
}
