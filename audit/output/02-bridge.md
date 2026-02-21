# Bridge Layer Audit Report

## Executive Summary

The bridge layer provides a functional testnet MVP with good foundational patterns (domain-separated Merkle trees, replay protection, versioned hashes, thread safety). However, there are several significant issues: the Merkle proof verifier is implemented but never called during withdrawals, the off-chain `ComputeWithdrawalHash` defaults diverge from on-chain values making the two hash paths incompatible without explicit parameter overrides, and `Unlock()` never decrements locked balance tracking. The multisig relayer lacks a minimum quorum enforcement (`M > N/2`), which weakens the security guarantee if misconfigured.

---

## Critical Issues

### CRIT-01: Merkle Proof Never Verified During Withdrawal

- **Location**: `src/bridge/Basalt.Bridge/BridgeState.cs:67-84` (`Unlock()`)
- **Description**: `BridgeProofVerifier` is fully implemented with domain-separated hashing (BRIDGE-06) and DoS-bounded proof depth (BRIDGE-08), but neither `BridgeState.Unlock()` nor `BridgeETH.Unlock()` ever call it. The `BridgeWithdrawal.Proof` and `BridgeWithdrawal.StateRoot` fields are populated but ignored. The bridge relies entirely on M-of-N multisig for security, making the Merkle proof infrastructure dead code.
- **Impact**: If relayers are compromised (or threshold is set too low), there is no secondary verification layer. An attacker who controls `M` relayer keys can mint arbitrary withdrawals without any chain-state proof. The Merkle proof was presumably designed to prevent this.
- **Recommendation**: Call `BridgeProofVerifier.VerifyMerkleProof()` inside `Unlock()` before accepting the withdrawal. The leaf should be the serialized deposit data, the root should come from a trusted on-chain state commitment, and the proof should demonstrate the deposit's inclusion. If the proof system is intentionally deferred for testnet, document this clearly and add a `// TODO` with a tracking issue.
- **Severity**: Critical

---

## High Severity

### HIGH-01: ComputeWithdrawalHash Default Parameters Mismatch On-Chain Values

- **Location**: `src/bridge/Basalt.Bridge/BridgeState.cs:177` (signature) and `src/bridge/Basalt.Bridge/BridgeState.cs:77` (call site in `Unlock()`)
- **Description**: `BridgeState.ComputeWithdrawalHash()` has default parameters `chainId = 1` and `contractAddress = null` (which becomes `new byte[20]`, i.e., all zeros). The on-chain `BridgeETH.ComputeWithdrawalHash()` uses `Context.ChainId` and `Context.Self` (the actual contract address, `0x...1008`). The `Unlock()` method calls `ComputeWithdrawalHash(withdrawal)` without overriding these defaults. If relayers sign using the off-chain defaults and the on-chain contract verifies with real values, the hashes will never match.
- **Impact**: In production, relayer signatures produced with the off-chain code using defaults will fail verification on-chain. This creates a silent incompatibility between the two bridge halves. While the defaults can be overridden, the current call site in `Unlock()` (line 77) does not do so, creating a footgun for anyone wiring the relay pipeline.
- **Recommendation**: Either (a) remove the default parameters and force callers to provide `chainId` and `contractAddress` explicitly, or (b) store `chainId` and `contractAddress` as `BridgeState` instance fields (set at construction) and use them automatically. Add an integration test that verifies hash compatibility between `BridgeState.ComputeWithdrawalHash` and `BridgeETH.ComputeWithdrawalHash` for the same inputs.
- **Severity**: High

### HIGH-02: Unlock Does Not Decrement Locked Balance

- **Location**: `src/bridge/Basalt.Bridge/BridgeState.cs:67-84` (`Unlock()`)
- **Description**: `Lock()` (line 57) increments `_lockedBalances`, but `Unlock()` never decrements it. By contrast, the on-chain `BridgeETH.Unlock()` correctly decrements `_totalLocked` (line 292). Over time, `GetLockedBalance()` will monotonically increase, permanently overstating the locked amount.
- **Impact**: Any monitoring, accounting, or invariant checks based on `GetLockedBalance()` will be incorrect. This could mask insolvency or enable double-accounting exploits if the locked balance is used to authorize further operations.
- **Recommendation**: Add `_lockedBalances[tokenKey] -= amount` (or equivalent) inside `Unlock()` after verifying the withdrawal. Ensure the balance cannot underflow. Add a test that verifies `GetLockedBalance()` decreases after a successful withdrawal.
- **Severity**: High

