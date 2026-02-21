# Basalt Security & Quality Audit — Storage Layer

## Scope

Audit the state database implementations, Merkle Patricia Trie, RocksDB persistence, and flat state cache:

| Project | Path | Description |
|---|---|---|
| `Basalt.Storage` | `src/storage/Basalt.Storage/` | State database (Trie + Flat cache), Merkle Patricia Trie, RocksDB stores (blocks, receipts, trie nodes, flat state) |

Corresponding test project: `tests/Basalt.Storage.Tests/`

---

## Files to Audit

### State Database
- `IStateDatabase.cs` (~62 lines) — `IStateDatabase` interface, `AccountState` (readonly struct), `AccountType` enum
- `TrieStateDb.cs` (~188 lines) — Merkle Patricia Trie-backed state database
- `FlatStateDb.cs` (~317 lines) — O(1) dictionary cache wrapping TrieStateDb (decorator pattern)
- `InMemoryStateDb.cs` (~100 lines) — Pure in-memory state (tests)
- `StateDbRef.cs` (~46 lines) — Reference-counted state database wrapper
- `IFlatStatePersistence.cs` (~26 lines) — Persistence interface for flat state

### Merkle Patricia Trie
- `Trie/MerklePatriciaTrie.cs` (~594 lines) — Full MPT implementation with proofs
- `Trie/TrieNode.cs` (~335 lines) — Trie node types (Leaf, Branch, Extension)
- `Trie/NibblePath.cs` (~199 lines) — Nibble-level path representation (readonly struct)
- `Trie/ITrieNodeStore.cs` (~38 lines) — Node storage interface + `InMemoryTrieNodeStore`
- `Trie/OverlayTrieNodeStore.cs` (~27 lines) — Overlay for fork isolation

### RocksDB Persistence
- `RocksDb/RocksDbStore.cs` (~168 lines) — RocksDB wrapper, column family management
- `RocksDb/RocksDbTrieNodeStore.cs` (~45 lines) — Trie nodes in RocksDB
- `RocksDb/RocksDbFlatStatePersistence.cs` (~131 lines) — Flat state in RocksDB `state` CF
- `RocksDb/BlockStore.cs` (~279 lines) — Block storage with raw + indexed data
- `RocksDb/ReceiptStore.cs` (~172 lines) — Transaction receipt storage

---

## Audit Objectives

### 1. Merkle Patricia Trie Correctness (CRITICAL)
The MPT is the backbone of state integrity — the state root hash in each block header commits to the entire world state.

- Verify insertion, update, deletion, and lookup operations are correct for all node types:
  - **Leaf nodes**: terminal key-value pairs
  - **Branch nodes**: 16-way branching at each nibble
  - **Extension nodes**: path compression for shared prefixes
- Check that trie operations produce deterministic state roots — the same set of key-value pairs must always produce the same root hash.
- Verify node hashing: each node's hash must include its full content (type, path, children/value).
- Check for path encoding correctness: odd vs. even nibble counts, prefix flags for leaf vs. extension.
- Verify Merkle proof generation and verification:
  - Inclusion proofs: prove a key-value pair exists in the trie
  - Exclusion proofs: prove a key does NOT exist
  - Proof verification against a known root hash
- Check for trie corruption scenarios:
  - Concurrent reads and writes
  - Partial updates (node written but parent not updated)
  - Hash collisions in the node store

### 2. Flat State Database (CRITICAL)
The `FlatStateDb` decorator provides O(1) reads by caching the trie state in dictionaries.

- Verify write-through consistency: every write goes to both the cache AND the underlying trie.
- Check cache-on-read behavior: trie misses must populate the cache correctly.
- Verify `_deletedAccounts` and `_deletedStorage` HashSets prevent fallthrough for deleted entries.
- Check `Fork()` semantics: the forked database must be independent of the parent.
  - Shallow-copy of dictionaries — verify this is correct (value types should be fine, reference types could share state).
  - The inner trie is forked separately — verify this creates a proper overlay.
- Verify `ComputeStateRoot()` delegates correctly to the inner `TrieStateDb`.
- Check `GetAllAccounts()`: returns cached accounts (since `TrieStateDb` throws `NotSupportedException`).
- Verify `FlushToPersistence()` and `LoadFromPersistence()` for correctness:
  - All cached state must be flushed
  - Loading must correctly populate caches from persistent storage
  - No data loss or corruption during flush/load cycle

### 3. State Database Interface Consistency
- Verify all `IStateDatabase` implementations (`TrieStateDb`, `FlatStateDb`, `InMemoryStateDb`) produce identical results for the same operations.
- Check `AccountState` readonly struct: verify no hidden copies or mutation.
- Verify `AccountType` enum covers all necessary account types.
- Check `SetAccount` / `GetAccount` / `SetStorage` / `GetStorage` consistency across implementations.

