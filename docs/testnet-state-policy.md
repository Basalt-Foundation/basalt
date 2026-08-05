# Testnet State-Retention Policy

How Basalt bounds on-disk state growth on the public testnet, and what to do if a node's
`trie_nodes` column family grows faster than pruning reclaims it.

## Why state grows

The state trie is a Merkle-Patricia trie stored in RocksDB (column family `trie_nodes`), keyed by the
32-byte hash of each node. It is append-only and copy-on-write: changing one account or storage slot
writes a fresh path of nodes from the changed leaf up to a new root, and never overwrites the old path.
Every historical state version therefore stays on disk. Without a sweep, `trie_nodes` grows without
bound and a long-running node eventually fills its disk. This is the second of the two long-run
"freeze" risks (the first, fork/sync wedging, is handled separately by the sync watchdog).

## What pruning keeps

A prune sweep deletes only trie nodes that are unreachable from a retained set of state roots. The
retained set is rebuilt from the block store on every sweep (see `TrieRetention`), so it survives a
restart with no separate checkpoint file:

- **Genesis** (block 0). Deep rollback and full replay anchor here.
- **A sliding window** of the last `WindowSize` canonical blocks below the tip. `WindowSize` is at least
  `MaxRollbackDepth` (1000), so any legal fork recovery re-roots onto a state whose nodes still exist.
- **The current tip.**

Reachability is computed storage-aware: the walk descends from each world-trie account into that
account's storage sub-trie. A world-only walk would miss every contract-storage node and a sweep marked
that way would corrupt state. This is enforced by `TrieReachability` and its tests.

## Safety guards

A wrong sweep is unrecoverable, so the sweep refuses to run unless every guard holds:

1. **Storage-aware mark.** Reachability includes storage sub-tries, not just the world spine.
2. **Single pinned snapshot.** Marking and enumeration read one RocksDB snapshot, so a node written
   after the snapshot is never enumerated and a node reachable as of the snapshot is never missed.
3. **Abort on missing.** If a retained root references a node absent from the store, the sweep throws
   and deletes nothing. A corrupt or incomplete store is never swept.
4. **Deletion tripwire.** If the sweep would delete more than 95 percent of nodes, it aborts without
   deleting. That fraction signals a marking bug, not legitimate garbage.
5. **Bounded batches.** Deletes commit in fixed-size write batches, never one unbounded batch.
6. **Serialized against state mutation.** The sweep does not run concurrently with block commit,
   rollback, or sync re-root. Content-addressing means a node stale as of the snapshot could be
   recreated by a later block with identical content and the same hash, and deleting it would then
   corrupt the new tip. Serialization closes that race.
7. **Genesis and tip always retained**, independent of window arithmetic.

Reclaiming disk space also requires a compaction of `trie_nodes` after the sweep (a bare delete only
writes a tombstone). Compaction is slow and runs outside any consensus lock.

## Operator-visible behaviour

- Sweeps run periodically (every epoch by default) and are expected to keep `trie_nodes` flat over time
  on a node that stays online.
- A sweep introduces a brief pause in block commit while it runs, because it must not overlap state
  mutation (guard 6). On a regularly pruned node the live working set is small and the pause is short.
  The first sweep on a node that has never pruned scans the whole accumulated set once and pauses
  longer. This tradeoff is acceptable for the testnet.
- Pruning can be disabled by configuration. A node with pruning disabled will grow `trie_nodes`
  without bound and is expected to be reset periodically (see below).

## Reset escape hatch

The testnet is explicitly resettable. If state growth outpaces reclamation on a deployment, or a
coordinated upgrade requires it, the network can be regenesed:

1. Announce the reset window and the new genesis parameters ahead of time.
2. Stop all nodes.
3. Remove each node's data directory (or the `trie_nodes` column family) so it starts from the new
   genesis.
4. Restart from the published genesis.

A reset discards testnet state by design. Testnet points and balances carry no value and are not
transferable, so a reset has no economic effect. Mainnet will not rely on this escape hatch and will
use incremental pruning as its only state-bound mechanism.

## What is deferred to mainnet

- Reference-counted or generational pruning that avoids any block-commit pause.
- Larger validator sets (the current cap is 64, bounded by the commit bitmap).
