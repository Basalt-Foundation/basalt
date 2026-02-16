namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Time-locked escrow contract. Lock native tokens until a release block.
/// Type ID: 0x0103
/// </summary>
[BasaltContract]
public partial class Escrow
{
    private readonly StorageValue<ulong> _nextEscrowId;
    private readonly StorageMap<string, string> _depositors;     // id -> depositor hex
    private readonly StorageMap<string, string> _recipients;     // id -> recipient hex
    private readonly StorageMap<string, ulong> _amounts;         // id -> amount
    private readonly StorageMap<string, ulong> _releaseBlocks;   // id -> release block
    private readonly StorageMap<string, string> _status;         // id -> "locked"/"released"/"refunded"

    public Escrow()
    {
        _nextEscrowId = new StorageValue<ulong>("esc_next");
        _depositors = new StorageMap<string, string>("esc_dep");
        _recipients = new StorageMap<string, string>("esc_rec");
        _amounts = new StorageMap<string, ulong>("esc_amt");
        _releaseBlocks = new StorageMap<string, ulong>("esc_rel");
        _status = new StorageMap<string, string>("esc_status");
    }

    /// <summary>
    /// Create a new escrow. Locks the sent value until releaseBlock.
    /// Returns the escrow ID.
    /// </summary>
    [BasaltEntrypoint]
    public ulong Create(byte[] recipient, ulong releaseBlock)
    {
        Context.Require(Context.TxValue > 0, "ESCROW: must send value");
        Context.Require(releaseBlock > Context.BlockHeight, "ESCROW: release must be in future");

        var id = _nextEscrowId.Get();
        _nextEscrowId.Set(id + 1);

        var key = id.ToString();
        _depositors.Set(key, Convert.ToHexString(Context.Caller));
        _recipients.Set(key, Convert.ToHexString(recipient));
        _amounts.Set(key, Context.TxValue);
        _releaseBlocks.Set(key, releaseBlock);
        _status.Set(key, "locked");

        Context.Emit(new EscrowCreatedEvent
        {
            EscrowId = id,
            Depositor = Context.Caller,
            Recipient = recipient,
            Amount = Context.TxValue,
            ReleaseBlock = releaseBlock,
        });

        return id;
    }

    /// <summary>
    /// Release escrowed funds to the recipient. Only callable after release block.
    /// Either depositor or recipient can trigger release.
    /// </summary>
    [BasaltEntrypoint]
    public void Release(ulong escrowId)
    {
        var key = escrowId.ToString();
        Context.Require(_status.Get(key) == "locked", "ESCROW: not locked");
        Context.Require(Context.BlockHeight >= _releaseBlocks.Get(key), "ESCROW: not yet releasable");

        var callerHex = Convert.ToHexString(Context.Caller);
        var depositorHex = _depositors.Get(key);
        var recipientHex = _recipients.Get(key);
        Context.Require(callerHex == depositorHex || callerHex == recipientHex,
            "ESCROW: not authorized");

        var amount = _amounts.Get(key);
        _status.Set(key, "released");

        Context.TransferNative(Convert.FromHexString(recipientHex), amount);

        Context.Emit(new EscrowReleasedEvent { EscrowId = escrowId, Amount = amount });
    }

    /// <summary>
    /// Refund escrowed funds to the depositor. Only depositor, before release block.
    /// </summary>
    [BasaltEntrypoint]
    public void Refund(ulong escrowId)
    {
        var key = escrowId.ToString();
        Context.Require(_status.Get(key) == "locked", "ESCROW: not locked");

        var callerHex = Convert.ToHexString(Context.Caller);
        var depositorHex = _depositors.Get(key);
        Context.Require(callerHex == depositorHex, "ESCROW: only depositor");
        Context.Require(Context.BlockHeight < _releaseBlocks.Get(key), "ESCROW: already releasable");

        var amount = _amounts.Get(key);
        _status.Set(key, "refunded");

        Context.TransferNative(Convert.FromHexString(depositorHex), amount);

        Context.Emit(new EscrowRefundedEvent { EscrowId = escrowId, Amount = amount });
    }

    [BasaltView]
    public string GetStatus(ulong escrowId)
    {
        return _status.Get(escrowId.ToString()) ?? "unknown";
    }

    [BasaltView]
    public ulong GetAmount(ulong escrowId)
    {
        return _amounts.Get(escrowId.ToString());
    }
}

[BasaltEvent]
public class EscrowCreatedEvent
{
    [Indexed] public ulong EscrowId { get; set; }
    [Indexed] public byte[] Depositor { get; set; } = null!;
    public byte[] Recipient { get; set; } = null!;
    public ulong Amount { get; set; }
    public ulong ReleaseBlock { get; set; }
}

[BasaltEvent]
public class EscrowReleasedEvent
{
    [Indexed] public ulong EscrowId { get; set; }
    public ulong Amount { get; set; }
}

[BasaltEvent]
public class EscrowRefundedEvent
{
    [Indexed] public ulong EscrowId { get; set; }
    public ulong Amount { get; set; }
}
