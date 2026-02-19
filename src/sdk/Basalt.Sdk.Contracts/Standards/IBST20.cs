using Basalt.Core;

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
    UInt256 TotalSupply();

    /// <summary>Get balance of an address.</summary>
    UInt256 BalanceOf(byte[] account);

    /// <summary>Transfer tokens to a recipient.</summary>
    bool Transfer(byte[] to, UInt256 amount);

    /// <summary>Get the allowance granted by owner to spender.</summary>
    UInt256 Allowance(byte[] owner, byte[] spender);

    /// <summary>Approve a spender to spend tokens on behalf of the caller.</summary>
    bool Approve(byte[] spender, UInt256 amount);

    /// <summary>Transfer tokens from one address to another using an allowance.</summary>
    bool TransferFrom(byte[] from, byte[] to, UInt256 amount);
}

/// <summary>
/// Transfer event for BST-20 tokens.
/// </summary>
[BasaltEvent]
public sealed class TransferEvent
{
    [Indexed] public byte[] From { get; init; } = [];
    [Indexed] public byte[] To { get; init; } = [];
    public UInt256 Amount { get; init; }
}

/// <summary>
/// Approval event for BST-20 tokens.
/// </summary>
[BasaltEvent]
public sealed class ApprovalEvent
{
    [Indexed] public byte[] Owner { get; init; } = [];
    [Indexed] public byte[] Spender { get; init; } = [];
    public UInt256 Amount { get; init; }
}
