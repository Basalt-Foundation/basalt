using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Storage;

namespace Basalt.Sdk.Testing;

/// <summary>
/// In-process blockchain emulator for testing smart contracts.
/// Provides a simple environment to deploy and call contracts without a full node.
/// </summary>
public sealed class BasaltTestHost : IDisposable
{
    private readonly List<(string EventName, object Event)> _emittedEvents = new();
    private readonly Dictionary<string, object> _deployedContracts = new();
    private byte[] _currentCaller = new byte[20];
    private ulong _blockTimestamp = 1_000_000;
    private ulong _blockHeight = 1;

    public BasaltTestHost()
    {
        // Clear all shared state for a clean test
        ContractStorage.Clear();
        Context.Reset();

        // Wire up the Context
        Context.EventEmitted = (name, evt) => _emittedEvents.Add((name, evt));
        Context.BlockTimestamp = (long)_blockTimestamp;
        Context.BlockHeight = _blockHeight;

        // Wire up cross-contract call handler
        Context.CrossContractCallHandler = HandleCrossContractCall;
    }

    /// <summary>
    /// Deploy a contract at a given address for cross-contract calls.
    /// </summary>
    public void Deploy(byte[] address, object contract)
    {
        _deployedContracts[Convert.ToHexString(address)] = contract;
    }

    /// <summary>
    /// Set the caller address for subsequent contract calls.
    /// </summary>
    public void SetCaller(byte[] caller)
    {
        _currentCaller = caller;
        Context.Caller = caller;
    }

    /// <summary>
    /// Set the block timestamp.
    /// </summary>
    public void SetBlockTimestamp(ulong timestamp)
    {
        _blockTimestamp = timestamp;
        Context.BlockTimestamp = (long)timestamp;
    }

    /// <summary>
    /// Set the block height.
    /// </summary>
    public void SetBlockHeight(ulong height)
    {
        _blockHeight = height;
        Context.BlockHeight = height;
    }

    /// <summary>
    /// Advance the block by a number of blocks.
    /// </summary>
    public void AdvanceBlocks(ulong count)
    {
        _blockHeight += count;
        _blockTimestamp += count * 2000; // 2s per block
        Context.BlockHeight = _blockHeight;
        Context.BlockTimestamp = (long)_blockTimestamp;
    }

    /// <summary>
    /// Get all emitted events.
    /// </summary>
    public IReadOnlyList<(string EventName, object Event)> EmittedEvents => _emittedEvents;

    /// <summary>
    /// Get events of a specific type.
    /// </summary>
    public IEnumerable<T> GetEvents<T>() where T : class
    {
        return _emittedEvents
            .Where(e => e.Event is T)
            .Select(e => (T)e.Event);
    }

    /// <summary>
    /// Clear all emitted events.
    /// </summary>
    public void ClearEvents() => _emittedEvents.Clear();

    /// <summary>
    /// Execute a contract call, expecting it to succeed.
    /// </summary>
    public T Call<T>(Func<T> contractMethod)
    {
        PrepareContext();
        return contractMethod();
    }

    /// <summary>
    /// Execute a contract call (void return).
    /// </summary>
    public void Call(Action contractMethod)
    {
        PrepareContext();
        contractMethod();
    }

    /// <summary>
    /// Execute a contract call, expecting it to revert.
    /// Returns the revert message.
    /// </summary>
    public string? ExpectRevert(Action contractMethod)
    {
        PrepareContext();

        try
        {
            contractMethod();
            return null; // Did not revert
        }
        catch (ContractRevertException ex)
        {
            return ex.Message;
        }
    }

    // ---- Snapshot / Restore ----

    private Dictionary<string, object>? _snapshot;

    /// <summary>
    /// Take a snapshot of the current storage state.
    /// </summary>
    public void TakeSnapshot()
    {
        _snapshot = ContractStorage.Snapshot();
    }

    /// <summary>
    /// Restore storage to the last snapshot. Throws if no snapshot exists.
    /// </summary>
    public void RestoreSnapshot()
    {
        if (_snapshot == null)
            throw new InvalidOperationException("No snapshot to restore");
        ContractStorage.Restore(_snapshot);
    }

    // ---- Cross-Contract Calls ----

    #pragma warning disable IL2075 // Test infrastructure uses reflection for cross-contract dispatch
    private object? HandleCrossContractCall(byte[] targetAddress, string methodName, object?[] args)
    {
        var key = Convert.ToHexString(targetAddress);
        if (!_deployedContracts.TryGetValue(key, out var contract))
            throw new ContractRevertException($"Contract not found at {key}");

        var method = contract.GetType().GetMethod(methodName);
        if (method == null)
            throw new ContractRevertException($"Method '{methodName}' not found on contract");

        try
        {
            return method.Invoke(contract, args);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            return null; // unreachable
        }
    }
    #pragma warning restore IL2075

    // ---- Helpers ----

    private void PrepareContext()
    {
        Context.Caller = _currentCaller;
        Context.BlockTimestamp = (long)_blockTimestamp;
        Context.BlockHeight = _blockHeight;
        Context.CallDepth = 0;
    }

    /// <summary>
    /// Create a test address from a seed byte.
    /// </summary>
    public static byte[] CreateAddress(byte seed)
    {
        var addr = new byte[20];
        addr[19] = seed;
        return addr;
    }

    /// <summary>
    /// Create a test address from a hex string.
    /// </summary>
    public static byte[] CreateAddress(string hex)
    {
        if (hex.StartsWith("0x")) hex = hex[2..];
        return Convert.FromHexString(hex.PadLeft(40, '0'));
    }

    public void Dispose()
    {
        ContractStorage.Clear();
        Context.Reset();
    }
}
