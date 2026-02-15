using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Comprehensive tests for BST-1155 Multi-Token Standard.
/// </summary>
public class BST1155Tests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BST1155Token _token;
    private readonly byte[] _owner;
    private readonly byte[] _alice;
    private readonly byte[] _bob;
    private readonly byte[] _carol;
    private readonly byte[] _operator;

    public BST1155Tests()
    {
        _token = new BST1155Token("https://tokens.basalt.io/");
        _owner = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);
        _carol = BasaltTestHost.CreateAddress(4);
        _operator = BasaltTestHost.CreateAddress(5);
        _host.SetCaller(_owner);
    }

    // --- Mint ---

    [Fact]
    public void Mint_IncreasesBalance()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 500, ""));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(500);
    }

    [Fact]
    public void Mint_AccumulatesOnMultipleCalls()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 300, ""));
        _host.Call(() => _token.Mint(_alice, 0, 200, ""));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(500);
    }

    [Fact]
    public void Mint_DifferentTokenIds_Independent()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));
        _host.Call(() => _token.Mint(_alice, 1, 200, ""));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(100);
        _host.Call(() => _token.BalanceOf(_alice, 1)).Should().Be(200);
    }

    [Fact]
    public void Mint_EmitsTransferSingleEvent()
    {
        _host.SetCaller(_owner);
        _host.ClearEvents();
        _host.Call(() => _token.Mint(_alice, 5, 1000, ""));

        var events = _host.GetEvents<TransferSingleEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].From.Should().BeEquivalentTo(new byte[20]);
        events[0].To.Should().BeEquivalentTo(_alice);
        events[0].TokenId.Should().Be(5);
        events[0].Amount.Should().Be(1000);
    }

    [Fact]
    public void Mint_WithUri_SetsTokenUri()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 42, 10, "custom://token42"));

        _host.Call(() => _token.Uri(42)).Should().Be("custom://token42");
    }

    // --- Transfer ---

    [Fact]
    public void Transfer_UpdatesBalances()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 1000, ""));

        _host.SetCaller(_alice);
        _host.Call(() => _token.SafeTransferFrom(_alice, _bob, 0, 400));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(600);
        _host.Call(() => _token.BalanceOf(_bob, 0)).Should().Be(400);
    }

    [Fact]
    public void Transfer_InsufficientBalance_Reverts()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.SafeTransferFrom(_alice, _bob, 0, 200));
        msg.Should().Contain("insufficient balance");
    }

    [Fact]
    public void Transfer_Unauthorized_Reverts()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.SafeTransferFrom(_alice, _bob, 0, 50));
        msg.Should().Contain("not owner or approved");
    }

    [Fact]
    public void Transfer_ByApprovedOperator_Succeeds()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 1000, ""));

        _host.SetCaller(_alice);
        _host.Call(() => _token.SetApprovalForAll(_operator, true));

        _host.SetCaller(_operator);
        _host.Call(() => _token.SafeTransferFrom(_alice, _bob, 0, 300));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(700);
        _host.Call(() => _token.BalanceOf(_bob, 0)).Should().Be(300);
    }

    [Fact]
    public void Transfer_EmitsTransferSingleEvent()
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

    // --- Batch Transfer ---

    [Fact]
    public void BatchTransfer_MovesMultipleTokens()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 1000, ""));
        _host.Call(() => _token.Mint(_alice, 1, 500, ""));
        _host.Call(() => _token.Mint(_alice, 2, 300, ""));

        _host.SetCaller(_alice);
        _host.Call(() => _token.SafeBatchTransferFrom(
            _alice, _bob,
            new ulong[] { 0, 1, 2 },
            new ulong[] { 100, 50, 30 }));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(900);
        _host.Call(() => _token.BalanceOf(_alice, 1)).Should().Be(450);
        _host.Call(() => _token.BalanceOf(_alice, 2)).Should().Be(270);
        _host.Call(() => _token.BalanceOf(_bob, 0)).Should().Be(100);
        _host.Call(() => _token.BalanceOf(_bob, 1)).Should().Be(50);
        _host.Call(() => _token.BalanceOf(_bob, 2)).Should().Be(30);
    }

    [Fact]
    public void BatchTransfer_LengthMismatch_Reverts()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() =>
            _token.SafeBatchTransferFrom(_alice, _bob, new ulong[] { 0, 1 }, new ulong[] { 10 }));
        msg.Should().Contain("length mismatch");
    }

    [Fact]
    public void BatchTransfer_InsufficientBalance_Reverts()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 10, ""));
        _host.Call(() => _token.Mint(_alice, 1, 5, ""));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() =>
            _token.SafeBatchTransferFrom(_alice, _bob, new ulong[] { 0, 1 }, new ulong[] { 10, 100 }));
        msg.Should().Contain("insufficient balance");
    }

    [Fact]
    public void BatchTransfer_EmitsTransferBatchEvent()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));
        _host.Call(() => _token.Mint(_alice, 1, 100, ""));
        _host.ClearEvents();

        _host.SetCaller(_alice);
        _host.Call(() => _token.SafeBatchTransferFrom(
            _alice, _bob,
            new ulong[] { 0, 1 },
            new ulong[] { 10, 20 }));

        var events = _host.GetEvents<TransferBatchEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].From.Should().BeEquivalentTo(_alice);
        events[0].To.Should().BeEquivalentTo(_bob);
        events[0].TokenIds.Should().BeEquivalentTo(new ulong[] { 0, 1 });
        events[0].Amounts.Should().BeEquivalentTo(new ulong[] { 10, 20 });
    }

    [Fact]
    public void BatchTransfer_ByApprovedOperator_Succeeds()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 1000, ""));
        _host.Call(() => _token.Mint(_alice, 1, 500, ""));

        _host.SetCaller(_alice);
        _host.Call(() => _token.SetApprovalForAll(_operator, true));

        _host.SetCaller(_operator);
        _host.Call(() => _token.SafeBatchTransferFrom(
            _alice, _bob,
            new ulong[] { 0, 1 },
            new ulong[] { 100, 50 }));

        _host.Call(() => _token.BalanceOf(_alice, 0)).Should().Be(900);
        _host.Call(() => _token.BalanceOf(_bob, 1)).Should().Be(50);
    }

    // --- Approval ---

    [Fact]
    public void SetApprovalForAll_SetsApproval()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.SetApprovalForAll(_operator, true));

        _host.Call(() => _token.IsApprovedForAll(_alice, _operator)).Should().BeTrue();
    }

    [Fact]
    public void SetApprovalForAll_CanRevoke()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.SetApprovalForAll(_operator, true));
        _host.Call(() => _token.SetApprovalForAll(_operator, false));

        _host.Call(() => _token.IsApprovedForAll(_alice, _operator)).Should().BeFalse();
    }

    [Fact]
    public void SetApprovalForAll_EmitsApprovalForAllEvent()
    {
        _host.ClearEvents();
        _host.SetCaller(_alice);
        _host.Call(() => _token.SetApprovalForAll(_operator, true));

        var events = _host.GetEvents<ApprovalForAllEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Owner.Should().BeEquivalentTo(_alice);
        events[0].Operator.Should().BeEquivalentTo(_operator);
        events[0].Approved.Should().BeTrue();
    }

    [Fact]
    public void IsApprovedForAll_DefaultFalse()
    {
        _host.Call(() => _token.IsApprovedForAll(_alice, _operator)).Should().BeFalse();
    }

    // --- Create ---

    [Fact]
    public void Create_ReturnsIncrementingIds()
    {
        _host.SetCaller(_owner);
        var id0 = _host.Call(() => _token.Create(_alice, 100, ""));
        var id1 = _host.Call(() => _token.Create(_bob, 50, ""));

        id0.Should().Be(0UL);
        id1.Should().Be(1UL);
    }

    [Fact]
    public void Create_MintsInitialSupply()
    {
        _host.SetCaller(_owner);
        var id = _host.Call(() => _token.Create(_alice, 1000, ""));

        _host.Call(() => _token.BalanceOf(_alice, id)).Should().Be(1000);
    }

    [Fact]
    public void Create_ZeroSupply_NoBalance()
    {
        _host.SetCaller(_owner);
        var id = _host.Call(() => _token.Create(_alice, 0, "custom://zero"));

        _host.Call(() => _token.BalanceOf(_alice, id)).Should().Be(0);
        _host.Call(() => _token.Uri(id)).Should().Be("custom://zero");
    }

    // --- BalanceOfBatch ---

    [Fact]
    public void BalanceOfBatch_ReturnsCorrectBalances()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 0, 100, ""));
        _host.Call(() => _token.Mint(_bob, 1, 200, ""));
        _host.Call(() => _token.Mint(_carol, 2, 300, ""));

        var balances = _host.Call(() => _token.BalanceOfBatch(
            new[] { _alice, _bob, _carol },
            new ulong[] { 0, 1, 2 }));

        balances.Should().BeEquivalentTo(new ulong[] { 100, 200, 300 });
    }

    [Fact]
    public void BalanceOfBatch_LengthMismatch_Reverts()
    {
        var msg = _host.ExpectRevert(() =>
            _token.BalanceOfBatch(
                new[] { _alice },
                new ulong[] { 0, 1 }));
        msg.Should().Contain("length mismatch");
    }

    // --- Uri ---

    [Fact]
    public void Uri_ReturnsBaseUriForUnsetToken()
    {
        _host.Call(() => _token.Uri(999)).Should().Be("https://tokens.basalt.io/999");
    }

    [Fact]
    public void Uri_ReturnsCustomUriWhenSet()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, 7, 1, "custom://token/7"));

        _host.Call(() => _token.Uri(7)).Should().Be("custom://token/7");
    }

    // --- BalanceOf edge case ---

    [Fact]
    public void BalanceOf_UnknownAccountAndToken_ReturnsZero()
    {
        var unknown = BasaltTestHost.CreateAddress(99);
        _host.Call(() => _token.BalanceOf(unknown, 999)).Should().Be(0);
    }

    public void Dispose() => _host.Dispose();
}
