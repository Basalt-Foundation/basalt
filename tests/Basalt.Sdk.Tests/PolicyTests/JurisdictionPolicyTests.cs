using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

public class JurisdictionPolicyTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _policyAddr = BasaltTestHost.CreateAddress(0xF1);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xF2);
    private readonly JurisdictionPolicy _policy;

    public JurisdictionPolicyTests()
    {
        _host = new BasaltTestHost();
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy = new JurisdictionPolicy();
        _host.Deploy(_policyAddr, _policy);
        Context.IsDeploying = false;
    }

    [Fact]
    public void WhitelistMode_AllowsListedJurisdiction()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetMode(_tokenAddr, true); // whitelist
        _policy.SetJurisdiction(_tokenAddr, 840, true); // US allowed
        _policy.SetAddressJurisdiction(_alice, 840);
        _policy.SetAddressJurisdiction(_bob, 840);

        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeTrue();
    }

    [Fact]
    public void WhitelistMode_DeniesUnlistedJurisdiction()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetMode(_tokenAddr, true); // whitelist
        _policy.SetJurisdiction(_tokenAddr, 840, true); // US allowed
        _policy.SetAddressJurisdiction(_alice, 840);
        _policy.SetAddressJurisdiction(_bob, 410); // South Korea, not listed

        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeFalse();
    }

    [Fact]
    public void BlacklistMode_DeniesListedJurisdiction()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetMode(_tokenAddr, false); // blacklist
        _policy.SetJurisdiction(_tokenAddr, 408, true); // North Korea blocked
        _policy.SetAddressJurisdiction(_alice, 408);

        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeFalse();
    }

    [Fact]
    public void BlacklistMode_AllowsUnlistedJurisdiction()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetMode(_tokenAddr, false); // blacklist
        _policy.SetJurisdiction(_tokenAddr, 408, true); // North Korea blocked
        _policy.SetAddressJurisdiction(_alice, 840); // US
        _policy.SetAddressJurisdiction(_bob, 276); // Germany

        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeTrue();
    }

    [Fact]
    public void NoJurisdictionRegistered_AllowsByDefault()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetMode(_tokenAddr, true); // whitelist

        // Neither Alice nor Bob has a registered jurisdiction
        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeTrue();
    }

    [Fact]
    public void GetAddressJurisdiction_ReturnsStoredValue()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetAddressJurisdiction(_alice, 840);

        _policy.GetAddressJurisdiction(_alice).Should().Be(840);
    }

    [Fact]
    public void CheckTransfer_DeniesReceiverInBlockedJurisdiction()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetMode(_tokenAddr, false); // blacklist
        _policy.SetJurisdiction(_tokenAddr, 408, true); // blocked
        _policy.SetAddressJurisdiction(_bob, 408);

        // Sender has no jurisdiction (allowed), receiver blocked
        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeFalse();
    }

    [Fact]
    public void SetMode_RevertsForNonAdmin()
    {
        _host.SetCaller(_alice);
        Context.Self = _policyAddr;
        var msg = _host.ExpectRevert(() => _policy.SetMode(_tokenAddr, true));
        msg.Should().Contain("not admin");
    }

    public void Dispose() => _host.Dispose();
}
