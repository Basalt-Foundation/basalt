using Basalt.Core;

namespace Basalt.Compliance;

/// <summary>
/// KYC verification level.
/// </summary>
public enum KycLevel : byte
{
    None = 0,
    Basic = 1,
    Enhanced = 2,
    Institutional = 3,
}

/// <summary>
/// On-chain identity attestation linking an address to a verified identity.
/// No personal data stored — only a cryptographic commitment (ClaimHash).
/// </summary>
public sealed class IdentityAttestation
{
    /// <summary>Account address that was verified.</summary>
    public byte[] Subject { get; init; } = [];

    /// <summary>Approved KYC provider that issued this attestation.</summary>
    public byte[] Issuer { get; init; } = [];

    /// <summary>Unix timestamp (seconds) when attestation was issued.</summary>
    public long IssuedAt { get; init; }

    /// <summary>Unix timestamp (seconds) when attestation expires. 0 = no expiry.</summary>
    public long ExpiresAt { get; init; }

    /// <summary>KYC verification level.</summary>
    public KycLevel Level { get; init; }

    /// <summary>ISO 3166-1 numeric country code.</summary>
    public ushort CountryCode { get; init; }

    /// <summary>ZK commitment (Pedersen) to underlying identity data. No PII on-chain.</summary>
    public byte[] ClaimHash { get; init; } = [];

    /// <summary>Whether this attestation has been revoked.</summary>
    public bool Revoked { get; set; }
}

/// <summary>
/// Interface for KYC providers that can issue identity attestations.
/// </summary>
public interface IKycProvider
{
    /// <summary>Issue an identity attestation for a subject address.</summary>
    IdentityAttestation Issue(byte[] subject, KycLevel level, ushort countryCode, long expiresAt, byte[] claimHash);

    /// <summary>Revoke a previously issued attestation.</summary>
    void Revoke(byte[] subject, string reason);

    /// <summary>Check if this provider is approved to issue attestations.</summary>
    bool IsApproved { get; }
}
