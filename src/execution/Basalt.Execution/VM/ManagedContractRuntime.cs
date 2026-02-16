using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Execution.VM;

/// <summary>
/// Phase 1 contract runtime using in-process managed execution.
/// Contracts are stored as bytecode with a simple ABI dispatch table.
/// This provides a functional contract system while the AOT sandbox is developed.
///
/// Contract bytecode format:
/// - First 4 bytes: method selector (BLAKE3 hash of method signature, first 4 bytes)
/// - Remaining bytes: ABI-encoded parameters
///
/// Contract code stored on-chain:
/// - The contract "code" is simply stored and its code hash recorded.
/// - The dispatch is handled by a lookup table keyed by method selector.
/// </summary>
public sealed class ManagedContractRuntime : IContractRuntime
{
    // Pre-computed BLAKE3-based method selectors (first 4 bytes of BLAKE3(method_name))
    private static readonly string SelectorStorageSet = Convert.ToHexString(ComputeSelector("storage_set")).ToLowerInvariant();
    private static readonly string SelectorStorageGet = Convert.ToHexString(ComputeSelector("storage_get")).ToLowerInvariant();
    private static readonly string SelectorStorageDel = Convert.ToHexString(ComputeSelector("storage_del")).ToLowerInvariant();
    private static readonly string SelectorEmitEvent = Convert.ToHexString(ComputeSelector("emit_event")).ToLowerInvariant();

    public ContractDeployResult Deploy(byte[] code, byte[] constructorArgs, VmExecutionContext ctx)
    {
        var host = new HostInterface(ctx);

        try
        {
            // Charge base deployment gas + gas for code storage
            ctx.GasMeter.Consume(GasTable.ContractCreation);
            ctx.GasMeter.Consume((ulong)code.Length * GasTable.TxDataNonZeroByte);

            // Store the code hash
            var codeHash = Blake3Hasher.Hash(code);

            // Store the code in contract storage under a well-known key
            var codeStorageKey = GetCodeStorageKey();
            host.StorageWrite(codeStorageKey, code);

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
    }

    public ContractCallResult Execute(byte[] code, byte[] callData, VmExecutionContext ctx)
    {
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

            // For Phase 1, contracts use a simple dispatch mechanism:
            // The first 4 bytes of callData are the method selector.
            // The contract code is a serialized dispatch table that maps
            // selectors to predefined operations.
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

            // Dispatch to built-in contract operations
            // Phase 1 provides a simple key-value store contract interface
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
