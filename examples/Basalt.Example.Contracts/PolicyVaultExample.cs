using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;

namespace Basalt.Example.Contracts;

/// <summary>
/// Example: A BST-4626 tokenized vault where both the underlying asset
/// and the vault shares have independent policy enforcement.
///
/// Scenario:
///   - An underlying BST-20 "stablecoin" with a sanctions policy
///   - A BST-4626 vault that wraps the stablecoin
///   - The vault shares have a holding limit policy (max 100K shares per address)
///   - Sanctioned users can't deposit (asset transfer blocked)
///   - Large holders can't accumulate unlimited shares
/// </summary>
public static class PolicyVaultExample
{
    public static void Run()
    {
        using var host = new BasaltTestHost();

        var admin = BasaltTestHost.CreateAddress(1);
        var alice = BasaltTestHost.CreateAddress(2);
        var bob = BasaltTestHost.CreateAddress(3);
        var assetAddr = BasaltTestHost.CreateAddress(0xA0);
        var vaultAddr = BasaltTestHost.CreateAddress(0xA1);
        var sanctionsAddr = BasaltTestHost.CreateAddress(0xA2);
        var holdingLimitAddr = BasaltTestHost.CreateAddress(0xA3);

        host.SetCaller(admin);

        // === Deploy underlying asset (stablecoin) ===
        Context.Self = assetAddr;
        var asset = new BST20Token("USD Coin", "USDC", 6, new UInt256(10_000_000));
        host.Deploy(assetAddr, asset);

        // === Deploy vault ===
        Context.Self = vaultAddr;
        var vault = new BST4626Vault("Vault USDC", "vUSDC", 6, assetAddr);
        host.Deploy(vaultAddr, vault);

        // === Deploy policies ===
        Context.Self = sanctionsAddr;
        var sanctions = new SanctionsPolicy();
        host.Deploy(sanctionsAddr, sanctions);

        Context.Self = holdingLimitAddr;
        var holdingLimit = new HoldingLimitPolicy();
        host.Deploy(holdingLimitAddr, holdingLimit);

        Context.IsDeploying = false;

        // === Configure ===

        // Sanctions on the underlying asset
        host.SetCaller(admin);
        Context.Self = assetAddr;
        asset.AddPolicy(sanctionsAddr);

        // Holding limit on vault shares (max 100K shares per address)
        Context.Self = holdingLimitAddr;
        holdingLimit.SetDefaultLimit(vaultAddr, new UInt256(100_000));

        Context.Self = vaultAddr;
        vault.AddPolicy(holdingLimitAddr);

        // === Distribute stablecoins ===
        host.SetCaller(admin);
        Context.Self = assetAddr;
        asset.Transfer(alice, new UInt256(500_000));
        asset.Transfer(bob, new UInt256(500_000));
        Console.WriteLine($"Alice USDC: {asset.BalanceOf(alice)}");

        // === Alice deposits into vault ===
        // First approve vault to pull tokens
        host.SetCaller(alice);
        Context.Self = assetAddr;
        asset.Approve(vaultAddr, new UInt256(200_000));

        // Deposit
        Context.Self = vaultAddr;
        var shares = vault.Deposit(new UInt256(50_000));
        Console.WriteLine($"Alice deposited 50K USDC, got {shares} shares");
        Console.WriteLine($"Alice vault shares: {vault.BalanceOf(alice)}");
        Console.WriteLine($"Vault total assets: {vault.TotalAssets()}");

        // === Bob deposits too ===
        host.SetCaller(bob);
        Context.Self = assetAddr;
        asset.Approve(vaultAddr, new UInt256(200_000));

        Context.Self = vaultAddr;
        shares = vault.Deposit(new UInt256(80_000));
        Console.WriteLine($"Bob deposited 80K USDC, got {shares} shares");

        // === Alice tries to transfer shares to Bob (holding limit check) ===
        // Bob has ~80K shares, limit is 100K — small transfer works
        host.SetCaller(alice);
        Context.Self = vaultAddr;
        vault.Transfer(bob, new UInt256(10_000));
        Console.WriteLine("Alice -> Bob 10K shares: OK");

        // Large transfer would exceed Bob's 100K limit
        try { vault.Transfer(bob, new UInt256(50_000)); }
        catch (ContractRevertException e)
        {
            Console.WriteLine($"Alice -> Bob 50K shares: DENIED ({e.Message})");
        }

        // === Sanction Bob — he can't withdraw (asset transfer blocked) ===
        host.SetCaller(admin);
        Context.Self = sanctionsAddr;
        sanctions.AddSanction(bob);

        // Bob tries to redeem shares for USDC — asset transfer to sanctioned Bob blocked
        host.SetCaller(bob);
        Context.Self = vaultAddr;
        try { vault.Redeem(new UInt256(1_000)); }
        catch (ContractRevertException e)
        {
            Console.WriteLine($"Bob redeem: DENIED ({e.Message})");
        }

        Console.WriteLine($"Final vault total assets: {vault.TotalAssets()}");
    }
}
