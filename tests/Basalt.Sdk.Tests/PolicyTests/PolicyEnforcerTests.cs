using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

public class PolicyEnforcerTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xF0);
    private readonly byte[] _policyAddr1 = BasaltTestHost.CreateAddress(0xF1);
    private readonly byte[] _policyAddr2 = BasaltTestHost.CreateAddress(0xF2);
    private readonly PolicyEnforcer _enforcer;

    public PolicyEnforcerTests()
    {
        _host = new BasaltTestHost();
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _enforcer = new PolicyEnforcer("test_pol");
        Context.IsDeploying = false;
    }

    [Fact]
    public void AddPolicy_IncrementsCount()
    {
        _enforcer.AddPolicy(_policyAddr1);
        _enforcer.Count.Should().Be(1);

        _enforcer.AddPolicy(_policyAddr2);
        _enforcer.Count.Should().Be(2);
    }

    [Fact]
    public void AddPolicy_RejectsDuplicates()
    {
        _enforcer.AddPolicy(_policyAddr1);
        var msg = _host.ExpectRevert(() => _enforcer.AddPolicy(_policyAddr1));
        msg.Should().Contain("already registered");
    }

    [Fact]
    public void AddPolicy_RejectsInvalidAddress()
    {
        var msg = _host.ExpectRevert(() => _enforcer.AddPolicy(new byte[10]));
        msg.Should().Contain("invalid address");
    }

    [Fact]
    public void RemovePolicy_DecrementsAndShifts()
    {
        _enforcer.AddPolicy(_policyAddr1);
        _enforcer.AddPolicy(_policyAddr2);

        _enforcer.RemovePolicy(_policyAddr1);
        _enforcer.Count.Should().Be(1);

        // Second policy should now be at index 0
        _enforcer.GetPolicy(0).Should().BeEquivalentTo(_policyAddr2);
    }

    [Fact]
    public void RemovePolicy_RevertsForUnregistered()
    {
        var msg = _host.ExpectRevert(() => _enforcer.RemovePolicy(_policyAddr1));
        msg.Should().Contain("not registered");
    }

    [Fact]
    public void GetPolicy_ReturnsCorrectAddress()
    {
        _enforcer.AddPolicy(_policyAddr1);
        _enforcer.GetPolicy(0).Should().BeEquivalentTo(_policyAddr1);
    }

    [Fact]
    public void GetPolicy_RevertsOnOutOfBounds()
    {
        var msg = _host.ExpectRevert(() => _enforcer.GetPolicy(0));
        msg.Should().Contain("index out of bounds");
    }

    [Fact]
    public void EnforceTransfer_PassesWithNoPolicies()
    {
        // Should not revert
        _enforcer.EnforceTransfer(_alice, _bob, new UInt256(100));
    }

    [Fact]
    public void EnforceTransfer_PassesWhenPolicyApproves()
    {
        // Deploy an approving sanctions policy (nothing sanctioned)
        var sanctionsAddr = BasaltTestHost.CreateAddress(0xE0);
        _host.SetCaller(_admin);
        Context.Self = sanctionsAddr;
        Context.IsDeploying = true;
        var sanctions = new SanctionsPolicy();
        _host.Deploy(sanctionsAddr, sanctions);
        Context.IsDeploying = false;

        Context.Self = _tokenAddr;
        _enforcer.AddPolicy(sanctionsAddr);

        // Should not revert — no one is sanctioned
        _enforcer.EnforceTransfer(_alice, _bob, new UInt256(100));
    }

    [Fact]
    public void EnforceTransfer_RevertsWhenPolicyDenies()
    {
        // Deploy a sanctions policy and sanction Alice
        var sanctionsAddr = BasaltTestHost.CreateAddress(0xE0);
        _host.SetCaller(_admin);
        Context.Self = sanctionsAddr;
        Context.IsDeploying = true;
        var sanctions = new SanctionsPolicy();
        _host.Deploy(sanctionsAddr, sanctions);
        Context.IsDeploying = false;

        _host.SetCaller(_admin);
        Context.Self = sanctionsAddr;
        sanctions.AddSanction(_alice);

        Context.Self = _tokenAddr;
        _enforcer.AddPolicy(sanctionsAddr);

        var msg = _host.ExpectRevert(() => _enforcer.EnforceTransfer(_alice, _bob, new UInt256(100)));
        msg.Should().Contain("transfer denied");
    }

    [Fact]
    public void RemovePolicy_AllowsReAddingRemovedPolicy()
    {
        _enforcer.AddPolicy(_policyAddr1);
        _enforcer.RemovePolicy(_policyAddr1);
        _enforcer.Count.Should().Be(0);

        // Should succeed — existence flag was cleared on remove
        _enforcer.AddPolicy(_policyAddr1);
        _enforcer.Count.Should().Be(1);
    }

    public void Dispose() => _host.Dispose();
}
