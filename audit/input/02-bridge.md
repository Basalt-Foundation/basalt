# Basalt Security & Quality Audit — Bridge Layer

## Scope

Audit the off-chain bridge relay infrastructure that enables cross-chain asset transfers between Basalt and external EVM chains:

| Project | Path | Description |
|---|---|---|
| `Basalt.Bridge` | `src/bridge/Basalt.Bridge/` | Off-chain relay: multisig coordination, proof verification, bridge state management |

Corresponding test project: `tests/Basalt.Bridge.Tests/`

**Note:** The on-chain counterpart is `BridgeETH` in `src/sdk/Basalt.Sdk.Contracts/BridgeETH.cs` — covered by the SDK audit. This audit focuses on the off-chain relay infrastructure.

---

## Files to Audit

- `BridgeMessages.cs` (~102 lines) — `BridgeDirection`, `BridgeTransferStatus`, `BridgeDeposit`, `BridgeWithdrawal`, `RelayerSignature`
- `BridgeProofVerifier.cs` (~159 lines) — Merkle proof verification for cross-chain state proofs
- `BridgeState.cs` (~230 lines) — Bridge state management, deposit lifecycle (pending → confirmed → finalized)
- `MultisigRelayer.cs` (~116 lines) — M-of-N Ed25519 multisig relayer coordination

---

## Audit Objectives

### 1. Multisig Security (CRITICAL)
- Verify M-of-N threshold logic is correctly implemented — ensure `M` signatures from `N` authorized relayers are required and that `M > N/2`.
- Check for signature malleability: Ed25519 signatures must be verified against canonical form.
- Verify relayer public keys are validated against an authorized set and cannot be spoofed.
- Check for replay protection: each relay operation must have a unique identifier that prevents the same signatures from being reused.
- Verify that relayer set changes (adding/removing relayers) cannot be exploited during in-flight operations.

### 2. Deposit Lifecycle Integrity
- Verify the state machine transitions (`pending → confirmed → finalized`) are strictly enforced and cannot be bypassed or reversed.
- Check for race conditions in concurrent deposit processing.
- Verify that a deposit cannot be double-finalized or double-credited.
- Ensure deposits cannot be stuck in an intermediate state permanently (liveness).
- Check timeout/expiry handling for stale deposits.

### 3. Merkle Proof Verification
- Verify `BridgeProofVerifier` correctly validates Merkle inclusion proofs.
- Check for empty proof, single-element proof, and maximum-depth proof edge cases.
- Verify the proof verification algorithm matches the tree construction algorithm used on-chain.
- Check for second-preimage attacks on the Merkle tree.

### 4. Value Handling
- Verify deposit/withdrawal amounts are handled with `UInt256` precision and cannot overflow or underflow.
- Check that withdrawal hash computation (`ComputeWithdrawalHash`) uses the correct serialization format (32-byte LE for amounts).
- Verify that bridge fees, if any, are correctly deducted and cannot be manipulated.

### 5. Cross-Chain Consistency
- Verify that the off-chain `BridgeState` and on-chain `BridgeETH` contract maintain consistent views of bridge state.
- Check what happens if the Basalt chain reorganizes after a deposit is confirmed — can funds be double-spent?
- Verify that the bridge handles chain ID mismatches and prevents cross-chain replay.

### 6. Error Handling & Recovery
- Verify `BridgeException` usage and that error conditions are clearly distinguished.
- Check recovery paths for partial failures (e.g., signatures collected but not yet submitted).
- Verify that failed operations can be safely retried without side effects.

### 7. Test Coverage
- Review `tests/Basalt.Bridge.Tests/` for coverage of:
  - Happy path deposit and withdrawal flows
  - Insufficient signatures (below M threshold)
  - Duplicate/replayed signatures
  - Invalid Merkle proofs
  - Concurrent operations
  - Edge cases (zero amounts, max amounts, empty relayer sets)

---

## Key Context

- Bridge architecture: lock/unlock native BST tokens on Basalt side.
- M-of-N Ed25519 multisig: relayers independently sign attestations of cross-chain events.
- The on-chain `BridgeETH` contract (0x0107 at address `0x...1008`) handles lock/unlock, admin/pause, deposit lifecycle.
- `ComputeWithdrawalHash` serializes amounts as 32-byte little-endian (wire format change from 8 bytes after UInt256 migration).
- Bridge is a high-value target — vulnerabilities here could lead to direct loss of funds.

---

## Output Format

Write your findings to `audit/output/02-bridge.md` with the following structure:

```markdown
# Bridge Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Issues that must be fixed — fund loss, signature bypass, double-spend]

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
3. **Impact**: What could go wrong (quantify fund-loss risk where applicable)
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
