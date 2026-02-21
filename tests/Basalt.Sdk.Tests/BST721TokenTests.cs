using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class BST721TokenTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BST721Token _token;
    private readonly byte[] _owner;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    public BST721TokenTests()
    {
        _owner = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);
        _host.SetCaller(_owner);
        // Token must be created AFTER SetCaller so _contractAdmin captures the owner
        _token = new BST721Token("TestNFT", "TNFT");
    }

    [Fact]
    public void Name_And_Symbol()
    {
        _token.Name().Should().Be("TestNFT");
        _token.Symbol().Should().Be("TNFT");
    }

    [Fact]
    public void Mint_Creates_Token()
    {
        _host.SetCaller(_owner);
        var tokenId = _host.Call(() => _token.Mint(_alice, "ipfs://metadata/0"));

        tokenId.Should().Be(0UL);
        _host.Call(() => _token.OwnerOf(0)).Should().BeEquivalentTo(_alice);
        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(1);
        _host.Call(() => _token.TokenURI(0)).Should().Be("ipfs://metadata/0");
    }

    [Fact]
    public void Mint_Increments_TokenId()
    {
        _host.SetCaller(_owner);
        var id0 = _host.Call(() => _token.Mint(_alice, "uri0"));
        var id1 = _host.Call(() => _token.Mint(_alice, "uri1"));

        id0.Should().Be(0UL);
        id1.Should().Be(1UL);
        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(2);
    }

    [Fact]
    public void Transfer_By_Owner()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Transfer(_bob, 0));

        _host.Call(() => _token.OwnerOf(0)).Should().BeEquivalentTo(_bob);
        _host.Call(() => _token.BalanceOf(_alice)).Should().Be(0);
        _host.Call(() => _token.BalanceOf(_bob)).Should().Be(1);
    }

    [Fact]
    public void Transfer_By_Approved()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 0));

        _host.SetCaller(_bob);
        _host.Call(() => _token.Transfer(_bob, 0));

        _host.Call(() => _token.OwnerOf(0)).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void Transfer_Reverts_For_NonOwner()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.Transfer(_bob, 0));
        msg.Should().Contain("not owner or approved");
    }

    [Fact]
    public void Transfer_Clears_Approval()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        _host.SetCaller(_alice);
        _host.Call(() => _token.Approve(_bob, 0));
        _host.Call(() => _token.Transfer(_bob, 0));

        // Approval should be cleared after transfer
        _host.Call(() => _token.GetApproved(0)).Should().BeEquivalentTo(new byte[20]);
    }

    [Fact]
    public void OwnerOf_Reverts_For_NonExistent_Token()
    {
        var msg = _host.ExpectRevert(() => _token.OwnerOf(999));
        msg.Should().Contain("token does not exist");
    }

    [Fact]
    public void Mint_Emits_NftTransferEvent()
    {
        _host.SetCaller(_owner);
        _host.Call(() => _token.Mint(_alice, "uri"));

        var events = _host.GetEvents<NftTransferEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].To.Should().BeEquivalentTo(_alice);
        events[0].TokenId.Should().Be(0);
    }

    public void Dispose() => _host.Dispose();
}
