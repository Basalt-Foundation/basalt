using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

public class LockupPolicyTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _policyAddr = BasaltTestHost.CreateAddress(0xF1);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xF2);
    private readonly LockupPolicy _policy;

    public LockupPolicyTests()
    {
        _host = new BasaltTestHost();
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy = new LockupPolicy();
        _host.Deploy(_policyAddr, _policy);
        Context.IsDeploying = false;
    }

    [Fact]
    public void SetLockup_StoresUnlockTimestamp()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetLockup(_tokenAddr, _alice, 2_000_000);

        _policy.GetUnlockTime(_tokenAddr, _alice).Should().Be(2_000_000);
    }

    [Fact]
    public void RemoveLockup_ClearsLockup()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetLockup(_tokenAddr, _alice, 2_000_000);
        _policy.RemoveLockup(_tokenAddr, _alice);

        _policy.GetUnlockTime(_tokenAddr, _alice).Should().Be(0);
    }

    [Fact]
    public void IsLocked_ReturnsTrueBeforeUnlock()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _host.SetBlockTimestamp(1_000_000);
        _policy.SetLockup(_tokenAddr, _alice, 2_000_000);

        _policy.IsLocked(_tokenAddr, _alice).Should().BeTrue();
    }

    [Fact]
    public void IsLocked_ReturnsFalseAfterUnlock()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetLockup(_tokenAddr, _alice, 2_000_000);

        _host.SetBlockTimestamp(2_000_001);
        _policy.IsLocked(_tokenAddr, _alice).Should().BeFalse();
    }

    [Fact]
    public void CheckTransfer_AllowsWhenNoLockup()
    {
        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeTrue();
    }

    [Fact]
    public void CheckTransfer_DeniesWhenLocked()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _host.SetBlockTimestamp(1_000_000);
        _policy.SetLockup(_tokenAddr, _alice, 2_000_000);

        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeFalse();
    }

    [Fact]
    public void CheckTransfer_AllowsAfterUnlock()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _policy.SetLockup(_tokenAddr, _alice, 2_000_000);

        _host.SetBlockTimestamp(2_000_000);
        _policy.CheckTransfer(_tokenAddr, _alice, _bob, new UInt256(100)).Should().BeTrue();
    }

    [Fact]
    public void CheckNftTransfer_DeniesWhenLocked()
    {
        _host.SetCaller(_admin);
        Context.Self = _policyAddr;
        _host.SetBlockTimestamp(1_000_000);
        _policy.SetLockup(_tokenAddr, _alice, 2_000_000);

        _policy.CheckNftTransfer(_tokenAddr, _alice, _bob, 1).Should().BeFalse();
    }

    [Fact]
    public void SetLockup_RevertsForNonAdmin()
    {
        _host.SetCaller(_alice);
        Context.Self = _policyAddr;
        var msg = _host.ExpectRevert(() => _policy.SetLockup(_tokenAddr, _bob, 2_000_000));
        msg.Should().Contain("not admin");
    }

    public void Dispose() => _host.Dispose();
}
