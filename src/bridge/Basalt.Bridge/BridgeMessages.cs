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

    /// <summary>Block height at which the deposit was made.</summary>
    public ulong BlockHeight { get; init; }

    /// <summary>Timestamp of the deposit.</summary>
    public long Timestamp { get; init; }

    /// <summary>Direction of the bridge transfer.</summary>
    public BridgeDirection Direction { get; init; }

    /// <summary>Current status.</summary>
    public BridgeTransferStatus Status { get; set; } = BridgeTransferStatus.Pending;
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
