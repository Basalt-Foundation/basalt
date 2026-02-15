namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-721 Non-Fungible Token Standard — equivalent to ERC-721.
/// </summary>
public interface IBST721
{
    /// <summary>Token collection name.</summary>
    string Name();

    /// <summary>Token collection symbol.</summary>
    string Symbol();

    /// <summary>Get the owner of a token.</summary>
    byte[] OwnerOf(ulong tokenId);

    /// <summary>Get the number of tokens owned by an address.</summary>
    ulong BalanceOf(byte[] owner);

    /// <summary>Transfer a token to a new owner.</summary>
    void Transfer(byte[] to, ulong tokenId);

    /// <summary>Approve an address to transfer a specific token.</summary>
    void Approve(byte[] approved, ulong tokenId);

    /// <summary>Get the approved address for a token.</summary>
    byte[] GetApproved(ulong tokenId);

    /// <summary>Get the token URI (metadata URL).</summary>
    string TokenURI(ulong tokenId);
}

/// <summary>
/// NFT Transfer event.
/// </summary>
[BasaltEvent]
public sealed class NftTransferEvent
{
    [Indexed] public byte[] From { get; init; } = [];
    [Indexed] public byte[] To { get; init; } = [];
    [Indexed] public ulong TokenId { get; init; }
}

/// <summary>
/// NFT Approval event.
/// </summary>
[BasaltEvent]
public sealed class NftApprovalEvent
{
    [Indexed] public byte[] Owner { get; init; } = [];
    [Indexed] public byte[] Approved { get; init; } = [];
    [Indexed] public ulong TokenId { get; init; }
}
