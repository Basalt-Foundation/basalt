using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

/// <summary>
/// End-to-end test: deploy a BST-20 token with multiple policies
/// (sanctions + lockup), demonstrate the full compliance lifecycle.
/// </summary>
public class EndToEndPolicyTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _charlie = BasaltTestHost.CreateAddress(4);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xA0);
    private readonly byte[] _sanctionsAddr = BasaltTestHost.CreateAddress(0xA1);
    private readonly byte[] _lockupAddr = BasaltTestHost.CreateAddress(0xA2);
    private readonly BST20Token _token;
    private readonly SanctionsPolicy _sanctions;
    private readonly LockupPolicy _lockup;

    public EndToEndPolicyTests()
    {
        _host = new BasaltTestHost();
        _host.SetBlockTimestamp(1_000_000);

        // Deploy token with 1M supply
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token = new BST20Token("ComplianceToken", "CMPL", 18, new UInt256(1_000_000));
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
    public void FullComplianceLifecycle()
    {
        // --- Step 1: Register both policies ---
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);
        _token.AddPolicy(_lockupAddr);
        _token.PolicyCount().Should().Be(2);

        // --- Step 2: Distribute tokens ---
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_alice, new UInt256(10_000)));
        _host.Call(() => _token.Transfer(_bob, new UInt256(10_000)));
        _token.BalanceOf(_alice).Should().Be(new UInt256(10_000));

        // --- Step 3: Normal transfers work ---
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_charlie, new UInt256(100)));
        _token.BalanceOf(_charlie).Should().Be(new UInt256(100));

        // --- Step 4: Sanction Charlie --- transfers to Charlie blocked ---
        _host.SetCaller(_admin);
        Context.Self = _sanctionsAddr;
        _sanctions.AddSanction(_charlie);

        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        var msg = _host.ExpectRevert(() => _token.Transfer(_charlie, new UInt256(50)));
        msg.Should().Contain("transfer denied");

        // --- Step 5: Set lockup on Bob ---
        _host.SetCaller(_admin);
        Context.Self = _lockupAddr;
        _lockup.SetLockup(_tokenAddr, _bob, 2_000_000); // Unlocks at t=2M

        // Bob can receive tokens
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_bob, new UInt256(200)));

        // Bob cannot send tokens (locked)
        _host.SetCaller(_bob);
        Context.Self = _tokenAddr;
        msg = _host.ExpectRevert(() => _token.Transfer(_alice, new UInt256(50)));
        msg.Should().Contain("transfer denied");

        // --- Step 6: Time passes, lockup expires ---
        _host.SetBlockTimestamp(2_000_001);

        _host.SetCaller(_bob);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_alice, new UInt256(50)));
        _token.BalanceOf(_alice).Should().Be(new UInt256(9_750)); // 10000 - 100 - 200 + 50

        // --- Step 7: Remove sanctions, Charlie can receive again ---
        _host.SetCaller(_admin);
        Context.Self = _sanctionsAddr;
        _sanctions.RemoveSanction(_charlie);

        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_charlie, new UInt256(25)));
        _token.BalanceOf(_charlie).Should().Be(new UInt256(125));

        // --- Step 8: Remove all policies, no restrictions ---
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.RemovePolicy(_sanctionsAddr);
        _token.RemovePolicy(_lockupAddr);
        _token.PolicyCount().Should().Be(0);
    }

    [Fact]
    public void PolicyEvents_EmittedCorrectly()
    {
        _host.ClearEvents();

        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);

        var addEvents = _host.GetEvents<PolicyAddedEvent>().ToList();
        addEvents.Should().HaveCount(1);
        addEvents[0].Policy.Should().BeEquivalentTo(_sanctionsAddr);

        _host.ClearEvents();
        _token.RemovePolicy(_sanctionsAddr);

        var removeEvents = _host.GetEvents<PolicyRemovedEvent>().ToList();
        removeEvents.Should().HaveCount(1);
        removeEvents[0].Policy.Should().BeEquivalentTo(_sanctionsAddr);
    }

    public void Dispose() => _host.Dispose();
}
