# Storage Layer Audit Report

## Executive Summary

The storage layer provides a well-architected Merkle Patricia Trie backed by RocksDB with an O(1) flat cache decorator. The core data structures (MPT, NibblePath, TrieNode) are implemented correctly with strong determinism guarantees, proper compact (hex-prefix) encoding, and comprehensive Merkle proof support. However, the audit identifies several medium-severity issues: unbounded trie node store growth (no GC), a subtle fork semantics gap where in-progress storage tries are lost, shared `ColumnFamilyOptions` across all RocksDB column families, missing `WriteBatch.Commit()` guarantee in `Dispose()`, and the `HasKey` method performing a full read instead of using `KeyMayExist`. Test coverage is excellent for core trie operations (~417 tests) but has critical gaps — zero tests for `BlockStore`, `ReceiptStore`, RocksDB persistence, and concurrent access patterns.

---

## Critical Issues

*No critical issues found.* The core MPT operations (insert, update, delete, proof generation/verification) are correct. State root computation is deterministic. The flat cache write-through pattern ensures cache-trie consistency. Serialization formats use fixed-size encodings with varint lengths, preventing ambiguity.

---

## High Severity

### H-01: Unbounded Trie Node Store Growth — No Garbage Collection

**Location:** `src/storage/Basalt.Storage/Trie/ITrieNodeStore.cs:9-13` (TODO comment), all `_store.Put()` calls in `MerklePatriciaTrie.cs`

**Description:** Every trie mutation (Put, Delete, SplitLeaf, SplitExtension, CompactBranch, RebuildAfterDelete) creates new nodes and stores them via `_store.Put()`, but old nodes whose hashes are no longer reachable from the root are never removed. Over time, the `RocksDbTrieNodeStore` accumulates unbounded stale data.

**Impact:** Disk usage grows monotonically without bound. For a busy chain processing many transactions per block, the `trie_nodes` column family will eventually consume significant disk space. This is acknowledged in a TODO comment but has no mitigation path.

**Recommendation:** Implement a mark-and-sweep or reference-counting GC pass that runs periodically (e.g., every N blocks). Alternatively, implement state pruning that retains only the last K state roots and purges unreachable nodes.

**Severity:** High

---

### H-02: Fork Loses In-Progress Storage Tries

**Location:** `src/storage/Basalt.Storage/TrieStateDb.cs:80-85`

**Description:** `TrieStateDb.Fork()` calls `ComputeStateRoot()` (which flushes storage trie roots into account state), then creates a new `TrieStateDb` with an `OverlayTrieNodeStore` from the current root. However, the `_storageTries` dictionary (per-account sub-tries) is NOT carried over to the forked instance. This means any storage trie that was modified but not yet committed to the state root will behave differently in the fork — specifically, `GetOrCreateStorageTrie()` in the fork will reconstruct storage tries from the committed storage root, losing any in-progress modifications that were committed to the trie nodes but not yet reflected in a `ComputeStateRoot()` call before the fork.

**Impact:** The `FlatStateDb.Fork()` comments acknowledge this as "accepted design trade-off (S-10)" and the `ComputeStateRoot()` call before forking mitigates it. However, if any caller of `TrieStateDb.Fork()` bypasses the `FlatStateDb` layer, they could silently lose storage mutations.

**Recommendation:** Document this invariant prominently on `TrieStateDb.Fork()`. Consider adding an assertion that `_storageTries` is empty or fully flushed before forking.

**Severity:** High (mitigated by FlatStateDb wrapper, but exposed API is dangerous)

---

### H-03: `WriteBatchScope` Does Not Auto-Commit on Dispose

**Location:** `src/storage/Basalt.Storage/RocksDb/RocksDbStore.cs:136-168`

**Description:** `WriteBatchScope` implements `IDisposable` but only disposes the `WriteBatch` — it does not check whether `Commit()` was called. If a caller creates a batch, adds operations, but forgets to call `Commit()` (or an exception is thrown between operations and `Commit()`), the entire batch is silently dropped.

