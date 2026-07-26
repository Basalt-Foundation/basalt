# Compliance on the replay path: why COMPL-C01 is not simple wiring

## Summary

COMPL-C02 (bind the identity registry and sanctions list to the Governance address) is done and safe.

COMPL-C01 (make RPC and standalone nodes run the same compliance checks as validators) looked like a
one-line fix (pass the compliance engine as the executor's 4th argument). A design review of the real
execution paths shows it is not. Naively wiring the verifier into the replay path introduces silent
state divergence that the node cannot detect today. COMPL-C01 is therefore deferred until the
prerequisites below are in place. This note records why, so the wiring is not "fixed" unsafely later.

## The enforcement point

`TransactionExecutor` is the only place compliance runs. It calls `VerifyProofs` and
`CheckTransferCompliance` only when its `IComplianceVerifier` field is non-null
(`TransactionExecutor.cs:51-112`). A failed check returns a failed receipt with no state change and does
not throw, so a compliance failure drops a single transaction. It never rejects the block.

- Validators wire the verifier (`NodeCoordinator.cs:704` builds the executor with `_complianceVerifier`).
- RPC builds its executor with the 3-argument constructor (`Program.cs:518`), so the verifier is null and
  the whole compliance path is skipped.
- Standalone likewise runs without a verifier.

Today this asymmetry is harmless: no proof requirements are registered and no transaction carries proofs,
so even the validator path never enters `VerifyProofs`, and `CheckTransferCompliance` short-circuits to
success because no policy exists for the token sentinel.

## Why naive COMPL-C01 wiring is unsafe

The replay path (RPC sync, standalone, and a validator's own catch-up) runs through
`BlockApplier.ApplyBlock` / `ApplyBatch`, not through `HandleBlockFinalized`. Three problems follow once
proof requirements exist:

1. **No state-root gate on replay.** `BlockApplier` calls the single-argument `_chainManager.AddBlock`
   (`BlockApplier.cs:142` and `:269`), so `computedStateRoot` is null and the check at
   `ChainManager.cs:78` never fires. Any execution asymmetry between a validator and a replaying node
   produces a different state root that is silently accepted. The node forks off canonical with no error.

2. **Nullifier window never advances on replay.** `ResetNullifiers(blockNumber)` is called from exactly
   one place, `NodeCoordinator.cs:587` in `HandleBlockFinalized`. No replay path calls it. If a verifier
   is wired into a replay executor, the nullifier set grows without bound and its retention window is
   never applied, so a nullifier the proposer legitimately forgot is seen as a duplicate on replay. A
   transaction that finalized as success replays as failure, diverging the state root (silently, per
   point 1).

3. **VK lookup is bound to live state, not the fork.** The verifying-key closure reads
   `stateDbRef.GetStorage(...)` (`Program.cs:434`), the live canonical reference. A sync batch executes on
   a fork that is not swapped to canonical until phase 3, so a VK registered by an earlier block in the
   same batch is invisible when a later block in that batch is replayed. That yields a null VK and a
   false compliance failure that did not occur at finalization.

A further subtlety: nullifiers live on the verifier, not inside the `ApplyBatch` fork, so a discarded or
partial batch leaves consumed nullifiers stuck (a poisoned retry).

## Prerequisites for a correct COMPL-C01

Do all of these together, then wire the shared engine into the RPC and standalone executors:

1. Verify the recomputed state root against the header on the replay path (pass the computed root to
   `AddBlock` and decide the failure policy: halt and re-sync rather than silently fork).
2. Advance the nullifier window per block on the replay path (call `ResetNullifiers(block.Number)` with
   each block's own number inside `BlockApplier`), so replay matches finalization.
3. Resolve VKs against the fork state under replay, not the live canonical reference.
4. Make nullifier consumption transactional with the fork (snapshot and roll back with the batch, or only
   commit consumed nullifiers after the phase-3 swap succeeds).
5. Replay existing chain data before enabling, to confirm no historical finalized block would now fail.

Point 1 is a consensus-safety change worth validating on the testnet soak. Point 2 also fixes a latent
bug on a validator's own catch-up sync, which shares the same replay path.

### Progress

- **1. State-root gate on replay: done.** Both paths now refuse rather than accept. `ApplyBatch` already
  refused; `ApplyBlock` logged and applied anyway, and now returns a failure. Verified on a fresh testnet
  where the roots agree and no block is refused. This was blocked until the divergence it kept reporting
  was traced to a stale account cache in `FlatStateDb` and fixed.
- **2. Nullifier window on replay: done.** `BlockApplier` takes the verifier and calls
  `ResetNullifiers(block.Number)` before each block's transactions, in the order finalization uses. The
  engine moved above the mode switch so a replaying node has one at all, rather than being built inside
  the validator case.
- **3. VK lookup on the executing state: done.** `ExecutionStateRef` holds which state a lookup should
  read. Canonical by default, the sync fork while a batch replays, so a key registered by an earlier
  block in the same batch is visible to a later one.
- **4. Nullifiers roll back with the batch: done.** `ComplianceEngine` snapshots them before a batch and
  `BlockApplier` restores on both refusal paths, the execution failure and the state-root gate. They
  live on the verifier rather than in the fork, so without this a refused batch kept them consumed and
  every retry replayed as a duplicate.
- **5. Replay before enabling: run, and it failed.** An RPC node given an empty data directory replays
  the chain in batches and refuses at the first one, ending #100, on a state-root divergence. A negative
  control on the commit before any of this work reproduces it exactly, same expected and computed roots,
  so it predates COMPL-C01 and is not caused by it.

  It had never been seen because an RPC node that has followed since genesis applies blocks one at a
  time through `ApplyBlock`. Replaying history uses `ApplyBatch`, a different path, and nothing had
  exercised it from an empty state until this prerequisite asked for exactly that.

COMPL-C01 is wired but must not be considered complete. The RPC executor takes the compliance engine and
an RPC node runs the same checks a validator does, and points 1 through 4 hold. Point 5 is the gate, it
has not passed, and the batch replay divergence has to be resolved before this is relied on.

## Decision

- COMPL-C02: done (`Program.cs` binds both registries to Governance 0x1003, guarded-method tests added).
- COMPL-C01: deferred. Tracked here with the prerequisites above. Not wired naively.
