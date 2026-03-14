using Basalt.Core;

namespace Basalt.Sdk.Contracts.Policies;

/// <summary>
/// Interface for transfer policy contracts. Deploy as a standalone BasaltContract
/// and register with any BST token to enforce compliance rules on every transfer.
/// </summary>
/// <remarks>
/// Policies are invoked via cross-contract calls before each transfer.
/// Return true to allow the transfer, false to deny it.
/// The runtime reverts the entire transaction if any policy returns false.
/// </remarks>
public interface ITransferPolicy
{
    /// <summary>
    /// Called before a fungible token transfer (BST-20, BST-1155 single, BST-3525 value).
    /// </summary>
    /// <param name="token">Address of the token contract.</param>
    /// <param name="from">Sender address.</param>
    /// <param name="to">Recipient address.</param>
    /// <param name="amount">Transfer amount.</param>
    /// <returns>True to allow, false to deny.</returns>
    bool CheckTransfer(byte[] token, byte[] from, byte[] to, UInt256 amount);
}

/// <summary>
/// Interface for NFT transfer policy contracts (BST-721, BST-3525 token ownership).
/// </summary>
public interface INftTransferPolicy
{
    /// <summary>
    /// Called before an NFT ownership transfer.
    /// </summary>
    /// <param name="token">Address of the token contract.</param>
    /// <param name="from">Current owner address.</param>
    /// <param name="to">New owner address.</param>
    /// <param name="tokenId">Token ID being transferred.</param>
    /// <returns>True to allow, false to deny.</returns>
    bool CheckNftTransfer(byte[] token, byte[] from, byte[] to, ulong tokenId);
}

/// <summary>
/// Event emitted when a policy is added to a token.
/// </summary>
[BasaltEvent]
public sealed class PolicyAddedEvent
{
    [Indexed] public byte[] Token { get; init; } = [];
    [Indexed] public byte[] Policy { get; init; } = [];
}

/// <summary>
/// Event emitted when a policy is removed from a token.
/// </summary>
[BasaltEvent]
public sealed class PolicyRemovedEvent
{
    [Indexed] public byte[] Token { get; init; } = [];
    [Indexed] public byte[] Policy { get; init; } = [];
}

/// <summary>
/// Event emitted when a transfer is denied by a policy.
/// </summary>
[BasaltEvent]
public sealed class TransferDeniedEvent
{
    [Indexed] public byte[] Token { get; init; } = [];
    [Indexed] public byte[] Policy { get; init; } = [];
    [Indexed] public byte[] From { get; init; } = [];
    public byte[] To { get; init; } = [];
}