**Impact:** Data loss on exceptional paths. For example, if `ReceiptStore.PutReceipts()` throws during encoding of one receipt, the `using` block will dispose the batch without committing any of the successfully-added entries. While the current code always calls `Commit()` immediately before the `using` scope ends, any future modifications or exception scenarios could silently lose data.

**Recommendation:** Either (a) add a `_committed` flag and log a warning in `Dispose()` if the batch was populated but not committed, or (b) document this behavior prominently. Do NOT auto-commit in Dispose (that could mask bugs), but do make the silent drop detectable.

**Severity:** High

---

## Medium Severity

### M-01: Shared `ColumnFamilyOptions` Across All Column Families

**Location:** `src/storage/Basalt.Storage/RocksDb/RocksDbStore.cs:45-49`

**Description:** A single `ColumnFamilyOptions()` instance is used for all 7 column families. Different CFs have very different access patterns:
- `trie_nodes`: point lookups by hash, write-heavy during block processing
- `block_index`: sequential scans by block number
- `state`: prefix scans with `0x01`/`0x02` prefix
- `receipts`: point lookups by tx hash

**Impact:** Suboptimal RocksDB performance. The default options may not suit all access patterns — for instance, `block_index` benefits from a bloom filter and prefix extractor, while `trie_nodes` benefits from a large block cache.

**Recommendation:** Create per-CF `ColumnFamilyOptions` tuned for each access pattern. At minimum, enable bloom filters on point-lookup-heavy CFs (`trie_nodes`, `receipts`, `blocks`).

**Severity:** Medium

---

### M-02: `HasKey` Performs Full Read

**Location:** `src/storage/Basalt.Storage/RocksDb/RocksDbStore.cs:86-89`

**Description:** `HasKey()` calls `_db.Get(key, cf)` which reads the entire value into managed memory, then checks for null. RocksDB has `KeyMayExist` and `KeyExists` APIs that can avoid reading the value.

**Impact:** Performance degradation for existence checks on large values (e.g., raw block data). Each `HasKey` call on a block incurs unnecessary I/O and memory allocation.

**Recommendation:** Use `_db.Get(key, cf, readOptions)` with a read option that limits value size, or use RocksDB's native key-existence check if the C# bindings support it.

**Severity:** Medium

---

### M-03: `StateDbRef.Swap()` Is Not Truly Atomic for Multi-Threaded Readers

**Location:** `src/storage/Basalt.Storage/StateDbRef.cs:24-27`

**Description:** `StateDbRef` uses `volatile` on `_inner` and delegates all operations to it. `Swap()` simply reassigns the reference. While the volatile read/write is atomic for the reference itself, a caller performing multiple sequential operations (e.g., `GetAccount` then `GetStorage`) could observe state from two different databases if a `Swap()` occurs between calls.

**Impact:** API handlers that read multiple state values without forking could observe inconsistent cross-account state. The class documentation says to use `Fork()` for concurrent readers, but this invariant is easy to violate.

**Recommendation:** Add a `Snapshot()` method that returns a forked copy, and make `Fork()` the recommended API for all read paths. Consider logging a warning if `GetAccount`/`GetStorage` are called without forking.

**Severity:** Medium

---

### M-04: `InMemoryStateDb.ComputeStateRoot()` Uses Different Algorithm Than `TrieStateDb`

**Location:** `src/storage/Basalt.Storage/InMemoryStateDb.cs:41-65` vs `src/storage/Basalt.Storage/TrieStateDb.cs:55-78`

**Description:** `InMemoryStateDb` computes state root by sorting accounts by address and hashing all states together with BLAKE3. `TrieStateDb` computes state root via the MPT root hash. These produce different root hashes for the same state, making the implementations non-interchangeable.

**Impact:** Code that depends on state root consistency across implementations (e.g., tests that substitute `InMemoryStateDb` for `TrieStateDb`) will get different root hashes. This is documented implicitly but could cause subtle bugs if someone assumes `IStateDatabase` implementations are root-compatible.

