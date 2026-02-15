namespace Basalt.Compliance;

/// <summary>
/// Mock KYC provider for testnet/development use.
/// Auto-approves all attestation requests at the specified level.
/// NOT for production use.
/// </summary>
public sealed class MockKycProvider : IKycProvider
{
    private readonly IdentityRegistry _registry;
    private readonly byte[] _providerAddress;

    public MockKycProvider(IdentityRegistry registry, byte[] providerAddress)
    {
        _registry = registry;
        _providerAddress = providerAddress;

        // Self-register as approved provider
        registry.ApproveProvider(providerAddress);
    }

    public bool IsApproved => _registry.IsApprovedProvider(_providerAddress);

    /// <summary>
    /// Issue an attestation — auto-approves for testnet.
    /// </summary>
    public IdentityAttestation Issue(byte[] subject, KycLevel level, ushort countryCode, long expiresAt, byte[] claimHash)
    {
        var attestation = new IdentityAttestation
        {
            Subject = subject,
            Issuer = _providerAddress,
            IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ExpiresAt = expiresAt,
            Level = level,
            CountryCode = countryCode,
            ClaimHash = claimHash.Length > 0 ? claimHash : new byte[32], // Dummy hash for testnet
        };

        _registry.IssueAttestation(_providerAddress, attestation);
        return attestation;
    }

    /// <summary>
    /// Revoke an attestation.
    /// </summary>
    public void Revoke(byte[] subject, string reason)
    {
        _registry.RevokeAttestation(_providerAddress, subject, reason);
    }

    /// <summary>
    /// Convenience: issue a Basic KYC attestation with no expiry.
    /// </summary>
    public IdentityAttestation IssueBasic(byte[] subject, ushort countryCode = 840)
    {
        return Issue(subject, KycLevel.Basic, countryCode, 0, []);
    }

    /// <summary>
    /// Convenience: issue an Enhanced KYC attestation with 1-year expiry.
    /// </summary>
    public IdentityAttestation IssueEnhanced(byte[] subject, ushort countryCode = 840)
    {
        var oneYear = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
        return Issue(subject, KycLevel.Enhanced, countryCode, oneYear, []);
    }

    /// <summary>
    /// Convenience: issue an Institutional KYC attestation.
    /// </summary>
    public IdentityAttestation IssueInstitutional(byte[] subject, ushort countryCode = 840)
    {
        return Issue(subject, KycLevel.Institutional, countryCode, 0, []);
    }
}
