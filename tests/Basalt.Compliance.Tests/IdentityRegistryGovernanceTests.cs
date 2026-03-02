using Basalt.Compliance;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

public class IdentityRegistryGovernanceTests
{
    private static byte[] Addr(byte seed) { var a = new byte[20]; a[19] = seed; return a; }

    private readonly byte[] _governance = Addr(0xFF);
    private readonly byte[] _provider = Addr(1);
    private readonly byte[] _subject = Addr(2);
    private readonly byte[] _randomUser = Addr(3);

    private IdentityRegistry CreateRegistryWithAttestation()
    {
        var registry = new IdentityRegistry(_governance);
        registry.ApproveProvider(_provider, _governance);
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
        registry.IssueAttestation(_provider, att);
        return registry;
    }

    [Fact]
    public void GovernanceCanRevokeAttestation()
    {
        var registry = CreateRegistryWithAttestation();

        var result = registry.RevokeAttestation(_governance, _subject, "Governance revocation");
        result.Should().BeTrue();

        var att = registry.GetAttestation(_subject);
        att.Should().NotBeNull();
        att!.Revoked.Should().BeTrue();
    }

    [Fact]
    public void NonIssuerNonGovernanceCannotRevoke()
    {
        var registry = CreateRegistryWithAttestation();

        var result = registry.RevokeAttestation(_randomUser, _subject, "Unauthorized attempt");
        result.Should().BeFalse();

        var att = registry.GetAttestation(_subject);
        att.Should().NotBeNull();
        att!.Revoked.Should().BeFalse();
    }

    [Fact]
    public void OriginalIssuerCanStillRevoke()
    {
        var registry = CreateRegistryWithAttestation();

        var result = registry.RevokeAttestation(_provider, _subject, "Issuer revocation");
        result.Should().BeTrue();

        var att = registry.GetAttestation(_subject);
        att.Should().NotBeNull();
        att!.Revoked.Should().BeTrue();
    }

    [Fact]
    public void RevocationProducesAuditLogEntry()
    {
        var registry = CreateRegistryWithAttestation();

        registry.RevokeAttestation(_governance, _subject, "Audit test");

        var auditLog = registry.GetAuditLog(ComplianceEventType.AttestationRevoked);
        auditLog.Should().HaveCountGreaterOrEqualTo(1);
        auditLog[^1].Details.Should().Contain("Audit test");
    }

    [Fact]
    public void NoGovernance_BackwardCompatible()
    {
        // Registry without governance address — only original issuer can revoke
        var registry = new IdentityRegistry();
        registry.ApproveProvider(_provider);
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
        registry.IssueAttestation(_provider, att);

        // Random user still can't revoke
        registry.RevokeAttestation(_randomUser, _subject, "Should fail").Should().BeFalse();

        // Original issuer can revoke
        registry.RevokeAttestation(_provider, _subject, "Issuer revoke").Should().BeTrue();
    }
}
