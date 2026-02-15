using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Comprehensive tests for BST-721 Non-Fungible Token Standard.
/// </summary>
public class BST721Tests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BST721Token _token;
    private readonly byte[] _owner;
    private readonly byte[] _alice;
    private readonly byte[] _bob;
    private readonly byte[] _carol;

    public BST721Tests()
    {
        _token = new BST721Token("BasaltNFT", "BNFT");
        _owner = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);
        _carol = BasaltTestHost.CreateAddress(4);
        _host.SetCaller(_owner);
    }

    // --- Mint ---

    [Fact]
    public void Mint_AssignsOwnership()
    {
        _host.SetCaller(_owner);
        var tokenId = _host.Call(() => _token.Mint(_alice, "ipfs://meta/0"));

        tokenId.Should().Be(0UL);
        _host.Call(() => _token.OwnerOf(0)).Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void Mint_SetsTokenURI()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "ipfs://meta/custom"));

        _host.Call(() => _token.TokenURI(0)).Should().Be("ipfs://meta/custom");
    }

    [Fact]
    public void Mint_IncrementsTokenIds()
    {
        _host.SetCaller(_owner);
        var id0 = _host.Call(() => _token.Mint(_alice, "uri0"));
        var id1 = _host.Call(() => _token.Mint(_bob, "uri1"));
        var id2 = _host.Call(() => _token.Mint(_alice, "uri2"));

        id0.Should().Be(0UL);
        id1.Should().Be(1UL);
        id2.Should().Be(2UL);
    }

    [Fact]
    public void Mint_EmitsNftTransferEvent()
    {
        _host.SetCaller(_owner);
        _host.ClearEvents();
        _host.Call(() => _token.Mint(_alice, "uri"));

        var events = _host.GetEvents<NftTransferEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].From.Should().BeEquivalentTo(new byte[20]); // Zero address = mint
        events[0].To.Should().BeEquivalentTo(_alice);
        events[0].TokenId.Should().Be(0UL);
    }

    // --- Transfer ---

    [Fact]
    public void Transfer_UpdatesOwnership()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Transfer(_bob, 0));

        _host.Call(() => _token.OwnerOf(0)).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void Transfer_UpdatesBalances()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri0"));
        _host.Call(() => _token.Mint(_alice, "uri1"));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(2);
        _host.Call(() => _token.BalanceOf(_bob)).Should().Be(0);

        _host.SetCaller(_alice);
        _host.Call(() => _token.Transfer(_bob, 0));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(1);
        _host.Call(() => _token.BalanceOf(_bob)).Should().Be(1);
    }

    [Fact]
    public void Transfer_ByNonOwner_Reverts()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.Transfer(_carol, 0));
        msg.Should().Contain("not owner or approved");
    }

    [Fact]
    public void Transfer_NonExistentToken_Reverts()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.Transfer(_bob, 999));
        msg.Should().Contain("token does not exist");
    }

    [Fact]
    public void Transfer_ClearsApproval()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 0));

        // Verify approval is set
        _host.Call(() => _token.GetApproved(0)).Should().BeEquivalentTo(_bob);

        // Transfer clears approval
        _host.Call(() => _token.Transfer(_carol, 0));
        _host.Call(() => _token.GetApproved(0)).Should().BeEquivalentTo(new byte[20]);
    }

    [Fact]
    public void Transfer_EmitsNftTransferEvent()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));
        _host.ClearEvents();

        _host.SetCaller(_alice);
        _host.Call(() => _token.Transfer(_bob, 0));

        var events = _host.GetEvents<NftTransferEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].From.Should().BeEquivalentTo(_alice);
        events[0].To.Should().BeEquivalentTo(_bob);
        events[0].TokenId.Should().Be(0UL);
    }

    // --- Approve ---

    [Fact]
    public void Approve_SetsApproval()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 0));

        _host.Call(() => _token.GetApproved(0)).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void Approve_ByNonOwner_Reverts()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.Approve(_carol, 0));
        msg.Should().Contain("caller is not owner");
    }

    [Fact]
    public void Approve_NonExistentToken_Reverts()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.Approve(_bob, 999));
        msg.Should().Contain("token does not exist");
    }

    [Fact]
    public void Approve_EmitsNftApprovalEvent()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));
        _host.ClearEvents();

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 0));

        var events = _host.GetEvents<NftApprovalEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Owner.Should().BeEquivalentTo(_alice);
        events[0].Approved.Should().BeEquivalentTo(_bob);
        events[0].TokenId.Should().Be(0UL);
    }

    [Fact]
    public void Approve_AllowsApprovedToTransfer()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 0));

        _host.SetCaller(_bob);
        _host.Call(() => _token.Transfer(_carol, 0));

        _host.Call(() => _token.OwnerOf(0)).Should().BeEquivalentTo(_carol);
    }

    // --- BalanceOf ---

    [Fact]
    public void BalanceOf_ReturnsCorrectCount()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri0"));
        _host.Call(() => _token.Mint(_alice, "uri1"));
        _host.Call(() => _token.Mint(_alice, "uri2"));
        _host.Call(() => _token.Mint(_bob, "uri3"));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(3);
        _host.Call(() => _token.BalanceOf(_bob)).Should().Be(1);
        _host.Call(() => _token.BalanceOf(_carol)).Should().Be(0);
    }

    [Fact]
    public void BalanceOf_UpdatesAfterTransfer()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(1);

        _host.SetCaller(_alice);
        _host.Call(() => _token.Transfer(_bob, 0));

        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(0);
        _host.Call(() => _token.BalanceOf(_bob)).Should().Be(1);
    }

    // --- OwnerOf ---

    [Fact]
    public void OwnerOf_NonExistentToken_Reverts()
    {
        var msg = _host.ExpectRevert(() => _token.OwnerOf(999));
        msg.Should().Contain("token does not exist");
    }

    // --- TokenURI ---

    [Fact]
    public void TokenURI_ReturnsEmptyForNonExistentToken()
    {
        _host.Call(() => _token.TokenURI(999)).Should().Be("");
    }

    // --- Name and Symbol ---

    [Fact]
    public void Name_ReturnsConfiguredName()
    {
        _token.Name().Should().Be("BasaltNFT");
    }

    [Fact]
    public void Symbol_ReturnsConfiguredSymbol()
    {
        _token.Symbol().Should().Be("BNFT");
    }

    public void Dispose() => _host.Dispose();
}
