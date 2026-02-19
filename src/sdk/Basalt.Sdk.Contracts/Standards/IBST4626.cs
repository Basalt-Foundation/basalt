using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-4626 Tokenized Vault Standard — equivalent to ERC-4626.
/// Extends BST-20: deposit underlying BST-20 assets, receive vault shares.
/// </summary>
public interface IBST4626 : IBST20
{
    byte[] Asset();
    UInt256 TotalAssets();
    UInt256 ConvertToShares(UInt256 assets);
    UInt256 ConvertToAssets(UInt256 shares);
    UInt256 PreviewDeposit(UInt256 assets);
    UInt256 PreviewMint(UInt256 shares);
    UInt256 PreviewWithdraw(UInt256 assets);
    UInt256 PreviewRedeem(UInt256 shares);
    UInt256 Deposit(UInt256 assets);
    UInt256 Withdraw(UInt256 assets);
    UInt256 Redeem(UInt256 shares);
    void Harvest(UInt256 yieldAmount);
}

[BasaltEvent]
public sealed class VaultDepositEvent
{
    [Indexed] public byte[] Caller { get; init; } = [];
    public UInt256 Assets { get; init; }
    public UInt256 Shares { get; init; }
}

[BasaltEvent]
public sealed class VaultWithdrawEvent
{
    [Indexed] public byte[] Caller { get; init; } = [];
    public UInt256 Assets { get; init; }
    public UInt256 Shares { get; init; }
}

[BasaltEvent]
public sealed class VaultHarvestEvent
{
    [Indexed] public byte[] Caller { get; init; } = [];
    public UInt256 YieldAmount { get; init; }
    public UInt256 NewTotalAssets { get; init; }
}
