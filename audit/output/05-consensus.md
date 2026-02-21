# Consensus Layer Audit Report

## Executive Summary

The Basalt consensus layer implements a well-structured 3-phase BFT protocol (BasaltBft) and an advanced pipelined variant (PipelinedConsensus) with domain-separated BLS signatures, aggregate quorum certificates, and proper view change handling. The staking/slashing subsystem is correctly synchronized under locks with atomic slash application. **No critical safety violations were found** — the protocol correctly enforces >2/3 quorum, domain-separates all signatures, and prevents cross-phase replay. Several medium-severity concurrency and liveness issues warrant attention, along with a few design edge cases that could affect larger deployments.

## Critical Issues

**No critical safety violations identified.**

The BFT protocol satisfies the key safety properties:
- Quorum threshold `(n*2/3)+1` is correct for all validator set sizes (verified by exhaustive tests for n=1..20).
- Domain-separated signing payloads (`[phase || view || blockNumber || blockHash]`) prevent cross-phase/cross-view signature replay.
- Aggregate signature verification checks bitmap popcount against quorum threshold before accepting QCs.
- View changes use a distinct `0xFF` phase tag to avoid collision with consensus PREPARE votes.

## High Severity

### H-01: ViewChange signature not verified in BasaltBft.HandleViewChange

**Location:** `BasaltBft.cs:372-442`
**Description:** `HandleViewChange` accepts view change messages without verifying the `VoterSignature` on the message. While view change votes are tracked by `SenderId` (a PeerId derived from a public key established during the P2P handshake), the BLS signature on the view change message itself is never verified. A compromised P2P layer or man-in-the-middle could forge `ViewChangeMessage` objects with arbitrary `SenderId` values, potentially triggering spurious view changes.

Compare with `HandleVote` (line 316-323) and `HandleProposal` (line 265-271), which both verify BLS signatures. This check is also missing in `PipelinedConsensus.HandleViewChange` (line 314-393).

**Impact:** An attacker able to inject messages into the P2P gossip layer (without holding validator private keys) could forge view change votes and force unnecessary view changes, stalling consensus (liveness attack). In the worst case with n=4/f=1, forging just 3 view change messages (from 3 PeerIds) would trigger a view change.

**Recommendation:** Verify `viewChange.VoterSignature` against the validator's BLS public key (looked up from `_validatorSet` by `viewChange.SenderId`) using the view change signing payload `[0xFF || proposedView]`. Reject messages that fail verification. The same fix is needed in `PipelinedConsensus.HandleViewChange`.

**Severity:** High

---

### H-02: ValidatorSet and ValidatorInfo are not thread-safe

**Location:** `ValidatorSet.cs:24-35`, `ValidatorInfo.cs:9-17`
**Description:** `ValidatorSet` uses mutable `Dictionary<PeerId, ValidatorInfo>` and `List<ValidatorInfo>` internally, and `ValidatorInfo` has mutable properties (`PeerId`, `PublicKey`, `BlsPublicKey`, `Stake`). These are accessed from consensus message handlers (network threads) and from `UpdateValidatorIdentity` / `TransferIdentities` without synchronization.

While `BasaltBft` and `PipelinedConsensus` use `lock` for vote sets, reads from `_validatorSet` (e.g., `IsValidator`, `GetLeader`, `GetByPeerId`) happen without any lock and can race with `UpdateValidatorIdentity` calls from the handshake protocol.

**Impact:** Race conditions could cause dictionary corruption, leading to `KeyNotFoundException` crashes or incorrect leader selection during identity updates. This is most likely during epoch transitions or when new peers connect.

**Recommendation:** Either:
1. Make `ValidatorSet` immutable (create new instances instead of mutating), or
2. Add `lock` synchronization to all `_byPeerId` / `_byAddress` dictionary accesses, or
3. Use `ConcurrentDictionary` for the internal lookups.

**Severity:** High

---

### H-03: SlashingEngine._slashingHistory is not thread-safe

**Location:** `SlashingEngine.cs:18`
**Description:** `_slashingHistory` is a plain `List<SlashingEvent>` that is appended to in `ApplySlash` (line 109) without any lock. If two slashing operations execute concurrently (e.g., double-sign detection from two different message handlers), the list can corrupt.

