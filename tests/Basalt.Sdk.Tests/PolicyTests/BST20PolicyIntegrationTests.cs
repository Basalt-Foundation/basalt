using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

public class BST20PolicyIntegrationTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xF0);
    private readonly byte[] _sanctionsAddr = BasaltTestHost.CreateAddress(0xF1);
    private readonly byte[] _lockupAddr = BasaltTestHost.CreateAddress(0xF2);
    private readonly BST20Token _token;
    private readonly SanctionsPolicy _sanctions;
    private readonly LockupPolicy _lockup;

    public BST20PolicyIntegrationTests()
    {
        _host = new BasaltTestHost();

        // Deploy token
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token = new BST20Token("Test", "TST", 18, new UInt256(1_000_000));
        _host.Deploy(_tokenAddr, _token);

        // Deploy sanctions policy
        Context.Self = _sanctionsAddr;
        _sanctions = new SanctionsPolicy();
        _host.Deploy(_sanctionsAddr, _sanctions);

        // Deploy lockup policy
        Context.Self = _lockupAddr;
        _lockup = new LockupPolicy();
        _host.Deploy(_lockupAddr, _lockup);

        Context.IsDeploying = false;
    }

    [Fact]
    public void Transfer_SucceedsWithNoPolicies()
    {
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        var result = _host.Call(() => _token.Transfer(_alice, new UInt256(100)));
        result.Should().BeTrue();
    }

    [Fact]
    public void Transfer_SucceedsWhenPolicyApproves()
    {
        // Register sanctions policy (no one sanctioned)
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);

        var result = _host.Call(() => _token.Transfer(_alice, new UInt256(100)));
        result.Should().BeTrue();
    }

    [Fact]
    public void Transfer_RevertsWhenPolicyDenies()
    {
        // Sanction Bob
        _host.SetCaller(_admin);
        Context.Self = _sanctionsAddr;
        _sanctions.AddSanction(_bob);

        // Register sanctions policy on token
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);

        // Transfer to sanctioned Bob should revert
        var msg = _host.ExpectRevert(() => _token.Transfer(_bob, new UInt256(100)));
        msg.Should().Contain("transfer denied");
    }

    [Fact]
    public void TransferFrom_RespectsPolicies()
    {
        // Give Alice some tokens and Bob approval
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.Transfer(_alice, new UInt256(500));

        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _token.Approve(_bob, new UInt256(500));

        // Sanction Alice (sender in TransferFrom)
        _host.SetCaller(_admin);
        Context.Self = _sanctionsAddr;
        _sanctions.AddSanction(_alice);

        // Register policy
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);

        // Bob tries TransferFrom Alice (sanctioned) to himself
        _host.SetCaller(_bob);
        Context.Self = _tokenAddr;
        var msg = _host.ExpectRevert(() => _token.TransferFrom(_alice, _bob, new UInt256(100)));
        msg.Should().Contain("transfer denied");
    }

    [Fact]
    public void AddPolicy_OnlyCallableByAdmin()
    {
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        var msg = _host.ExpectRevert(() => _token.AddPolicy(_sanctionsAddr));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void RemovePolicy_OnlyCallableByAdmin()
    {
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);

        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        var msg = _host.ExpectRevert(() => _token.RemovePolicy(_sanctionsAddr));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void PolicyCount_ReturnsCorrectCount()
    {
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;

        _token.PolicyCount().Should().Be(0);
        _token.AddPolicy(_sanctionsAddr);
        _token.PolicyCount().Should().Be(1);
        _token.AddPolicy(_lockupAddr);
        _token.PolicyCount().Should().Be(2);
    }

    [Fact]
    public void MultiplePolicies_AllMustPass()
    {
        // Set lockup on admin (sender)
        _host.SetCaller(_admin);
        Context.Self = _lockupAddr;
        _host.SetBlockTimestamp(1_000_000);
        _lockup.SetLockup(_tokenAddr, _admin, 2_000_000);

        // Register both policies
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);
        _token.AddPolicy(_lockupAddr);

        // Transfer should fail due to lockup (even though sanctions pass)
        var msg = _host.ExpectRevert(() => _token.Transfer(_alice, new UInt256(100)));
        msg.Should().Contain("transfer denied");
    }

    [Fact]
    public void RemovePolicy_AllowsPreviouslyDeniedTransfer()
    {
        // Sanction Bob
        _host.SetCaller(_admin);
        Context.Self = _sanctionsAddr;
        _sanctions.AddSanction(_bob);

        // Register then remove policy
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);
        _token.RemovePolicy(_sanctionsAddr);

        // Transfer to Bob should now succeed
        var result = _host.Call(() => _token.Transfer(_bob, new UInt256(100)));
        result.Should().BeTrue();
    }

    public void Dispose() => _host.Dispose();
}
