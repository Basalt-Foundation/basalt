namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-20 Fungible Token Standard — equivalent to ERC-20.
/// </summary>
public interface IBST20
{
    /// <summary>Token name.</summary>
    string Name();

    /// <summary>Token symbol.</summary>
    string Symbol();

    /// <summary>Decimal places.</summary>
    byte Decimals();

    /// <summary>Total supply of tokens.</summary>
    ulong TotalSupply();

    /// <summary>Get balance of an address.</summary>
    ulong BalanceOf(byte[] account);

    /// <summary>Transfer tokens to a recipient.</summary>
    bool Transfer(byte[] to, ulong amount);

    /// <summary>Get the allowance granted by owner to spender.</summary>
    ulong Allowance(byte[] owner, byte[] spender);

    /// <summary>Approve a spender to spend tokens on behalf of the caller.</summary>
    bool Approve(byte[] spender, ulong amount);

    /// <summary>Transfer tokens from one address to another using an allowance.</summary>
    bool TransferFrom(byte[] from, byte[] to, ulong amount);
}

/// <summary>
/// Transfer event for BST-20 tokens.
/// </summary>
[BasaltEvent]
public sealed class TransferEvent
{
    [Indexed] public byte[] From { get; init; } = [];
    [Indexed] public byte[] To { get; init; } = [];
    public ulong Amount { get; init; }
}

/// <summary>
/// Approval event for BST-20 tokens.
/// </summary>
[BasaltEvent]
public sealed class ApprovalEvent
{
    [Indexed] public byte[] Owner { get; init; } = [];
    [Indexed] public byte[] Spender { get; init; } = [];
    public ulong Amount { get; init; }
}
