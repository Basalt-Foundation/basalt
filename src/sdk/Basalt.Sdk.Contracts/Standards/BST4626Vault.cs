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
    private readonly StorageValue<ulong> _totalAssets;
    private readonly StorageMap<string, string> _admin;

    // Virtual offset to prevent inflation attack (EIP-4626 mitigation)
    private const ulong VirtualShares = 1;
    private const ulong VirtualAssets = 1;

    public BST4626Vault(string name, string symbol, byte decimals, byte[] assetAddress)
        : base(name, symbol, decimals)
    {
        _assetAddress = assetAddress;
        _totalAssets = new StorageValue<ulong>("vault_assets");
        _admin = new StorageMap<string, string>("vault_admin");
        _admin.Set("admin", Convert.ToHexString(Context.Caller));
    }

    // --- Views ---

    [BasaltView]
    public byte[] Asset() => _assetAddress;

    [BasaltView]
    public ulong TotalAssets() => _totalAssets.Get();

    [BasaltView]
    public ulong ConvertToShares(ulong assets)
    {
        var supply = TotalSupply() + VirtualShares;
        var total = TotalAssets() + VirtualAssets;
        return assets * supply / total;
    }

    [BasaltView]
    public ulong ConvertToAssets(ulong shares)
    {
        var supply = TotalSupply() + VirtualShares;
        var total = TotalAssets() + VirtualAssets;
        return shares * total / supply;
    }

    [BasaltView]
    public ulong PreviewDeposit(ulong assets) => ConvertToShares(assets);

    [BasaltView]
    public ulong PreviewMint(ulong shares)
    {
        var supply = TotalSupply() + VirtualShares;
        var total = TotalAssets() + VirtualAssets;
        return (shares * total + supply - 1) / supply;
    }

    [BasaltView]
    public ulong PreviewWithdraw(ulong assets)
    {
        var supply = TotalSupply() + VirtualShares;
        var total = TotalAssets() + VirtualAssets;
        return (assets * supply + total - 1) / total;
    }

    [BasaltView]
    public ulong PreviewRedeem(ulong shares) => ConvertToAssets(shares);

    // --- Entrypoints ---

    [BasaltEntrypoint]
    public ulong Deposit(ulong assets)
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

    [BasaltEntrypoint]
    public ulong Withdraw(ulong assets)
    {
        Context.Require(assets > 0, "VAULT: zero withdraw");
        var shares = PreviewWithdraw(assets);
        Context.Require(shares > 0, "VAULT: zero shares");

        Burn(Context.Caller, shares);
        _totalAssets.Set(TotalAssets() - assets);

        Context.CallContract(_assetAddress, "Transfer",
            Context.Caller, assets);

        Context.Emit(new VaultWithdrawEvent
        {
            Caller = Context.Caller,
            Assets = assets,
            Shares = shares,
        });

        return shares;
    }

    [BasaltEntrypoint]
    public ulong Redeem(ulong shares)
    {
        Context.Require(shares > 0, "VAULT: zero redeem");
        var assets = ConvertToAssets(shares);
        Context.Require(assets > 0, "VAULT: zero assets");

        Burn(Context.Caller, shares);
        _totalAssets.Set(TotalAssets() - assets);

        Context.CallContract(_assetAddress, "Transfer",
            Context.Caller, assets);

        Context.Emit(new VaultWithdrawEvent
        {
            Caller = Context.Caller,
            Assets = assets,
            Shares = shares,
        });

        return assets;
    }

    [BasaltEntrypoint]
    public void Harvest(ulong yieldAmount)
    {
        Context.Require(yieldAmount > 0, "VAULT: zero yield");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "VAULT: not admin");

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
