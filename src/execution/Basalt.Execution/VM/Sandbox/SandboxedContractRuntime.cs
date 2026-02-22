using Basalt.Core;
using Basalt.Crypto;
using Basalt.Sdk.Contracts;

namespace Basalt.Execution.VM.Sandbox;

/// <summary>
/// AOT-sandbox-aware contract runtime implementation.
///
/// Phase 1: Uses the same built-in dispatch table as <see cref="ManagedContractRuntime"/>
/// (storage_set, storage_get, storage_del, emit_event), but wraps every invocation in:
///   - A <see cref="ContractAssemblyContext"/> (collectible ALC for future AOT assembly loading)
///   - A <see cref="ResourceLimiter"/> for memory tracking
///   - A <see cref="CancellationTokenSource"/> for wall-clock timeout enforcement
///
/// Phase 2+: The dispatch will load AOT-compiled contract assemblies into the ALC
/// and invoke them through the <see cref="SandboxedHostBridge"/>.
/// </summary>
public sealed class SandboxedContractRuntime : IContractRuntime
{
    // Pre-computed BLAKE3-based method selectors (must match ManagedContractRuntime)
    private static readonly string SelectorStorageSet = Convert.ToHexString(ManagedContractRuntime.ComputeSelector("storage_set")).ToLowerInvariant();
    private static readonly string SelectorStorageGet = Convert.ToHexString(ManagedContractRuntime.ComputeSelector("storage_get")).ToLowerInvariant();
    private static readonly string SelectorStorageDel = Convert.ToHexString(ManagedContractRuntime.ComputeSelector("storage_del")).ToLowerInvariant();
    private static readonly string SelectorEmitEvent = Convert.ToHexString(ManagedContractRuntime.ComputeSelector("emit_event")).ToLowerInvariant();

    private readonly SandboxConfiguration _config;
    private readonly ContractRegistry _registry;

    public SandboxedContractRuntime(SandboxConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _registry = ContractRegistry.CreateDefault();
    }

