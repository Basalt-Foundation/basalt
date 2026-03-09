using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

public class BST721PolicyIntegrationTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xF0);
    private readonly byte[] _sanctionsAddr = BasaltTestHost.CreateAddress(0xF1);
    private readonly BST721Token _token;
    private readonly SanctionsPolicy _sanctions;

    public BST721PolicyIntegrationTests()
    {
        _host = new BasaltTestHost();

        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token = new BST721Token("TestNFT", "TNFT");
        _host.Deploy(_tokenAddr, _token);

        Context.Self = _sanctionsAddr;
        _sanctions = new SanctionsPolicy();
        _host.Deploy(_sanctionsAddr, _sanctions);

        Context.IsDeploying = false;
    }

    [Fact]
    public void Transfer_SucceedsWithNoPolicies()
    {
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        var tokenId = _host.Call(() => _token.Mint(_alice, "uri://1"));

        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_bob, tokenId));

        _token.OwnerOf(tokenId).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void Transfer_RevertsWhenPolicyDenies()
    {
        // Mint to Alice
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        var tokenId = _host.Call(() => _token.Mint(_alice, "uri://1"));

        // Sanction Bob
        Context.Self = _sanctionsAddr;
        _sanctions.AddSanction(_bob);

        // Register policy
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);

        // Alice tries to transfer to sanctioned Bob
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        var msg = _host.ExpectRevert(() => _token.Transfer(_bob, tokenId));
        msg.Should().Contain("transfer denied");
    }

    [Fact]
    public void Transfer_SucceedsWhenPolicyApproves()
    {
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        var tokenId = _host.Call(() => _token.Mint(_alice, "uri://1"));

        // Register policy (no one sanctioned)
        _token.AddPolicy(_sanctionsAddr);

        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_bob, tokenId));
        _token.OwnerOf(tokenId).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void PolicyManagement_AdminOnly()
    {
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        var msg = _host.ExpectRevert(() => _token.AddPolicy(_sanctionsAddr));
        msg.Should().Contain("not owner");
    }

    [Fact]
    public void PolicyCount_Tracks()
    {
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.PolicyCount().Should().Be(0);
        _token.AddPolicy(_sanctionsAddr);
        _token.PolicyCount().Should().Be(1);
    }

    public void Dispose() => _host.Dispose();
}
