namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-3525 Semi-Fungible Token Standard — equivalent to ERC-3525.
/// Three-component model: (tokenId, slot, value). Tokens in the same slot
/// are fungible by value. Each token has a unique ID but carries a fungible
/// value that is transferable within the same slot.
/// </summary>
public interface IBST3525
{
    string Name();
    string Symbol();
    byte ValueDecimals();
    ulong BalanceOf(ulong tokenId);
    ulong TokenOwnerBalance(byte[] owner);
    ulong SlotOf(ulong tokenId);
    byte[] OwnerOf(ulong tokenId);
    ulong ValueAllowance(ulong tokenId, byte[] operatorAddr);
    byte[] GetApproved(ulong tokenId);
    string SlotUri(ulong slot);
    string TokenUri(ulong tokenId);
    ulong Mint(byte[] to, ulong slot, ulong value);
    void TransferValueToId(ulong fromId, ulong toId, ulong value);
    ulong TransferValueToAddress(ulong fromId, byte[] to, ulong value);
    void ApproveValue(ulong tokenId, byte[] operatorAddr, ulong value);
    void TransferToken(byte[] to, ulong tokenId);
    void ApproveToken(byte[] to, ulong tokenId);
    void SetSlotUri(ulong slot, string uri);
    void SetTokenUri(ulong tokenId, string uri);
}

[BasaltEvent]
public sealed class TransferValueEvent
{
    [Indexed] public ulong FromTokenId { get; init; }
    [Indexed] public ulong ToTokenId { get; init; }
    public ulong Value { get; init; }
}

[BasaltEvent]
public sealed class SftTransferEvent
{
    [Indexed] public byte[] From { get; init; } = [];
    [Indexed] public byte[] To { get; init; } = [];
    public ulong TokenId { get; init; }
}

[BasaltEvent]
public sealed class ApproveValueEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public byte[] Operator { get; init; } = [];
    public ulong Value { get; init; }
}

[BasaltEvent]
public sealed class SftApprovalEvent
{
    [Indexed] public byte[] Owner { get; init; } = [];
    [Indexed] public byte[] Approved { get; init; } = [];
    public ulong TokenId { get; init; }
}

[BasaltEvent]
public sealed class SftMintEvent
{
    [Indexed] public byte[] To { get; init; } = [];
    public ulong TokenId { get; init; }
    public ulong Slot { get; init; }
    public ulong Value { get; init; }
}