**Recommendation:** Document on `IStateDatabase.ComputeStateRoot()` that the root hash algorithm is implementation-specific and not interchangeable. Consider adding a `StateRootAlgorithm` property or marker interface.

**Severity:** Medium

---

### M-05: `InMemoryStateDb.ComputeStateRoot()` Does Not Include Storage

**Location:** `src/storage/Basalt.Storage/InMemoryStateDb.cs:41-65`

**Description:** The naive state root hash in `InMemoryStateDb` only hashes account states (which include `StorageRoot` field). However, `InMemoryStateDb` never updates `StorageRoot` when `SetStorage()` is called — the `StorageRoot` in `AccountState` remains whatever was set by the caller (typically `Hash256.Zero`). Contrast with `TrieStateDb.ComputeStateRoot()` which flushes storage tries into account state's `StorageRoot` field before computing the world root.

**Impact:** Storage modifications in `InMemoryStateDb` are invisible to `ComputeStateRoot()`. Two databases with different storage but same accounts will produce the same root hash.

**Recommendation:** Either update `InMemoryStateDb.ComputeStateRoot()` to also hash storage entries, or document that it is account-only and not suitable for verifying storage integrity.

**Severity:** Medium

---

### M-06: `FlatStateDb.FlushToPersistence()` Clears Deletion Sets After Flush

**Location:** `src/storage/Basalt.Storage/FlatStateDb.cs:263-273`

**Description:** After a successful flush, `_deletedAccounts` and `_deletedStorage` are cleared. This means if a crash occurs after `Flush()` but before the next block is processed, and a new `FlatStateDb` is constructed and `LoadFromPersistence()` is called, the deleted entries will correctly be absent (they were deleted in the batch). However, the in-memory `_deletedAccounts`/`_deletedStorage` sets are cleared, so the `GetAccount`/`GetStorage` fallthrough prevention is lost. If the trie still contains the old data (which it does — the trie is not pruned), subsequent reads could return stale deleted data from the trie.

**Impact:** After `FlushToPersistence()` and before the process terminates, reading a previously-deleted account/storage slot will fall through to the trie and return stale data. This is a correctness issue if any reads occur after flush but before shutdown.

**Recommendation:** Do not clear the deletion sets in `FlushToPersistence()`. Alternatively, clear them only if the trie is also known to not contain the deleted entries (which requires trie pruning).

**Severity:** Medium

---

### M-07: `RocksDbStore` Column Family Names Mismatch With Audit Spec

**Location:** `src/storage/Basalt.Storage/RocksDb/RocksDbStore.cs:18-26`

**Description:** The `CF` class defines: `state`, `blocks`, `receipts`, `metadata`, `trie_nodes`, `block_index`. The audit spec mentions `txIndex` and `logs` column families, but these are not defined in `RocksDbStore.CF`. The `ReceiptData` encodes logs inline rather than in a separate CF.

**Impact:** No `txIndex` CF means transaction-by-hash lookups must scan blocks. No `logs` CF means event log queries must deserialize full receipts. This limits query efficiency for common API operations.

**Recommendation:** Consider adding `txIndex` (tx hash → block hash + index) and `logs` (indexed by contract + event signature + topic) column families for efficient lookups. These are important for block explorer and API functionality.

**Severity:** Medium

---

### M-08: `BlockStore.GetRawBlockByNumber()` Reconstructs Raw Key Manually

**Location:** `src/storage/Basalt.Storage/RocksDb/BlockStore.cs:92-102`

**Description:** `GetRawBlockByNumber()` fetches the hash key from `BlockIndex`, then manually constructs the raw key by prefixing `"raw:"` and copying the hash key. This duplicates the logic in `RawBlockKey()` but cannot call it because `RawBlockKey()` takes a `Hash256`, not a `byte[]`.

