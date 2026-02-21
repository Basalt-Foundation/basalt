# Basalt Security & Quality Audit — Consensus Layer

## Scope

Audit the Byzantine Fault Tolerant consensus engine, validator management, staking, slashing, and epoch transitions:

| Project | Path | Description |
|---|---|---|
| `Basalt.Consensus` | `src/consensus/Basalt.Consensus/` | BFT consensus, pipelined consensus, epochs, validator sets, staking, slashing |

Corresponding test project: `tests/Basalt.Consensus.Tests/`

---

## Files to Audit

### Consensus Engines
- `BasaltBft.cs` (~754 lines) — 3-phase BFT engine (PREPARE → PRE-COMMIT → COMMIT)
- `PipelinedConsensus.cs` (~687 lines) — Pipelined BFT with view changes, `_minNextView` anti-double-sign

### Validator & Epoch Management
- `ValidatorSet.cs` (~166 lines) — `ValidatorInfo`, validator set representation
- `EpochManager.cs` (~253 lines) — Epoch boundary detection, validator set rebuild
- `WeightedLeaderSelector.cs` (~101 lines) — BLAKE3-weighted deterministic leader election

### Staking & Slashing
- `Staking/StakingState.cs` (~295 lines) — Validator registration, delegation, unbonding, `IStakingState` implementation
- `Staking/SlashingEngine.cs` (~161 lines) — Double-sign (100%), inactivity (5%), invalid block (1%) penalties

---

## Audit Objectives

### 1. BFT Safety (CRITICAL)
- Verify that the 3-phase BFT protocol guarantees safety with `f < n/3` Byzantine validators (where `n` is the validator set size).
- Check vote counting: does the protocol require `2f+1` votes (i.e., >2/3) for each phase transition?
- Verify that equivocation (voting for conflicting blocks in the same view) is detected and results in slashing.
- Check that a committed block can never be reverted (finality guarantee).
- Verify that consensus state transitions (PREPARE → PRE-COMMIT → COMMIT) are strictly sequential and cannot be skipped.

### 2. View Change Protocol (CRITICAL)
- Verify that view changes correctly handle leader failures without allowing safety violations.
- Check the `_minNextView` mechanism: verify it correctly prevents same-view re-proposals that cause false double-sign detection.
- Verify that view change votes use a distinct phase key (`(VotePhase)0xFF`) to avoid collision with consensus PREPARE votes.
- Check that a malicious validator cannot trigger unnecessary view changes to stall the network (liveness attack).
- Verify that after a view change, the new leader proposes for the correct view/block number.

### 3. Pipelined Consensus
- Verify `PipelinedConsensus` correctly pipelines block proposals without violating safety.
- Check the `ConsensusRound` inner class for proper state isolation between concurrent rounds.
- Verify `OnBehindDetected` correctly triggers sync and resumes consensus.
- Check `UpdateLastFinalizedBlock` behavior after sync catches up.
- Verify that `UpdateValidatorSet()` atomically swaps the validator set and clears in-progress state.

### 4. Leader Election
- Verify `WeightedLeaderSelector` produces deterministic results given the same view and validator set.
- Check that leader selection is proportional to stake weight — a validator with 2x stake should be selected ~2x as often.
- Verify that the BLAKE3-based randomness is unpredictable but verifiable.
- Check edge cases: single validator, all validators with equal stake, validator with zero stake.

### 5. Staking State Machine
- Verify `RegisterValidator` enforces minimum stake (`MinValidatorStake = 100,000`).
- Check `DelegateStake` and `RequestUnbond` for correct balance accounting.
- Verify `ProcessUnbonding` respects the unbonding period and releases funds correctly.
- Check for reentrancy or state inconsistency during concurrent staking operations.
- Verify that `IStakingState` interface methods (`GetActiveValidators`, `GetStakeInfo`, `GetTotalStake`) return consistent snapshots.

### 6. Slashing Correctness
- Verify double-sign detection: 100% slash is correctly applied and the validator is removed.
- Verify inactivity tracking: `InactivityThresholdBlocks = 100` blocks of inactivity triggers 5% slash.
- Verify invalid block detection: 1% slash for proposing invalid blocks.
- Check that slashed validators cannot continue participating in consensus.
- Verify that slashing events are properly recorded and cannot be replayed.
- Check for false positive slashing: verify that legitimate validator behavior is never penalized.

### 7. Epoch Transitions
- Verify `EpochManager` correctly detects epoch boundaries (`blockNumber % EpochLength == 0`).
- Check validator set rebuild: `GetActiveValidators()` → cap at `ValidatorSetSize` → sort by address ascending.
- Verify `TransferIdentities()` correctly preserves PeerId/PublicKey/BlsPublicKey across set rebuilds.
- Check that epoch transitions during sync or block-payload processing are handled correctly.
- Verify that a new validator joining via `ValidatorRegister` tx is correctly included in the next epoch.

### 8. Self-Vote Handling
- Verify that nodes count their own votes locally before broadcasting (PREPARE, PRE-COMMIT, COMMIT).
- Check that self-votes are not double-counted when received back via gossip.

### 9. Concurrency & Thread Safety
- Check for race conditions in vote collection, view changes, and state updates.
- Verify that consensus state is protected against concurrent access from network message handlers and block production.

### 10. Test Coverage
- Review `tests/Basalt.Consensus.Tests/` for:
  - Normal 3-phase commit flow
  - View change triggered by leader timeout
  - False double-sign prevention (`_minNextView`)
  - Slashing for actual double-sign
  - Epoch transitions with validator set changes
  - Leader election distribution over many views
  - Edge cases: 1 validator, minimum quorum, max validators

---

## Key Context

- `BasaltBft` is the basic 3-phase BFT; `PipelinedConsensus` adds pipelining and view changes.
- Self-votes must be counted locally (discovered during P2P debugging — nodes that didn't self-vote stalled).
- `_minNextView` was added to fix false double-sign detection after view changes.
- `EpochManager` in `NodeCoordinator` triggers `ApplyEpochTransition()` which swaps validator set, rewires leader selector, updates consensus, and resets tracking.
- `IStakingState` is defined in `Basalt.Core` to break the Execution→Consensus circular dependency.
- `WeightedLeaderSelector` uses `BLAKE3(view ++ validator_address)` weighted by stake.
- Docker devnet: 4 validators, `EpochLength=100`, `ValidatorSetSize=4`.

---

## Output Format

Write your findings to `audit/output/05-consensus.md` with the following structure:

```markdown
# Consensus Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Safety violations, finality breaks, exploitable slashing]

## High Severity
[Significant security or correctness issues]

## Medium Severity
[Issues to address]

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
3. **Impact**: What could go wrong (safety violation, liveness stall, incorrect slashing)
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
