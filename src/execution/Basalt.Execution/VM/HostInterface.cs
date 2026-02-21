using Basalt.Core;
using Basalt.Crypto;
using Basalt.Storage;

namespace Basalt.Execution.VM;

/// <summary>
/// Host interface functions available to smart contracts.
/// In Phase 1, these are called directly from managed code.
/// In Phase 2+, they'll be exposed as native function pointers for AOT-compiled contracts.
/// </summary>
public sealed class HostInterface
{
    private readonly VmExecutionContext _ctx;

    public HostInterface(VmExecutionContext ctx)
    {
        _ctx = ctx;
    }

    // === Storage ===

    public byte[]? StorageRead(Hash256 key)
    {
        _ctx.GasMeter.Consume(GasTable.StorageRead);
        return _ctx.StateDb.GetStorage(_ctx.ContractAddress, key);
    }

    public void StorageWrite(Hash256 key, byte[] value)
    {
        var existing = _ctx.StateDb.GetStorage(_ctx.ContractAddress, key);
        if (existing == null)
            _ctx.GasMeter.Consume(GasTable.StorageWriteNew);
        else
            _ctx.GasMeter.Consume(GasTable.StorageWrite);

        _ctx.StateDb.SetStorage(_ctx.ContractAddress, key, value);
    }

    public void StorageDelete(Hash256 key)
    {
        _ctx.GasMeter.Consume(GasTable.StorageDelete);
        var existing = _ctx.StateDb.GetStorage(_ctx.ContractAddress, key);
        if (existing != null)
        {
            _ctx.StateDb.DeleteStorage(_ctx.ContractAddress, key);
            _ctx.GasMeter.AddRefund(GasTable.StorageDeleteRefund);
        }
    }

    // === Cryptographic ===

    public Hash256 Blake3Hash(ReadOnlySpan<byte> data)
    {
        // H-11: Cast to ulong before addition to prevent int overflow
        _ctx.GasMeter.Consume(GasTable.Blake3Hash + ((ulong)data.Length + 31) / 32 * GasTable.Blake3HashPerWord);
        return Blake3Hasher.Hash(data);
    }

    public byte[] Keccak256(ReadOnlySpan<byte> data)
    {
        // H-11: Cast to ulong before addition to prevent int overflow
        _ctx.GasMeter.Consume(GasTable.Keccak256 + ((ulong)data.Length + 31) / 32 * GasTable.Keccak256PerWord);
        return KeccakHasher.Hash(data);
    }

    public bool Ed25519Verify(PublicKey publicKey, ReadOnlySpan<byte> message, Signature signature)
    {
        _ctx.GasMeter.Consume(GasTable.Ed25519Verify);
        return Ed25519Signer.Verify(publicKey, message, signature);
    }

    // === Context ===

    public Address GetCaller()
    {
        _ctx.GasMeter.Consume(GasTable.Caller);
        return _ctx.Caller;
    }

    public UInt256 GetValue()
    {
        _ctx.GasMeter.Consume(GasTable.Caller);
        return _ctx.Value;
    }

    public ulong GetBlockTimestamp()
    {
        _ctx.GasMeter.Consume(GasTable.Timestamp);
        return _ctx.BlockTimestamp;
    }

    public ulong GetBlockNumber()
    {
        _ctx.GasMeter.Consume(GasTable.BlockNumber);
        return _ctx.BlockNumber;
    }

    public UInt256 GetBalance(Address address)
    {
        _ctx.GasMeter.Consume(GasTable.Balance);
        var account = _ctx.StateDb.GetAccount(address);
        return account?.Balance ?? UInt256.Zero;
    }

    // === Events ===

    public void EmitEvent(Hash256 eventSignature, Hash256[] topics, byte[] data)
    {
        // L-13: Cap event logs per transaction
        if (_ctx.EmittedLogs.Count >= VmExecutionContext.MaxLogsPerTransaction)
            throw new ContractRevertException($"Maximum event logs per transaction ({VmExecutionContext.MaxLogsPerTransaction}) exceeded.");

        _ctx.GasMeter.Consume(
            GasTable.Log +
            (ulong)topics.Length * GasTable.LogTopic +
            (ulong)data.Length * GasTable.LogDataPerByte);

        _ctx.EmittedLogs.Add(new EventLog
        {
            Contract = _ctx.ContractAddress,
            EventSignature = eventSignature,
            Topics = topics,
            Data = data,
        });
    }

    // === Control ===

    public void Revert(string message)
    {
        throw new ContractRevertException(message);
    }

    public void Require(bool condition, string message)
    {
        if (!condition)
            throw new ContractRevertException(message);
    }
}

/// <summary>
/// Thrown when a contract explicitly reverts execution.
/// </summary>
public sealed class ContractRevertException : BasaltException
{
    public ContractRevertException(string message)
        : base(BasaltErrorCode.ContractReverted, message) { }
}