**Impact:** List corruption, potential `IndexOutOfRangeException`, or lost slashing records.

**Recommendation:** Use a `ConcurrentBag<SlashingEvent>` or protect with a lock. Since `StakingState.ApplySlash` already serializes the stake mutation, adding a lock around the history append would be minimal overhead.

**Severity:** High

## Medium Severity

### M-01: PipelinedConsensus.HandleProposal does not verify block hash correctness

**Location:** `PipelinedConsensus.cs:210-218`
**Description:** When a round already exists for the proposed block number (created by a previous proposal or internally), `HandleProposal` overwrites `round.BlockHash` and `round.BlockData` (lines 234-235) without checking whether the existing round already has different data. If a malicious leader sends conflicting proposals for the same block, the non-leader will silently accept the second one and potentially vote for both.

In `BasaltBft.HandleVote` this is partially mitigated by the `F-CON-02` check (line 310-314: "vote.BlockHash != _currentProposalHash"), but in `PipelinedConsensus` the round's `BlockHash` gets overwritten before vote checking occurs.

**Impact:** A Byzantine leader could send two different proposals for the same block number. Honest validators might vote for both, leading to conflicting PREPARE votes. While the BLS aggregate signature verification should ultimately prevent finalization of conflicting blocks (different hashes produce different signatures), this is still a protocol hygiene issue.

**Recommendation:** If a round already exists with a different `BlockHash`, reject the second proposal rather than overwriting.

**Severity:** Medium

---

### M-02: PipelinedConsensus.CreateVote double-records self-vote

**Location:** `PipelinedConsensus.cs:593-618`
**Description:** `CreateVote` calls `RecordVote` at line 615 to self-record the vote. However, callers of `CreateVote` (such as `CheckPhaseTransition` at line 477) are within a `lock(votes)` block, and the subsequent vote is returned and may also be processed by `HandleVote`. Additionally, in `HandleProposal` (line 242), after calling `CreateVote`, the leader's PREPARE vote was already recorded via `RecordVote` at line 239.

Since `RecordVote` uses `HashSet<PeerId>.Add` (which is idempotent for duplicates), this doesn't cause double-counting. However, `PrepareSignatures` / `PreCommitSignatures` / `CommitSignatures` are `List<>` (not sets), so duplicate signatures are recorded.

**Impact:** Duplicate BLS signatures in aggregation lists. If the same signature is aggregated twice, the aggregate will be incorrect, potentially causing verification failures. In practice this may not cause failures if the aggregation handles duplicates gracefully, but it's technically incorrect.

**Recommendation:** Check for duplicate entries in the signature lists, or use a dictionary keyed by public key to avoid duplicates.

**Severity:** Medium

---

### M-03: WeightedLeaderSelector.StakeToWeight truncates large stakes

**Location:** `WeightedLeaderSelector.cs:74-88`
**Description:** `StakeToWeight` reads bytes 24-31 of the UInt256 (the most significant 8 bytes of the little-endian representation). For stakes that fit in a `ulong` (< 2^64), this works correctly since the LE representation stores the value in bytes 0-7 and bytes 24-31 are zero. The function returns `weight == 0 ? 1 : weight`.

For typical staking amounts (e.g., `100000000000000000000000` = 100K tokens with 18 decimals), the LE bytes 0-7 contain the lower 64 bits, while bytes 24-31 are zero. This means **all validators with stakes < 2^64 have weight 1 regardless of their actual stake**, effectively making weighted selection equivalent to equal-weight round-robin.

The `StakeToWeight` function reads the wrong end of the UInt256. It should read bytes 0-7 (the least significant bytes in LE format) to capture the meaningful part of the stake value.

**Impact:** Leader selection is not proportional to stake for typical staking amounts. All validators get equal selection probability regardless of their stake. The existing test `SelectLeader_HigherStake_SelectedMoreOften` passes because it uses small UInt256 values (1000, 10000) that fit in the lower 8 bytes, and the BLAKE3-based seed distribution happens to select the higher-staked validator more often — but this is coincidental, not causal.

