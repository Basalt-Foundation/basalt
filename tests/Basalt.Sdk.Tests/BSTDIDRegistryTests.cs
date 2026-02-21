using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class BSTDIDRegistryTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BSTDIDRegistry _registry;
    private readonly byte[] _controller;
    private readonly byte[] _otherUser;
    private readonly byte[] _issuer;

    public BSTDIDRegistryTests()
    {
        _registry = new BSTDIDRegistry();
        _controller = BasaltTestHost.CreateAddress(1);
        _otherUser = BasaltTestHost.CreateAddress(2);
        _issuer = BasaltTestHost.CreateAddress(3);
        _host.SetCaller(_controller);
    }

    [Fact]
    public void RegisterDID_Returns_DID_String()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        did.Should().StartWith("did:basalt:");
        did.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void RegisterDID_Increments_Index()
    {
        _host.SetCaller(_controller);
        var did0 = _host.Call(() => _registry.RegisterDID(_controller));
        var did1 = _host.Call(() => _registry.RegisterDID(_otherUser));

        did0.Should().NotBe(did1);
    }

    [Fact]
    public void ResolveDID_Returns_Document()
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
    public void ResolveDID_Returns_Null_For_Unknown_DID()
    {
        var doc = _host.Call(() => _registry.ResolveDID("did:basalt:unknown"));
        doc.Should().BeNull();
    }

    [Fact]
    public void AddAttestation_By_Controller()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetBlockHeight(100);
        _host.Call(() => _registry.AddAttestation(did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1, 2, 3 }));

        _host.Call(() => _registry.HasValidAttestation(did, "KYC")).Should().BeTrue();
    }

    [Fact]
    public void AddAttestation_By_Issuer_Reverts()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        // H-9: Only controller can add attestations (issuer param is informational only)
        _host.SetCaller(_issuer);
        _host.SetBlockHeight(100);
        var msg = _host.ExpectRevert(() =>
            _registry.AddAttestation(did, "AML", Convert.ToHexString(_issuer), 9999, new byte[] { 4, 5 }));
        msg.Should().Contain("not controller");
    }

    [Fact]
    public void AddAttestation_Reverts_For_Unauthorized()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        // H-9: Non-controller callers are rejected
        _host.SetCaller(_otherUser);
        var msg = _host.ExpectRevert(() =>
            _registry.AddAttestation(did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));
        msg.Should().Contain("not controller");
    }

    [Fact]
    public void RevokeAttestation_Makes_It_Invalid()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetBlockHeight(100);
        _host.Call(() => _registry.AddAttestation(did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));
        _host.Call(() => _registry.HasValidAttestation(did, "KYC")).Should().BeTrue();

        var attId = "KYC:0"; // M-13: credentialType:attestationIndex (monotonic counter)
        _host.Call(() => _registry.RevokeAttestation(did, attId));

        _host.Call(() => _registry.HasValidAttestation(did, "KYC")).Should().BeFalse();
    }

    [Fact]
    public void RevokeAttestation_Reverts_For_NonController()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetBlockHeight(100);
        _host.Call(() => _registry.AddAttestation(did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));

        _host.SetCaller(_otherUser);
        var msg = _host.ExpectRevert(() => _registry.RevokeAttestation(did, "KYC:0"));
        msg.Should().Contain("not controller");
    }

    [Fact]
    public void HasValidAttestation_Returns_False_For_Unknown_Type()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.Call(() => _registry.HasValidAttestation(did, "UNKNOWN")).Should().BeFalse();
    }

    [Fact]
    public void TransferDID_Changes_Controller()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.Call(() => _registry.TransferDID(did, _otherUser));

        var doc = _host.Call(() => _registry.ResolveDID(did));
        doc!.Controller.Should().BeEquivalentTo(_otherUser);
    }

    [Fact]
    public void TransferDID_Reverts_For_NonController()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetCaller(_otherUser);
        var msg = _host.ExpectRevert(() => _registry.TransferDID(did, _otherUser));
        msg.Should().Contain("not controller");
    }

    [Fact]
    public void DeactivateDID_Sets_Active_False()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.Call(() => _registry.DeactivateDID(did));

        var doc = _host.Call(() => _registry.ResolveDID(did));
        doc!.Active.Should().BeFalse();
    }

    [Fact]
    public void DeactivateDID_Reverts_For_NonController()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.SetCaller(_otherUser);
        var msg = _host.ExpectRevert(() => _registry.DeactivateDID(did));
        msg.Should().Contain("not controller");
    }

    [Fact]
    public void RegisterDID_Emits_DIDRegisteredEvent()
    {
        _host.SetCaller(_controller);
        _host.Call(() => _registry.RegisterDID(_controller));

        var events = _host.GetEvents<DIDRegisteredEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Controller.Should().BeEquivalentTo(_controller);
    }

    [Fact]
    public void AddAttestation_Emits_AttestationAddedEvent()
    {
        _host.SetCaller(_controller);
        var did = _host.Call(() => _registry.RegisterDID(_controller));

        _host.ClearEvents();
        _host.SetBlockHeight(42);
        _host.Call(() => _registry.AddAttestation(did, "KYC", Convert.ToHexString(_issuer), 9999, new byte[] { 1 }));

        var events = _host.GetEvents<AttestationAddedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].CredentialType.Should().Be("KYC");
        events[0].AttestationId.Should().Be("KYC:0");
    }

    public void Dispose() => _host.Dispose();
}
