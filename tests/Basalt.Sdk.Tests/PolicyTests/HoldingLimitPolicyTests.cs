using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

public class HoldingLimitPolicyTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _policyAddr = BasaltTestHost.CreateAddress(0xF1);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xF2);
    private readonly HoldingLimitPolicy _policy;
    private readonly BST20Token _token;

    public HoldingLimitPolicyTests()
    {
        _host = new BasaltTestHost();
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy = new HoldingLimitPolicy();
        Context.Self = _tokenAddr;
        _token = new BST20Token("Test", "TST", 18, new UInt256(1_000_000));
        _host.Deploy(_policyAddr, _policy);
        _host.Deploy(_tokenAddr, _token);
        Context.IsDeploying = false;
    }

    [Fact]
    public void SetDefaultLimit_StoresLimit()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetDefaultLimit(_tokenAddr, new UInt256(500));

        var limit = _policy.GetEffectiveLimit(_tokenAddr, _alice);
        limit.Should().Be(new UInt256(500));
    }

    [Fact]
    public void SetAddressLimit_OverridesDefault()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetDefaultLimit(_tokenAddr, new UInt256(500));
        _policy.SetAddressLimit(_tokenAddr, _alice, new UInt256(100));

        _policy.GetEffectiveLimit(_tokenAddr, _alice).Should().Be(new UInt256(100));
        _policy.GetEffectiveLimit(_tokenAddr, _bob).Should().Be(new UInt256(500));
    }

    [Fact]
    public void CheckTransfer_AllowsUnderLimit()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetDefaultLimit(_tokenAddr, new UInt256(1_000_000));

        var result = _host.Call(() => _policy.CheckTransfer(_tokenAddr, _admin, _alice, new UInt256(100)));
        result.Should().BeTrue();
    }

    [Fact]
    public void CheckTransfer_DeniesOverLimit()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetDefaultLimit(_tokenAddr, new UInt256(50));

        // Admin has 1M tokens, transfer 100 to Alice who has 0 — but limit is 50
        var result = _host.Call(() => _policy.CheckTransfer(_tokenAddr, _admin, _alice, new UInt256(100)));
        result.Should().BeFalse();
    }

    [Fact]
    public void CheckTransfer_AllowsWhenNoLimitConfigured()
    {
        Context.Self = _policyAddr;
        var result = _host.Call(() => _policy.CheckTransfer(_tokenAddr, _admin, _alice, new UInt256(999)));
        result.Should().BeTrue();
    }

    [Fact]
    public void SetDefaultLimit_RevertsForNonAdmin()
    {
        _host.SetCaller(_alice);
        Context.Self = _policyAddr;
        var msg = _host.ExpectRevert(() => _policy.SetDefaultLimit(_tokenAddr, new UInt256(100)));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void SetAddressLimit_RevertsForNonAdmin()
    {
        _host.SetCaller(_alice);
        Context.Self = _policyAddr;
        var msg = _host.ExpectRevert(() => _policy.SetAddressLimit(_tokenAddr, _bob, new UInt256(100)));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void CheckTransfer_DeniesWhenBalancePlusAmountOverflows()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetDefaultLimit(_tokenAddr, new UInt256(100));

        // Transfer a huge amount that would overflow when added to any balance
        var result = _host.Call(() =>
            _policy.CheckTransfer(_tokenAddr, _admin, _alice, UInt256.MaxValue));
        result.Should().BeFalse();
    }

    public void Dispose() => _host.Dispose();
}
