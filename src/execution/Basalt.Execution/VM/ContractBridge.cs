using Basalt.Core;
using Basalt.Crypto;
using Basalt.Sdk.Contracts;
using Basalt.Storage;

namespace Basalt.Execution.VM;

/// <summary>
/// Bridges the SDK's static Context and ContractStorage to a VmExecutionContext.
/// Call Setup() before executing an SDK contract; dispose the returned scope to restore.
/// </summary>
public static class ContractBridge
{
    /// <summary>
    /// Wire SDK Context and ContractStorage from the given execution context.
    /// Returns an IDisposable that restores previous state on dispose.
    /// </summary>
    public static IDisposable Setup(VmExecutionContext ctx, HostInterface host)
    {
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
        Context.TxValue = ctx.Value.IsZero ? 0UL : (ulong)ctx.Value.Lo;
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
            if (contractAccount is null || contractAccount.Value.Balance < new UInt256(amount))
                throw new ContractRevertException("Insufficient contract balance for transfer");

            var ca = contractAccount.Value;
            ctx.StateDb.SetAccount(contractAddr, new AccountState
            {
                Nonce = ca.Nonce,
                Balance = ca.Balance - new UInt256(amount),
                StorageRoot = ca.StorageRoot,
                CodeHash = ca.CodeHash,
                AccountType = ca.AccountType,
                ComplianceHash = ca.ComplianceHash,
            });

            // Credit recipient
            var recipientAccount = ctx.StateDb.GetAccount(recipientAddr);
            var recipientBalance = recipientAccount?.Balance ?? UInt256.Zero;
            var ra = recipientAccount ?? AccountState.Empty;
            ctx.StateDb.SetAccount(recipientAddr, new AccountState
            {
                Nonce = ra.Nonce,
                Balance = recipientBalance + new UInt256(amount),
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
        public ulong PreviousTxValue;
        public long PreviousBlockTimestamp;
        public ulong PreviousBlockHeight;
        public uint PreviousChainId;
        public ulong PreviousGasRemaining;
        public int PreviousCallDepth;
        public Action<string, object>? PreviousEventEmitted;
        public Action<byte[], ulong>? PreviousNativeTransferHandler;
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
        }
    }
}
