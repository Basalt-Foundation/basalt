using Basalt.Core;

namespace Basalt.Compliance;

/// <summary>
/// On-chain Identity Registry (system contract 0x0...0003).
/// Manages identity attestations for KYC/AML compliance.
/// Stores only cryptographic commitments — no personal data on-chain.
/// </summary>
public sealed class IdentityRegistry
{
    private readonly Dictionary<string, IdentityAttestation> _attestations = new();
    private readonly HashSet<string> _approvedProviders = new();
    private readonly List<ComplianceEvent> _auditLog = new();
    private readonly object _lock = new();
    private readonly string? _governanceAddress;

    public IdentityRegistry() { }

    /// <summary>
    /// Create an IdentityRegistry with governance access control.
    /// Only the governance address can approve/revoke providers (COMPL-03).
    /// </summary>
    public IdentityRegistry(byte[] governanceAddress)
    {
        _governanceAddress = ToHex(governanceAddress);
    }

    /// <summary>
    /// Register an approved KYC provider address.
    /// Requires governance authorization (COMPL-03).
    /// </summary>
    public bool ApproveProvider(byte[] providerAddress, byte[]? caller = null)
    {
        lock (_lock)
        {
            if (_governanceAddress != null && (caller == null || ToHex(caller) != _governanceAddress))
                return false;

            _approvedProviders.Add(ToHex(providerAddress));
            _auditLog.Add(new ComplianceEvent
            {
                EventType = ComplianceEventType.ProviderApproved,
                Subject = providerAddress,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Details = "KYC provider approved",
            });

            return true;
        }
    }

    /// <summary>
    /// Revoke a KYC provider's approval.
    /// Requires governance authorization (COMPL-03).
    /// </summary>
    public bool RevokeProvider(byte[] providerAddress, byte[]? caller = null)
    {
        lock (_lock)
        {
            if (_governanceAddress != null && (caller == null || ToHex(caller) != _governanceAddress))
                return false;

            _approvedProviders.Remove(ToHex(providerAddress));
            _auditLog.Add(new ComplianceEvent
            {
                EventType = ComplianceEventType.ProviderRevoked,
                Subject = providerAddress,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Details = "KYC provider revoked",
            });

            return true;
        }
    }

    /// <summary>
    /// Check if an address is an approved KYC provider.
    /// </summary>
    public bool IsApprovedProvider(byte[] providerAddress)
    {
        lock (_lock)
            return _approvedProviders.Contains(ToHex(providerAddress));
    }

    /// <summary>
    /// Issue an identity attestation. Only callable by approved providers.
    /// </summary>
    public bool IssueAttestation(byte[] issuer, IdentityAttestation attestation)
    {
        lock (_lock)
        {
            var issuerHex = ToHex(issuer);
            if (!_approvedProviders.Contains(issuerHex))
                return false;

            var subjectHex = ToHex(attestation.Subject);
            _attestations[subjectHex] = attestation;

            _auditLog.Add(new ComplianceEvent
            {
                EventType = ComplianceEventType.AttestationIssued,
                Subject = attestation.Subject,
                Issuer = issuer,
                Timestamp = attestation.IssuedAt,
                Details = $"KYC Level={attestation.Level}, Country={attestation.CountryCode}",
            });

            return true;
        }
    }

    /// <summary>
    /// Revoke an identity attestation. Callable by the issuer or governance.
    /// </summary>
    public bool RevokeAttestation(byte[] issuer, byte[] subject, string reason)
    {
        lock (_lock)
        {
            var subjectHex = ToHex(subject);
            if (!_attestations.TryGetValue(subjectHex, out var att))
                return false;

            // Only the original issuer can revoke (or governance — not implemented here)
            if (ToHex(att.Issuer) != ToHex(issuer))
                return false;

            att.Revoked = true;

            _auditLog.Add(new ComplianceEvent
            {
                EventType = ComplianceEventType.AttestationRevoked,
                Subject = subject,
                Issuer = issuer,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Details = $"Revoked: {reason}",
            });

            return true;
        }
    }

    /// <summary>
    /// Get the identity attestation for an address, if any.
    /// </summary>
    public IdentityAttestation? GetAttestation(byte[] subject)
    {
        lock (_lock)
        {
            _attestations.TryGetValue(ToHex(subject), out var att);
            return att;
        }
    }

    /// <summary>
    /// Check if an address has a valid (non-expired, non-revoked) attestation
    /// at or above the required KYC level.
    /// </summary>
    public bool HasValidAttestation(byte[] subject, KycLevel requiredLevel, long currentTimestamp)
    {
        lock (_lock)
        {
            if (!_attestations.TryGetValue(ToHex(subject), out var att))
                return false;

            if (att.Revoked)
                return false;

            if (att.ExpiresAt > 0 && att.ExpiresAt < currentTimestamp)
                return false;

            return att.Level >= requiredLevel;
        }
    }

    /// <summary>
    /// Get the country code for an address (0 if no attestation or attestation expired/revoked).
    /// M-02: Now checks attestation expiry in addition to revocation status.
    /// </summary>
    public ushort GetCountryCode(byte[] subject, long currentTimestamp = 0)
    {
        lock (_lock)
        {
            if (!_attestations.TryGetValue(ToHex(subject), out var att))
                return 0;
            if (att.Revoked)
                return 0;
            if (currentTimestamp > 0 && att.ExpiresAt > 0 && att.ExpiresAt < currentTimestamp)
                return 0;
            return att.CountryCode;
        }
    }

    /// <summary>
    /// Get the audit log of all compliance events.
    /// </summary>
    public IReadOnlyList<ComplianceEvent> GetAuditLog()
    {
        lock (_lock)
            return _auditLog.ToList();
    }

    /// <summary>
    /// Get audit events filtered by type.
    /// </summary>
    public IReadOnlyList<ComplianceEvent> GetAuditLog(ComplianceEventType eventType)
    {
        lock (_lock)
            return _auditLog.Where(e => e.EventType == eventType).ToList();
    }

    private static string ToHex(byte[] data) => Convert.ToHexString(data);
}
