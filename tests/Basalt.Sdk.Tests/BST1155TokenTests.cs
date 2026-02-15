using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class BST1155TokenTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BST1155Token _token;
    private readonly byte[] _owner;
    private readonly byte[] _alice;
    private readonly byte[] _bob;
    private readonly byte[] _operator;

    public BST1155TokenTests()
    {
        _token = new BST1155Token("https://example.com/tokens/");
        _owner = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);
        _operator = BasaltTestHost.CreateAddress(4);
        _host.SetCaller(_owner);
    }

    [Fact]
    public void Create_Returns_Incrementing_TokenIds()
    {
        _host.SetCaller(_owner);
        var id0 = _host.Call(() => _token.Create(_alice, 100, "custom0"));
        var id1 = _host.Call(() => _token.Create(_alice, 50, ""));

        id0.Should().Be(0UL);
        id1.Should().Be(1UL);
    }

    [Fact]
    public void Create_Mints_Initial_Supply()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Create(_alice, 100, ""));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(100);
    }

    [Fact]
    public void Mint_Adds_To_Balance()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 500, ""));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(500);

        _host.Call(() => _token.Mint(_alice, 0, 300, ""));
        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(800);
    }

    [Fact]
    public void SafeTransferFrom_Moves_Tokens()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 1000, ""));

        _host.SetCaller(_alice);
        _host.Call(() => _token.SafeTransferFrom(_alice, _bob, 0, 400));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(600);
        _host.Call(() => _token.BalanceOf(_bob, 0)).Should().Be(400);
    }

    [Fact]
    public void SafeTransferFrom_Reverts_On_Insufficient_Balance()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.SafeTransferFrom(_alice, _bob, 0, 200));
        msg.Should().Contain("insufficient balance");
    }

    [Fact]
    public void SafeTransferFrom_Reverts_For_Unauthorized_Caller()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.SafeTransferFrom(_alice, _bob, 0, 50));
        msg.Should().Contain("not owner or approved");
    }

    [Fact]
    public void SetApprovalForAll_Enables_Operator_Transfer()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 1000, ""));

        _host.SetCaller(_alice);
        _host.Call(() => _token.SetApprovalForAll(_operator, true));
        _host.Call(() => _token.IsApprovedForAll(_alice, _operator)).Should().BeTrue();

        _host.SetCaller(_operator);
        _host.Call(() => _token.SafeTransferFrom(_alice, _bob, 0, 200));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(800);
        _host.Call(() => _token.BalanceOf(_bob, 0)).Should().Be(200);
    }

    [Fact]
    public void SetApprovalForAll_Can_Revoke()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.SetApprovalForAll(_operator, true));
        _host.Call(() => _token.IsApprovedForAll(_alice, _operator)).Should().BeTrue();

        _host.Call(() => _token.SetApprovalForAll(_operator, false));
        _host.Call(() => _token.IsApprovedForAll(_alice, _operator)).Should().BeFalse();
    }

    [Fact]
    public void SafeBatchTransferFrom_Moves_Multiple_Tokens()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 1000, ""));
        _host.Call(() => _token.Mint(_alice, 1, 500, ""));

        _host.SetCaller(_alice);
        _host.Call(() => _token.SafeBatchTransferFrom(
            _alice, _bob, new ulong[] { 0, 1 }, new ulong[] { 100, 50 }));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(900);
        _host.Call(() => _token.BalanceOf(_alice, 1)).Should().Be(450);
        _host.Call(() => _token.BalanceOf(_bob, 0)).Should().Be(100);
        _host.Call(() => _token.BalanceOf(_bob, 1)).Should().Be(50);
    }

    [Fact]
    public void SafeBatchTransferFrom_Reverts_On_Length_Mismatch()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() =>
            _token.SafeBatchTransferFrom(_alice, _bob, new ulong[] { 0 }, new ulong[] { 1, 2 }));
        msg.Should().Contain("length mismatch");
    }

    [Fact]
    public void BalanceOfBatch_Returns_Multiple_Balances()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));
        _host.Call(() => _token.Mint(_bob, 1, 200, ""));

        var balances = _host.Call(() => _token.BalanceOfBatch(
            new[] { _alice, _bob }, new ulong[] { 0, 1 }));

        balances.Should().BeEquivalentTo(new ulong[] { 100, 200 });
    }

    [Fact]
    public void Uri_Returns_Custom_Or_BaseUri()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 10, "custom://token0"));

        _host.Call(() => _token.Uri(0)).Should().Be("custom://token0");
        _host.Call(() => _token.Uri(999)).Should().Be("https://example.com/tokens/999");
    }

    [Fact]
    public void SafeTransferFrom_Emits_TransferSingleEvent()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));
        _host.ClearEvents();

        _host.SetCaller(_alice);
        _host.Call(() => _token.SafeTransferFrom(_alice, _bob, 0, 50));

        var events = _host.GetEvents<TransferSingleEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].From.Should().BeEquivalentTo(_alice);
        events[0].To.Should().BeEquivalentTo(_bob);
        events[0].TokenId.Should().Be(0);
        events[0].Amount.Should().Be(50);
    }

    [Fact]
    public void SafeBatchTransferFrom_Emits_TransferBatchEvent()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));
        _host.Call(() => _token.Mint(_alice, 1, 100, ""));
        _host.ClearEvents();

        _host.SetCaller(_alice);
        _host.Call(() => _token.SafeBatchTransferFrom(
            _alice, _bob, new ulong[] { 0, 1 }, new ulong[] { 10, 20 }));

        var events = _host.GetEvents<TransferBatchEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].TokenIds.Should().BeEquivalentTo(new ulong[] { 0, 1 });
        events[0].Amounts.Should().BeEquivalentTo(new ulong[] { 10, 20 });
    }

    public void Dispose() => _host.Dispose();
}
