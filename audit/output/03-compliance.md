# Compliance Layer Audit Report

## Executive Summary

The compliance layer implements a well-structured hybrid architecture with both traditional (KYC/sanctions) and ZK proof-based compliance paths. The ZK proof integration into `TransactionExecutor` is correctly positioned before transaction dispatch, covering all 7 transaction types. However, there is a **critical logic flaw** in `ComplianceEngine.VerifyProofs()` that silently passes compliance when ZK requirements exist but no proofs are provided (in non-ZK engine configurations), a **high-severity holding limit overflow**, and **medium-severity governance enforcement gaps** where access control parameters are never used in production.

---

## Critical Issues

### COMPL-C01: Compliance Bypass via Missing Proofs in Non-ZK Engine Mode

- **Location**: `src/compliance/Basalt.Compliance/ComplianceEngine.cs:44-53`
- **Description**: When `_zkVerifier` is `null` (traditional mode), the `VerifyProofs()` method only rejects if BOTH `requirements.Length > 0` AND `proofs.Length > 0`. If a policy has `RequiredProofs` set but the transaction provides no proofs, the method falls through to `return ComplianceCheckOutcome.Success`.
- **Impact**: A transaction targeting an address with ZK proof requirements can bypass compliance entirely by simply omitting proofs, if the `ComplianceEngine` was instantiated without a `ZkComplianceVerifier`. The `TransactionExecutor` (line 50) enters the verification path when `requirements.Length > 0`, calls `VerifyProofs` with empty proofs, and gets `Success` back.
- **Severity**: Critical
- **Recommendation**: When `_zkVerifier` is null and `requirements.Length > 0`, return `ComplianceCheckOutcome.Fail(BasaltErrorCode.ComplianceProofMissing, "ZK verification not available but proofs are required")` regardless of whether proofs are present:
  ```csharp
  if (_zkVerifier == null && requirements.Length > 0)
      return ComplianceCheckOutcome.Fail(
          BasaltErrorCode.ComplianceProofMissing,
          "ZK verification not available but proofs are required");
  ```

---

## High Severity

### COMPL-H01: Holding Limit Integer Overflow Bypass

- **Location**: `src/compliance/Basalt.Compliance/ComplianceEngine.cs:206`
- **Description**: The holding limit check computes `var newBalance = receiverCurrentBalance + amount;` where both are `ulong`. If `receiverCurrentBalance` is near `ulong.MaxValue` (e.g., `ulong.MaxValue - 10`) and `amount` is `20`, the addition wraps around to `9`, which would pass the `> policy.MaxHoldingAmount` check.
- **Impact**: An attacker with an extremely large balance (in token units) could bypass the concentration/holding limit. While unlikely in practice due to token supply constraints, this is a correctness bug in a compliance-critical path.
- **Severity**: High
- **Recommendation**: Add an overflow check before the addition:
  ```csharp
  if (amount > ulong.MaxValue - receiverCurrentBalance)
      return ComplianceCheckResult.Fail(ComplianceErrorCode.HoldingLimit, "Overflow in holding limit check", "HOLDING_LIMIT");
  var newBalance = receiverCurrentBalance + amount;
  ```

### COMPL-H02: Traditional Compliance Path (CheckTransfer) Not Integrated into TransactionExecutor

- **Location**: `src/execution/Basalt.Execution/TransactionExecutor.cs:43-55` and `src/compliance/Basalt.Compliance/ComplianceEngine.cs:122-234`
- **Description**: The `TransactionExecutor` only calls `IComplianceVerifier.VerifyProofs()` (the ZK path). The traditional compliance pipeline (`CheckTransfer()` — KYC levels, sanctions screening, geo-restrictions, holding limits, lockup, travel rule, paused tokens) is NOT part of the `IComplianceVerifier` interface and is never invoked from the transaction execution pipeline.
- **Impact**: Traditional compliance policies (KYC, sanctions, geo-restrictions, etc.) set via `ComplianceEngine.SetPolicy()` are not enforced at the protocol level. They would only be enforced if explicitly called by token contract code, but no such integration exists in the SDK contract base classes.
- **Severity**: High
- **Recommendation**: Either add `CheckTransfer` to the `IComplianceVerifier` interface and call it from `TransactionExecutor`, or document clearly that traditional compliance is a token-level concern that must be explicitly integrated by each token contract.

