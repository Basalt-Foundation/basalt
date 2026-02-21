using Basalt.Core;

namespace Basalt.Bridge;

/// <summary>
/// Direction of a bridge transfer.
/// </summary>
public enum BridgeDirection
{
    /// <summary>Basalt -> Ethereum (lock on Basalt, mint wBST on Ethereum).</summary>
    BasaltToEthereum,

    /// <summary>Ethereum -> Basalt (burn wBST on Ethereum, unlock on Basalt).</summary>
    EthereumToBasalt,
}

/// <summary>
/// Status of a bridge transfer.
/// </summary>
public enum BridgeTransferStatus
{
    Pending,
    Confirmed,
    Finalized,
    Failed,
}

/// <summary>
/// A deposit message representing tokens locked on the source chain.
/// MED-03: Status transitions are validated via <see cref="TransitionStatus"/>.
/// </summary>
public sealed class BridgeDeposit
{
    /// <summary>Unique deposit ID (incrementing nonce).</summary>
    public ulong Nonce { get; init; }

    /// <summary>Sender address on the source chain.</summary>
    public byte[] Sender { get; init; } = [];

    /// <summary>Recipient address on the destination chain.</summary>
    public byte[] Recipient { get; init; } = [];

    /// <summary>Amount of tokens locked/deposited.</summary>
    public UInt256 Amount { get; init; }

    /// <summary>Token address on the source chain (zero = native token).</summary>
    public byte[] TokenAddress { get; init; } = [];

    /// <summary>Source chain ID.</summary>
    public uint SourceChainId { get; init; }

    /// <summary>Destination chain ID.</summary>
    public uint DestinationChainId { get; init; }

    /// <summary>Block height at which the deposit was confirmed.</summary>
    public ulong BlockHeight { get; set; }

    /// <summary>Timestamp of the deposit.</summary>
    public long Timestamp { get; init; }

    /// <summary>Direction of the bridge transfer.</summary>
    public BridgeDirection Direction { get; init; }

    private BridgeTransferStatus _status = BridgeTransferStatus.Pending;

    /// <summary>
    /// Current status. Use <see cref="TransitionStatus"/> for validated transitions.
    /// Direct setter is kept for backward compat with init patterns but validates transitions.
    /// </summary>
    public BridgeTransferStatus Status
    {
        get => _status;
        set
        {
            if (value == _status) return;
            if (!IsValidTransition(_status, value))
                throw new InvalidOperationException(
                    $"MED-03: Invalid deposit status transition from {_status} to {value}.");
            _status = value;
        }
    }

    /// <summary>
    /// MED-03: Attempt a validated status transition.
    /// Returns true if the transition was valid and applied.
    /// </summary>
    public bool TransitionStatus(BridgeTransferStatus newStatus)
    {
        if (newStatus == _status) return false;
        if (!IsValidTransition(_status, newStatus)) return false;
        _status = newStatus;
        return true;
    }

    /// <summary>
    /// MED-03: Valid transitions: Pending → Confirmed → Finalized, or any → Failed.
    /// </summary>
    private static bool IsValidTransition(BridgeTransferStatus from, BridgeTransferStatus to) =>
        (from, to) switch
        {
            (BridgeTransferStatus.Pending, BridgeTransferStatus.Confirmed) => true,
            (BridgeTransferStatus.Confirmed, BridgeTransferStatus.Finalized) => true,
            (_, BridgeTransferStatus.Failed) => true, // LOW-03: Failed can be reached from any state
            _ => false,
        };
}

/// <summary>
/// A withdrawal/claim message to release tokens on the destination chain.
/// Requires a valid Merkle proof from the source chain.
/// </summary>
public sealed class BridgeWithdrawal
{
    /// <summary>Nonce matching the original deposit.</summary>
    public ulong DepositNonce { get; init; }

    /// <summary>Recipient address on the destination chain.</summary>
    public byte[] Recipient { get; init; } = [];

    /// <summary>Amount to release.</summary>
    public UInt256 Amount { get; init; }

    /// <summary>Merkle proof of the deposit on the source chain.</summary>
    public byte[][] Proof { get; init; } = [];

    /// <summary>State root of the source chain at the deposit block.</summary>
    public byte[] StateRoot { get; init; } = [];

    /// <summary>Relayer signatures (multisig).</summary>
    public List<RelayerSignature> Signatures { get; init; } = [];
}

/// <summary>
/// A relayer's signature attesting to a cross-chain message.
/// </summary>
public sealed class RelayerSignature
{
    /// <summary>Relayer's public key.</summary>
    public byte[] PublicKey { get; init; } = [];

    /// <summary>Ed25519 signature over the message hash.</summary>
    public byte[] Signature { get; init; } = [];
}
