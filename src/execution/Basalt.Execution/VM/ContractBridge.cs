using Basalt.Core;
using Basalt.Crypto;
using Basalt.Sdk.Contracts;
using Basalt.Storage;

namespace Basalt.Execution.VM;

/// <summary>
/// Bridges the SDK's static Context and ContractStorage to a VmExecutionContext.
/// Call Setup() before executing an SDK contract; dispose the returned scope to restore.
///
/// C-5: THREAD SAFETY WARNING — The SDK Context class uses static mutable fields.
/// Contract execution MUST be single-threaded. Concurrent execution will cause
/// cross-contract state corruption, unauthorized fund transfers, and data loss.
/// A runtime guard enforces this invariant via Interlocked.CompareExchange.
/// </summary>
public static class ContractBridge
{
    /// <summary>Where a contract's code lives in its own storage. Must match ManagedContractRuntime.</summary>
    private static readonly Hash256 ContractCodeKey = MakeCodeKey();

    private static Hash256 MakeCodeKey()
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0xFF;
        key[1] = 0x01;
        return new Hash256(key);
    }

    // C-5: Concurrency guard — only one contract execution at a time.
    // Uses a Monitor with timeout to serialize concurrent callers rather than rejecting them,
    // since genesis deployment and test execution may overlap.
    private static readonly object _executionLock = new();

    /// <summary>
    /// Wire SDK Context and ContractStorage from the given execution context.
    /// Returns an IDisposable that restores previous state on dispose.
    /// C-5: Serializes concurrent access to protect static Context/ContractStorage.
    /// </summary>
    public static IDisposable Setup(VmExecutionContext ctx, HostInterface host, IContractRuntime? runtime = null)
    {
        // C-5: Serialize SDK contract execution (static Context is not thread-safe)
        // B5: Reduced timeout from 30s to 10s (5× block time) — sufficient for genesis;
        // longer waits indicate a deadlock or excessively long contract execution.
        if (!Monitor.TryEnter(_executionLock, TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException(
                "Contract execution lock timeout (10s). " +
                "Likely deadlock or excessively long contract execution.");

        var scope = new BridgeScope();

        // Save previous state
        scope.PreviousCaller = Context.Caller;
        scope.PreviousSelf = Context.Self;
        scope.PreviousTxValue = Context.TxValue;
        scope.PreviousBlockTimestamp = Context.BlockTimestamp;
        scope.PreviousBlockHeight = Context.BlockHeight;
        scope.PreviousChainId = Context.ChainId;
        scope.PreviousGasRemaining = Context.GasRemaining;
        scope.PreviousCallDepth = Context.CallDepth;
        scope.PreviousIsDeploying = Context.IsDeploying;
        scope.PreviousEventEmitted = Context.EventEmitted;
        scope.PreviousNativeTransferHandler = Context.NativeTransferHandler;
        scope.PreviousCrossContractCallHandler = Context.CrossContractCallHandler;
        scope.PreviousEncodedCrossContractCallHandler = Context.EncodedCrossContractCallHandler;
        scope.PreviousProvider = ContractStorage.Provider;

        // Wire context from VmExecutionContext
        Context.Caller = ctx.CallerBytes;
        Context.Self = ctx.ContractAddressBytes;
        Context.TxValue = ctx.Value;
        Context.BlockTimestamp = (long)ctx.BlockTimestamp;
        Context.BlockHeight = ctx.BlockNumber;
        Context.ChainId = ctx.ChainId;
        // L-10: This is a snapshot — becomes stale as gas is consumed via host calls.
        // SDK contracts should treat this as an approximate upper bound.
        // Making it a live delegate would require changing the SDK API (Context.GasRemaining is ulong).
        Context.GasRemaining = ctx.GasMeter.GasRemaining;
        Context.CallDepth = ctx.CallDepth;
        Context.IsDeploying = false; // Default to false; Deploy() sets true after Setup()

        // Wire event handler
        //
        // The event encodes itself, so the payload that reaches the receipt is the one the contract
        // declared. This used to log the type name and discard every field, which meant a transfer
        // recorded that a transfer had happened without saying who received what.
        Context.EventEmitted = (eventName, evt) =>
        {
            var sig = Blake3Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(eventName));
            host.EmitEvent(sig, evt.ToLogTopics(), evt.ToLogData());
        };

        // Wire native transfer
        Context.NativeTransferHandler = (recipient, amount) =>
        {
            ctx.GasMeter.Consume(GasTable.Call);
            var recipientAddr = new Address(recipient);
            var contractAddr = ctx.ContractAddress;

            // Debit contract
            var contractAccount = ctx.StateDb.GetAccount(contractAddr);
            if (contractAccount is null || contractAccount.Value.Balance < amount)
                throw new ContractRevertException("Insufficient contract balance for transfer");

            var ca = contractAccount.Value;
            ctx.StateDb.SetAccount(contractAddr, new AccountState
            {
                Nonce = ca.Nonce,
                Balance = ca.Balance - amount,
                StorageRoot = ca.StorageRoot,
                CodeHash = ca.CodeHash,
                AccountType = ca.AccountType,
                ComplianceHash = ca.ComplianceHash,
            });

            // Credit recipient
            var recipientAccount = ctx.StateDb.GetAccount(recipientAddr);
            var recipientBalance = recipientAccount?.Balance ?? UInt256.Zero;
            var ra = recipientAccount ?? AccountState.Empty;
            // H-10: Use checked addition to prevent silent balance overflow
            ctx.StateDb.SetAccount(recipientAddr, new AccountState
            {
                Nonce = ra.Nonce,
                Balance = UInt256.CheckedAdd(recipientBalance, amount),
                StorageRoot = ra.StorageRoot,
                CodeHash = ra.CodeHash,
                AccountType = ra.AccountType,
                ComplianceHash = ra.ComplianceHash,
            });
        };

        // Wire cross-contract calls
        //
        // Without this a contract cannot call another contract at all, and nothing in the SDK test suite
        // would say so, because the SDK test host installs a handler of its own that invokes the target's
        // C# method directly. Every composed contract passes its unit tests and reverts on a validator.
        //
        // State is deliberately not forked here. The whole transaction already runs on a fork that the
        // executor merges only if the outermost call succeeded, so a revert anywhere in the chain of
        // calls discards everything, and a second fork per call would only add a merge that has to be
        // right rather than isolation that is already there.
        EncodedCallResult Invoke(byte[] targetAddress, byte[] callData)
        {
            ctx.GasMeter.Consume(GasTable.Call);

            var target = new Address(targetAddress);
            var targetCode = ctx.StateDb.GetStorage(target, ContractCodeKey);
            if (targetCode is null || targetCode.Length == 0)
                throw new ContractRevertException(
                    $"Cross-contract call target has no code: 0x{Convert.ToHexString(targetAddress).ToLowerInvariant()}");

            var nested = new VmExecutionContext
            {
                Caller = ctx.ContractAddress,
                ContractAddress = target,
                Value = UInt256.Zero, // value is not forwarded, matching Context.CallContract
                BlockTimestamp = ctx.BlockTimestamp,
                BlockNumber = ctx.BlockNumber,
                BlockProposer = ctx.BlockProposer,
                ChainId = ctx.ChainId,
                GasMeter = ctx.GasMeter, // one budget for the whole call tree, not one per hop
                StateDb = ctx.StateDb,
                CallDepth = ctx.CallDepth + 1,
            };

            var result = runtime!.Execute(targetCode, callData, nested);

            if (!result.Success)
                throw new ContractRevertException(result.ErrorMessage ?? "Cross-contract call failed");

            // The callee's events belong to this transaction. Dropping them would make a composed call
            // look like it did half of what it did to anyone reading receipts.
            foreach (var log in nested.EmittedLogs)
                ctx.EmittedLogs.Add(log);

            return new EncodedCallResult(result.ReturnData ?? []);
        }

        Context.CrossContractCallHandler = runtime is null
            ? null
            : (targetAddress, methodName, args)
                => Invoke(targetAddress, CrossContractArgumentEncoder.Encode(methodName, args));

        // The same call for arguments encoded before the call was made. Governance writes a proposal now
        // and executes it days later, and an object[] does not survive that wait.
        Context.EncodedCrossContractCallHandler = runtime is null
            ? null
            : (targetAddress, methodName, encodedArgs)
                => Invoke(targetAddress, CrossContractArgumentEncoder.WithSelector(methodName, encodedArgs));

        // Wire storage provider
        ContractStorage.SetProvider(new HostStorageProvider(host));

        return scope;
    }

    private sealed class BridgeScope : IDisposable
    {
        public byte[] PreviousCaller = null!;
        public byte[] PreviousSelf = null!;
        public UInt256 PreviousTxValue;
        public long PreviousBlockTimestamp;
        public ulong PreviousBlockHeight;
        public uint PreviousChainId;
        public ulong PreviousGasRemaining;
        public int PreviousCallDepth;
        public bool PreviousIsDeploying;
        public Action<string, IBasaltEvent>? PreviousEventEmitted;
        public Action<byte[], UInt256>? PreviousNativeTransferHandler;
        public Func<byte[], string, object?[], object?>? PreviousCrossContractCallHandler;
        public Func<byte[], string, byte[], object?>? PreviousEncodedCrossContractCallHandler;
        public IStorageProvider PreviousProvider = null!;

        public void Dispose()
        {
            Context.Caller = PreviousCaller;
            Context.Self = PreviousSelf;
            Context.TxValue = PreviousTxValue;
            Context.BlockTimestamp = PreviousBlockTimestamp;
            Context.BlockHeight = PreviousBlockHeight;
            Context.ChainId = PreviousChainId;
            Context.GasRemaining = PreviousGasRemaining;
            Context.CallDepth = PreviousCallDepth;
            Context.IsDeploying = PreviousIsDeploying;
            Context.EventEmitted = PreviousEventEmitted;
            Context.NativeTransferHandler = PreviousNativeTransferHandler;
            Context.CrossContractCallHandler = PreviousCrossContractCallHandler;
            Context.EncodedCrossContractCallHandler = PreviousEncodedCrossContractCallHandler;
            ContractStorage.SetProvider(PreviousProvider);

            // C-5: Release the execution lock
            Monitor.Exit(_executionLock);
        }
    }
}
