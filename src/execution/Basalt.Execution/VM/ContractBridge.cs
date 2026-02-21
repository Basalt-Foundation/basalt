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
    // C-5: Concurrency guard — only one contract execution at a time
    private static int _executionLock;

    /// <summary>
    /// Wire SDK Context and ContractStorage from the given execution context.
    /// Returns an IDisposable that restores previous state on dispose.
    /// C-5: Throws if another contract execution is already in progress.
    /// </summary>
    public static IDisposable Setup(VmExecutionContext ctx, HostInterface host)
    {
        // C-5: Enforce single-threaded execution of SDK contracts
        if (Interlocked.CompareExchange(ref _executionLock, 1, 0) != 0)
            throw new InvalidOperationException(
                "Concurrent SDK contract execution detected. " +
                "Contract execution must be single-threaded due to static Context/ContractStorage.");

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
        scope.PreviousEventEmitted = Context.EventEmitted;
        scope.PreviousNativeTransferHandler = Context.NativeTransferHandler;
        scope.PreviousProvider = ContractStorage.Provider;

        // Wire context from VmExecutionContext
        Context.Caller = ctx.Caller.ToArray();
        Context.Self = ctx.ContractAddress.ToArray();
        Context.TxValue = ctx.Value;
        Context.BlockTimestamp = (long)ctx.BlockTimestamp;
        Context.BlockHeight = ctx.BlockNumber;
        Context.ChainId = ctx.ChainId;
        Context.GasRemaining = ctx.GasMeter.GasRemaining;
        Context.CallDepth = ctx.CallDepth;

        // Wire event handler
        Context.EventEmitted = (eventName, eventData) =>
        {
            var sig = Blake3Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(eventName));
            host.EmitEvent(sig, [], System.Text.Encoding.UTF8.GetBytes(eventName));
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
        public Action<string, object>? PreviousEventEmitted;
        public Action<byte[], UInt256>? PreviousNativeTransferHandler;
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
            Context.EventEmitted = PreviousEventEmitted;
            Context.NativeTransferHandler = PreviousNativeTransferHandler;
            ContractStorage.SetProvider(PreviousProvider);

            // C-5: Release the execution lock
            Interlocked.Exchange(ref _executionLock, 0);
        }
    }
}
