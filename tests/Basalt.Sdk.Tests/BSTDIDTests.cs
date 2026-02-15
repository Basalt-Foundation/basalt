using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Comprehensive tests for BST-DID Decentralized Identity Registry.
/// </summary>
public class BSTDIDTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BSTDIDRegistry _registry;
    private readonly byte[] _controller;
    private readonly byte[] _otherUser;
    private readonly byte[] _issuer;
    private readonly byte[] _newController;

    public BSTDIDTests()
    {
        _registry = new BSTDIDRegistry();
        _controller = BasaltTestHost.CreateAddress(1);
        _otherUser = BasaltTestHost.CreateAddress(2);
        _issuer = BasaltTestHost.CreateAddress(3);
        _newController = BasaltTestHost.CreateAddress(4);
        _host.SetCaller(_controller);
    }

    // --- RegisterDID ---

    [Fact]
    public void RegisterDID_CreatesDocument()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        did.Should().StartWith("did:basalt:");
        did.Should().NotBeNullOrEmpty();

        var doc = _host.Call(() => _registry.ResolveDID(did));
        doc.Should().NotBeNull();
        doc!.Id.Should().Be(did);
        doc.Controller.Should().BeEquivalentTo(_controller);
        doc.Active.Should().BeTrue();
    }

    [Fact]
    public void RegisterDID_IncrementsIndex()
    {
        _host.SetCaller(_controller);
        var did0 = _host.Call(() => _registry.RegisterDID(_controller));
        var did1 = _host.Call(() => _registry.RegisterDID(_otherUser));

        did0.Should().NotBe(did1);
        did0.Should().StartWith("did:basalt:");
        did1.Should().StartWith("did:basalt:");
    }

    [Fact]
    public void RegisterDID_EmitsDIDRegisteredEvent()
    {
        _host.ClearEvents();
        _host.SetCaller(_controller);
        _host.Call(() => _registry.RegisterDID(_controller));

        var events = _host.GetEvents<DIDRegisteredEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].DID.Should().StartWith("did:basalt:");
        events[0].Controller.Should().BeEquivalentTo(_controller);
    }

    [Fact]
    public void RegisterDID_CustomChainPrefix()
    {
        var customRegistry = new BSTDIDRegistry("did:custom:");
        _host.SetCaller(_controller);
        var did = _host.Call(() => customRegistry.RegisterDID(_controller));

        did.Should().StartWith("did:custom:");
    }

    // --- ResolveDID ---

    [Fact]
    public void ResolveDID_ReturnsDocument()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        var doc = _host.Call(() => _registry.ResolveDID(did));

        doc.Should().NotBeNull();
        doc!.Id.Should().Be(did);
        doc.Controller.Should().BeEquivalentTo(_controller);
        doc.Active.Should().BeTrue();
    }

    [Fact]
    public void ResolveDID_ReturnsNullForUnknownDID()
    {
        var doc = _host.Call(() => _registry.ResolveDID("did:basalt:unknown"));
        doc.Should().BeNull();
    }

    [Fact]
    public void ResolveDID_AfterDeactivation_ShowsInactive()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));
        _host.Call(() => _registry.DeactivateDID(did));

        var doc = _host.Call(() => _registry.ResolveDID(did));
        doc.Should().NotBeNull();
        doc!.Active.Should().BeFalse();
    }

    // --- AddAttestation / AddVerificationMethod ---

    [Fact]
    public void AddAttestation_ByController_Succeeds()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetBlockHeight(100);
        _host.Call(() => _registry.AddAttestation(
            did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1, 2, 3 }));

        _host.Call(() => _registry.HasValidAttestation(did, "KYC")).Should().BeTrue();
    }

    [Fact]
    public void AddAttestation_ByIssuer_Succeeds()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetCaller(_issuer);
        _host.SetBlockHeight(200);
        _host.Call(() => _registry.AddAttestation(
            did, "AML", Convert.ToHexString(_issuer), 9999, new byte[] { 4, 5 }));

        _host.Call(() => _registry.HasValidAttestation(did, "AML")).Should().BeTrue();
    }

    [Fact]
    public void AddAttestation_ByUnauthorized_Reverts()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetCaller(_otherUser);
        var msg = _host.ExpectRevert(() =>
            _registry.AddAttestation(did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));
        msg.Should().Contain("not authorized");
    }

    [Fact]
    public void AddAttestation_ForNonExistentDID_Reverts()
    {
        _host.SetCaller(_controller);
        var msg = _host.ExpectRevert(() =>
            _registry.AddAttestation("did:basalt:nonexistent", "KYC",
                Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));
        msg.Should().Contain("DID not found");
    }

    [Fact]
    public void AddAttestation_EmitsAttestationAddedEvent()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));
        _host.ClearEvents();

        _host.SetBlockHeight(42);
        _host.Call(() => _registry.AddAttestation(
            did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));

        var events = _host.GetEvents<AttestationAddedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].DID.Should().Be(did);
        events[0].CredentialType.Should().Be("KYC");
        events[0].AttestationId.Should().Be("KYC:42");
    }

    [Fact]
    public void AddVerificationMethod_MultipleTypes()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetBlockHeight(10);
        _host.Call(() => _registry.AddAttestation(
            did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));
        _host.SetBlockHeight(20);
        _host.Call(() => _registry.AddAttestation(
            did, "AML", Convert.ToHexString(_issuer), 9999, new byte[] { 2 }));
        _host.SetBlockHeight(30);
        _host.Call(() => _registry.AddAttestation(
            did, "ACCREDITATION", Convert.ToHexString(_issuer), 9999, new byte[] { 3 }));

        _host.Call(() => _registry.HasValidAttestation(did, "KYC")).Should().BeTrue();
        _host.Call(() => _registry.HasValidAttestation(did, "AML")).Should().BeTrue();
        _host.Call(() => _registry.HasValidAttestation(did, "ACCREDITATION")).Should().BeTrue();
    }

    // --- RevokeAttestation ---

    [Fact]
    public void RevokeAttestation_InvalidatesCredential()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetBlockHeight(100);
        _host.Call(() => _registry.AddAttestation(
            did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));

        _host.Call(() => _registry.HasValidAttestation(did, "KYC")).Should().BeTrue();

        _host.Call(() => _registry.RevokeAttestation(did, "KYC:100"));

        _host.Call(() => _registry.HasValidAttestation(did, "KYC")).Should().BeFalse();
    }

    [Fact]
    public void RevokeAttestation_ByNonController_Reverts()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetBlockHeight(100);
        _host.Call(() => _registry.AddAttestation(
            did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));

        _host.SetCaller(_otherUser);
        var msg = _host.ExpectRevert(() => _registry.RevokeAttestation(did, "KYC:100"));
        msg.Should().Contain("not controller");
    }

    [Fact]
    public void RevokeAttestation_ForNonExistentDID_Reverts()
    {
        _host.SetCaller(_controller);
        var msg = _host.ExpectRevert(() =>
            _registry.RevokeAttestation("did:basalt:nonexistent", "KYC:100"));
        msg.Should().Contain("DID not found");
    }

    [Fact]
    public void RevokeAttestation_EmitsAttestationRevokedEvent()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetBlockHeight(100);
        _host.Call(() => _registry.AddAttestation(
            did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));
        _host.ClearEvents();

        _host.Call(() => _registry.RevokeAttestation(did, "KYC:100"));

        var events = _host.GetEvents<AttestationRevokedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].DID.Should().Be(did);
        events[0].AttestationId.Should().Be("KYC:100");
    }

    // --- HasValidAttestation ---

    [Fact]
    public void HasValidAttestation_UnknownType_ReturnsFalse()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.Call(() => _registry.HasValidAttestation(did, "UNKNOWN_TYPE")).Should().BeFalse();
    }

    // --- TransferDID ---

    [Fact]
    public void TransferDID_ChangesController()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.Call(() => _registry.TransferDID(did, _newController));

        var doc = _host.Call(() => _registry.ResolveDID(did));
        doc!.Controller.Should().BeEquivalentTo(_newController);
    }

    [Fact]
    public void TransferDID_OldControllerCannotActAnymore()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.Call(() => _registry.TransferDID(did, _newController));

        // Old controller tries to deactivate - should fail
        var msg = _host.ExpectRevert(() => _registry.DeactivateDID(did));
        msg.Should().Contain("not controller");
    }

    [Fact]
    public void TransferDID_NewControllerCanAct()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));
        _host.Call(() => _registry.TransferDID(did, _newController));

        // New controller can deactivate
        _host.SetCaller(_newController);
        _host.Call(() => _registry.DeactivateDID(did));

        var doc = _host.Call(() => _registry.ResolveDID(did));
        doc!.Active.Should().BeFalse();
    }

    [Fact]
    public void TransferDID_ByNonController_Reverts()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetCaller(_otherUser);
        var msg = _host.ExpectRevert(() => _registry.TransferDID(did, _newController));
        msg.Should().Contain("not controller");
    }

    [Fact]
    public void TransferDID_NonExistentDID_Reverts()
    {
        _host.SetCaller(_controller);
        var msg = _host.ExpectRevert(() =>
            _registry.TransferDID("did:basalt:nonexistent", _newController));
        msg.Should().Contain("DID not found");
    }

    // --- DeactivateDID ---

    [Fact]
    public void DeactivateDID_SetsActiveToFalse()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.Call(() => _registry.DeactivateDID(did));

        var doc = _host.Call(() => _registry.ResolveDID(did));
        doc!.Active.Should().BeFalse();
    }

    [Fact]
    public void DeactivateDID_ByNonController_Reverts()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetCaller(_otherUser);
        var msg = _host.ExpectRevert(() => _registry.DeactivateDID(did));
        msg.Should().Contain("not controller");
    }

    [Fact]
    public void DeactivateDID_NonExistentDID_Reverts()
    {
        _host.SetCaller(_controller);
        var msg = _host.ExpectRevert(() =>
            _registry.DeactivateDID("did:basalt:nonexistent"));
        msg.Should().Contain("DID not found");
    }

    // --- Full lifecycle ---

    [Fact]
    public void FullLifecycle_Register_Attest_Revoke_Transfer_Deactivate()
    {
        // Register
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));
        _host.Call(() => _registry.ResolveDID(did))!.Active.Should().BeTrue();

        // Add attestation
        _host.SetBlockHeight(50);
        _host.Call(() => _registry.AddAttestation(
            did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1, 2 }));
        _host.Call(() => _registry.HasValidAttestation(did, "KYC")).Should().BeTrue();

        // Revoke attestation
        _host.Call(() => _registry.RevokeAttestation(did, "KYC:50"));
        _host.Call(() => _registry.HasValidAttestation(did, "KYC")).Should().BeFalse();

        // Transfer to new controller
        _host.Call(() => _registry.TransferDID(did, _newController));
        _host.Call(() => _registry.ResolveDID(did))!.Controller
            .Should().BeEquivalentTo(_newController);

        // New controller deactivates
        _host.SetCaller(_newController);
        _host.Call(() => _registry.DeactivateDID(did));
        _host.Call(() => _registry.ResolveDID(did))!.Active.Should().BeFalse();
    }

    public void Dispose() => _host.Dispose();
}
