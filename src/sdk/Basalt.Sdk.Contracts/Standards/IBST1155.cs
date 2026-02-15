namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-1155 Multi-Token Standard — equivalent to ERC-1155.
/// Supports both fungible and non-fungible tokens in a single contract.
/// </summary>
public interface IBST1155
{
    /// <summary>Get the balance of a specific token for an account.</summary>
    ulong BalanceOf(byte[] account, ulong tokenId);

    /// <summary>Get balances for multiple account/token pairs.</summary>
    ulong[] BalanceOfBatch(byte[][] accounts, ulong[] tokenIds);

    /// <summary>Transfer tokens from the caller to a recipient.</summary>
    void SafeTransferFrom(byte[] from, byte[] to, ulong tokenId, ulong amount);

    /// <summary>Batch transfer multiple tokens.</summary>
    void SafeBatchTransferFrom(byte[] from, byte[] to, ulong[] tokenIds, ulong[] amounts);

    /// <summary>Set approval for an operator to manage all of the caller's tokens.</summary>
    void SetApprovalForAll(byte[] operatorAddress, bool approved);

    /// <summary>Check if an operator is approved for all of an owner's tokens.</summary>
    bool IsApprovedForAll(byte[] owner, byte[] operatorAddress);

    /// <summary>Get the URI for a token's metadata.</summary>
    string Uri(ulong tokenId);
}

/// <summary>
/// Transfer event for BST-1155 single transfer.
/// </summary>
[BasaltEvent]
public sealed class TransferSingleEvent
{
    [Indexed] public byte[] Operator { get; init; } = [];
    [Indexed] public byte[] From { get; init; } = [];
    [Indexed] public byte[] To { get; init; } = [];
    public ulong TokenId { get; init; }
    public ulong Amount { get; init; }
}

/// <summary>
/// Transfer event for BST-1155 batch transfer.
/// </summary>
[BasaltEvent]
public sealed class TransferBatchEvent
{
    [Indexed] public byte[] Operator { get; init; } = [];
    [Indexed] public byte[] From { get; init; } = [];
    [Indexed] public byte[] To { get; init; } = [];
    public ulong[] TokenIds { get; init; } = [];
    public ulong[] Amounts { get; init; } = [];
}

/// <summary>
/// Approval event for BST-1155.
/// </summary>
[BasaltEvent]
public sealed class ApprovalForAllEvent
{
    [Indexed] public byte[] Owner { get; init; } = [];
    [Indexed] public byte[] Operator { get; init; } = [];
    public bool Approved { get; init; }
}