Wait — re-analyzing: for `new UInt256(10000)`, the LE bytes would be: `[10 27 00 00 ... 00]` in bytes 0-7. Bytes 24-31 would be all zero. So `StakeToWeight` returns 1 for both validators. The test passes because with equal weights, BLAKE3 randomness still distributes unevenly across only 1000 views. Let me verify: `new UInt256(10000)` → bytes 0-1 are `0x2710`, bytes 24-31 are `0x00...00`. So weight = 0, fallback to 1. Both validators have weight 1. The test just asserts `counts[0] > counts[1]`, which happens to pass by chance.

**Recommendation:** Change `StakeToWeight` to read bytes 0-7 (LE little-end) instead of bytes 24-31:
```csharp
for (int i = 7; i >= 0; i--)
    weight = (weight << 8) | bytes[i];
```
This correctly captures the lower 64 bits of the UInt256 in LE format, which covers stakes up to ~18.4 exatokens.

**Severity:** Medium

---

### M-04: Bitmap-based validator index limited to 64 validators

**Location:** `ValidatorSet.cs:107-114`, `EpochManager.cs:165-169`, `PipelinedConsensus.cs:572-582`
**Description:** The commit voter bitmap is a `ulong` (64-bit), which limits the validator set to 64 members. `EpochManager.BuildValidatorSetFromStaking` correctly caps at 64 with a warning (line 167-169), but `ChainParameters` defaults `ValidatorSetSize` to 100 for mainnet (line 51), creating a mismatch.

**Impact:** On mainnet with 100 validators, the effective set would be silently capped to 64, and the remaining 36 validators (by stake) would be excluded. Additionally, validators at index >= 64 cannot be tracked for inactivity slashing (line 240-241 of EpochManager: `if (validator.Index >= 64) continue`).

**Recommendation:** Either:
1. Change the mainnet `ValidatorSetSize` default to 64, or
2. Replace the `ulong` bitmap with a `BitArray` or `byte[]` to support larger sets, or
3. Add a validation check at startup that rejects `ValidatorSetSize > 64`.

**Severity:** Medium

---

### M-05: EpochManager.SlashInactiveValidators uses address-to-index mapping that may not match

