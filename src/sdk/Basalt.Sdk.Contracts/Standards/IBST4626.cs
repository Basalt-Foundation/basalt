namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-4626 Tokenized Vault Standard — equivalent to ERC-4626.
/// Extends BST-20: deposit underlying BST-20 assets, receive vault shares.
/// </summary>
public interface IBST4626 : IBST20
{
    byte[] Asset();
    ulong TotalAssets();
    ulong ConvertToShares(ulong assets);
    ulong ConvertToAssets(ulong shares);
    ulong PreviewDeposit(ulong assets);
    ulong PreviewMint(ulong shares);
    ulong PreviewWithdraw(ulong assets);
    ulong PreviewRedeem(ulong shares);
    ulong Deposit(ulong assets);
    ulong Withdraw(ulong assets);
    ulong Redeem(ulong shares);
    void Harvest(ulong yieldAmount);
}

[BasaltEvent]
public sealed class VaultDepositEvent
{
    [Indexed] public byte[] Caller { get; init; } = [];
    public ulong Assets { get; init; }
    public ulong Shares { get; init; }
}

[BasaltEvent]
public sealed class VaultWithdrawEvent
{
    [Indexed] public byte[] Caller { get; init; } = [];
    public ulong Assets { get; init; }
    public ulong Shares { get; init; }
}

[BasaltEvent]
public sealed class VaultHarvestEvent
{
    [Indexed] public byte[] Caller { get; init; } = [];
    public ulong YieldAmount { get; init; }
    public ulong NewTotalAssets { get; init; }
}
