using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class IssuerRegistryTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly IssuerRegistry _registry;
    private readonly byte[] _admin;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    public IssuerRegistryTests()
    {
        _admin = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);

        // Admin deploys the IssuerRegistry -- must set caller before construction
        // because the constructor writes Context.Caller as admin.
        _host.SetCaller(_admin);
        _registry = new IssuerRegistry();
    }

    [Fact]
    public void RegisterIssuer_Tier0_AnyoneCanRegister()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice Issuer", 0));

        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeTrue();
        _host.Call(() => _registry.GetIssuerTier(_alice)).Should().Be(0);
    }

    [Fact]
    public void GetIssuerTier_ReturnsCorrectTier()
    {
        // Tier 0
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice T0", 0));
        _host.Call(() => _registry.GetIssuerTier(_alice)).Should().Be(0);

        // Tier 2
        _host.SetCaller(_bob);
        _host.Call(() => _registry.RegisterIssuer("Bob T2", 2));
        _host.Call(() => _registry.GetIssuerTier(_bob)).Should().Be(2);
    }

    [Fact]
    public void IsActiveIssuer_ReturnsTrueAfterRegister()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice Active", 0));

        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeTrue();
    }

    [Fact]
    public void IsActiveIssuer_ReturnsFalseForUnregistered()
    {
        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeFalse();
    }

    [Fact]
    public void StakeCollateral_IncreasesStake()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice Staker", 2));

        Context.TxValue = 5000;
        _host.Call(() => _registry.StakeCollateral());

        _host.Call(() => _registry.GetCollateralStake(_alice)).Should().Be(5000);

        // Stake again to accumulate
        Context.TxValue = 3000;
        _host.Call(() => _registry.StakeCollateral());

        _host.Call(() => _registry.GetCollateralStake(_alice)).Should().Be(8000);
    }

    [Fact]
    public void StakeCollateral_ZeroValue_Fails()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));

        Context.TxValue = 0;
        var msg = _host.ExpectRevert(() => _registry.StakeCollateral());
        msg.Should().Contain("must send value");
    }

    [Fact]
    public void StakeCollateral_Unregistered_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 1000;
        var msg = _host.ExpectRevert(() => _registry.StakeCollateral());
        msg.Should().Contain("not registered");
    }

    [Fact]
    public void StakeCollateral_EmitsCollateralStakedEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 2));
        _host.ClearEvents();

        Context.TxValue = 7000;
        _host.Call(() => _registry.StakeCollateral());

        var events = _host.GetEvents<CollateralStakedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Issuer.Should().BeEquivalentTo(_alice);
        events[0].Amount.Should().Be(7000);
        events[0].TotalStake.Should().Be(7000);
    }

    [Fact]
    public void UpdateRevocationRoot_Succeeds()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice Revoker", 0));

        var root = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        _host.Call(() => _registry.UpdateRevocationRoot(root));

        _host.Call(() => _registry.GetRevocationRoot(_alice))
            .Should().Be(Convert.ToHexString(root));
    }

    [Fact]
    public void UpdateRevocationRoot_Unregistered_Fails()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.UpdateRevocationRoot(new byte[] { 0x01 }));
        msg.Should().Contain("not registered");
    }

    [Fact]
    public void UpdateRevocationRoot_EmitsEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));
        _host.ClearEvents();

        _host.Call(() => _registry.UpdateRevocationRoot(new byte[] { 0xAA }));

        var events = _host.GetEvents<RevocationRootUpdatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Issuer.Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void AddSchemaSupport_Succeeds()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice Schemas", 0));

        var schemaId = new byte[] { 0x01, 0x02, 0x03 };
        _host.Call(() => _registry.AddSchemaSupport(schemaId));

        _host.Call(() => _registry.SupportsSchema(_alice, schemaId)).Should().BeTrue();
    }

    [Fact]
    public void AddSchemaSupport_Unregistered_Fails()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.AddSchemaSupport(new byte[] { 0x01 }));
        msg.Should().Contain("not registered");
    }

    [Fact]
    public void RemoveSchemaSupport_RemovesFlag()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));

        var schemaId = new byte[] { 0x01, 0x02, 0x03 };
        _host.Call(() => _registry.AddSchemaSupport(schemaId));
        _host.Call(() => _registry.SupportsSchema(_alice, schemaId)).Should().BeTrue();

        _host.Call(() => _registry.RemoveSchemaSupport(schemaId));
        _host.Call(() => _registry.SupportsSchema(_alice, schemaId)).Should().BeFalse();
    }

    [Fact]
    public void DeactivateIssuer_SelfDeactivates()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice Deactivate", 0));
        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeTrue();

        _host.Call(() => _registry.DeactivateIssuer(_alice));

        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeFalse();
    }

    [Fact]
    public void DeactivateIssuer_ByAdmin_Succeeds()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));

        _host.SetCaller(_admin);
        _host.Call(() => _registry.DeactivateIssuer(_alice));

        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeFalse();
    }

    [Fact]
    public void DeactivateIssuer_ByUnauthorized_Fails()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _registry.DeactivateIssuer(_alice));
        msg.Should().Contain("not authorized");
    }

    [Fact]
    public void DeactivateIssuer_EmitsEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));
        _host.ClearEvents();

        _host.Call(() => _registry.DeactivateIssuer(_alice));

        var events = _host.GetEvents<IssuerDeactivatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Issuer.Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void SlashIssuer_BurnsCollateral()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice Slashed", 2));

        Context.TxValue = 10000;
        _host.Call(() => _registry.StakeCollateral());
        Context.TxValue = 0;

        _host.Call(() => _registry.GetCollateralStake(_alice)).Should().Be(10000);

        _host.SetCaller(_admin);
        _host.Call(() => _registry.SlashIssuer(_alice, "fraud"));

        _host.Call(() => _registry.GetCollateralStake(_alice)).Should().Be(0);
        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeFalse();
    }

    [Fact]
    public void SlashIssuer_EmitsEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 2));

        Context.TxValue = 5000;
        _host.Call(() => _registry.StakeCollateral());
        Context.TxValue = 0;

        _host.SetCaller(_admin);
        _host.ClearEvents();
        _host.Call(() => _registry.SlashIssuer(_alice, "misconduct"));

        var events = _host.GetEvents<IssuerSlashedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Issuer.Should().BeEquivalentTo(_alice);
        events[0].Reason.Should().Be("misconduct");
        events[0].SlashedAmount.Should().Be(5000);
    }

    [Fact]
    public void SlashIssuer_ByNonAdmin_Fails()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _registry.SlashIssuer(_alice, "reason"));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void GetIssuerName_ReturnsName()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice Identity Provider", 0));

        _host.Call(() => _registry.GetIssuerName(_alice)).Should().Be("Alice Identity Provider");
    }

    [Fact]
    public void GetIssuerName_Unregistered_ReturnsEmpty()
    {
        _host.Call(() => _registry.GetIssuerName(_alice)).Should().Be("");
    }

    [Fact]
    public void GetAdmin_ReturnsDeployer()
    {
        _host.Call(() => _registry.GetAdmin())
            .Should().Be(Convert.ToHexString(_admin));
    }

    [Fact]
    public void ReactivateIssuer_AdminOnly()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice Reactivate", 0));
        _host.Call(() => _registry.DeactivateIssuer(_alice));
        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeFalse();

        // Non-admin cannot reactivate
        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _registry.ReactivateIssuer(_alice));
        msg.Should().Contain("not admin");

        // Admin can reactivate
        _host.SetCaller(_admin);
        _host.Call(() => _registry.ReactivateIssuer(_alice));

        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeTrue();
    }

    [Fact]
    public void ReactivateIssuer_EmitsEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));
        _host.Call(() => _registry.DeactivateIssuer(_alice));

        _host.SetCaller(_admin);
        _host.ClearEvents();
        _host.Call(() => _registry.ReactivateIssuer(_alice));

        var events = _host.GetEvents<IssuerReactivatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Issuer.Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void RegisterIssuer_Tier1_RequiresAdmin()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.RegisterIssuer("Alice T1", 1));
        msg.Should().Contain("admin only");
    }

    [Fact]
    public void RegisterIssuer_Tier3_RequiresAdmin()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.RegisterIssuer("Alice T3", 3));
        msg.Should().Contain("admin only");
    }

    [Fact]
    public void RegisterIssuer_Tier1_ByAdmin_Succeeds()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _registry.RegisterIssuer("Admin T1 Issuer", 1));

        _host.Call(() => _registry.GetIssuerTier(_admin)).Should().Be(1);
        _host.Call(() => _registry.IsActiveIssuer(_admin)).Should().BeTrue();
    }

    [Fact]
    public void RegisterIssuer_InvalidTier_Fails()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.RegisterIssuer("Alice Bad", 4));
        msg.Should().Contain("invalid tier");
    }

    [Fact]
    public void RegisterIssuer_EmptyName_Fails()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.RegisterIssuer("", 0));
        msg.Should().Contain("name required");
    }

    [Fact]
    public void RegisterIssuer_EmitsIssuerRegisteredEvent()
    {
        _host.SetCaller(_alice);
        _host.ClearEvents();
        _host.Call(() => _registry.RegisterIssuer("Alice Event", 0));

        var events = _host.GetEvents<IssuerRegisteredEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Issuer.Should().BeEquivalentTo(_alice);
        events[0].Name.Should().Be("Alice Event");
        events[0].Tier.Should().Be(0);
    }

    [Fact]
    public void TransferAdmin_NewAdminCanSlash()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _registry.TransferAdmin(_bob));

        _host.Call(() => _registry.GetAdmin()).Should().Be(Convert.ToHexString(_bob));

        // Alice registers
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));

        // New admin (bob) can slash
        _host.SetCaller(_bob);
        _host.Call(() => _registry.SlashIssuer(_alice, "test"));

        _host.Call(() => _registry.IsActiveIssuer(_alice)).Should().BeFalse();
    }

    [Fact]
    public void TransferAdmin_ByNonAdmin_Fails()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.TransferAdmin(_bob));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void StakeCollateral_InactiveIssuer_Fails()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _registry.RegisterIssuer("Alice", 0));
        _host.Call(() => _registry.DeactivateIssuer(_alice));

        Context.TxValue = 1000;
        var msg = _host.ExpectRevert(() => _registry.StakeCollateral());
        msg.Should().Contain("not active");
    }

    [Fact]
    public void GetRevocationRoot_Unregistered_ReturnsEmpty()
    {
        _host.Call(() => _registry.GetRevocationRoot(_alice)).Should().Be("");
    }

    [Fact]
    public void GetCollateralStake_Unregistered_ReturnsZero()
    {
        _host.Call(() => _registry.GetCollateralStake(_alice)).Should().Be(0);
    }

    public void Dispose() => _host.Dispose();
}
