using Basalt.Compliance;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

public class IdentityRegistryTests
{
    private static byte[] Addr(byte seed) { var a = new byte[20]; a[19] = seed; return a; }

    private readonly IdentityRegistry _registry = new();
    private readonly byte[] _provider = Addr(1);
    private readonly byte[] _subject = Addr(2);

    [Fact]
    public void ApproveProvider_Makes_Provider_Approved()
    {
        _registry.ApproveProvider(_provider);
        _registry.IsApprovedProvider(_provider).Should().BeTrue();
    }

    [Fact]
    public void RevokeProvider_Removes_Approval()
    {
        _registry.ApproveProvider(_provider);
        _registry.RevokeProvider(_provider);
        _registry.IsApprovedProvider(_provider).Should().BeFalse();
    }

    [Fact]
    public void IssueAttestation_By_Approved_Provider_Succeeds()
    {
        _registry.ApproveProvider(_provider);

        var att = new IdentityAttestation
        {
            Subject = _subject,
            Issuer = _provider,
            IssuedAt = 1000,
            ExpiresAt = 0,
            Level = KycLevel.Basic,
            CountryCode = 840,
            ClaimHash = new byte[32],
        };

        _registry.IssueAttestation(_provider, att).Should().BeTrue();

        var stored = _registry.GetAttestation(_subject);
        stored.Should().NotBeNull();
        stored!.Level.Should().Be(KycLevel.Basic);
        stored.CountryCode.Should().Be(840);
    }

    [Fact]
    public void IssueAttestation_By_Unapproved_Provider_Fails()
    {
        var att = new IdentityAttestation
        {
            Subject = _subject,
            Issuer = _provider,
            IssuedAt = 1000,
            Level = KycLevel.Basic,
        };

        _registry.IssueAttestation(_provider, att).Should().BeFalse();
        _registry.GetAttestation(_subject).Should().BeNull();
    }

    [Fact]
    public void HasValidAttestation_Checks_Level()
    {
        _registry.ApproveProvider(_provider);
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _subject, Issuer = _provider, IssuedAt = 1000,
            Level = KycLevel.Basic, CountryCode = 840, ClaimHash = new byte[32],
        });

        _registry.HasValidAttestation(_subject, KycLevel.None, 2000).Should().BeTrue();
        _registry.HasValidAttestation(_subject, KycLevel.Basic, 2000).Should().BeTrue();
        _registry.HasValidAttestation(_subject, KycLevel.Enhanced, 2000).Should().BeFalse();
    }

    [Fact]
    public void HasValidAttestation_Checks_Expiry()
    {
        _registry.ApproveProvider(_provider);
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _subject, Issuer = _provider, IssuedAt = 1000,
            ExpiresAt = 5000, Level = KycLevel.Basic, ClaimHash = new byte[32],
        });

        _registry.HasValidAttestation(_subject, KycLevel.Basic, 4000).Should().BeTrue();
        _registry.HasValidAttestation(_subject, KycLevel.Basic, 6000).Should().BeFalse();
    }

    [Fact]
    public void RevokeAttestation_Makes_It_Invalid()
    {
        _registry.ApproveProvider(_provider);
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _subject, Issuer = _provider, IssuedAt = 1000,
            Level = KycLevel.Enhanced, ClaimHash = new byte[32],
        });

        _registry.HasValidAttestation(_subject, KycLevel.Basic, 2000).Should().BeTrue();
        _registry.RevokeAttestation(_provider, _subject, "GDPR request").Should().BeTrue();
        _registry.HasValidAttestation(_subject, KycLevel.Basic, 2000).Should().BeFalse();
    }

    [Fact]
    public void RevokeAttestation_Only_By_Original_Issuer()
    {
        var otherProvider = Addr(3);
        _registry.ApproveProvider(_provider);
        _registry.ApproveProvider(otherProvider);

        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _subject, Issuer = _provider, IssuedAt = 1000,
            Level = KycLevel.Basic, ClaimHash = new byte[32],
        });

        _registry.RevokeAttestation(otherProvider, _subject, "not my attestation").Should().BeFalse();
        _registry.HasValidAttestation(_subject, KycLevel.Basic, 2000).Should().BeTrue();
    }

    [Fact]
    public void GetCountryCode_Returns_Code_For_Valid_Attestation()
    {
        _registry.ApproveProvider(_provider);
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _subject, Issuer = _provider, IssuedAt = 1000,
            Level = KycLevel.Basic, CountryCode = 276, ClaimHash = new byte[32],
        });

        _registry.GetCountryCode(_subject).Should().Be(276);
    }

    [Fact]
    public void GetCountryCode_Returns_Zero_For_Unknown_Address()
    {
        _registry.GetCountryCode(Addr(99)).Should().Be(0);
    }

    [Fact]
    public void GetCountryCode_Returns_Zero_For_Expired_Attestation()
    {
        // M-02: GetCountryCode should check attestation expiry
        _registry.ApproveProvider(_provider);
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _subject, Issuer = _provider, IssuedAt = 1000,
            ExpiresAt = 3000, Level = KycLevel.Basic, CountryCode = 276, ClaimHash = new byte[32],
        });

        _registry.GetCountryCode(_subject, currentTimestamp: 2000).Should().Be(276); // Not expired
        _registry.GetCountryCode(_subject, currentTimestamp: 4000).Should().Be(0);   // Expired
    }

    [Fact]
    public void GetCountryCode_NoTimestamp_IgnoresExpiry()
    {
        // Backward compatible: default timestamp=0 skips expiry check
        _registry.ApproveProvider(_provider);
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _subject, Issuer = _provider, IssuedAt = 1000,
            ExpiresAt = 3000, Level = KycLevel.Basic, CountryCode = 276, ClaimHash = new byte[32],
        });

        _registry.GetCountryCode(_subject).Should().Be(276); // No timestamp = no expiry check
    }

    [Fact]
    public void AuditLog_Records_All_Operations()
    {
        _registry.ApproveProvider(_provider);
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _subject, Issuer = _provider, IssuedAt = 1000,
            Level = KycLevel.Basic, ClaimHash = new byte[32],
        });
        _registry.RevokeAttestation(_provider, _subject, "test");

        var log = _registry.GetAuditLog();
        log.Should().HaveCount(3);
        log[0].EventType.Should().Be(ComplianceEventType.ProviderApproved);
        log[1].EventType.Should().Be(ComplianceEventType.AttestationIssued);
        log[2].EventType.Should().Be(ComplianceEventType.AttestationRevoked);
    }

    [Fact]
    public void AuditLog_FilterByType()
    {
        _registry.ApproveProvider(_provider);
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _subject, Issuer = _provider, IssuedAt = 1000,
            Level = KycLevel.Basic, ClaimHash = new byte[32],
        });

        var filtered = _registry.GetAuditLog(ComplianceEventType.AttestationIssued);
        filtered.Should().HaveCount(1);
    }
}
