# Basalt.Compliance

Regulatory compliance module for the Basalt blockchain. Implements on-chain identity management, KYC/AML verification, sanctions screening, and per-token transfer policies with a full audit trail.

## Components

### ComplianceEngine

Evaluates token transfers against configurable policies through a sequential pipeline. Step 0 is the pause check; steps 1-7 are the compliance checks:

0. **Paused** -- Token transfers globally halted (`ComplianceErrorCode.Paused`)
1. **Sender KYC** -- Sender meets minimum KYC level (`ComplianceErrorCode.KycMissing`)
2. **Receiver KYC** -- Receiver meets minimum KYC level (`ComplianceErrorCode.KycMissing`)
3. **Sanctions** -- Neither party is sanctioned (`ComplianceErrorCode.Sanctioned`)
4. **Geographic restrictions** -- Country codes not in blocklist (`ComplianceErrorCode.GeoRestricted`)
5. **Holding limit** -- Receiver balance stays within concentration limits (`ComplianceErrorCode.HoldingLimit`)
6. **Lock-up period** -- Transfer allowed after lockup expiry (`ComplianceErrorCode.Lockup`)
7. **Travel Rule** -- Large transfers include required data (`ComplianceErrorCode.TravelRuleMissing`)

```csharp
var engine = new ComplianceEngine(identityRegistry, sanctionsList);

engine.SetPolicy(tokenAddress, new CompliancePolicy
{
    RequiredSenderKycLevel = KycLevel.Basic,
    SanctionsCheckEnabled = true,
    BlockedCountries = new HashSet<ushort> { 408 },  // North Korea
    MaxHoldingAmount = 1_000_000,
    TravelRuleThreshold = 1_000,
});

var result = engine.CheckTransfer(
    tokenAddr, sender, receiver, amount, timestamp,
    receiverCurrentBalance: 0,     // optional: for holding limit check
    hasTravelRuleData: false);     // optional: for travel rule check
if (!result.Allowed)
    Console.WriteLine($"Blocked: {result.ErrorCode} - {result.Reason} (Rule: {result.RuleId})");
```

#### ComplianceCheckResult

| Property | Type | Description |
|----------|------|-------------|
| `Allowed` | `bool` | Whether the transfer is allowed |
| `ErrorCode` | `ComplianceErrorCode` | Error code if rejected (`None` = no error) |
| `Reason` | `string` | Human-readable reason for rejection |
| `RuleId` | `string` | The compliance rule that triggered rejection (e.g., `"KYC_SENDER"`, `"SANCTIONS_RECEIVER"`, `"HOLDING_LIMIT"`) |

Static helpers: `ComplianceCheckResult.Success` and `ComplianceCheckResult.Fail(code, reason, ruleId)`.

### IdentityRegistry

On-chain identity attestation storage with provider approval/revocation.

```csharp
var registry = new IdentityRegistry();

// Approve a KYC provider
registry.ApproveProvider(providerAddress);

// Provider issues attestation
registry.IssueAttestation(issuer, new IdentityAttestation
{
    Subject = userAddress,
    Issuer = providerAddress,
    Level = KycLevel.Enhanced,
    CountryCode = 840,  // US (ISO 3166-1 numeric)
    IssuedAt = now,
    ExpiresAt = now + 365 * 86400,
});

// Query
bool valid = registry.HasValidAttestation(user, KycLevel.Basic, now);
ushort country = registry.GetCountryCode(user);
```

KYC levels: `None`, `Basic`, `Enhanced`, `Institutional`.

### IKycProvider

Interface for KYC providers that can issue identity attestations.

```csharp
public interface IKycProvider
{
    IdentityAttestation Issue(byte[] subject, KycLevel level, ushort countryCode, long expiresAt, byte[] claimHash);
    void Revoke(byte[] subject, string reason);
    bool IsApproved { get; }
}
```

### SanctionsList

Address-based sanctions screening.

```csharp
var sanctions = new SanctionsList();
sanctions.AddSanction(address, "OFAC SDN list");
bool blocked = sanctions.IsSanctioned(address);
sanctions.RemoveSanction(address, "Cleared by compliance officer");
```

### MockKycProvider

Test-friendly KYC provider for development and integration testing.

```csharp
var provider = new MockKycProvider(registry, providerAddress);
provider.IssueBasic(userAddress, countryCode: 840);
provider.IssueEnhanced(userAddress, countryCode: 276);
provider.IssueInstitutional(userAddress);
provider.Revoke(userAddress, "Expired documents");
```

### Audit Trail

All compliance operations are logged with timestamps for regulatory reporting.

```csharp
IReadOnlyList<ComplianceEvent> allEvents = engine.GetAuditLog();
IReadOnlyList<ComplianceEvent> blocks = engine.GetAuditLog(ComplianceEventType.TransferBlocked);
```

Event types: `AttestationIssued`, `AttestationRevoked`, `AttestationExpired`, `ProviderApproved`, `ProviderRevoked`, `TransferApproved`, `TransferBlocked`, `AddressBlocked`, `AddressUnblocked`, `PolicyChanged`.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Hash256, Address, UInt256 |
| `Basalt.Crypto` | BLAKE3 for attestation hashing |
| `Basalt.Execution` | Transaction types for compliance checks |
