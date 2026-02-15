namespace Basalt.Compliance;

/// <summary>
/// Per-token compliance policy configured by the token issuer.
/// Defines the rules enforced on every transfer of the token.
/// </summary>
public sealed class CompliancePolicy
{
    /// <summary>Token contract address this policy applies to.</summary>
    public byte[] TokenAddress { get; init; } = [];

    /// <summary>Minimum KYC level required for sender.</summary>
    public KycLevel RequiredSenderKycLevel { get; init; } = KycLevel.None;

    /// <summary>Minimum KYC level required for receiver.</summary>
    public KycLevel RequiredReceiverKycLevel { get; init; } = KycLevel.None;

    /// <summary>Whether sanctions list checking is enabled.</summary>
    public bool SanctionsCheckEnabled { get; init; } = true;

    /// <summary>Set of blocked ISO 3166-1 country codes (geographic restrictions).</summary>
    public HashSet<ushort> BlockedCountries { get; init; } = [];

    /// <summary>
    /// Maximum holding amount per address (0 = no limit).
    /// Prevents concentration of ownership.
    /// </summary>
    public ulong MaxHoldingAmount { get; init; }

    /// <summary>
    /// Lock-up end timestamp (Unix seconds). Transfers blocked until this time.
    /// 0 = no lock-up.
    /// </summary>
    public long LockupEndTimestamp { get; init; }

    /// <summary>
    /// Travel Rule threshold — transfers above this amount require Travel Rule data.
    /// 0 = no Travel Rule requirement.
    /// </summary>
    public ulong TravelRuleThreshold { get; init; }

    /// <summary>Whether the token is paused (all transfers blocked).</summary>
    public bool Paused { get; set; }
}

/// <summary>
/// Result of a compliance check on a transfer.
/// </summary>
public sealed class ComplianceCheckResult
{
    /// <summary>Whether the transfer is allowed.</summary>
    public bool Allowed { get; init; }

    /// <summary>Error code if rejected (0 = no error).</summary>
    public ComplianceErrorCode ErrorCode { get; init; }

    /// <summary>Human-readable reason for rejection.</summary>
    public string Reason { get; init; } = "";

    /// <summary>The compliance rule that triggered the rejection.</summary>
    public string RuleId { get; init; } = "";

    public static ComplianceCheckResult Success { get; } = new() { Allowed = true };

    public static ComplianceCheckResult Fail(ComplianceErrorCode code, string reason, string ruleId) =>
        new() { Allowed = false, ErrorCode = code, Reason = reason, RuleId = ruleId };
}

/// <summary>
/// Compliance error codes (from spec Appendix C).
/// </summary>
public enum ComplianceErrorCode : uint
{
    None = 0x0000,
    KycMissing = 0x0020,
    Sanctioned = 0x0021,
    GeoRestricted = 0x0022,
    HoldingLimit = 0x0023,
    Lockup = 0x0024,
    Paused = 0x0025,
    TravelRuleMissing = 0x0026,
}