**Location:** `EpochManager.cs:207-252`
**Description:** The inactivity slashing logic counts signed blocks per validator **index** (lines 222-230), then slashes by iterating over `_currentSet.Validators` and using `validator.Index` to look up the count. However, the bitmap is recorded against the validator set that was active **during** the epoch, while `_currentSet` is the set that was active at the **start** of the epoch. If `UpdateValidatorSet` was called mid-epoch (which shouldn't happen given the current architecture but is worth guarding against), the index mapping could be stale.

More concretely: the bitmap is recorded by `NodeCoordinator` after block finalization, using the **current** validator set's indices. But `RecordBlockSigners` stores flat bitmaps without identifying which validator set they belong to. If validators are reindexed within an epoch (e.g., via manual `UpdateValidatorSet` call), the bitmap bits would map to wrong validators.

**Impact:** Incorrect inactivity slashing — wrong validators could be penalized. Currently mitigated by the fact that `UpdateValidatorSet` only happens at epoch boundaries.

**Recommendation:** Add a guard that `RecordBlockSigners` only accepts bitmaps for the current epoch, or store the epoch number alongside each bitmap entry.

**Severity:** Medium

---

### M-06: BasaltBft.HandleViewChange does not verify sender is a validator

**Location:** `BasaltBft.cs:372-442`
**Description:** `HandleViewChange` adds `viewChange.SenderId` to the vote set without checking `_validatorSet.IsValidator(viewChange.SenderId)`. Compare with `HandleVote` (line 303-307) which does verify. A non-validator peer could send view change messages that count toward the quorum.

Same issue in `PipelinedConsensus.HandleViewChange` (line 314-393).

**Impact:** Non-validator peers could contribute to view change quorum, potentially causing unwanted view changes. With n=4 and quorum=3, an attacker spoofing 3 non-validator PeerIds could force a view change.

**Recommendation:** Add `if (!_validatorSet.IsValidator(viewChange.SenderId)) return null;` at the top of both `HandleViewChange` implementations.

**Severity:** Medium

## Low Severity / Recommendations

### L-01: ProcessUnbonding uses O(n) removal from List

**Location:** `StakingState.cs:130-143`
**Description:** `ProcessUnbonding` uses `List.Remove` in a loop, which is O(n) per removal (O(n^2) total for k completed entries). With many unbonding entries this could be slow.

**Recommendation:** Use `_unbondingQueue.RemoveAll(e => e.UnbondingCompleteBlock <= currentBlock)` or partition the list.

**Severity:** Low

---

### L-02: BasaltBft vote signatures tracked in List with no deduplication

**Location:** `BasaltBft.cs:326-331`
**Description:** `_voteSignatures` uses `List<(byte[], byte[])>` which allows duplicate entries if the same validator sends the same vote twice. The PeerId deduplication via `HashSet<PeerId>` in `_votes` prevents double-counting the vote, but the signature list may contain duplicates which get aggregated.

**Recommendation:** Use a dictionary keyed by PeerId or public key for signature tracking.

**Severity:** Low

---

### L-03: EpochManager.BuildValidatorSetFromStaking uses placeholder keys that could collide

**Location:** `EpochManager.cs:180-195`
**Description:** Placeholder private keys are constructed from the validator address: `key[0..19] = address, key[31] = 1`. For validators with addresses that share the first 20 bytes (impossible by construction, but theoretically), the placeholder BLS public keys would collide. More practically, the placeholder BLS keys are generated from deterministic but non-secret data, which is fine since `TransferIdentities` replaces them — but if `TransferIdentities` fails to match (new validator not in previous set), the placeholder keys remain.

**Impact:** New validators joining the set have placeholder BLS keys until the next P2P handshake updates their identity. During this window they cannot produce valid BLS signatures for consensus.

**Recommendation:** Document this explicitly or block new validators from participating in consensus until their real identity is established.

**Severity:** Low

---

### L-04: Auto-join view change condition in BasaltBft could be tighter

**Location:** `BasaltBft.cs:390-392`
**Description:** Auto-join triggers when `viewChange.ProposedView > _currentView && _viewChangeRequestedForView == _currentView`. The condition `_viewChangeRequestedForView == _currentView` checks that this node has already timed out for the **current** view. However, if the node has moved to a new view via a previous view change, `_viewChangeRequestedForView` might still be set to the old view, preventing auto-join for the new view even after timeout.

This is mitigated because `_viewChangeRequestedForView` is reset to `null` on successful view change (line 400), but the interaction is subtle.

**Impact:** Potential missed auto-join in edge cases, slightly reducing liveness.

**Recommendation:** Add a comment documenting the invariant, or explicitly clear `_viewChangeRequestedForView` in all state transitions.

**Severity:** Low

---

### L-05: BasaltBft.StartRound clears all vote state unconditionally

**Location:** `BasaltBft.cs:115-128`
**Description:** `StartRound` calls `_votes.Clear()` and `_voteSignatures.Clear()`, which removes any pre-arrived votes for future views. This means if votes arrive before `StartRound` is called (e.g., during sync), they are lost.

**Impact:** Minor liveness impact — pre-arrived votes must be re-sent after sync.

**Recommendation:** Consider selective clearing (only clear votes for past views/blocks) as is done in `HandleViewChange`.

**Severity:** Low

---

### L-06: Consensus signing payload doesn't include chain ID

**Location:** `BasaltBft.cs:701-709`, `PipelinedConsensus.cs:643-652`
**Description:** The domain-separated signing payload is `[phase || view || blockNumber || blockHash]`. It does not include the chain ID. If two Basalt networks run with the same validator keys (e.g., mainnet and testnet mirror), a signature from one network could be replayed on the other.

**Impact:** Cross-chain signature replay. In practice, validator keys should differ between networks, but defense-in-depth suggests including the chain ID.

**Recommendation:** Add the chain ID to the signing payload: `[chainId || phase || view || blockNumber || blockHash]`.

**Severity:** Low

## Test Coverage Gaps

### TG-01: No test for HandleAggregateVote with invalid/forged aggregate signature
The test suite verifies the happy path of aggregate vote handling but doesn't test rejection of forged aggregate signatures. A test should verify that `HandleAggregateVote` rejects an aggregate with a valid bitmap but tampered `AggregateSignature`.

### TG-02: No concurrent stress test for vote handling
All tests are sequential. A concurrent test where multiple threads submit votes simultaneously would verify thread safety of `_votes` dictionaries and vote counting.

### TG-03: No test for PipelinedConsensus.HandleProposal conflicting proposals
No test verifies behavior when a Byzantine leader sends two different proposals for the same block number. This relates to M-01.

### TG-04: No test for view change signature verification
Since H-01 identifies that view change signatures are not verified, there's no test that would catch a forged view change message. Once H-01 is fixed, tests should verify rejection of view changes with invalid signatures.

### TG-05: No test for WeightedLeaderSelector with realistic mainnet stake amounts
The `HigherStake_SelectedMoreOften` test uses small values (1000, 10000) that mask the M-03 bug. A test with realistic 18-decimal stakes (e.g., `UInt256.Parse("100000000000000000000000")` vs `UInt256.Parse("200000000000000000000000")`) would expose the `StakeToWeight` truncation issue.

### TG-06: No test for epoch-boundary inactivity slashing with validator index >= 64
The skip-logic at `EpochManager.cs:240` (`if (validator.Index >= 64) continue`) is untested.

### TG-07: No test for PipelinedConsensus.OnBehindDetected trigger
`HandleProposal` fires `OnBehindDetected` when a proposal is far ahead, but this path is not covered by tests.

### TG-08: No test for delegator undelegation after slashing
The staking tests cover delegation and slashing independently but don't test what happens to delegator records after a validator is fully slashed (100% double-sign). `StakeInfo.Delegators` still contains entries with recorded amounts that no longer exist in the stake.

### TG-09: No test for `BasaltBft.UpdateValidatorSet` during active consensus
Tests only call `UpdateValidatorSet` when consensus is idle. A test should verify that calling it during an active round (e.g., in `Preparing` state) correctly resets to `Idle`.

## Positive Findings

### P-01: Domain-separated BLS signatures (F-CON-01)
All consensus signatures include `[phase || view || blockNumber || blockHash]`, preventing cross-phase and cross-view replay attacks. View change messages use a distinct `0xFF` tag. This is a textbook-correct implementation.

### P-02: Atomic slash application (F-CON-03)
`StakingState.ApplySlash` runs the entire read-modify-write under a single lock, preventing the TOCTOU race that existed in earlier designs. The slash amount is capped at the validator's total stake to prevent underflow.

### P-03: Proper self-vote handling
Both `BasaltBft` and `PipelinedConsensus` correctly count the leader's own PREPARE vote locally before broadcasting. This prevents the stall where the leader waits for its own vote to come back via gossip.

### P-04: Sequential finalization ordering in PipelinedConsensus
The `TryFinalizeSequential` method correctly buffers out-of-order finalizations and drains them in sequence, ensuring blocks are always finalized in order. The `_pendingFinalizations` buffer prevents gaps in the chain.

### P-05: False double-sign prevention (`_minNextView`)
The `PipelinedConsensus._minNextView` mechanism correctly ensures that after a view change, new rounds use a higher view number. This prevents the subtle bug where `StartRound(blockN)` would reuse view=blockN after a view change, causing `NodeCoordinator` to detect a "double proposal" for the same view.

### P-06: Fast-forward with strict safety guards
`BasaltBft.HandleProposal` allows fast-forward only when: (1) future view, (2) `Proposing` state (no active consensus), (3) same block number. The leader signature is verified before fast-forwarding. This prevents liveness deadlocks while maintaining safety.

### P-07: Aggregate QC architecture
The leader-collected voting pattern (individual votes → leader → aggregate QC → broadcast) reduces O(n^2) message complexity to O(n) per phase. BLS signature aggregation is correctly verified by decomposing the bitmap into constituent public keys.

### P-08: View change auto-join with timeout guard
The auto-join mechanism prevents parity-split deadlocks while the timeout guard prevents cascade propagation. The tests comprehensively cover the parity-split resolution scenario.

### P-09: Comprehensive staking state machine
`StakingState` correctly handles all edge cases: registration at exact minimum, partial unstake with minimum check, full unstake deactivation, delegation to inactive validators (rejected), and the unbonding period lifecycle. All operations are protected by the same lock.

### P-10: Deterministic inactivity slashing via commit bitmaps
`EpochManager` uses finalized block commit bitmaps (identical across all nodes) for inactivity detection, ensuring deterministic slashing. The snapshot-and-clear pattern prevents cross-epoch contamination.