**Impact:** If the raw key format changes in `RawBlockKey()` but not in `GetRawBlockByNumber()`, they would diverge, causing silent data corruption (reads would miss existing data).

**Recommendation:** Extract the hash bytes from the key and construct a `Hash256` to call `RawBlockKey()`, or extract a shared `RawBlockKeyFromBytes(byte[])` method.

**Severity:** Medium

---

## Low Severity / Recommendations

### L-01: `RocksDbTrieNodeStore.Put()` Allocates Unnecessarily

**Location:** `src/storage/Basalt.Storage/RocksDb/RocksDbTrieNodeStore.cs:30-37`

**Description:** The `Put` method writes the hash to a `stackalloc` span, then immediately calls `.ToArray()` to convert it to a `byte[]` for `_store.Put()`. The span optimization is wasted.

**Recommendation:** Use the `ReadOnlySpan<byte>` overload of `_store.Put()`, or accept that the `byte[]` allocation is necessary for the RocksDB bindings.

**Severity:** Low

---

### L-02: `TrieNode.Encode()` Uses `MemoryStream` for Every Call

**Location:** `src/storage/Basalt.Storage/Trie/TrieNode.cs:104-174`

**Description:** Every `Encode()` call allocates a new `MemoryStream`, writes to it, then calls `ToArray()`. For hot paths (e.g., computing hashes during block processing), this creates significant GC pressure.

**Recommendation:** Pre-compute the encoded size and write directly to a `byte[]` buffer (similar to `BlockData.Encode()`). For branch nodes, the size is deterministic: 1 (type) + 2 (bitmap) + N*32 (children) + 1 (hasValue) + varint+value.

**Severity:** Low

---

### L-03: `NibblePath` Does Not Override `GetHashCode()` or `Equals(object)`

**Location:** `src/storage/Basalt.Storage/Trie/NibblePath.cs:190-198`

**Description:** `NibblePath` is a `readonly struct` with a custom `Equals(NibblePath)` method, but does not override `Equals(object)` or `GetHashCode()`. If used as a dictionary key or in a `HashSet`, the default struct equality (which compares all fields including the backing `byte[]` reference) would be used instead of the semantic nibble comparison.

**Impact:** Not currently used as a dictionary key, so no immediate bug. But the incomplete equality contract is a foot-gun for future use.

**Recommendation:** Implement `IEquatable<NibblePath>`, override `Equals(object)` and `GetHashCode()`.

**Severity:** Low

---

### L-04: `OverlayTrieNodeStore.Delete()` Stores `null` but `InMemoryTrieNodeStore.Delete()` Removes Key

**Location:** `src/storage/Basalt.Storage/Trie/OverlayTrieNodeStore.cs:26` vs `Trie/ITrieNodeStore.cs:34`

**Description:** `OverlayTrieNodeStore.Delete()` stores `null` in the overlay dictionary (tombstone pattern), while `InMemoryTrieNodeStore.Delete()` removes the key entirely. Both correctly make the node inaccessible, but the tombstone pattern means the overlay dictionary grows indefinitely with deletes.

**Impact:** For fork-and-discard patterns (speculative block building), this is fine since the overlay is short-lived. For long-lived overlays, memory could grow.

**Recommendation:** Acceptable for current usage patterns. Document that overlays are intended to be short-lived.

**Severity:** Low

---

### L-05: `BlockData.Encode()` Size Calculation Depends on Correct `ExtraData` Length

**Location:** `src/storage/Basalt.Storage/RocksDb/BlockStore.cs:210-213`

**Description:** The size calculation includes `4 + ExtraData.Length` for the extra data field (length prefix + data). If `BasaltWriter.WriteBytes()` uses a different length encoding (e.g., varint), the pre-allocated buffer could be too small or too large.

**Impact:** If `WriteBytes` uses varint encoding and `ExtraData` exceeds 127 bytes, the buffer would be too small, causing an `IndexOutOfRangeException`. If it uses 4-byte fixed encoding, this is correct.