### COMPL-H03: GetPolicy Returns Mutable Reference — Allows Unauthorized Policy Modification

- **Location**: `src/compliance/Basalt.Compliance/ComplianceEngine.cs:109-116`
- **Description**: `GetPolicy()` returns the actual `CompliancePolicy` object from the internal dictionary. `CompliancePolicy.Paused` and `CompliancePolicy.Issuer` have public `set` accessors (lines 48, 54 of `CompliancePolicy.cs`). Any code with access to the returned policy object can directly mutate `Paused` or `Issuer` without going through `SetPolicy()`, bypassing ownership checks and audit logging.
- **Impact**: A malicious or buggy caller could pause a token or change the policy issuer without being the original issuer and without generating an audit event.
- **Severity**: High
- **Recommendation**: Either return a defensive copy of the policy, make `Paused` and `Issuer` init-only, or add dedicated `PauseToken()`/`UnpauseToken()` methods with access control.

---

## Medium Severity

### COMPL-M01: Governance Access Control Not Wired in Production

- **Location**: `src/node/Basalt.Node/Program.cs:265-268`
- **Description**: Both `IdentityRegistry` and `SanctionsList` are instantiated with their parameterless constructors, which leaves `_governanceAddress` as `null`. This disables all governance authorization checks — any caller can `ApproveProvider()`, `RevokeProvider()`, `AddSanction()`, and `RemoveSanction()` without restriction.
- **Impact**: In the current deployment, there is no on-chain governance restriction on who can approve KYC providers or manage the sanctions list. Any entity with access to these methods can manipulate compliance infrastructure.
- **Severity**: Medium (likely intentional for devnet, but must be addressed before mainnet)
- **Recommendation**: Wire the governance address in production initialization. Add a configuration parameter or derive the governance address from the genesis block/governance contract.

### COMPL-M02: GetCountryCode Does Not Check Attestation Expiry

- **Location**: `src/compliance/Basalt.Compliance/IdentityRegistry.cs:179-187`
- **Description**: `GetCountryCode()` checks `!att.Revoked` but does NOT check `att.ExpiresAt` against a current timestamp. This is inconsistent with `HasValidAttestation()` (line 169) which correctly checks expiry. As a result, geo-restriction checks in `ComplianceEngine.CheckTransfer()` (lines 186-201) use potentially stale country data from expired attestations.
- **Impact**: An address with an expired attestation from a blocked country remains geo-restricted even though their attestation is no longer valid. Conversely, this cannot be used to bypass geo-restrictions (returning 0 for expired attestations would actually weaken enforcement). The impact is inconsistency, not a bypass.
- **Severity**: Medium
- **Recommendation**: Add a `currentTimestamp` parameter to `GetCountryCode()` and check `ExpiresAt`, or document the intentional conservative behavior.

### COMPL-M03: Nullifier Consumed Before Groth16 Verification — Failed Proofs Block Retries

- **Location**: `src/compliance/Basalt.Compliance/ZkComplianceVerifier.cs:106-112`
- **Description**: The nullifier is added to `_usedNullifiers` (line 108) BEFORE the Groth16 proof verification (line 146). If the Groth16 verification fails (e.g., decode error, invalid proof), the nullifier is still marked as used. The user cannot retry with a corrected proof using the same nullifier until the next block (when `ResetNullifiers()` is called).
- **Impact**: A user whose proof fails for a non-nullifier reason (e.g., VK lookup error, decode error, invalid curve point) has their nullifier permanently consumed for the current block. This is a DoS vector: submitting a malformed proof for a victim's nullifier (if known) could prevent the victim's legitimate proof from being accepted in the same block.
- **Severity**: Medium
- **Recommendation**: Only consume the nullifier after successful Groth16 verification, or use a two-phase approach: tentatively mark the nullifier, then roll back on verification failure.

### COMPL-M04: SetPolicy Ownership Check Bypassed When caller Is Null

- **Location**: `src/compliance/Basalt.Compliance/ComplianceEngine.cs:83-104`
- **Description**: The ownership check on line 88 is gated by `caller != null`. When `caller` is `null` (the default), no ownership verification occurs and any caller can overwrite any policy. All observed callers (tests and integration tests) pass `caller: null`.
- **Impact**: Without enforcing the `caller` parameter, the COMPL-05 policy ownership protection is entirely decorative. Any code path can overwrite any token's compliance policy without authorization.
- **Severity**: Medium
- **Recommendation**: Make `caller` required (not optional), or at minimum enforce that once a policy has an `Issuer`, updates without a matching caller are rejected.

