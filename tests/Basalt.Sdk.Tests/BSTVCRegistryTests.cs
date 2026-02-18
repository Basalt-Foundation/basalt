using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class BSTVCRegistryTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BSTVCRegistry _registry;
    private readonly byte[] _issuer;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    private readonly byte[] _credHash = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x01 };
    private readonly byte[] _subjectDid = new byte[] { 0xD1, 0xD0, 0x00, 0x01 };
    private readonly byte[] _schemaId = new byte[] { 0x5C, 0x4E, 0x01 };
    private const long FutureExpiry = 2_000_000_000L;
    private const long CurrentTimestamp = 1_000_000_000L;
    private const string MetadataUri = "ipfs://QmTestCredentialMetadata";

    public BSTVCRegistryTests()
    {
        _issuer = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);

        _host.SetBlockTimestamp((ulong)CurrentTimestamp);
        _host.SetCaller(_issuer);
        _registry = new BSTVCRegistry();
    }

    // ---------------------------------------------------------------
    // Helper: issue a credential with default params from _issuer
    // ---------------------------------------------------------------
    private string IssueDefault()
    {
        _host.SetCaller(_issuer);
        return _host.Call(() => _registry.IssueCredential(
            _credHash, _subjectDid, _schemaId, FutureExpiry, MetadataUri));
    }

    // ================================================================
    // 1. IssueCredential — returns credential ID, status = Active, fields stored
    // ================================================================
    [Fact]
    public void BSTVCIssueCredential_ReturnsIdAndSetsStatusActiveAndStoresFields()
    {
        var credId = IssueDefault();

        credId.Should().NotBeNullOrEmpty();
        credId.Should().StartWith("vc:" + Convert.ToHexString(_issuer) + ":");

        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(1); // Active
        _host.Call(() => _registry.GetCredentialIssuer(_credHash))
            .Should().BeEquivalentTo(_issuer);
        _host.Call(() => _registry.GetCredentialSubject(_credHash))
            .Should().BeEquivalentTo(_subjectDid);
        _host.Call(() => _registry.GetCredentialSchema(_credHash))
            .Should().BeEquivalentTo(_schemaId);
        _host.Call(() => _registry.GetCredentialExpiry(_credHash))
            .Should().Be(FutureExpiry);
        _host.Call(() => _registry.GetCredentialMetadataUri(_credHash))
            .Should().Be(MetadataUri);
    }

    // ================================================================
    // 2. IssueCredential — emits CredentialIssuedEvent
    // ================================================================
    [Fact]
    public void BSTVCIssueCredential_EmitsCredentialIssuedEvent()
    {
        _host.ClearEvents();
        var credId = IssueDefault();

        var events = _host.GetEvents<CredentialIssuedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].CredentialHash.Should().BeEquivalentTo(_credHash);
        events[0].Issuer.Should().BeEquivalentTo(_issuer);
        events[0].Subject.Should().BeEquivalentTo(_subjectDid);
        events[0].CredentialId.Should().Be(credId);
    }

    // ================================================================
    // 3. IssueCredential — reverts for duplicate credential hash
    // ================================================================
    [Fact]
    public void BSTVCIssueCredential_DuplicateHash_Reverts()
    {
        IssueDefault();

        var msg = _host.ExpectRevert(() => _registry.IssueCredential(
            _credHash, _subjectDid, _schemaId, FutureExpiry, MetadataUri));
        msg.Should().Contain("already exists");
    }

    // ================================================================
    // 4. IssueCredential — reverts for expired validUntil
    // ================================================================
    [Fact]
    public void BSTVCIssueCredential_ExpiredValidity_Reverts()
    {
        long pastExpiry = CurrentTimestamp - 100;
        var msg = _host.ExpectRevert(() => _registry.IssueCredential(
            _credHash, _subjectDid, _schemaId, pastExpiry, MetadataUri));
        msg.Should().Contain("expiry must be in future");
    }

    [Fact]
    public void BSTVCIssueCredential_ExpiryEqualToTimestamp_Reverts()
    {
        var msg = _host.ExpectRevert(() => _registry.IssueCredential(
            _credHash, _subjectDid, _schemaId, CurrentTimestamp, MetadataUri));
        msg.Should().Contain("expiry must be in future");
    }

    // ================================================================
    // 5. IssueCredential — reverts for empty hash / subject / schema
    // ================================================================
    [Fact]
    public void BSTVCIssueCredential_EmptyHash_Reverts()
    {
        var msg = _host.ExpectRevert(() => _registry.IssueCredential(
            Array.Empty<byte>(), _subjectDid, _schemaId, FutureExpiry, MetadataUri));
        msg.Should().Contain("empty credential hash");
    }

    [Fact]
    public void BSTVCIssueCredential_EmptySubject_Reverts()
    {
        var msg = _host.ExpectRevert(() => _registry.IssueCredential(
            _credHash, Array.Empty<byte>(), _schemaId, FutureExpiry, MetadataUri));
        msg.Should().Contain("empty subject");
    }

    [Fact]
    public void BSTVCIssueCredential_EmptySchema_Reverts()
    {
        var msg = _host.ExpectRevert(() => _registry.IssueCredential(
            _credHash, _subjectDid, Array.Empty<byte>(), FutureExpiry, MetadataUri));
        msg.Should().Contain("empty schema");
    }

    // ================================================================
    // 6. RevokeCredential — sets status to Revoked (2), stores reason, emits event
    // ================================================================
    [Fact]
    public void BSTVCRevokeCredential_SetsStatusRevokedAndStoresReasonAndEmitsEvent()
    {
        IssueDefault();
        _host.ClearEvents();

        _host.Call(() => _registry.RevokeCredential(_credHash, "compromised key"));

        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(2); // Revoked

        var events = _host.GetEvents<CredentialRevokedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].CredentialHash.Should().BeEquivalentTo(_credHash);
        events[0].Issuer.Should().BeEquivalentTo(_issuer);
        events[0].Reason.Should().Be("compromised key");
    }

    // ================================================================
    // 7. RevokeCredential — reverts for non-issuer
    // ================================================================
    [Fact]
    public void BSTVCRevokeCredential_NonIssuer_Reverts()
    {
        IssueDefault();

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.RevokeCredential(_credHash, "fraud"));
        msg.Should().Contain("not issuer");
    }

    // ================================================================
    // 8. RevokeCredential — can revoke from Suspended state
    // ================================================================
    [Fact]
    public void BSTVCRevokeCredential_FromSuspendedState_Succeeds()
    {
        IssueDefault();

        _host.Call(() => _registry.SuspendCredential(_credHash, "under review"));
        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(3); // Suspended

        _host.Call(() => _registry.RevokeCredential(_credHash, "confirmed fraud"));
        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(2); // Revoked
    }

    // ================================================================
    // 9. SuspendCredential — sets status to Suspended (3), emits event
    // ================================================================
    [Fact]
    public void BSTVCSuspendCredential_SetsStatusSuspendedAndEmitsEvent()
    {
        IssueDefault();
        _host.ClearEvents();

        _host.Call(() => _registry.SuspendCredential(_credHash, "pending investigation"));

        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(3); // Suspended

        var events = _host.GetEvents<CredentialSuspendedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].CredentialHash.Should().BeEquivalentTo(_credHash);
        events[0].Issuer.Should().BeEquivalentTo(_issuer);
        events[0].Reason.Should().Be("pending investigation");
    }

    // ================================================================
    // 10. SuspendCredential — reverts if not Active
    // ================================================================
    [Fact]
    public void BSTVCSuspendCredential_WhenNotActive_Reverts()
    {
        IssueDefault();
        _host.Call(() => _registry.SuspendCredential(_credHash, "first suspension"));

        // Attempt to suspend again while already Suspended
        var msg = _host.ExpectRevert(() => _registry.SuspendCredential(_credHash, "double suspend"));
        msg.Should().Contain("not active");
    }

    [Fact]
    public void BSTVCSuspendCredential_WhenRevoked_Reverts()
    {
        IssueDefault();
        _host.Call(() => _registry.RevokeCredential(_credHash, "revoked"));

        var msg = _host.ExpectRevert(() => _registry.SuspendCredential(_credHash, "too late"));
        msg.Should().Contain("not active");
    }

    // ================================================================
    // 11. ReinstateCredential — sets status back to Active, emits event
    // ================================================================
    [Fact]
    public void BSTVCReinstateCredential_SetsStatusActiveAndEmitsEvent()
    {
        IssueDefault();
        _host.Call(() => _registry.SuspendCredential(_credHash, "review"));
        _host.ClearEvents();

        _host.Call(() => _registry.ReinstateCredential(_credHash));

        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(1); // Active

        var events = _host.GetEvents<CredentialReinstatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].CredentialHash.Should().BeEquivalentTo(_credHash);
        events[0].Issuer.Should().BeEquivalentTo(_issuer);
    }

    // ================================================================
    // 12. ReinstateCredential — reverts if not Suspended
    // ================================================================
    [Fact]
    public void BSTVCReinstateCredential_WhenActive_Reverts()
    {
        IssueDefault();

        var msg = _host.ExpectRevert(() => _registry.ReinstateCredential(_credHash));
        msg.Should().Contain("not suspended");
    }

    [Fact]
    public void BSTVCReinstateCredential_WhenRevoked_Reverts()
    {
        IssueDefault();
        _host.Call(() => _registry.RevokeCredential(_credHash, "done"));

        var msg = _host.ExpectRevert(() => _registry.ReinstateCredential(_credHash));
        msg.Should().Contain("not suspended");
    }

    [Fact]
    public void BSTVCReinstateCredential_NonIssuer_Reverts()
    {
        IssueDefault();
        _host.Call(() => _registry.SuspendCredential(_credHash, "review"));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.ReinstateCredential(_credHash));
        msg.Should().Contain("not issuer");
    }

    // ================================================================
    // 13. IsCredentialValid — true when active + not expired; false otherwise
    // ================================================================
    [Fact]
    public void BSTVCIsCredentialValid_ActiveAndNotExpired_ReturnsTrue()
    {
        IssueDefault();
        _host.Call(() => _registry.IsCredentialValid(_credHash)).Should().BeTrue();
    }

    [Fact]
    public void BSTVCIsCredentialValid_Revoked_ReturnsFalse()
    {
        IssueDefault();
        _host.Call(() => _registry.RevokeCredential(_credHash, "revoked"));
        _host.Call(() => _registry.IsCredentialValid(_credHash)).Should().BeFalse();
    }

    [Fact]
    public void BSTVCIsCredentialValid_Suspended_ReturnsFalse()
    {
        IssueDefault();
        _host.Call(() => _registry.SuspendCredential(_credHash, "suspended"));
        _host.Call(() => _registry.IsCredentialValid(_credHash)).Should().BeFalse();
    }

    [Fact]
    public void BSTVCIsCredentialValid_Expired_ReturnsFalse()
    {
        IssueDefault();

        // Advance time past the credential's expiry
        _host.SetBlockTimestamp((ulong)(FutureExpiry + 1));
        _host.Call(() => _registry.IsCredentialValid(_credHash)).Should().BeFalse();
    }

    [Fact]
    public void BSTVCIsCredentialValid_ExactlyAtExpiry_ReturnsFalse()
    {
        IssueDefault();

        // At exact expiry boundary: expiry > timestamp is false when equal
        _host.SetBlockTimestamp((ulong)FutureExpiry);
        _host.Call(() => _registry.IsCredentialValid(_credHash)).Should().BeFalse();
    }

    [Fact]
    public void BSTVCIsCredentialValid_Unknown_ReturnsFalse()
    {
        var unknownHash = new byte[] { 0xFF, 0xFF };
        _host.Call(() => _registry.IsCredentialValid(unknownHash)).Should().BeFalse();
    }

    // ================================================================
    // 14. GetIssuerCredentialCount — increments with each issuance
    // ================================================================
    [Fact]
    public void BSTVCGetIssuerCredentialCount_IncrementsPerIssuance()
    {
        _host.Call(() => _registry.GetIssuerCredentialCount(_issuer)).Should().Be(0);

        IssueDefault();
        _host.Call(() => _registry.GetIssuerCredentialCount(_issuer)).Should().Be(1);

        // Issue a second credential with a different hash
        var credHash2 = new byte[] { 0xDE, 0xAD, 0x00, 0x02 };
        _host.SetCaller(_issuer);
        _host.Call(() => _registry.IssueCredential(
            credHash2, _subjectDid, _schemaId, FutureExpiry, "ipfs://second"));
        _host.Call(() => _registry.GetIssuerCredentialCount(_issuer)).Should().Be(2);

        // Issue a third credential with yet another hash
        var credHash3 = new byte[] { 0xBE, 0xEF, 0x00, 0x03 };
        _host.Call(() => _registry.IssueCredential(
            credHash3, _subjectDid, _schemaId, FutureExpiry, "ipfs://third"));
        _host.Call(() => _registry.GetIssuerCredentialCount(_issuer)).Should().Be(3);
    }

    // ================================================================
    // 15. HasIssuerIssuedCredential — true after issuance; false for unrelated
    // ================================================================
    [Fact]
    public void BSTVCHasIssuerIssuedCredential_TrueAfterIssuance()
    {
        IssueDefault();
        _host.Call(() => _registry.HasIssuerIssuedCredential(_issuer, _credHash))
            .Should().BeTrue();
    }

    [Fact]
    public void BSTVCHasIssuerIssuedCredential_FalseForUnrelatedIssuer()
    {
        IssueDefault();
        _host.Call(() => _registry.HasIssuerIssuedCredential(_alice, _credHash))
            .Should().BeFalse();
    }

    [Fact]
    public void BSTVCHasIssuerIssuedCredential_FalseForUnissuedHash()
    {
        IssueDefault();
        var otherHash = new byte[] { 0x99, 0x88, 0x77 };
        _host.Call(() => _registry.HasIssuerIssuedCredential(_issuer, otherHash))
            .Should().BeFalse();
    }

    // ================================================================
    // 16. VerifyCredentialSet — true when valid; false otherwise
    // ================================================================
    [Fact]
    public void BSTVCVerifyCredentialSet_ValidCredential_ReturnsTrue()
    {
        IssueDefault();
        _host.Call(() => _registry.VerifyCredentialSet(_credHash)).Should().BeTrue();
    }

    [Fact]
    public void BSTVCVerifyCredentialSet_RevokedCredential_ReturnsFalse()
    {
        IssueDefault();
        _host.Call(() => _registry.RevokeCredential(_credHash, "revoked"));
        _host.Call(() => _registry.VerifyCredentialSet(_credHash)).Should().BeFalse();
    }

    [Fact]
    public void BSTVCVerifyCredentialSet_ExpiredCredential_ReturnsFalse()
    {
        IssueDefault();
        _host.SetBlockTimestamp((ulong)(FutureExpiry + 1));
        _host.Call(() => _registry.VerifyCredentialSet(_credHash)).Should().BeFalse();
    }

    [Fact]
    public void BSTVCVerifyCredentialSet_UnknownCredential_ReturnsFalse()
    {
        var unknownHash = new byte[] { 0xAA, 0xBB };
        _host.Call(() => _registry.VerifyCredentialSet(unknownHash)).Should().BeFalse();
    }

    // ================================================================
    // 17. Full lifecycle: Issue -> Suspend -> Reinstate -> Revoke
    // ================================================================
    [Fact]
    public void BSTVCFullLifecycle_Issue_Suspend_Reinstate_Revoke()
    {
        // Issue
        var credId = IssueDefault();
        credId.Should().NotBeNullOrEmpty();
        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(1);
        _host.Call(() => _registry.IsCredentialValid(_credHash)).Should().BeTrue();

        // Suspend
        _host.Call(() => _registry.SuspendCredential(_credHash, "under review"));
        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(3);
        _host.Call(() => _registry.IsCredentialValid(_credHash)).Should().BeFalse();

        // Reinstate
        _host.Call(() => _registry.ReinstateCredential(_credHash));
        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(1);
        _host.Call(() => _registry.IsCredentialValid(_credHash)).Should().BeTrue();

        // Revoke (terminal)
        _host.Call(() => _registry.RevokeCredential(_credHash, "final revocation"));
        _host.Call(() => _registry.GetCredentialStatus(_credHash)).Should().Be(2);
        _host.Call(() => _registry.IsCredentialValid(_credHash)).Should().BeFalse();
        _host.Call(() => _registry.VerifyCredentialSet(_credHash)).Should().BeFalse();

        // Cannot reinstate after revocation
        var msg = _host.ExpectRevert(() => _registry.ReinstateCredential(_credHash));
        msg.Should().Contain("not suspended");

        // Cannot suspend after revocation
        var msg2 = _host.ExpectRevert(() => _registry.SuspendCredential(_credHash, "nope"));
        msg2.Should().Contain("not active");
    }

    // ================================================================
    // Additional edge cases
    // ================================================================
    [Fact]
    public void BSTVCIssueCredential_CredentialIdIsSequential()
    {
        var credHash1 = new byte[] { 0x01, 0x01 };
        var credHash2 = new byte[] { 0x02, 0x02 };

        _host.SetCaller(_issuer);
        var id1 = _host.Call(() => _registry.IssueCredential(
            credHash1, _subjectDid, _schemaId, FutureExpiry, MetadataUri));
        var id2 = _host.Call(() => _registry.IssueCredential(
            credHash2, _subjectDid, _schemaId, FutureExpiry, MetadataUri));

        // Both should start with the issuer prefix
        var prefix = "vc:" + Convert.ToHexString(_issuer) + ":";
        id1.Should().StartWith(prefix);
        id2.Should().StartWith(prefix);

        // Index portion should differ (sequential)
        id1.Should().NotBe(id2);
    }

    [Fact]
    public void BSTVCRevokeCredential_NonExistent_Reverts()
    {
        var unknownHash = new byte[] { 0xFF, 0xEE };
        var msg = _host.ExpectRevert(() => _registry.RevokeCredential(unknownHash, "reason"));
        msg.Should().Contain("credential not found");
    }

    [Fact]
    public void BSTVCSuspendCredential_NonIssuer_Reverts()
    {
        IssueDefault();

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.SuspendCredential(_credHash, "reason"));
        msg.Should().Contain("not issuer");
    }

    [Fact]
    public void BSTVCRevokeCredential_AlreadyRevoked_Reverts()
    {
        IssueDefault();
        _host.Call(() => _registry.RevokeCredential(_credHash, "first"));

        var msg = _host.ExpectRevert(() => _registry.RevokeCredential(_credHash, "second"));
        msg.Should().Contain("cannot revoke");
    }

    [Fact]
    public void BSTVCGetCredentialIssuer_Unknown_ReturnsEmpty()
    {
        var unknownHash = new byte[] { 0xAA };
        _host.Call(() => _registry.GetCredentialIssuer(unknownHash))
            .Should().BeEmpty();
    }

    [Fact]
    public void BSTVCGetCredentialSubject_Unknown_ReturnsEmpty()
    {
        var unknownHash = new byte[] { 0xBB };
        _host.Call(() => _registry.GetCredentialSubject(unknownHash))
            .Should().BeEmpty();
    }

    [Fact]
    public void BSTVCGetCredentialSchema_Unknown_ReturnsEmpty()
    {
        var unknownHash = new byte[] { 0xCC };
        _host.Call(() => _registry.GetCredentialSchema(unknownHash))
            .Should().BeEmpty();
    }

    [Fact]
    public void BSTVCGetCredentialMetadataUri_Unknown_ReturnsEmpty()
    {
        var unknownHash = new byte[] { 0xDD };
        _host.Call(() => _registry.GetCredentialMetadataUri(unknownHash))
            .Should().BeEmpty();
    }

    [Fact]
    public void BSTVCMultipleIssuers_IndependentCredentials()
    {
        // Issuer 1 issues a credential
        _host.SetCaller(_issuer);
        _host.Call(() => _registry.IssueCredential(
            _credHash, _subjectDid, _schemaId, FutureExpiry, MetadataUri));

        // Issuer 2 (Alice) issues a different credential
        var aliceCredHash = new byte[] { 0xA1, 0x1C, 0xE0, 0x01 };
        _host.SetCaller(_alice);
        _host.Call(() => _registry.IssueCredential(
            aliceCredHash, _subjectDid, _schemaId, FutureExpiry, "ipfs://alice-cred"));

        // Each issuer has count = 1
        _host.Call(() => _registry.GetIssuerCredentialCount(_issuer)).Should().Be(1);
        _host.Call(() => _registry.GetIssuerCredentialCount(_alice)).Should().Be(1);

        // Cross-issuer lookup returns false
        _host.Call(() => _registry.HasIssuerIssuedCredential(_issuer, aliceCredHash))
            .Should().BeFalse();
        _host.Call(() => _registry.HasIssuerIssuedCredential(_alice, _credHash))
            .Should().BeFalse();

        // Each can only revoke their own
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.RevokeCredential(_credHash, "not mine"));
        msg.Should().Contain("not issuer");

        _host.SetCaller(_issuer);
        var msg2 = _host.ExpectRevert(() => _registry.RevokeCredential(aliceCredHash, "not mine"));
        msg2.Should().Contain("not issuer");
    }

    public void Dispose() => _host.Dispose();
}
