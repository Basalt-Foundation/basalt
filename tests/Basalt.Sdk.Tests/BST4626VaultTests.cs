using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Comprehensive tests for the BST-4626 Tokenized Vault standard.
/// The vault extends BST20Token for share accounting, and interacts
/// with an underlying BST-20 asset token via cross-contract calls.
///
/// Because ContractStorage is globally shared in the test host (no per-contract
/// isolation), full deposit/withdraw flows that call into a deployed BST-20 would
/// collide on storage keys. We therefore use two strategies:
///   1. Test math/preview/harvest functions by directly manipulating vault state
///      (MintPublic to create shares, Harvest to increase _totalAssets).
///   2. Test deposit/withdraw/redeem with a custom CrossContractCallHandler that
///      tracks asset token state in a separate dictionary, bypassing ContractStorage.
/// </summary>
public class BST4626VaultTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly byte[] _admin;
    private readonly byte[] _alice;
    private readonly byte[] _bob;
    private readonly byte[] _assetAddress;
    private readonly byte[] _vaultAddress;

    // Separate ledger to simulate asset token state without ContractStorage collision
    private readonly Dictionary<string, UInt256> _assetBalances = new();
    private readonly Dictionary<string, UInt256> _assetAllowances = new();

    public BST4626VaultTests()
    {
        _admin = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);
        _assetAddress = BasaltTestHost.CreateAddress(0xA0);
        _vaultAddress = BasaltTestHost.CreateAddress(0xB0);

        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
    }

    /// <summary>
    /// Create a vault with our mock cross-contract handler wired up.
    /// The handler simulates the asset BST-20 TransferFrom/Transfer calls
    /// using a separate in-memory ledger, avoiding ContractStorage collisions.
    /// </summary>
    private TestableVault CreateVault()
    {
        var vault = new TestableVault("Vault Shares", "vTOKEN", 18, _assetAddress);

        // Wire up a mock cross-contract handler that simulates the asset token
        Context.CrossContractCallHandler = (target, method, args) =>
        {
            var targetKey = Convert.ToHexString(target);
            var assetKey = Convert.ToHexString(_assetAddress);

            if (targetKey != assetKey)
                throw new ContractRevertException($"Contract not found at {targetKey}");

            if (method == "TransferFrom")
            {
                var from = (byte[])args[0]!;
                var to = (byte[])args[1]!;
                var amount = (UInt256)args[2]!;
                var fromHex = Convert.ToHexString(from);
                var toHex = Convert.ToHexString(to);

                // Check allowance (spender is Context.Caller which was set to vault Self by CallContract)
                var spenderHex = Convert.ToHexString(Context.Caller);
                var allowanceKey = $"{fromHex}:{spenderHex}";
                var currentAllowance = _assetAllowances.GetValueOrDefault(allowanceKey, UInt256.Zero);
                if (currentAllowance < amount)
                    throw new ContractRevertException("BST20: insufficient allowance");
                _assetAllowances[allowanceKey] = currentAllowance - amount;

                // Check balance
                var fromBalance = _assetBalances.GetValueOrDefault(fromHex, UInt256.Zero);
                if (fromBalance < amount)
                    throw new ContractRevertException("BST20: insufficient balance");

                _assetBalances[fromHex] = fromBalance - amount;
                _assetBalances[toHex] = _assetBalances.GetValueOrDefault(toHex, UInt256.Zero) + amount;
                return true;
            }

            if (method == "Transfer")
            {
                var to = (byte[])args[0]!;
                var amount = (UInt256)args[1]!;
                var toHex = Convert.ToHexString(to);
                // Sender is Context.Caller which is the vault
                var senderHex = Convert.ToHexString(Context.Caller);
                var senderBalance = _assetBalances.GetValueOrDefault(senderHex, UInt256.Zero);
                if (senderBalance < amount)
                    throw new ContractRevertException("BST20: insufficient balance");
                _assetBalances[senderHex] = senderBalance - amount;
                _assetBalances[toHex] = _assetBalances.GetValueOrDefault(toHex, UInt256.Zero) + amount;
                return true;
            }

            throw new ContractRevertException($"Method '{method}' not found on asset contract");
        };

        return vault;
    }

    /// <summary>
    /// Give asset tokens to a user and approve the vault to spend them.
    /// </summary>
    private void MintAssetTokens(byte[] to, UInt256 amount)
    {
        var toHex = Convert.ToHexString(to);
        _assetBalances[toHex] = _assetBalances.GetValueOrDefault(toHex, UInt256.Zero) + amount;
    }

    private void ApproveVaultForAssets(byte[] owner, UInt256 amount)
    {
        var ownerHex = Convert.ToHexString(owner);
        var vaultHex = Convert.ToHexString(_vaultAddress);
        _assetAllowances[$"{ownerHex}:{vaultHex}"] = amount;
    }

    private UInt256 GetAssetBalance(byte[] account)
    {
        return _assetBalances.GetValueOrDefault(Convert.ToHexString(account), UInt256.Zero);
    }

    // ============================================================
    // 1. Constructor: Asset() returns correct address, TotalAssets starts at 0
    // ============================================================

    [Fact]
    public void Constructor_Asset_ReturnsCorrectAddress()
    {
        var vault = CreateVault();
        vault.Asset().Should().BeEquivalentTo(_assetAddress);
    }

    [Fact]
    public void Constructor_TotalAssets_StartsAtZero()
    {
        var vault = CreateVault();
        vault.TotalAssets().Should().Be((UInt256)0);
    }

    // ============================================================
    // 2. Inherited BST-20: Name, Symbol, Decimals work
    // ============================================================

    [Fact]
    public void InheritedBST20_Name_Symbol_Decimals()
    {
        var vault = CreateVault();
        vault.Name().Should().Be("Vault Shares");
        vault.Symbol().Should().Be("vTOKEN");
        vault.Decimals().Should().Be(18);
    }

    // ============================================================
    // 3. ConvertToShares / ConvertToAssets at initial state: 1:1 ratio
    // ============================================================

    [Fact]
    public void ConvertToShares_InitialState_NearOneToOne()
    {
        var vault = CreateVault();

        // With VirtualShares=1, VirtualAssets=1, TotalSupply=0, TotalAssets=0:
        // ConvertToShares(1000) = 1000 * (0 + 1) / (0 + 1) = 1000
        vault.ConvertToShares(1000).Should().Be((UInt256)1000);
    }

    [Fact]
    public void ConvertToAssets_InitialState_NearOneToOne()
    {
        var vault = CreateVault();

        // ConvertToAssets(1000) = 1000 * (0 + 1) / (0 + 1) = 1000
        vault.ConvertToAssets(1000).Should().Be((UInt256)1000);
    }

    [Fact]
    public void ConvertToShares_And_ConvertToAssets_AreInverse_InitialState()
    {
        var vault = CreateVault();

        var shares = vault.ConvertToShares(5000);
        var roundTrip = vault.ConvertToAssets(shares);
        roundTrip.Should().Be((UInt256)5000);
    }

    // ============================================================
    // 4. Deposit: mints shares, increases TotalAssets, emits event
    // ============================================================

    [Fact]
    public void Deposit_MintsSharesAndIncreasesTotalAssets()
    {
        var vault = CreateVault();

        // Give Alice 10_000 asset tokens and approve vault
        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);

        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;

        var shares = _host.Call(() => vault.Deposit(5000));

        // At initial state: shares = 5000 * (0 + 1) / (0 + 1) = 5000
        shares.Should().Be((UInt256)5000);
        vault.TotalAssets().Should().Be((UInt256)5000);
        vault.BalanceOf(_alice).Should().Be((UInt256)5000);
        vault.TotalSupply().Should().Be((UInt256)5000);
    }

    [Fact]
    public void Deposit_EmitsVaultDepositEvent()
    {
        var vault = CreateVault();

        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);

        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.ClearEvents();

        var shares = _host.Call(() => vault.Deposit(2000));

        var events = _host.GetEvents<VaultDepositEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Caller.Should().BeEquivalentTo(_alice);
        events[0].Assets.Should().Be((UInt256)2000);
        events[0].Shares.Should().Be(shares);
    }

    [Fact]
    public void Deposit_TransfersAssetTokensFromCaller()
    {
        var vault = CreateVault();

        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);

        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;

        _host.Call(() => vault.Deposit(3000));

        // Alice's asset balance should decrease by 3000
        GetAssetBalance(_alice).Should().Be((UInt256)7000);
        // Vault should hold the 3000 assets
        GetAssetBalance(_vaultAddress).Should().Be((UInt256)3000);
    }

    // ============================================================
    // 5. Deposit reverts with zero amount
    // ============================================================

    [Fact]
    public void Deposit_ZeroAmount_Reverts()
    {
        var vault = CreateVault();

        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;

        var msg = _host.ExpectRevert(() => vault.Deposit(0));
        msg.Should().Contain("VAULT: zero deposit");
    }

    // ============================================================
    // 6. Withdraw: burns shares (rounded up), decreases TotalAssets, emits event
    // ============================================================

    [Fact]
    public void Withdraw_BurnsSharesAndDecreasesTotalAssets()
    {
        var vault = CreateVault();

        // First deposit
        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(5000));

        // Give the vault asset tokens so it can Transfer out
        // (deposit already moved 5000 to vault)

        // Now withdraw 2000 assets
        var sharesBurned = _host.Call(() => vault.Withdraw(2000));

        vault.TotalAssets().Should().Be((UInt256)3000);
        // Alice should have gotten her assets back
        GetAssetBalance(_alice).Should().Be((UInt256)(5000 + 2000)); // 5000 remaining + 2000 withdrawn
    }

    [Fact]
    public void Withdraw_EmitsVaultWithdrawEvent()
    {
        var vault = CreateVault();

        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(5000));

        _host.ClearEvents();
        var sharesBurned = _host.Call(() => vault.Withdraw(2000));

        var events = _host.GetEvents<VaultWithdrawEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Caller.Should().BeEquivalentTo(_alice);
        events[0].Assets.Should().Be((UInt256)2000);
        events[0].Shares.Should().Be(sharesBurned);
    }

    [Fact]
    public void Withdraw_ZeroAmount_Reverts()
    {
        var vault = CreateVault();

        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;

        var msg = _host.ExpectRevert(() => vault.Withdraw(0));
        msg.Should().Contain("VAULT: zero withdraw");
    }

    // ============================================================
    // 7. Redeem: burns exact shares, returns proportional assets
    // ============================================================

    [Fact]
    public void Redeem_BurnsExactSharesAndReturnsAssets()
    {
        var vault = CreateVault();

        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(6000));

        // Redeem 3000 shares -> should get ~3000 assets (1:1 ratio at this point)
        var assetsReturned = _host.Call(() => vault.Redeem(3000));

        // ConvertToAssets(3000) = 3000 * (6000 + 1) / (6000 + 1) = 3000
        assetsReturned.Should().Be((UInt256)3000);
        vault.TotalAssets().Should().Be((UInt256)3000);
        vault.BalanceOf(_alice).Should().Be((UInt256)3000); // 6000 - 3000 shares burned
    }

    [Fact]
    public void Redeem_ZeroShares_Reverts()
    {
        var vault = CreateVault();

        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;

        var msg = _host.ExpectRevert(() => vault.Redeem(0));
        msg.Should().Contain("VAULT: zero redeem");
    }

    // ============================================================
    // 8. Harvest: only admin can call; increases TotalAssets; emits event
    // ============================================================

    [Fact]
    public void Harvest_Admin_IncreasesTotalAssets()
    {
        var vault = CreateVault();

        // Do an initial deposit so there's some total assets
        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(5000));

        // Admin harvests yield (C-6: requires actual asset transfer)
        MintAssetTokens(_admin, 1000);
        ApproveVaultForAssets(_admin, 1000);
        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Harvest(1000));

        vault.TotalAssets().Should().Be((UInt256)6000); // 5000 + 1000 yield
    }

    [Fact]
    public void Harvest_EmitsVaultHarvestEvent()
    {
        var vault = CreateVault();

        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(5000));

        MintAssetTokens(_admin, 2000);
        ApproveVaultForAssets(_admin, 2000);
        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
        _host.ClearEvents();
        _host.Call(() => vault.Harvest(2000));

        var events = _host.GetEvents<VaultHarvestEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Caller.Should().BeEquivalentTo(_admin);
        events[0].YieldAmount.Should().Be((UInt256)2000);
        events[0].NewTotalAssets.Should().Be((UInt256)7000);
    }

    // ============================================================
    // 9. Harvest: non-admin reverts
    // ============================================================

    [Fact]
    public void Harvest_NonAdmin_Reverts()
    {
        var vault = CreateVault();

        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;

        var msg = _host.ExpectRevert(() => vault.Harvest(1000));
        msg.Should().Contain("VAULT: not admin");
    }

    [Fact]
    public void Harvest_ZeroYield_Reverts()
    {
        var vault = CreateVault();

        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;

        var msg = _host.ExpectRevert(() => vault.Harvest(0));
        msg.Should().Contain("VAULT: zero yield");
    }

    // ============================================================
    // 10. Exchange rate after yield: ConvertToShares returns fewer shares per asset
    // ============================================================

    [Fact]
    public void ExchangeRate_AfterYield_FewerSharesPerAsset()
    {
        var vault = CreateVault();

        // Deposit 5000 assets (get ~5000 shares at 1:1)
        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(5000));

        var sharesBefore = vault.ConvertToShares(1000);

        // Admin reports 5000 yield (C-6: requires actual asset transfer)
        MintAssetTokens(_admin, 5000);
        ApproveVaultForAssets(_admin, 5000);
        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Harvest(5000));

        // Now totalAssets = 10_000, totalSupply = 5000
        // ConvertToShares(1000) = 1000 * (5000 + 1) / (10_000 + 1) = ~500
        var sharesAfter = vault.ConvertToShares(1000);

        sharesAfter.Should().BeLessThan(sharesBefore,
            "after yield, each asset should be worth fewer shares since existing shares are now more valuable");
    }

    [Fact]
    public void ExchangeRate_AfterYield_MoreAssetsPerShare()
    {
        var vault = CreateVault();

        // M-4: With 10^18 virtual offsets, use amounts comparable to the offset
        // so that the yield materially affects the exchange rate
        UInt256 depositAmount = new UInt256(1_000_000_000_000_000_000); // 10^18
        UInt256 yieldAmount = new UInt256(1_000_000_000_000_000_000);   // 10^18

        MintAssetTokens(_alice, depositAmount * 2);
        ApproveVaultForAssets(_alice, depositAmount * 2);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(depositAmount));

        var assetsBefore = vault.ConvertToAssets(1000);

        MintAssetTokens(_admin, yieldAmount);
        ApproveVaultForAssets(_admin, yieldAmount);
        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Harvest(yieldAmount));

        var assetsAfter = vault.ConvertToAssets(1000);

        assetsAfter.Should().BeGreaterThan(assetsBefore,
            "after yield, each share should be worth more assets");
    }

    // ============================================================
    // 11. PreviewMint rounds up vs ConvertToShares
    // ============================================================

    [Fact]
    public void PreviewMint_RoundsUp()
    {
        var vault = CreateVault();

        // Set up a non-trivial exchange rate
        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(3000));

        MintAssetTokens(_admin, 1000);
        ApproveVaultForAssets(_admin, 1000);
        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Harvest(1000));

        // totalSupply=3000, totalAssets=4000
        // ConvertToShares(X) rounds down: X * (3000+1) / (4000+1)
        // PreviewMint(S) rounds up:  S * (4000+1) + (3000+1) - 1) / (3000+1)
        // For any shares S, PreviewMint(S) >= the exact inverse of ConvertToShares
        // Verify: if you deposit PreviewMint(S) assets, you get >= S shares
        for (ulong s = 1; s <= 100; s++)
        {
            UInt256 shares = s;
            var assetsNeeded = vault.PreviewMint(shares);
            var sharesGot = vault.ConvertToShares(assetsNeeded);
            sharesGot.Should().BeGreaterThanOrEqualTo(shares,
                $"PreviewMint({s}) should return enough assets to mint at least {s} shares");
        }
    }

    // ============================================================
    // 12. PreviewWithdraw rounds up vs ConvertToAssets
    // ============================================================

    [Fact]
    public void PreviewWithdraw_RoundsUp()
    {
        var vault = CreateVault();

        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(3000));

        MintAssetTokens(_admin, 1000);
        ApproveVaultForAssets(_admin, 1000);
        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Harvest(1000));

        // totalSupply=3000, totalAssets=4000
        // ConvertToAssets(S) rounds down: S * (4000+1) / (3000+1)
        // PreviewWithdraw(A) rounds up: A * (3000+1) + (4000+1) - 1) / (4000+1)
        // PreviewWithdraw(assets) should return enough shares to cover the withdrawal
        for (ulong a = 1; a <= 100; a++)
        {
            UInt256 assets = a;
            var sharesNeeded = vault.PreviewWithdraw(assets);
            var assetsGot = vault.ConvertToAssets(sharesNeeded);
            assetsGot.Should().BeGreaterThanOrEqualTo(assets,
                $"PreviewWithdraw({a}) should burn enough shares to cover withdrawal of {a} assets");
        }
    }

    // ============================================================
    // 13. Multiple depositors: both deposit, yield reported, proportional redeem
    // ============================================================

    [Fact]
    public void MultipleDepositors_ProportionalRedemption()
    {
        var vault = CreateVault();

        // M-4: With 10^18 virtual offsets, use amounts comparable to offset
        UInt256 unit = new UInt256(1_000_000_000_000_000_000); // 10^18

        // Alice deposits 4 * unit
        MintAssetTokens(_alice, unit * 10);
        ApproveVaultForAssets(_alice, unit * 10);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        var aliceShares = _host.Call(() => vault.Deposit(unit * 4));

        // Bob deposits 6 * unit
        MintAssetTokens(_bob, unit * 10);
        ApproveVaultForAssets(_bob, unit * 10);
        _host.SetCaller(_bob);
        Context.Self = _vaultAddress;
        var bobShares = _host.Call(() => vault.Deposit(unit * 6));

        vault.TotalAssets().Should().Be(unit * 10);
        vault.TotalSupply().Should().Be(aliceShares + bobShares);

        // Yield of 5 * unit. Credit vault's asset balance in mock ledger.
        MintAssetTokens(_admin, unit * 5);
        ApproveVaultForAssets(_admin, unit * 5);
        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Harvest(unit * 5));

        vault.TotalAssets().Should().Be(unit * 15);

        // Alice redeems all her shares
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        var aliceAssets = _host.Call(() => vault.Redeem(aliceShares));

        // Bob redeems all his shares
        _host.SetCaller(_bob);
        Context.Self = _vaultAddress;
        var bobAssets = _host.Call(() => vault.Redeem(bobShares));

        // Alice had ~40% of shares -> ~40% of 15*unit = ~6*unit
        // Bob had ~60% of shares -> ~60% of 15*unit = ~9*unit
        // With virtual offsets the actual share proportions are slightly diluted,
        // but should still be close. Allow some rounding tolerance.
        var aliceLow = unit * 5; // generous lower bound
        var aliceHigh = unit * 7;
        var bobLow = unit * 8;
        var bobHigh = unit * 10;
        aliceAssets.Should().BeGreaterThanOrEqualTo(aliceLow);
        aliceAssets.Should().BeLessThanOrEqualTo(aliceHigh);
        bobAssets.Should().BeGreaterThanOrEqualTo(bobLow);
        bobAssets.Should().BeLessThanOrEqualTo(bobHigh);

        // After both redeem, vault should have ~0 assets and 0 shares
        vault.TotalSupply().Should().Be((UInt256)0);
    }

    // ============================================================
    // 14. Share transfers work (inherited BST-20 Transfer)
    // ============================================================

    [Fact]
    public void ShareTransfer_InheritedBST20()
    {
        var vault = CreateVault();

        // Alice deposits and gets shares
        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(5000));

        var aliceShares = vault.BalanceOf(_alice);
        aliceShares.Should().BeGreaterThan((UInt256)0);

        // Alice transfers half her shares to Bob
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Transfer(_bob, aliceShares / 2));

        vault.BalanceOf(_alice).Should().Be(aliceShares - aliceShares / 2);
        vault.BalanceOf(_bob).Should().Be(aliceShares / 2);
        vault.TotalSupply().Should().Be(aliceShares); // unchanged
    }

    // ============================================================
    // Additional edge case tests
    // ============================================================

    [Fact]
    public void PreviewDeposit_MatchesConvertToShares()
    {
        var vault = CreateVault();

        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(3000));

        MintAssetTokens(_admin, 1000);
        ApproveVaultForAssets(_admin, 1000);
        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Harvest(1000));

        // PreviewDeposit delegates to ConvertToShares
        vault.PreviewDeposit(500).Should().Be(vault.ConvertToShares(500));
    }

    [Fact]
    public void PreviewRedeem_MatchesConvertToAssets()
    {
        var vault = CreateVault();

        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(3000));

        MintAssetTokens(_admin, 1000);
        ApproveVaultForAssets(_admin, 1000);
        _host.SetCaller(_admin);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Harvest(1000));

        // PreviewRedeem delegates to ConvertToAssets
        vault.PreviewRedeem(500).Should().Be(vault.ConvertToAssets(500));
    }

    [Fact]
    public void Deposit_InsufficientAssetAllowance_Reverts()
    {
        var vault = CreateVault();

        // Give Alice tokens but do NOT approve the vault
        MintAssetTokens(_alice, 10_000);

        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;

        var act = () => _host.Call(() => vault.Deposit(1000));
        act.Should().Throw<ContractRevertException>()
            .WithMessage("*insufficient allowance*");
    }

    [Fact]
    public void Deposit_InsufficientAssetBalance_Reverts()
    {
        var vault = CreateVault();

        // Give Alice only 500 but try to deposit 1000
        MintAssetTokens(_alice, 500);
        ApproveVaultForAssets(_alice, 10_000);

        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;

        var act = () => _host.Call(() => vault.Deposit(1000));
        act.Should().Throw<ContractRevertException>()
            .WithMessage("*insufficient balance*");
    }

    [Fact]
    public void Withdraw_MoreThanTotalAssets_Reverts()
    {
        var vault = CreateVault();

        MintAssetTokens(_alice, 10_000);
        ApproveVaultForAssets(_alice, 10_000);
        _host.SetCaller(_alice);
        Context.Self = _vaultAddress;
        _host.Call(() => vault.Deposit(1000));

        // Try to withdraw more than deposited
        // C-5: Transfer happens before burn, so the vault's asset balance check fails first
        var act = () => _host.Call(() => vault.Withdraw(2000));
        act.Should().Throw<ContractRevertException>()
            .WithMessage("*insufficient balance*");
    }

    [Fact]
    public void VirtualOffset_PreventsInflationAttack()
    {
        var vault = CreateVault();

        // With virtual offset, even small deposits get a reasonable share count.
        // If totalSupply=0 and totalAssets=0:
        // ConvertToShares(1) = 1 * (0+1)/(0+1) = 1  (not zero!)
        vault.ConvertToShares(1).Should().Be((UInt256)1);
        vault.ConvertToAssets(1).Should().Be((UInt256)1);
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>
/// Derived class to expose protected Mint/Burn from BST4626Vault (inherited from BST20Token)
/// for testing purposes. Also allows direct state manipulation.
/// </summary>
public class TestableVault : BST4626Vault
{
    public TestableVault(string name, string symbol, byte decimals, byte[] assetAddress)
        : base(name, symbol, decimals, assetAddress) { }

    public void MintPublic(byte[] to, UInt256 amount) => Mint(to, amount);
    public void BurnPublic(byte[] from, UInt256 amount) => Burn(from, amount);
}
