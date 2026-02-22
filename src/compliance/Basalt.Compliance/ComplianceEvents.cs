namespace Basalt.Compliance;

/// <summary>
/// Types of compliance audit events.
/// </summary>
public enum ComplianceEventType
{
    // Identity events
    AttestationIssued,
    AttestationRevoked,
    AttestationExpired,

    // Provider events
    ProviderApproved,
    ProviderRevoked,

    // Transfer compliance events
    TransferApproved,
    TransferBlocked,

    // Sanctions events
    AddressBlocked,
    AddressUnblocked,

    // Policy events
    PolicyChanged,
}

/// <summary>
/// Immutable audit event for compliance tracking.
/// All compliance actions generate an event for regulatory audit.
/// </summary>
public sealed class ComplianceEvent
{
    /// <summary>Type of compliance event.</summary>
    public ComplianceEventType EventType { get; init; }

    /// <summary>Primary subject address.</summary>
    public byte[] Subject { get; init; } = [];

    /// <summary>Issuer/operator address (if applicable).</summary>
    public byte[] Issuer { get; init; } = [];

    /// <summary>Receiver address (for transfer events).</summary>
    public byte[] Receiver { get; init; } = [];

    /// <summary>Token address (for transfer events).</summary>
    public byte[] TokenAddress { get; init; } = [];

    /// <summary>Transfer amount (for transfer events).</summary>
    public ulong Amount { get; init; }

    /// <summary>
    /// Unix timestamp (seconds) when the event occurred.
    /// LOW-02: Some audit events use DateTimeOffset.UtcNow for this field (provider approval,
    /// attestation revocation, policy changes). This is metadata-only and NOT consensus-critical —
    /// audit log timestamps are never used in block validation or state transitions.
    /// </summary>
    public long Timestamp { get; init; }

    /// <summary>Human-readable details of the event.</summary>
    public string Details { get; init; } = "";
}