### HIGH-03: No Minimum Quorum Enforcement (M > N/2)

- **Location**: `src/bridge/Basalt.Bridge/MultisigRelayer.cs:21-25` (constructor)
- **Description**: The `MultisigRelayer` constructor accepts any `threshold >= 1` regardless of the number of relayers. A 1-of-10 multisig means any single compromised relayer can authorize withdrawals. The audit objective states "ensure `M > N/2`" but this is not enforced. The on-chain `BridgeETH` constructor enforces `threshold >= 2` but also does not enforce `M > N/2`.
- **Impact**: A misconfigured threshold (e.g., 1-of-5) reduces the bridge's security to single-key security. If any one relayer key is compromised, all bridge funds can be drained.
- **Recommendation**: Either enforce `threshold > relayerCount / 2` dynamically (checking on each `AddRelayer`/`RemoveRelayer` call), or at minimum validate `threshold >= 2` in the constructor and validate `threshold <= relayerCount` before allowing verification. Add a method `ValidateConfiguration()` or enforce the invariant in `AddRelayer()`.
- **Severity**: High

### HIGH-04: ConfirmDeposit Ignores blockHeight Parameter

- **Location**: `src/bridge/Basalt.Bridge/BridgeState.cs:90-104` (`ConfirmDeposit()`)
- **Description**: `ConfirmDeposit(ulong nonce, ulong blockHeight)` accepts a `blockHeight` parameter but never uses it. The deposit's `BlockHeight` remains at its initial value (0, since `Lock()` sets `BlockHeight = 0` with a comment "Set by block executor"). This means the confirmation block height is lost.
- **Impact**: Without a stored confirmation block height, it's impossible to implement deposit expiry, timeout logic, or challenge windows based on when the deposit was confirmed. Any future liveness mechanism depends on this data.
- **Recommendation**: Add `deposit.BlockHeight = blockHeight;` (or a separate `ConfirmationBlockHeight` field) inside the `ConfirmDeposit()` method. Add a test that verifies the block height is persisted.
- **Severity**: High

---

## Medium Severity

### MED-01: No Deposit Expiry/Cancellation in Off-Chain State

- **Location**: `src/bridge/Basalt.Bridge/BridgeState.cs` (entire class)
- **Description**: The on-chain `BridgeETH` has a `CancelDeposit()` method (line 204) with `DepositExpiryBlocks = 50400` that allows the original sender to reclaim funds after ~7 days. The off-chain `BridgeState` has no equivalent. Deposits that cannot be relayed (e.g., due to relayer failures) will remain in `Pending` or `Confirmed` state indefinitely.
- **Impact**: Users whose deposits are stuck have no recourse through the off-chain system. The locked balance grows permanently for unfinalized deposits.
- **Recommendation**: Add a `CancelDeposit(ulong nonce)` method with a similar expiry check, or add a `MarkFailed(ulong nonce)` method that transitions deposits to the `Failed` status (which exists in the enum but is never used).
- **Severity**: Medium

### MED-02: BitConverter Endianness Dependency

- **Location**: `src/bridge/Basalt.Bridge/BridgeState.cs:198,206` (`ComputeWithdrawalHash()`)
- **Description**: `BitConverter.TryWriteBytes()` writes integers in the platform's native byte order. The comments document "LE_u32" and "LE_u64", and all modern x86/ARM64 platforms are little-endian, but there is no explicit `BitConverter.IsLittleEndian` guard. The on-chain `BridgeETH` has the same pattern.
- **Impact**: If the code ever runs on a big-endian platform, withdrawal hashes will silently differ, breaking all signature verification. This is unlikely for .NET 9 targets but violates defense-in-depth.
- **Recommendation**: Add an assertion or static constructor check: `if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Bridge requires little-endian");` or use `BinaryPrimitives.WriteUInt32LittleEndian` / `BinaryPrimitives.WriteUInt64LittleEndian` for explicit endianness.
- **Severity**: Medium

### MED-03: BridgeDeposit.Status Has No Type-Level State Machine Enforcement