### COMPL-M05: Audit Log Unbounded Memory Growth

- **Location**: `src/compliance/Basalt.Compliance/ComplianceEngine.cs:21`, `IdentityRegistry.cs:14`, `SanctionsList.cs:10`
- **Description**: All three classes use `List<ComplianceEvent>` with no size bounds or rotation. Every compliance check, attestation operation, and sanctions update appends to the in-memory list indefinitely.
- **Impact**: Over time (especially on a long-running node), the audit log will consume unbounded memory. With active compliance checking, this could grow to millions of entries.
- **Severity**: Medium
- **Recommendation**: Implement a circular buffer, persist to RocksDB, or add a configurable maximum size with oldest-first eviction.

---

## Low Severity / Recommendations

### COMPL-L01: DateTimeOffset.UtcNow Used in Audit Log Timestamps

- **Location**: `src/compliance/Basalt.Compliance/ComplianceEngine.cs:98,264`, `IdentityRegistry.cs:45,68,135`, `SanctionsList.cs:38,63`
- **Description**: Audit log timestamps use `DateTimeOffset.UtcNow` rather than the block timestamp. This creates non-deterministic audit entries that differ across validators for the same logical event.
- **Impact**: Audit logs are not reproducible across validators. Minor for auditing but could cause confusion when correlating events.
- **Severity**: Low
- **Recommendation**: Accept a `long blockTimestamp` parameter for operations that should be deterministic, or document that audit timestamps are wall-clock approximations.

### COMPL-L02: ComplianceEvent Marked as "Immutable" But Uses Mutable Class

- **Location**: `src/compliance/Basalt.Compliance/ComplianceEvents.cs:33`
- **Description**: The XML doc says "Immutable audit event" but `ComplianceEvent` is a `sealed class` with `init` setters and `byte[]` array properties. The `byte[]` fields (`Subject`, `Issuer`, `Receiver`, `TokenAddress`) are mutable arrays — callers can modify the contents after creation.
- **Impact**: Audit log integrity could be compromised if a caller modifies the byte arrays referenced by logged events.
- **Severity**: Low
- **Recommendation**: Clone arrays in the init setters, or use `ReadOnlyMemory<byte>` for true immutability.

### COMPL-L03: MockKycProvider Self-Approves in Constructor

- **Location**: `src/compliance/Basalt.Compliance/MockKycProvider.cs:19`
- **Description**: The `MockKycProvider` calls `registry.ApproveProvider(providerAddress)` in its constructor without the `caller` parameter. If the registry has a governance address, this would fail silently (return `false`), and the provider would operate thinking it's approved when it isn't.
- **Impact**: Only affects test/devnet scenarios. The failure is silent (no exception thrown), which could mask configuration errors.
- **Severity**: Low
- **Recommendation**: Check the return value and throw if approval fails, or document that `MockKycProvider` requires a non-governed `IdentityRegistry`.

### COMPL-L04: CompliancePolicy.SanctionsCheckEnabled Defaults to True

- **Location**: `src/compliance/Basalt.Compliance/CompliancePolicy.cs:24`
- **Description**: The default value for `SanctionsCheckEnabled` is `true`. This means that creating a `CompliancePolicy {}` with default values will enable sanctions checking even if not explicitly requested.
- **Impact**: Unexpected sanctions checking on tokens where it wasn't intended. The "no policy = unrestricted" path (line 134) is safe since `CheckTransfer` returns early, but any explicit policy creation inherits sanctions checks by default.
- **Severity**: Low
- **Recommendation**: Consider defaulting to `false` (opt-in) rather than `true` (opt-out), or document the default clearly.

### COMPL-L05: ZkComplianceVerifier Does Not Validate MinIssuerTier

- **Location**: `src/compliance/Basalt.Compliance/ZkComplianceVerifier.cs:88-152`
- **Description**: `ProofRequirement.MinIssuerTier` is defined but never checked in `VerifySingleProof()`. The `requirement` parameter is used only for `SchemaId` (VK lookup) and proof matching. The issuer tier validation is presumably expected to be embedded in the ZK circuit's public inputs, but this is not verified by the verifier.
- **Impact**: If the ZK circuit does not enforce issuer tier, a proof from a Tier 0 (self-attested) issuer would satisfy a Tier 3 (sovereign) requirement. The security depends entirely on correct circuit design.
- **Severity**: Low (assuming correct circuit design, but should be documented)
- **Recommendation**: Either validate `MinIssuerTier` against a public input field, or document that issuer tier enforcement is a circuit-level responsibility with clear spec requirements.

