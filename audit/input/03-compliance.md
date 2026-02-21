# Basalt Security & Quality Audit — Compliance Layer

## Scope

Audit the regulatory compliance infrastructure that enforces KYC, sanctions screening, and zero-knowledge proof-based identity verification:

| Project | Path | Description |
|---|---|---|
| `Basalt.Compliance` | `src/compliance/Basalt.Compliance/` | Hybrid compliance engine: traditional KYC/sanctions + ZK proofs |

Corresponding test project: `tests/Basalt.Compliance.Tests/`

**Note:** The ZK cryptographic primitives (Groth16, SparseMerkleTree, PedersenCommitment) live in `Basalt.Confidentiality` — covered by the confidentiality audit. This audit focuses on the compliance policy logic and verification orchestration.

---

## Files to Audit

- `ComplianceEngine.cs` (~270 lines) — Hybrid compliance verifier: traditional KYC + ZK proof verification
- `ComplianceEvents.cs` (~58 lines) — Compliance event types and logging
- `CompliancePolicy.cs` (~104 lines) — Policy rules, check results, error codes
- `IdentityAttestation.cs` (~60 lines) — KYC levels, attestation structure, `IKycProvider` interface
- `IdentityRegistry.cs` (~208 lines) — Identity storage, attestation management, expiry handling
- `MockKycProvider.cs` (~76 lines) — Test KYC provider implementation
- `SanctionsList.cs` (~92 lines) — Sanctions screening database
- `ZkComplianceVerifier.cs` (~165 lines) — ZK proof-based compliance verification

---

## Audit Objectives

### 1. Compliance Bypass Prevention (CRITICAL)
- Verify that `ComplianceEngine.CheckTransfer()` cannot be bypassed — all transaction paths must go through compliance checks.
- Check that both traditional (KYC level + sanctions) and ZK proof paths are correctly enforced.
- Verify that a transaction cannot satisfy compliance by providing a valid ZK proof for a *different* transaction (proof binding).
- Ensure compliance checks cannot be skipped by malformed or missing `ComplianceProof` on transactions.

### 2. Identity Registry Integrity
- Verify attestation storage is tamper-resistant — only authorized KYC providers can issue attestations.
- Check attestation expiry logic: expired attestations must fail compliance checks.
- Verify that attestation revocation is immediate and cannot be circumvented.
- Check for provider impersonation: can an unauthorized entity register attestations?

### 3. Sanctions Screening
- Verify `SanctionsList.IsSanctioned()` correctly matches addresses.
- Check whether sanctions checks are case-sensitive or handle address normalization correctly.
- Verify that sanctions list updates take effect immediately for new transactions.
- Check for timing attacks: can a sanctioned address transact between list update and enforcement?

### 4. ZK Compliance Verification
- Verify `ZkComplianceVerifier` correctly delegates to Groth16 verification.
- Check that nullifiers are tracked to prevent proof reuse (anti-correlation).
- Verify that ZK proofs are bound to specific transactions (sender, receiver, amount, timestamp).
- Check that the verification key is correctly loaded and cannot be substituted.
- Verify that `SparseMerkleTree` membership proofs are correctly validated.

### 5. Policy Configuration
- Verify `CompliancePolicy` rules are correctly evaluated (KYC level requirements, transfer limits, jurisdiction checks).
- Check that policy changes take effect correctly and cannot create windows of non-enforcement.
- Verify `ComplianceErrorCode` values are meaningful and do not leak sensitive information.

### 6. Privacy Considerations
- Verify that compliance checks in traditional mode do not leak more identity information than necessary.
- Check that ZK proof verification does not inadvertently reveal the underlying identity.
- Verify that `ComplianceEvent` logging does not store PII in plaintext.

### 7. Integration Points
- Verify that `IComplianceVerifier` interface (defined in `Basalt.Core`) is correctly implemented.
- Check how `ComplianceEngine` is wired into `TransactionExecutor` — ensure no code path skips compliance.
- Verify interaction with `SchemaRegistry` (0x0105) and `IssuerRegistry` (0x0106) system contracts.

### 8. Test Coverage
- Review `tests/Basalt.Compliance.Tests/` for coverage of:
  - Valid KYC → transfer allowed
  - Expired KYC → transfer blocked
  - Sanctioned address → transfer blocked
  - Valid ZK proof → transfer allowed
  - Invalid/replayed ZK proof → transfer blocked
  - Mixed mode (both traditional and ZK in same engine)
  - Edge cases (zero-value transfers, self-transfers, contract addresses)

---

## Key Context

- 6-layer ZK compliance architecture: SchemaRegistry → IssuerRegistry → SparseMerkleTree → ZkComplianceVerifier → ComplianceEngine → ComplianceProof on Transaction.
- `ComplianceEngine` has two constructors: `(IdentityRegistry, SanctionsList)` for traditional mode, `(IdentityRegistry, SanctionsList, ZkComplianceVerifier)` for hybrid mode.
- `MockKycProvider(IdentityRegistry, byte[] providerAddress)` — needs provider address parameter.
- `MockKycProvider.IssueBasic(byte[] subject, ushort countryCode)` — country code is `ushort`, not `string`.
- `ComplianceProof` is a readonly struct stored on `Transaction`.
- Compliance is enforced in `TransactionExecutor` before execution.

---

## Output Format

Write your findings to `audit/output/03-compliance.md` with the following structure:

```markdown
# Compliance Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Bypass vulnerabilities, enforcement gaps]

## High Severity
[Significant security or correctness issues]

## Medium Severity
[Issues to address but not immediately exploitable]

## Low Severity / Recommendations
[Code quality, best practices]

## Test Coverage Gaps
[Untested scenarios]

## Positive Findings
[Well-implemented patterns]
```

For each finding, include:
1. **Location**: File path and line number(s)
2. **Description**: What the issue is
3. **Impact**: What could go wrong (regulatory exposure, enforcement bypass)
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
