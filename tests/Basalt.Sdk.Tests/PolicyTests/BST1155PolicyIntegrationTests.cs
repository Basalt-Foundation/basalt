using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

public class BST1155PolicyIntegrationTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xF0);
    private readonly byte[] _sanctionsAddr = BasaltTestHost.CreateAddress(0xF1);
    private readonly BST1155Token _token;
    private readonly SanctionsPolicy _sanctions;

    public BST1155PolicyIntegrationTests()
    {
        _host = new BasaltTestHost();

        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token = new BST1155Token("https://example.com/");
        _host.Deploy(_tokenAddr, _token);

        Context.Self = _sanctionsAddr;
        _sanctions = new SanctionsPolicy();
        _host.Deploy(_sanctionsAddr, _sanctions);

        Context.IsDeploying = false;

        // Mint some tokens to admin
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.Mint(_admin, 1, new UInt256(1000), "");
    }

    [Fact]
    public void SafeTransferFrom_SucceedsWithNoPolicies()
    {
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.SafeTransferFrom(_admin, _alice, 1, new UInt256(100)));
        _token.BalanceOf(_alice, 1).Should().Be(new UInt256(100));
    }

    [Fact]
    public void SafeTransferFrom_RevertsWhenPolicyDenies()
    {
        // Sanction Alice
        _host.SetCaller(_admin);
        Context.Self = _sanctionsAddr;
        _sanctions.AddSanction(_alice);

        // Register policy
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);

        var msg = _host.ExpectRevert(() => _token.SafeTransferFrom(_admin, _alice, 1, new UInt256(100)));
        msg.Should().Contain("transfer denied");
    }

    [Fact]
    public void SafeBatchTransferFrom_ChecksAllItems()
    {
        // Mint token ID 2
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.Mint(_admin, 2, new UInt256(500), "");

        // Sanction Bob
        Context.Self = _sanctionsAddr;
        _sanctions.AddSanction(_bob);

        // Register policy
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);

        // Batch transfer to sanctioned Bob should fail
        var msg = _host.ExpectRevert(() =>
            _token.SafeBatchTransferFrom(_admin, _bob, new ulong[] { 1, 2 }, new ulong[] { 10, 20 }));
        msg.Should().Contain("transfer denied");
    }

    [Fact]
    public void PolicyManagement_AdminOnly()
    {
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        var msg = _host.ExpectRevert(() => _token.AddPolicy(_sanctionsAddr));
        msg.Should().Contain("not owner");
    }

    public void Dispose() => _host.Dispose();
}