    public SandboxedContractRuntime(SandboxConfiguration config, ContractRegistry registry)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _registry = registry;
    }

    // =========================================================================
    // Deploy
    // =========================================================================

    public ContractDeployResult Deploy(byte[] code, byte[] constructorArgs, VmExecutionContext ctx)
    {
        if (code == null || code.Length == 0)
        {
            return new ContractDeployResult
            {
                Success = false,
                Code = [],
                ErrorMessage = "Contract code must not be empty.",
            };
        }

        // Create a temporary ALC for validation (will be unloaded immediately)
        var codeHash = Blake3Hasher.Hash(code);
        var alc = new ContractAssemblyContext($"deploy-{codeHash}");

        try
        {
            var host = new HostInterface(ctx);

            // Charge base deployment gas + per-byte gas for code storage
            ctx.GasMeter.Consume(GasTable.ContractCreation);
            ctx.GasMeter.Consume((ulong)code.Length * GasTable.TxDataNonZeroByte);

            // Store the code in contract storage under the well-known key
            var codeStorageKey = GetCodeStorageKey();
            host.StorageWrite(codeStorageKey, code);

            // M-13: Run SDK contract constructor (matching ManagedContractRuntime behavior)
            if (ContractRegistry.IsSdkContract(code))
            {
                var (typeId, ctorArgs) = ContractRegistry.ParseManifest(code);
                using var scope = ContractBridge.Setup(ctx, host);
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
                ErrorMessage = "Out of gas during contract deployment.",
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
        catch (SandboxTimeoutException)
        {
            return new ContractDeployResult
            {
                Success = false,
                Code = [],
                ErrorMessage = "Contract deployment timed out.",
            };
        }
        finally
        {
            alc.Unload();
        }
    }

    // =========================================================================
    // Execute
    // =========================================================================

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

        if (code == null || code.Length == 0)
        {
            return new ContractCallResult
            {
                Success = false,
                ErrorMessage = "Contract has no code.",
            };
        }

        // Set up sandbox infrastructure
        var codeHash = Blake3Hasher.Hash(code);
        var alc = new ContractAssemblyContext($"exec-{codeHash}");
        var limiter = _config.EnableMemoryTracking
            ? new ResourceLimiter(_config.MemoryLimitBytes)
            : new ResourceLimiter(long.MaxValue);

        try
        {
            var host = new HostInterface(ctx);
            var bridge = new SandboxedHostBridge(host, limiter);

            // Charge base call gas
            ctx.GasMeter.Consume(GasTable.Call);

            // H-12: SDK contract path — now wrapped in timeout scope
            // MED-05: Uses Task.Run + WaitAsync for preemptive cancellation.
            // Previously, CancellationToken was only checked before/after Dispatch(),
            // so an infinite loop inside Dispatch() would never be interrupted.
            if (ContractRegistry.IsSdkContract(code))
            {
                using var sdkCts = new CancellationTokenSource(_config.ExecutionTimeout);
                try
                {
                    var (typeId, ctorArgs) = ContractRegistry.ParseManifest(code);
                    using var scope = ContractBridge.Setup(ctx, host);
                    var contract = _registry.CreateInstance(typeId, ctorArgs);

                    sdkCts.Token.ThrowIfCancellationRequested();

                    if (callData.Length < 4)
                        return new ContractCallResult { Success = true, Logs = [.. ctx.EmittedLogs] };

                    var selectorBytes = callData[..4];
                    var argBytes = callData.Length > 4 ? callData[4..] : Array.Empty<byte>();

                    // Preemptive timeout: run Dispatch on a thread pool thread and cancel via WaitAsync
                    var dispatchTask = Task.Run(() => contract.Dispatch(selectorBytes, argBytes));
                    var result = dispatchTask.WaitAsync(sdkCts.Token).GetAwaiter().GetResult();

                    return new ContractCallResult { Success = true, ReturnData = result, Logs = [.. ctx.EmittedLogs] };
                }
                catch (OperationCanceledException) when (sdkCts.IsCancellationRequested)
                {
                    throw new SandboxTimeoutException(_config.ExecutionTimeout);
                }
            }

            // Short-circuit: if callData is too short for a selector, treat as fallback
            if (callData.Length < 4)
            {
                return new ContractCallResult
                {
                    Success = true,
                    Logs = [.. ctx.EmittedLogs],
                };
            }

            var selector = callData[..4];
            var args = callData.Length > 4 ? callData[4..] : Array.Empty<byte>();

            // Execute with timeout enforcement
            using var cts = new CancellationTokenSource(_config.ExecutionTimeout);

            try
            {
                // Phase 1: dispatch to built-in operations (same selectors as ManagedContractRuntime).
                // The bridge and ALC are wired up so that Phase 2 can swap in real assembly loading.
                return DispatchCall(bridge, host, ctx, selector, args, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new SandboxTimeoutException(_config.ExecutionTimeout);
            }
        }
        catch (OutOfGasException)
        {
            return new ContractCallResult
            {
                Success = false,
                ErrorMessage = "Out of gas.",
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
        catch (SandboxTimeoutException ex)
        {
            return new ContractCallResult
            {
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
        catch (BasaltException ex) when (ex.ErrorCode == BasaltErrorCode.MemoryLimitExceeded)
        {
            return new ContractCallResult
            {
                Success = false,
                ErrorMessage = ex.Message,
            };
        }
        finally
        {
            alc.Unload();
        }
    }

    // =========================================================================
    // Phase 1 Dispatch
    // =========================================================================

    private static ContractCallResult DispatchCall(
        SandboxedHostBridge bridge,
        HostInterface host,
        VmExecutionContext ctx,
        byte[] selector,
        byte[] args,
        CancellationToken ct)
    {
        var selectorHex = Convert.ToHexString(selector).ToLowerInvariant();

        if (selectorHex == SelectorStorageSet)
            return ExecuteStorageSet(bridge, ctx, args, ct);
        if (selectorHex == SelectorStorageGet)
            return ExecuteStorageGet(bridge, ctx, args, ct);
        if (selectorHex == SelectorStorageDel)
            return ExecuteStorageDelete(bridge, ctx, args, ct);
        if (selectorHex == SelectorEmitEvent)
            return ExecuteEmitEvent(bridge, ctx, args, ct);

        return new ContractCallResult
        {
            Success = false,
            ErrorMessage = $"Unknown method selector: 0x{selectorHex}",
        };
    }

    private static ContractCallResult ExecuteStorageSet(
        SandboxedHostBridge bridge, VmExecutionContext ctx, byte[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (args.Length < Hash256.Size)
            return new ContractCallResult { Success = false, ErrorMessage = "Invalid arguments for storage_set." };

        var key = args[..Hash256.Size];
        var value = args[Hash256.Size..];
        bridge.StorageWrite(key, value);

        return new ContractCallResult
        {
            Success = true,
            Logs = [.. ctx.EmittedLogs],
        };
    }

    private static ContractCallResult ExecuteStorageGet(
        SandboxedHostBridge bridge, VmExecutionContext ctx, byte[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (args.Length < Hash256.Size)
            return new ContractCallResult { Success = false, ErrorMessage = "Invalid arguments for storage_get." };

        var key = args[..Hash256.Size];
        var value = bridge.StorageRead(key);

        return new ContractCallResult
        {
            Success = true,
            ReturnData = value ?? [],
            Logs = [.. ctx.EmittedLogs],
        };
    }

    private static ContractCallResult ExecuteStorageDelete(
        SandboxedHostBridge bridge, VmExecutionContext ctx, byte[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (args.Length < Hash256.Size)
            return new ContractCallResult { Success = false, ErrorMessage = "Invalid arguments for storage_del." };

        var key = args[..Hash256.Size];
        bridge.StorageDelete(key);

        return new ContractCallResult
        {
            Success = true,
            Logs = [.. ctx.EmittedLogs],
        };
    }

    private static ContractCallResult ExecuteEmitEvent(
        SandboxedHostBridge bridge, VmExecutionContext ctx, byte[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (args.Length < Hash256.Size)
            return new ContractCallResult { Success = false, ErrorMessage = "Invalid arguments for emit_event." };

        var eventSig = args[..Hash256.Size];
        var data = args[Hash256.Size..];
        bridge.EmitEvent(eventSig, [], data);

        return new ContractCallResult
        {
            Success = true,
            Logs = [.. ctx.EmittedLogs],
        };
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static Hash256 GetCodeStorageKey()
    {
        // Well-known storage key for contract code (matches ManagedContractRuntime)
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0xFF; // Reserved prefix for system storage
        key[1] = 0x01; // Code slot
        return new Hash256(key);
    }
}
