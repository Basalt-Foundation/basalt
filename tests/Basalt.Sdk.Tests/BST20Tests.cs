using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Comprehensive tests for BST-20 Fungible Token Standard.
/// Uses MintableBST20 (defined in BST20TokenTests.cs) to expose protected Mint/Burn.
/// </summary>
public class BST20Tests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly MintableBST20 _token;
    private readonly byte[] _owner;
    private readonly byte[] _alice;
    private readonly byte[] _bob;
    private readonly byte[] _carol;

    public BST20Tests()
    {
        _token = new MintableBST20("BasaltCoin", "BSC", 8);
        _owner = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);
        _carol = BasaltTestHost.CreateAddress(4);
        _host.SetCaller(_owner);
    }

    // --- Metadata ---

    [Fact]
    public void Name_Symbol_Decimals_ReturnCorrectValues()
    {
        _token.Name().Should().Be("BasaltCoin");
        _token.Symbol().Should().Be("BSC");
        _token.Decimals().Should().Be(8);
    }

    // --- Mint ---

    [Fact]
    public void Mint_IncreasesTotalSupplyAndBalance()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 1000));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(1000);
        _host.Call(() => _token.TotalSupply()).Should().Be(1000);
    }

    [Fact]
    public void Mint_MultipleCalls_Accumulate()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 500));
        _host.Call(() => _token.MintPublic(_alice, 300));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(800);
        _host.Call(() => _token.TotalSupply()).Should().Be(800);
    }

    [Fact]
    public void Mint_ToDifferentAccounts_IndependentBalances()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 1000));
        _host.Call(() => _token.MintPublic(_bob, 2000));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(1000);
        _host.Call(() => _token.BalanceOf(_bob)).Should().Be(2000);
        _host.Call(() => _token.TotalSupply()).Should().Be(3000);
    }

    [Fact]
    public void Mint_EmitsTransferEvent_FromZeroAddress()
    {
        _host.SetCaller(_owner);
        _host.ClearEvents();
        _host.Call(() => _token.MintPublic(_alice, 500));

        var events = _host.GetEvents<TransferEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].From.Should().BeEquivalentTo(new byte[20]);
        events[0].To.Should().BeEquivalentTo(_alice);
        events[0].Amount.Should().Be(500);
    }

    // --- Transfer ---

    [Fact]
    public void Transfer_UpdatesBalances()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 1000));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Transfer(_bob, 400)).Should().BeTrue();

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(600);
        _host.Call(() => _token.BalanceOf(_bob)).Should().Be(400);
        _host.Call(() => _token.TotalSupply()).Should().Be(1000); // Supply unchanged
    }

    [Fact]
    public void Transfer_ExactBalance_Succeeds()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 500));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Transfer(_bob, 500)).Should().BeTrue();

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(0);
        _host.Call(() => _token.BalanceOf(_bob)).Should().Be(500);
    }

    [Fact]
    public void Transfer_InsufficientBalance_Reverts()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 100));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.Transfer(_bob, 200));
        msg.Should().Contain("insufficient balance");
    }

    [Fact]
    public void Transfer_ZeroAmount_Succeeds()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 100));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Transfer(_bob, 0)).Should().BeTrue();

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(100);
        _host.Call(() => _token.BalanceOf(_bob)).Should().Be(0);
    }

    [Fact]
    public void Transfer_EmitsTransferEvent()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 1000));
        _host.ClearEvents();

        _host.SetCaller(_alice);
        _host.Call(() => _token.Transfer(_bob, 250));

        var events = _host.GetEvents<TransferEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].From.Should().BeEquivalentTo(_alice);
        events[0].To.Should().BeEquivalentTo(_bob);
        events[0].Amount.Should().Be(250);
    }

    // --- Approve ---

    [Fact]
    public void Approve_SetsAllowance()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 500));

        _host.Call(() => _token.Allowance(_alice, _bob)).Should().Be(500);
    }

    [Fact]
    public void Approve_CanOverwrite()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 500));
        _host.Call(() => _token.Approve(_bob, 200));

        _host.Call(() => _token.Allowance(_alice, _bob)).Should().Be(200);
    }

    [Fact]
    public void Approve_EmitsApprovalEvent()
    {
        _host.ClearEvents();
        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 300));

        var events = _host.GetEvents<ApprovalEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Owner.Should().BeEquivalentTo(_alice);
        events[0].Spender.Should().BeEquivalentTo(_bob);
        events[0].Amount.Should().Be(300);
    }

    [Fact]
    public void Approve_DifferentSpenders_Independent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 100));
        _host.Call(() => _token.Approve(_carol, 200));

        _host.Call(() => _token.Allowance(_alice, _bob)).Should().Be(100);
        _host.Call(() => _token.Allowance(_alice, _carol)).Should().Be(200);
    }

    // --- TransferFrom ---

    [Fact]
    public void TransferFrom_WithAllowance_Succeeds()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 1000));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 500));

        _host.SetCaller(_bob);
        _host.Call(() => _token.TransferFrom(_alice, _carol, 300)).Should().BeTrue();

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(700);
        _host.Call(() => _token.BalanceOf(_carol)).Should().Be(300);
        _host.Call(() => _token.Allowance(_alice, _bob)).Should().Be(200);
    }

    [Fact]
    public void TransferFrom_InsufficientAllowance_Reverts()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 1000));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 50));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.TransferFrom(_alice, _carol, 100));
        msg.Should().Contain("insufficient allowance");
    }

    [Fact]
    public void TransferFrom_InsufficientBalance_Reverts()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 50));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 1000));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.TransferFrom(_alice, _carol, 100));
        msg.Should().Contain("insufficient balance");
    }

    [Fact]
    public void TransferFrom_ExactAllowance_Succeeds()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 1000));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 300));

        _host.SetCaller(_bob);
        _host.Call(() => _token.TransferFrom(_alice, _carol, 300)).Should().BeTrue();

        _host.Call(() => _token.Allowance(_alice, _bob)).Should().Be(0);
    }

    // --- Burn ---

    [Fact]
    public void Burn_DecreasesSupplyAndBalance()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 1000));

        _host.Call(() => _token.BurnPublic(_alice, 400));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(600);
        _host.Call(() => _token.TotalSupply()).Should().Be(600);
    }

    [Fact]
    public void Burn_ExceedsBalance_Reverts()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 100));

        var msg = _host.ExpectRevert(() => _token.BurnPublic(_alice, 200));
        msg.Should().Contain("burn exceeds balance");
    }

    [Fact]
    public void Burn_EntireBalance_Succeeds()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 500));
        _host.Call(() => _token.BurnPublic(_alice, 500));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(0);
        _host.Call(() => _token.TotalSupply()).Should().Be(0);
    }

    [Fact]
    public void Burn_EmitsTransferEvent_ToZeroAddress()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.MintPublic(_alice, 1000));
        _host.ClearEvents();

        _host.Call(() => _token.BurnPublic(_alice, 300));

        var events = _host.GetEvents<TransferEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].From.Should().BeEquivalentTo(_alice);
        events[0].To.Should().BeEquivalentTo(new byte[20]);
        events[0].Amount.Should().Be(300);
    }

    // --- BalanceOf edge cases ---

    [Fact]
    public void BalanceOf_UnknownAddress_ReturnsZero()
    {
        var unknown = BasaltTestHost.CreateAddress(99);
        _host.Call(() => _token.BalanceOf(unknown)).Should().Be(0);
    }

    [Fact]
    public void Allowance_UnsetPair_ReturnsZero()
    {
        _host.Call(() => _token.Allowance(_alice, _bob)).Should().Be(0);
    }

    public void Dispose() => _host.Dispose();
}