**Recommendation:** Verify that `BasaltWriter.WriteBytes()` uses a 4-byte fixed-length prefix matching the size calculation. Consider using a `MemoryStream` or `ArrayBufferWriter<byte>` to avoid size prediction.

**Severity:** Low

---

### L-06: `ReceiptData.Encode()` Potential Buffer Overrun With Large Log Data

**Location:** `src/storage/Basalt.Storage/RocksDb/ReceiptStore.cs:73-106`

**Description:** The size calculation at line 75-78 accounts for log fields, but `log.Data` is written via `writer.WriteBytes(log.Data)` which likely includes a length prefix. The size calculation includes `4 + log.Data.Length` for each log, but if `WriteBytes` uses varint length encoding, the calculation could be off.

**Impact:** Same as L-05 — potential buffer size mismatch.

**Recommendation:** Same as L-05.

**Severity:** Low

---

### L-07: `FlatStateDb.LoadFromPersistence()` Uses `TryAdd` — First Writer Wins

**Location:** `src/storage/Basalt.Storage/FlatStateDb.cs:279-288`

**Description:** `LoadFromPersistence()` uses `TryAdd` for both account and storage caches. This means if a key already exists in the cache (from runtime operations before load), the persisted value is silently ignored. This is the correct behavior for warm restart (runtime state is fresher), but could be surprising if called after partial state modifications.

**Impact:** No current bug — `LoadFromPersistence()` is called on startup before any runtime modifications.

**Recommendation:** Add a comment explaining the "first writer wins" semantics and that this must be called before runtime operations begin.

**Severity:** Low

---

### L-08: `MerklePatriciaTrie.Delete()` Return Convention Is Confusing

**Location:** `src/storage/Basalt.Storage/Trie/MerklePatriciaTrie.cs:62-78`

**Description:** The private `Delete()` method returns:
- `null` → child was entirely deleted
- Same hash as input → key not found (nothing changed)
- Different hash → child was modified

But the public `Delete()` method interprets `null` as "root deleted" (entire trie emptied) and sets `_rootHash = Hash256.Zero`. The sentinel pattern using hash equality is subtle and could be misread.

**Recommendation:** Consider using a `(Hash256? newHash, bool changed)` tuple or a `DeleteResult` enum for clarity.

**Severity:** Low

---

### L-09: `RocksDbStore.Get(ReadOnlySpan<byte>)` Copies Span to Array

**Location:** `src/storage/Basalt.Storage/RocksDb/RocksDbStore.cs:66-69`

**Description:** The `ReadOnlySpan<byte>` overload of `Get` calls `key.ToArray()`, defeating the purpose of the span-based API. This is a limitation of the RocksDbSharp bindings.

**Recommendation:** Acceptable limitation. Consider checking if newer RocksDbSharp versions support span-based APIs.

**Severity:** Low

---

## Test Coverage Gaps

### T-01: **BlockStore — Zero Tests** (Critical Gap)

No tests exist for `BlockStore.PutBlock()`, `GetByHash()`, `GetByNumber()`, `PutFullBlock()`, `GetRawBlock()`, `GetRawBlockByNumber()`, `HasBlock()`, `GetLatestBlockNumber()`, `SetLatestBlockNumber()`, `PutCommitBitmap()`, `GetCommitBitmap()`. No tests for `BlockData.Encode()`/`Decode()` roundtrips.

### T-02: **ReceiptStore — Zero Tests** (Critical Gap)

No tests exist for `ReceiptStore.PutReceipt()`, `PutReceipts()`, `GetReceipt()`. No tests for `ReceiptData.Encode()`/`Decode()` roundtrips or `LogData` serialization.

### T-03: **RocksDB Integration — Zero Tests** (Major Gap)

All state database tests use `InMemoryTrieNodeStore`. No tests exercise:
- `RocksDbTrieNodeStore` encode/decode through RocksDB
- `RocksDbFlatStatePersistence` flush/load cycles
- Recovery after simulated crash (write partial state, verify consistency on reload)
- Column family isolation

