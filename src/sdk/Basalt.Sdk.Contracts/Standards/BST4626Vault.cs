using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-4626 Tokenized Vault — equivalent to ERC-4626.
/// Deposit underlying BST-20 assets, receive vault shares (also BST-20).
/// Exchange rate adjusts as yield accrues via Harvest().
/// Type ID: 0x0006
/// </summary>
[BasaltContract]
public partial class BST4626Vault : BST20Token, IBST4626
{
    private readonly byte[] _assetAddress;
    private readonly StorageValue<UInt256> _totalAssets;
    private readonly StorageMap<string, string> _admin;

    // Virtual offset to prevent inflation attack (EIP-4626 mitigation)
    // M-4: Offset must be large enough (10^decimals) to make donation-based inflation attacks uneconomical
    private static readonly UInt256 VirtualShares = new UInt256(1_000_000_000_000_000_000); // 10^18
    private static readonly UInt256 VirtualAssets = new UInt256(1_000_000_000_000_000_000); // 10^18

    public BST4626Vault(string name, string symbol, byte decimals, byte[] assetAddress)
        : base(name, symbol, decimals)
    {
        _assetAddress = assetAddress;
        _totalAssets = new StorageValue<UInt256>("vault_assets");
        _admin = new StorageMap<string, string>("vault_admin");
        _admin.Set("admin", Convert.ToHexString(Context.Caller));
    }

    // --- Views ---

    [BasaltView]
    public byte[] Asset() => _assetAddress;

    [BasaltView]
    public UInt256 TotalAssets() => _totalAssets.Get();

    [BasaltView]
    public UInt256 ConvertToShares(UInt256 assets)
    {
        var supply = TotalSupply() + VirtualShares;
        var total = TotalAssets() + VirtualAssets;
        return assets * supply / total;
    }

    [BasaltView]
    public UInt256 ConvertToAssets(UInt256 shares)
    {
        var supply = TotalSupply() + VirtualShares;
        var total = TotalAssets() + VirtualAssets;
        return shares * total / supply;
    }

    [BasaltView]
    public UInt256 PreviewDeposit(UInt256 assets) => ConvertToShares(assets);

    [BasaltView]
    public UInt256 PreviewMint(UInt256 shares)
    {
        var supply = TotalSupply() + VirtualShares;
        var total = TotalAssets() + VirtualAssets;
        return (shares * total + supply - UInt256.One) / supply;
    }

    [BasaltView]
    public UInt256 PreviewWithdraw(UInt256 assets)
    {
        var supply = TotalSupply() + VirtualShares;
        var total = TotalAssets() + VirtualAssets;
        return (assets * supply + total - UInt256.One) / total;
    }

    [BasaltView]
    public UInt256 PreviewRedeem(UInt256 shares) => ConvertToAssets(shares);

    // --- Entrypoints ---

    [BasaltEntrypoint]
    public UInt256 Deposit(UInt256 assets)
    {
        Context.Require(assets > 0, "VAULT: zero deposit");
        var shares = ConvertToShares(assets);
        Context.Require(shares > 0, "VAULT: zero shares");

        Context.CallContract(_assetAddress, "TransferFrom",
            Context.Caller, Context.Self, assets);

        Mint(Context.Caller, shares);
        _totalAssets.Set(TotalAssets() + assets);

        Context.Emit(new VaultDepositEvent
        {
            Caller = Context.Caller,
            Assets = assets,
            Shares = shares,
        });

        return shares;
    }

    /// <remarks>
    /// C-5: Transfer assets before burning shares to avoid loss if the external call reverts
    /// without atomic state rollback.
    /// </remarks>
    [BasaltEntrypoint]
    public UInt256 Withdraw(UInt256 assets)
    {
        Context.Require(assets > 0, "VAULT: zero withdraw");
        var shares = PreviewWithdraw(assets);
        Context.Require(shares > 0, "VAULT: zero shares");

        // Transfer assets first, then burn shares (C-5: prevents share loss on revert)
        Context.CallContract(_assetAddress, "Transfer",
            Context.Caller, assets);

        Burn(Context.Caller, shares);
        _totalAssets.Set(TotalAssets() - assets);

        Context.Emit(new VaultWithdrawEvent
        {
            Caller = Context.Caller,
            Assets = assets,
            Shares = shares,
        });

        return shares;
    }

    /// <remarks>
    /// C-5: Transfer assets before burning shares to avoid loss if the external call reverts
    /// without atomic state rollback.
    /// </remarks>
    [BasaltEntrypoint]
    public UInt256 Redeem(UInt256 shares)
    {
        Context.Require(shares > 0, "VAULT: zero redeem");
        var assets = ConvertToAssets(shares);
        Context.Require(assets > 0, "VAULT: zero assets");

        // Transfer assets first, then burn shares (C-5: prevents share loss on revert)
        Context.CallContract(_assetAddress, "Transfer",
            Context.Caller, assets);

        Burn(Context.Caller, shares);
        _totalAssets.Set(TotalAssets() - assets);

        Context.Emit(new VaultWithdrawEvent
        {
            Caller = Context.Caller,
            Assets = assets,
            Shares = shares,
        });

        return assets;
    }

    /// <remarks>
    /// C-6: Requires an actual TransferFrom of underlying assets into the vault to prevent
    /// phantom yield injection by a compromised admin.
    /// </remarks>
    [BasaltEntrypoint]
    public void Harvest(UInt256 yieldAmount)
    {
        Context.Require(yieldAmount > 0, "VAULT: zero yield");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "VAULT: not admin");

        // C-6: Require actual asset transfer — prevents phantom yield injection
        Context.CallContract(_assetAddress, "TransferFrom",
            Context.Caller, Context.Self, yieldAmount);

        var newTotal = TotalAssets() + yieldAmount;
        _totalAssets.Set(newTotal);

        Context.Emit(new VaultHarvestEvent
        {
            Caller = Context.Caller,
            YieldAmount = yieldAmount,
            NewTotalAssets = newTotal,
        });
    }
}
