using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class BST20TokenTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BST20Token _token;
    private readonly byte[] _owner;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    public BST20TokenTests()
    {
        _token = new BST20Token("TestToken", "TT", 18);
        _owner = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);
        _host.SetCaller(_owner);
    }

    [Fact]
    public void Name_Returns_Configured_Name()
    {
        _token.Name().Should().Be("TestToken");
    }

    [Fact]
    public void Symbol_Returns_Configured_Symbol()
    {
        _token.Symbol().Should().Be("TT");
    }

    [Fact]
    public void Decimals_Returns_Configured_Decimals()
    {
        _token.Decimals().Should().Be(18);
    }

    [Fact]
    public void Mint_Increases_Balance_And_Supply()
    {
        // BST20Token.Mint is protected, so we need a derived class for testing
        var mintable = new MintableBST20("Test", "T", 18);
        _host.SetCaller(_owner);
        _host.Call(() => mintable.MintPublic(_alice, 1000));

        _host.Call(() => mintable.BalanceOf(_alice)).Should().Be(1000);
        _host.Call(() => mintable.TotalSupply()).Should().Be(1000);
    }

    [Fact]
    public void Transfer_Moves_Tokens()
    {
        var mintable = new MintableBST20("Test", "T", 18);
        _host.SetCaller(_owner);
        _host.Call(() => mintable.MintPublic(_alice, 1000));

        _host.SetCaller(_alice);
        _host.Call(() => mintable.Transfer(_bob, 300)).Should().BeTrue();

        _host.Call(() => mintable.BalanceOf(_alice)).Should().Be(700);
        _host.Call(() => mintable.BalanceOf(_bob)).Should().Be(300);
    }

    [Fact]
    public void Transfer_Reverts_On_Insufficient_Balance()
    {
        var mintable = new MintableBST20("Test", "T", 18);
        _host.SetCaller(_owner);
        _host.Call(() => mintable.MintPublic(_alice, 100));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => mintable.Transfer(_bob, 200));
        msg.Should().Contain("insufficient balance");
    }

    [Fact]
    public void Approve_And_TransferFrom()
    {
        var mintable = new MintableBST20("Test", "T", 18);
        _host.SetCaller(_owner);
        _host.Call(() => mintable.MintPublic(_alice, 1000));

        _host.SetCaller(_alice);
        _host.Call(() => mintable.Approve(_bob, 500));
        _host.Call(() => mintable.Allowance(_alice, _bob)).Should().Be(500);

        _host.SetCaller(_bob);
        _host.Call(() => mintable.TransferFrom(_alice, _bob, 300)).Should().BeTrue();

        _host.Call(() => mintable.BalanceOf(_alice)).Should().Be(700);
        _host.Call(() => mintable.BalanceOf(_bob)).Should().Be(300);
        _host.Call(() => mintable.Allowance(_alice, _bob)).Should().Be(200);
    }

    [Fact]
    public void TransferFrom_Reverts_On_Insufficient_Allowance()
    {
        var mintable = new MintableBST20("Test", "T", 18);
        _host.SetCaller(_owner);
        _host.Call(() => mintable.MintPublic(_alice, 1000));

        _host.SetCaller(_alice);
        _host.Call(() => mintable.Approve(_bob, 50));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => mintable.TransferFrom(_alice, _bob, 100));
        msg.Should().Contain("insufficient allowance");
    }

    [Fact]
    public void Transfer_Emits_TransferEvent()
    {
        var mintable = new MintableBST20("Test", "T", 18);
        _host.SetCaller(_owner);
        _host.Call(() => mintable.MintPublic(_alice, 1000));
        _host.ClearEvents();

        _host.SetCaller(_alice);
        _host.Call(() => mintable.Transfer(_bob, 100));

        var events = _host.GetEvents<TransferEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Amount.Should().Be(100);
    }

    [Fact]
    public void Burn_Decreases_Balance_And_Supply()
    {
        var mintable = new MintableBST20("Test", "T", 18);
        _host.SetCaller(_owner);
        _host.Call(() => mintable.MintPublic(_alice, 1000));
        _host.Call(() => mintable.BurnPublic(_alice, 300));

        _host.Call(() => mintable.BalanceOf(_alice)).Should().Be(700);
        _host.Call(() => mintable.TotalSupply()).Should().Be(700);
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>
/// Derived class to expose protected Mint/Burn for testing.
/// </summary>
public class MintableBST20 : BST20Token
{
    public MintableBST20(string name, string symbol, byte decimals) : base(name, symbol, decimals) { }
    public void MintPublic(byte[] to, ulong amount) => Mint(to, amount);
    public void BurnPublic(byte[] from, ulong amount) => Burn(from, amount);
}
