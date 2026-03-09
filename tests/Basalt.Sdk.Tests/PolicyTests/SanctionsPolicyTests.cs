using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

public class SanctionsPolicyTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _policyAddr = BasaltTestHost.CreateAddress(0xF1);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xF2);
    private readonly SanctionsPolicy _policy;

    public SanctionsPolicyTests()
    {
        _host = new BasaltTestHost();
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy = new SanctionsPolicy();
        _host.Deploy(_policyAddr, _policy);
        Context.IsDeploying = false;
    }

    [Fact]
    public void AddSanction_MarksSanctioned()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.AddSanction(_alice);

        _policy.IsSanctioned(_alice).Should().BeTrue();
        _policy.IsSanctioned(_bob).Should().BeFalse();
    }

    [Fact]
    public void RemoveSanction_ClearsSanction()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.AddSanction(_alice);
        _policy.RemoveSanction(_alice);

        _policy.IsSanctioned(_alice).Should().BeFalse();
    }

    [Fact]
    public void CheckTransfer_DeniesFromSanctioned()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.AddSanction(_alice);

        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeFalse();
    }

    [Fact]
    public void CheckTransfer_DeniesToSanctioned()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.AddSanction(_bob);

        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeFalse();
    }

    [Fact]
    public void CheckTransfer_AllowsUnsanctioned()
    {
        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeTrue();
    }

    [Fact]
    public void CheckNftTransfer_DeniesFromSanctioned()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.AddSanction(_alice);

        _policy.CheckNftTransfer(_tokenAddr, _alice, _bob, 1).Should().BeFalse();
    }

    [Fact]
    public void AddSanction_RevertsForNonAdmin()
    {
        _host.SetCaller(_alice);
        Context.Self = _policyAddr;
        var msg = _host.ExpectRevert(() => _policy.AddSanction(_bob));
        msg.Should().Contain("not admin");
    }

    public void Dispose() => _host.Dispose();
}