---

## Test Coverage Gaps

### Missing Test Scenarios

1. **Expired KYC in ComplianceEngine**: No test for `CheckTransfer` with an expired attestation (only tested in `IdentityRegistryTests`).
2. **Revoked attestation in CheckTransfer**: No test verifying that revoking a KYC attestation after policy creation blocks subsequent transfers.
3. **Self-transfer compliance**: No test for sender == receiver in `CheckTransfer`.
4. **Zero-value transfer compliance**: No test for `amount = 0` in `CheckTransfer`.
5. **Contract address compliance**: No test for compliance checks on contract addresses (e.g., deployer address).
6. **Holding limit overflow**: No test for `receiverCurrentBalance + amount > ulong.MaxValue` (the overflow case in COMPL-H01).
7. **Lockup at exact boundary**: No test for `currentTimestamp == policy.LockupEndTimestamp` (currently blocks — is this intended?).
8. **Travel rule at exact threshold**: No test for `amount == policy.TravelRuleThreshold` (currently blocks — the `>=` operator).
9. **Policy update by non-issuer**: No test for `SetPolicy` with a `caller` parameter to verify COMPL-05 ownership protection.
10. **Governance-controlled IdentityRegistry**: No test for `ApproveProvider`/`RevokeProvider` with governance address enforcement.
11. **Governance-controlled SanctionsList**: No test for `AddSanction`/`RemoveSanction` with governance address enforcement.
12. **Concurrent access**: No test for thread safety of compliance checks under concurrent calls.
13. **Mixed ZK + traditional mode**: No test for a ComplianceEngine with `ZkComplianceVerifier` that also uses `CheckTransfer` policies.
14. **Multiple schema requirements**: No test for a proof set that must satisfy multiple different schema requirements.
15. **Nullifier DoS**: No test for the scenario where a malformed proof consumes a nullifier (COMPL-M03).

---

## Positive Findings

1. **Correct compliance gate placement** (`TransactionExecutor.cs:43-55`): The ZK compliance check executes BEFORE the transaction type dispatch, ensuring all 7 transaction types pass through the same gate. No code path skips the check.

2. **Proof binding via COMPL-02** (`Transaction.cs:114-143`): Compliance proofs are included in the transaction signing hash via `ComputeComplianceProofsHash()`. This prevents relay nodes from stripping or substituting proofs after the sender signs.

3. **Nullifier anti-replay** (`ZkComplianceVerifier.cs:106-112`): Same-block proof replay is correctly prevented by tracking nullifiers in a `HashSet<Hash256>`. The nullifier set is correctly reset at block boundaries via `ResetNullifiers()`.

4. **Block-boundary nullifier reset** (`NodeCoordinator.cs:438-446`): `ResetNullifiers()` is called at the start of `HandleBlockFinalized`, before transaction execution, correctly bounding memory and implementing COMPL-07.

5. **Provider authorization** (`IdentityRegistry.cs:89-111`): Attestation issuance is correctly gated by provider approval status. Unapproved providers cannot issue attestations.

6. **Attestation revocation is immediate** (`IdentityRegistry.cs:116-141`): Revoking an attestation sets `Revoked = true` on the stored object, and `HasValidAttestation()` checks this flag. The effect is immediate for all subsequent compliance checks.

7. **Thread safety**: All state-mutating operations in `ComplianceEngine`, `IdentityRegistry`, `SanctionsList`, and `ZkComplianceVerifier` are protected by `lock` statements.

8. **Clean separation of concerns**: The `IComplianceVerifier` interface in `Basalt.Core` cleanly decouples the execution layer from the compliance implementation, allowing the compliance module to be swapped or extended without modifying the executor.

9. **Comprehensive policy model**: `CompliancePolicy` supports 7 distinct compliance rules (KYC, sanctions, geo-restrictions, holding limits, lockup, travel rule, pause) with clear error codes and audit logging.

10. **Address normalization via hex**: All address comparisons use `Convert.ToHexString()` for consistent normalization, avoiding case-sensitivity issues with byte array comparisons.
