# Basalt.Compliance

Regulatory compliance module for the Basalt blockchain. Implements a **hybrid compliance model** with two verification paths:

- **ZK proofs (default, privacy-preserving)**: Users attach Groth16 ZK-SNARK proofs to transactions proving they satisfy credential schema requirements without revealing any identity information. Zero identity data is stored on-chain.
- **On-chain attestations (legacy/opt-in)**: Traditional KYC/AML verification with identity metadata stored on-chain for entities that prefer public compliance status.

The engine checks for ZK proofs first. If present, validates them via `ZkComplianceVerifier`. Otherwise, falls back to on-chain attestation checks.

## Components

### ZkComplianceVerifier

ZK-based compliance verifier. Validates Groth16 proofs attached to transactions to verify the sender satisfies credential schema requirements without revealing any identity information.

Each proof demonstrates:
- The sender holds a valid credential matching the required schema
- The credential was issued by a sufficiently-trusted issuer (tier >= required)
- The credential has not expired (checked against block timestamp)
- The credential has not been revoked (non-membership in issuer's revocation tree)

```csharp
// Create a verifier with VK lookup function
var zkVerifier = new ZkComplianceVerifier(schemaId =>
{
    // Lookup verification key from SchemaRegistry contract
    return schemaRegistryStorage.GetVerificationKey(schemaId);
});

// Verify compliance proofs
var outcome = zkVerifier.VerifyProofs(proofs, requirements, blockTimestamp);
if (!outcome.Allowed)
    Console.WriteLine($"ZK verification failed: {outcome.ErrorCode} - {outcome.Reason}");

// Reset nullifiers at block boundaries (allows cross-block proof reuse)
zkVerifier.ResetNullifiers();
```

Verification pipeline per proof:
1. **Format validation** -- Proof must be 192 bytes (Groth16 A+B+C), public inputs non-empty and multiple of 32 bytes
2. **Nullifier uniqueness** -- Prevents proof replay within a block (same nullifier = rejected)
3. **VK lookup** -- Retrieves the verification key for the proof's schema ID
4. **Groth16 verification** -- Mathematical proof verification via BLS12-381 pairings

### ComplianceEngine

Hybrid compliance engine implementing `IComplianceVerifier`. Evaluates token transfers against configurable policies through two paths:

- **ZK path** (when `ZkComplianceVerifier` is configured): Delegates proof verification to the ZK verifier
- **Attestation path** (fallback): Sequential compliance pipeline with steps 0-7:

Steps 0-7 are the on-chain compliance checks:

0. **Paused** -- Token transfers globally halted (`ComplianceErrorCode.Paused`)
1. **Sender KYC** -- Sender meets minimum KYC level (`ComplianceErrorCode.KycMissing`)
2. **Receiver KYC** -- Receiver meets minimum KYC level (`ComplianceErrorCode.KycMissing`)
3. **Sanctions** -- Neither party is sanctioned (`ComplianceErrorCode.Sanctioned`)
4. **Geographic restrictions** -- Country codes not in blocklist (`ComplianceErrorCode.GeoRestricted`)
5. **Holding limit** -- Receiver balance stays within concentration limits (`ComplianceErrorCode.HoldingLimit`)
6. **Lock-up period** -- Transfer allowed after lockup expiry (`ComplianceErrorCode.Lockup`)
7. **Travel Rule** -- Large transfers include required data (`ComplianceErrorCode.TravelRuleMissing`)

```csharp
// Hybrid engine: ZK proofs first, attestation fallback
var engine = new ComplianceEngine(identityRegistry, sanctionsList, zkVerifier);

// Or attestation-only (no ZK)
var engine = new ComplianceEngine(identityRegistry, sanctionsList);

// ZK compliance: define required schemas
engine.SetPolicy(tokenAddress, new CompliancePolicy
{
    RequiredProofs = new[]
    {
        new ProofRequirement { SchemaId = kycSchemaId, MinIssuerTier = 1 },
    },
});

// On-chain attestation compliance
engine.SetPolicy(tokenAddress, new CompliancePolicy
{
    RequiredSenderKycLevel = KycLevel.Basic,
    SanctionsCheckEnabled = true,
    BlockedCountries = new HashSet<ushort> { 408 },  // North Korea
    MaxHoldingAmount = 1_000_000,
    TravelRuleThreshold = 1_000,
});

// Verify ZK proofs (from IComplianceVerifier)
var outcome = engine.VerifyProofs(proofs, requirements, blockTimestamp);

// Or check on-chain attestation transfer
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
| `Basalt.Core` | Hash256, Address, UInt256, IComplianceVerifier, ComplianceProof |
| `Basalt.Crypto` | BLAKE3 for attestation hashing |
| `Basalt.Execution` | Transaction types for compliance checks |
| `Basalt.Confidentiality` | Groth16Verifier, Groth16Codec for ZK proof verification |