- **Location**: `src/bridge/Basalt.Bridge/BridgeMessages.cs:64` (`Status` property)
- **Description**: `BridgeDeposit.Status` is a plain `{ get; set; }` property. The state machine (Pending -> Confirmed -> Finalized) is enforced only by `BridgeState.ConfirmDeposit()` and `BridgeState.FinalizeDeposit()`. Any code with a reference to the deposit object can set `Status` to any value, including invalid transitions like `Finalized -> Pending`.
- **Impact**: If any code outside `BridgeState` mutates a deposit's status (e.g., during serialization/deserialization or in a future module), the state machine guarantees are silently violated.
- **Recommendation**: Either make `Status` internal-set (only settable within the assembly) or move to an encapsulated method pattern where transitions are validated at the type level.
- **Severity**: Medium

### MED-04: No Pause Mechanism in Off-Chain BridgeState

- **Location**: `src/bridge/Basalt.Bridge/BridgeState.cs` (entire class)
- **Description**: The on-chain `BridgeETH` has `Pause()`/`Unpause()` with `RequireNotPaused()` guards on `Lock()` and `Unlock()`. The off-chain `BridgeState` has no equivalent. If a vulnerability is discovered, there is no way to halt the off-chain relay without shutting down the entire process.
- **Impact**: Slower incident response in case of bridge exploit.
- **Recommendation**: Add a `bool _paused` flag with `Pause()`/`Unpause()` methods and check it in `Lock()` and `Unlock()`.
- **Severity**: Medium

### MED-05: Relayer Set Changes During In-Flight Operations

- **Location**: `src/bridge/Basalt.Bridge/MultisigRelayer.cs:44-49` (`RemoveRelayer()`)
- **Description**: If a relayer is removed between when signatures are collected and when `VerifyMessage()` is called, previously valid signatures become invalid. There is no grace period or epoch mechanism for relayer set changes.
- **Impact**: In-flight withdrawal operations could fail if the relayer set is modified concurrently. An admin could (maliciously or accidentally) remove relayers to block legitimate withdrawals.
- **Recommendation**: Consider adding an epoch or nonce to the relayer set, where signatures collected under epoch N remain valid for verification under epoch N even if the set changes in epoch N+1. Alternatively, add a delay/queuing mechanism for relayer removal.
- **Severity**: Medium

---

## Low Severity / Recommendations

### LOW-01: RelayerSignature Does Not Validate Byte Array Lengths

- **Location**: `src/bridge/Basalt.Bridge/BridgeMessages.cs:96-102`
- **Description**: `RelayerSignature.PublicKey` and `RelayerSignature.Signature` accept arbitrary-length byte arrays. Ed25519 public keys must be 32 bytes and signatures 64 bytes. While `MultisigRelayer.VerifyMessage()` will reject invalid lengths downstream (Ed25519Signer.Verify will fail), early validation would produce clearer error messages.
- **Impact**: Confusing failure modes; invalid signatures waste computation.
- **Recommendation**: Add length validation in the `init` setters or add a `Validate()` method.
- **Severity**: Low

### LOW-02: BridgeWithdrawal.Signatures Is a Mutable List

- **Location**: `src/bridge/Basalt.Bridge/BridgeMessages.cs:89`
- **Description**: `Signatures` is `List<RelayerSignature>` with `{ get; init; }`, but `List<T>` is mutable — callers can `Add()`, `Remove()`, or `Clear()` signatures after construction. The test at `BridgeMessagesTests:185-208` explicitly verifies this mutability.
- **Impact**: Low in practice, but defense-in-depth suggests immutable collections for signed data.
- **Recommendation**: Consider using `IReadOnlyList<RelayerSignature>` for the public API.
- **Severity**: Low

### LOW-03: Failed Status Never Used

- **Location**: `src/bridge/Basalt.Bridge/BridgeMessages.cs:25`
- **Description**: `BridgeTransferStatus.Failed` exists in the enum but is never assigned anywhere in the codebase. No code path sets a deposit to `Failed`, and no tests verify `Failed` behavior.
- **Impact**: Dead code; may confuse consumers of the API.
- **Recommendation**: Either implement failure transitions (e.g., for expired or invalid deposits) or remove the status value until it's needed.
- **Severity**: Low

### LOW-04: GetPendingDeposits Uses LINQ Allocation on Every Call

- **Location**: `src/bridge/Basalt.Bridge/BridgeState.cs:139-143`
- **Description**: `GetPendingDeposits()` allocates a new filtered, sorted list on every call. Under high deposit volume, this could create GC pressure.
- **Impact**: Performance only; no correctness issue.
- **Recommendation**: Acceptable for testnet. For production, consider a pre-sorted secondary index.
- **Severity**: Low

### LOW-05: No Threshold <= RelayerCount Validation