### 4. RocksDB Integration Security
- Verify `RocksDbStore` correctly manages column families:
  - `blocks`, `blockIndex`, `txIndex`, `receipts`, `logs`, `state`, `trieNodes`
- Check for data corruption risks:
  - Concurrent read/write safety (RocksDB is thread-safe but application-level atomicity matters)
  - Write batch usage for atomic multi-key updates
  - Column family isolation (data from one CF can't leak to another)
- Verify `Dispose()` correctly closes the database and all column family handles.
- Check error handling for disk-full, corruption, and I/O errors.

### 5. Flat State Persistence
- Verify `RocksDbFlatStatePersistence` key format:
  - `0x01 + 20B address` for account state
  - `0x02 + 20B address + 32B slot` for contract storage
- Check that key format cannot produce collisions between account keys and storage keys.
- Verify serialization/deserialization of `AccountState` values.
- Check that `FlushToPersistence()` handles large state sets without OOM.
- Verify warm restart: `LoadFromPersistence()` must reconstruct the exact same state.

### 6. Block & Receipt Storage
- Verify `BlockStore.PutFullBlock()` stores both raw block data and index entries atomically.
- Check `BlockStore` indexing: block-by-number, block-by-hash, transaction-by-hash lookups.
- Verify `ReceiptStore` correctly stores and retrieves `ReceiptData` and `LogData`.
- Check that block/receipt data cannot be tampered with after storage.
- Verify that orphaned blocks (from reorgs) are handled correctly.

### 7. Reference Counting (StateDbRef)
- Verify `StateDbRef` correctly tracks references and prevents premature disposal.
- Check for reference leaks (ref count never reaches zero → memory leak).
- Check for use-after-dispose (ref count reaches zero while still in use).

### 8. NibblePath Correctness
- Verify nibble extraction from byte arrays is correct (high nibble = `byte >> 4`, low nibble = `byte & 0xF`).
- Check boundary conditions: empty path, single nibble, maximum path length.
- Verify `CommonPrefix` calculation for path matching.
- Check `Slice` and `Append` operations for off-by-one errors.

### 9. Overlay Trie Node Store
- Verify `OverlayTrieNodeStore` correctly provides fork isolation.
- Check that writes go to the overlay and reads fall through to the base store.
- Verify that the overlay does not modify the base store.

### 10. Performance Considerations
- Check for unnecessary copying of large byte arrays (blocks, receipts, state values).
- Verify that RocksDB read options (fill cache, read-ahead) are configured appropriately.
- Check that trie traversal doesn't create excessive garbage collection pressure.
- Verify batch write sizes for RocksDB operations.

### 11. ARM64 / Platform Compatibility
- Note: RocksDB NuGet 8.9.1 has a 52-byte ARM64 stub — Dockerfile installs `librocksdb-dev`.
- Verify that the platform detection and native library loading work correctly.
- Check that endianness assumptions in key encoding are correct for ARM64.

### 12. Test Coverage
- Review `tests/Basalt.Storage.Tests/` for:
  - MPT: insert, update, delete, lookup, proof generation/verification, deterministic roots
  - FlatStateDb: cache hits, cache misses, deletion tracking, fork isolation, persistence round-trip
  - NibblePath: all operations, boundary cases
  - TrieNode: all node types, serialization, hashing
  - BlockStore: store and retrieve blocks, index lookups
  - ReceiptStore: store and retrieve receipts
  - Concurrent access patterns

---

## Key Context

- `FlatStateDb` wraps `TrieStateDb` (decorator pattern), implements `IStateDatabase`.
- `Dictionary<Address, AccountState>` + `Dictionary<(Address, Hash256), byte[]>` caches.
- Write-through: every write goes to cache + trie simultaneously.
- `Fork()`: forks inner trie + shallow-copies all dictionaries/sets.
- `ComputeStateRoot()`: delegates to inner `TrieStateDb`.
- `InnerTrie` property for Merkle proof access.
- RocksDB column families: blocks, blockIndex, txIndex, receipts, logs, state, trieNodes.
- Key format for flat state: `0x01 + 20B address` (accounts), `0x02 + 20B address + 32B slot` (storage).
- Block time: 2s (devnet), blocks stored as raw bytes + indexed.
- NuGet: `RocksDB 8.9.1.44383`, `Microsoft.Extensions.Logging.Abstractions 9.0.0`.

---

## Output Format

Write your findings to `audit/output/13-storage.md` with the following structure:

```markdown
# Storage Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[State corruption, trie inconsistency, data loss]

## High Severity
[Significant security or correctness issues]

## Medium Severity
[Issues to address]

## Low Severity / Recommendations
[Code quality, performance, best practices]

## Test Coverage Gaps
[Untested scenarios]

## Positive Findings
[Well-implemented patterns]
```

For each finding, include:
1. **Location**: File path and line number(s)
2. **Description**: What the issue is
3. **Impact**: What could go wrong
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
