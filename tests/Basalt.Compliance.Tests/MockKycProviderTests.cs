using Basalt.Compliance;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

public class MockKycProviderTests
{
    private static byte[] Addr(byte seed) { var a = new byte[20]; a[19] = seed; return a; }

    private readonly IdentityRegistry _registry = new();
    private readonly byte[] _providerAddr = Addr(1);
    private readonly MockKycProvider _provider;

    public MockKycProviderTests()
    {
        _provider = new MockKycProvider(_registry, _providerAddr);
    }

    [Fact]
    public void Provider_Is_Auto_Approved()
    {
        _provider.IsApproved.Should().BeTrue();
        _registry.IsApprovedProvider(_providerAddr).Should().BeTrue();
    }

    [Fact]
    public void IssueBasic_Creates_Basic_Attestation()
    {
        var subject = Addr(10);
        var att = _provider.IssueBasic(subject, 276);

        att.Level.Should().Be(KycLevel.Basic);
        att.CountryCode.Should().Be(276);
        att.ExpiresAt.Should().Be(0);
        _registry.HasValidAttestation(subject, KycLevel.Basic, 9999).Should().BeTrue();
    }

    [Fact]
    public void IssueEnhanced_Creates_Enhanced_With_Expiry()
    {
        var subject = Addr(10);
        var att = _provider.IssueEnhanced(subject, 840);

        att.Level.Should().Be(KycLevel.Enhanced);
        att.ExpiresAt.Should().BeGreaterThan(0);
        _registry.HasValidAttestation(subject, KycLevel.Enhanced, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Should().BeTrue();
    }

    [Fact]
    public void IssueInstitutional_Creates_Institutional()
    {
        var subject = Addr(10);
        _provider.IssueInstitutional(subject);

        _registry.HasValidAttestation(subject, KycLevel.Institutional, 9999).Should().BeTrue();
    }

    [Fact]
    public void Revoke_Invalidates_Attestation()
    {
        var subject = Addr(10);
        _provider.IssueBasic(subject);

        _registry.HasValidAttestation(subject, KycLevel.Basic, 9999).Should().BeTrue();
        _provider.Revoke(subject, "GDPR erasure request");
        _registry.HasValidAttestation(subject, KycLevel.Basic, 9999).Should().BeFalse();
    }

    [Fact]
    public void Issue_With_Custom_ClaimHash()
    {
        var subject = Addr(10);
        var hash = new byte[32];
        hash[0] = 0xAB;
        var att = _provider.Issue(subject, KycLevel.Enhanced, 276, 0, hash);

        att.ClaimHash[0].Should().Be(0xAB);
    }
}
