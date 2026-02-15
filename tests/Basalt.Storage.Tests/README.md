# Basalt.Storage.Tests

Unit tests for Basalt storage: Merkle Patricia Trie, state databases, nibble path encoding, and trie node serialization. **175 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| MerklePatriciaTrie | 51 | Insert/get/delete, state root changes, proof generation and verification, tampered proof detection, batch operations, key collision handling, large datasets, empty trie edge cases |
| NibblePath | 42 | Construction, slicing, common prefix, compact encoding roundtrip, odd/even lengths, boundary conditions, equality, indexer access |
| TrieStateDb | 30 | Persistent trie-backed state database: account CRUD, storage operations, state root computation, all account types, deletion behavior |
| TrieNode | 27 | Leaf/branch/extension/empty node creation, encode/decode roundtrip, hash computation, dirty flag tracking, hash caching, cross-type hashing |
| InMemoryStateDb | 25 | In-memory state database: account CRUD, storage operations, state root computation, deterministic roots, account type preservation |

**Total: 175 tests**

## Test Files

- `MerklePatriciaTrieTests.cs` -- Full MPT operations: insert, get, delete, state roots, Merkle proofs, proof verification, batch operations
- `NibblePathTests.cs` -- Nibble-level path manipulation: construction, slicing, prefix matching, compact hex encoding
- `TrieStateDbTests.cs` -- Trie-backed IStateDb implementation with persistent node storage
- `TrieNodeTests.cs` -- Trie node types (empty, leaf, extension, branch): serialization, hashing, dirty tracking
- `InMemoryStateDbTests.cs` -- In-memory IStateDb implementation: accounts, storage, state roots

## Running

```bash
dotnet test tests/Basalt.Storage.Tests
```