### T-04: **Concurrent Access — Zero Tests** (Major Gap)

No tests validate:
- Concurrent reads from `StateDbRef` during `Swap()`
- Parallel fork operations on `FlatStateDb`
- Concurrent `InMemoryTrieNodeStore` access

### T-05: **FlatStateDb Persistence Round-Trip Not Tested**

`FlushToPersistence()` and `LoadFromPersistence()` are never exercised in tests. The interaction between deletion set clearing and trie fallthrough (M-06) is untested.

### T-06: **Large Value / Boundary Condition Gaps**

- No tests with values exceeding 4KB (only up to 4096 bytes for trie, 512 bytes for TrieNode)
- No tests with maximum-length keys (e.g., 32-byte addresses producing 64-nibble paths — tested in NibblePath but not through full MPT stack)
- No tests for `BlockData` with very large `ExtraData` or many `TransactionHashes`

### T-07: **Error Handling Not Tested**

- No tests for `TrieNode.Decode()` with malformed/truncated data
- No tests for varint overflow in `ReadLength()`
- No tests for `BlockData.Decode()` with corrupt data
- No tests for `ReceiptData.Decode()` with corrupt data

---

## Positive Findings

### P-01: Correct Merkle Patricia Trie Implementation
The MPT implementation handles all node types (Leaf, Branch, Extension) correctly, including complex operations like `SplitLeaf`, `SplitExtension`, `CompactBranch`, and `RebuildAfterDelete`. Extension-to-branch splitting and branch compaction after deletion are correctly implemented. Determinism is verified across different insertion orders.

### P-02: Robust Compact (Hex-Prefix) Encoding
`NibblePath.ToCompactEncoding()` and `FromCompactEncoding()` correctly implement the Ethereum hex-prefix encoding with proper handling of odd/even path lengths and leaf/extension flag bits. The 84 tests thoroughly validate this.

### P-03: Write-Through Cache Consistency
`FlatStateDb` correctly implements write-through caching — every `SetAccount`/`SetStorage` writes to both the dictionary cache and the underlying `TrieStateDb`. This ensures `ComputeStateRoot()` always reflects the latest state.

### P-04: Proper Fork Isolation
`FlatStateDb.Fork()` deep-copies storage byte arrays (preventing cross-fork mutation), shallow-copies value-type caches (safe for `AccountState` readonly struct), and creates a trie overlay via `OverlayTrieNodeStore`. The forked instance correctly gets no persistence layer.

### P-05: Defensive Deletion Tracking
Both `_deletedAccounts` and `_deletedStorage` HashSets correctly prevent fallthrough to the trie for deleted entries. `SetAccount` and `SetStorage` correctly clear the deletion markers when re-adding previously deleted entries.

### P-06: Atomic Batch Writes in RocksDB
`BlockStore.PutFullBlock()` and `RocksDbFlatStatePersistence.Flush()` correctly use `WriteBatchScope` for atomic multi-key writes, preventing partial state on disk.

### P-07: Secure Varint Decoding
`TrieNode.ReadLength()` includes proper bounds checking (max 5 bytes for 32-bit value, overflow detection at shift=28, end-of-data checks). `EnsureBounds()` validates all field reads before accessing data, preventing buffer over-reads from malformed input.

### P-08: Cache Size Monitoring
`FlatStateDb.CheckCacheSize()` logs warnings when cache entries exceed 1M, providing operational visibility into memory pressure without imposing eviction overhead.

### P-09: Clean Key Format Design
The flat state persistence key format (`0x01 + 20B addr` for accounts, `0x02 + 20B addr + 32B slot` for storage) uses distinct prefixes that are impossible to collide — account keys are always 21 bytes and storage keys are always 53 bytes, with different leading bytes.

### P-10: Proper AccountState as Readonly Struct
`AccountState` is correctly defined as a `readonly struct` with `init`-only properties, preventing accidental mutation. The update pattern `new AccountState { ... }` used throughout the codebase is correct.