- **Location**: `src/bridge/Basalt.Bridge/MultisigRelayer.cs:21-25`
- **Description**: A `MultisigRelayer` with `threshold=5` but only 2 registered relayers will silently reject all verifications. There is no upfront warning or validation.
- **Impact**: Silent misconfiguration; always-failing verification.
- **Recommendation**: Add a `bool IsConfigured => RelayerCount >= Threshold;` property, or throw in `VerifyMessage()` if the configuration is impossible.
- **Severity**: Low

---

## Test Coverage Gaps

| Gap | Description |
|---|---|
| **No Merkle proof verification in Unlock flow** | Since `Unlock()` doesn't call `BridgeProofVerifier`, there are no tests for end-to-end proof-verified withdrawals. |
| **No cross-hash compatibility test** | No test verifies that `BridgeState.ComputeWithdrawalHash(w, chainId, contractAddr)` produces the same output as `BridgeETH.ComputeWithdrawalHash(nonce, recipient, amount, stateRoot)` for identical inputs. |
| **No ComputeWithdrawalHash with non-default parameters** | All tests use defaults (`chainId=1`, `contractAddress=null`). No test exercises explicit chain ID or contract address. |
| **No max proof depth rejection test** | `BridgeProofVerifier` has `MaxProofDepth = 64` (BRIDGE-08) but no test submits a proof exceeding this depth. |
| **No concurrent Unlock test** | `BridgeState.Unlock()` is synchronized via `lock`, but no concurrency test verifies that concurrent unlock attempts on the same nonce result in exactly one success. |
| **No ConfirmDeposit blockHeight persistence test** | No test verifies whether the `blockHeight` parameter to `ConfirmDeposit()` is actually stored (it isn't — see HIGH-04). |
| **No Failed status transition test** | `BridgeTransferStatus.Failed` is never exercised in any test. |
| **No locked balance after Unlock test** | No test checks `GetLockedBalance()` after a withdrawal (because it currently doesn't decrement — see HIGH-02). |
| **No zero-amount withdrawal test** | No test submits a `BridgeWithdrawal` with `Amount = 0` to `BridgeState.Unlock()`. |
| **No Finalized -> re-Finalize test** | While `ConfirmDeposit` is tested for re-confirm, `FinalizeDeposit` is not tested for double-finalization on an already-finalized deposit. |

---

## Positive Findings

1. **Domain-separated Merkle tree (BRIDGE-06)**: `BridgeProofVerifier` correctly implements RFC 6962 domain separation with `0x00` prefix for leaves and `0x01` for internal nodes, preventing second-preimage attacks. Well-implemented.

2. **Versioned withdrawal hash (BRIDGE-12)**: The `0x02` version byte in `ComputeWithdrawalHash()` enables future format migrations without breaking existing signatures.

3. **Cross-chain replay protection (BRIDGE-01)**: Chain ID and contract address are included in the withdrawal hash, preventing signatures from being replayed across chains or contracts (when correct parameters are used).

4. **Strict length validation (BRIDGE-03)**: `ComputeWithdrawalHash()` rejects non-20-byte recipients and non-32-byte state roots with clear error messages, preventing ambiguous serialization.

5. **Proof depth bound (BRIDGE-08)**: `MaxProofDepth = 64` prevents DoS via excessively deep proof arrays.

6. **Replay protection**: `BridgeState._processedWithdrawals` HashSet correctly prevents double-processing of the same deposit nonce.

7. **Duplicate signature detection**: `MultisigRelayer.VerifyMessage()` uses `seenRelayers` HashSet to prevent the same relayer from counting twice.

8. **Thread safety**: All mutable state in `BridgeState` and `MultisigRelayer` is protected by `lock` statements, with concurrent tests verifying correctness.

9. **Zero-amount rejection**: `BridgeState.Lock()` rejects zero amounts upfront.

10. **Comprehensive Merkle proof tests**: 21 tests cover valid proofs, invalid proofs, non-power-of-2 trees, large trees, tampered data, truncated proofs, and edge cases. Thorough and well-structured.

11. **UInt256 amounts throughout**: All value fields correctly use `UInt256` for full 256-bit precision, matching the on-chain contract.

12. **Clean state machine enforcement**: `ConfirmDeposit()` and `FinalizeDeposit()` correctly enforce `Pending -> Confirmed -> Finalized` transitions, rejecting out-of-order transitions (BRIDGE-05).
