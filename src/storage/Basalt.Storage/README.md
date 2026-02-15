# Basalt.Storage

Persistent state storage for the Basalt blockchain. Provides a Merkle Patricia Trie backed by RocksDB for cryptographically verifiable state, plus block and receipt stores with dual indexing.

## Components

### IStateDatabase

Common interface for all state storage backends.

```csharp
public interface IStateDatabase
{
    AccountState? GetAccount(Address address);
    void SetAccount(Address address, AccountState state);
    bool AccountExists(Address address);
    void DeleteAccount(Address address);
    Hash256 ComputeStateRoot();
    IEnumerable<(Address Address, AccountState State)> GetAllAccounts();

    byte[]? GetStorage(Address contract, Hash256 key);
    void SetStorage(Address contract, Hash256 key, byte[] value);
    void DeleteStorage(Address contract, Hash256 key);
}
```

### AccountState

Per-account state stored in the trie. Encoded as 137 bytes (nonce 8 + balance 32 + storageRoot 32 + codeHash 32 + accountType 1 + complianceHash 32).

```csharp
public readonly struct AccountState
{
    public ulong Nonce { get; init; }
    public UInt256 Balance { get; init; }
    public Hash256 StorageRoot { get; init; }
    public Hash256 CodeHash { get; init; }
    public AccountType AccountType { get; init; }   // ExternallyOwned, Contract, SystemContract, Validator
    public Hash256 ComplianceHash { get; init; }

    public static AccountState Empty { get; }  // All fields zeroed, AccountType = ExternallyOwned
}

public enum AccountType : byte
{
    ExternallyOwned = 0,
    Contract = 1,
    SystemContract = 2,
    Validator = 3,
}
```

### InMemoryStateDb

Dictionary-backed implementation for development and testing. Computes a naive state root by hashing sorted account data with BLAKE3 incremental hashing.

### TrieStateDb

Production implementation backed by a Merkle Patricia Trie. Each account is stored as a leaf in the world state trie. Contract storage uses per-account sub-tries that share the same `ITrieNodeStore`. Supports Merkle proof generation for light clients.

```csharp
var nodeStore = new RocksDbTrieNodeStore(rocksDb);
var stateDb = new TrieStateDb(nodeStore);
// Or with a known state root for recovery:
var stateDb = new TrieStateDb(nodeStore, stateRoot);

stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(1000) });
Hash256 root = stateDb.ComputeStateRoot();

MerkleProof? proof = stateDb.GenerateAccountProof(addr);
MerkleProof? storageProof = stateDb.GenerateStorageProof(contractAddr, storageKey);
bool valid = MerklePatriciaTrie.VerifyProof(proof);
```

`ComputeStateRoot()` flushes pending storage trie changes into each account's `StorageRoot` before returning the world trie root hash.

**Important limitation**: `GetAllAccounts()` throws `NotSupportedException` on `TrieStateDb`. Iterating all accounts is not efficiently supported by the trie-backed store. Use `InMemoryStateDb` for development or maintain a separate index.

### MerklePatriciaTrie

BLAKE3-based MPT with Branch, Extension, and Leaf nodes. Nibble-path encoding follows Ethereum's compact (hex-prefix) encoding scheme.

Node types:
- **Empty** -- placeholder for absent data
- **Leaf** -- stores a nibble path and a value
- **Extension** -- stores a shared nibble path prefix and a child hash
- **Branch** -- 16 child slots (one per nibble) plus an optional value; uses a 2-byte bitmap to track which children are present

Nodes are serialized with varint-encoded lengths and BLAKE3-hashed for content-addressing. Proof verification works by rebuilding the trie path from proof nodes in an `InMemoryTrieNodeStore` and checking the value.

```csharp
var trie = new MerklePatriciaTrie(nodeStore);
trie.Put(key, value);
byte[]? result = trie.Get(key);
bool deleted = trie.Delete(key);
Hash256 root = trie.RootHash;

MerkleProof? proof = trie.GenerateProof(key);
bool valid = MerklePatriciaTrie.VerifyProof(proof);
```

`MerkleProof` contains `Key`, `Value` (null if absent), `ProofNodes` (list of encoded trie nodes), and `RootHash`.

### RocksDB Backend

Persistent storage with 6 column families, accessed via the inner class `CF` (named `CF` to avoid conflicts with the `RocksDbSharp.ColumnFamilies` type):

| Column Family | `CF` Constant | Contents |
|---------------|---------------|----------|
| `state` | `CF.State` | Account state data |
| `blocks` | `CF.Blocks` | Serialized block data + raw blocks (prefixed with `raw:`) |
| `receipts` | `CF.Receipts` | Transaction receipts |
| `metadata` | `CF.Metadata` | Chain metadata (latest block number, etc.) |
| `trie_nodes` | `CF.TrieNodes` | MPT node data |
| `block_index` | `CF.BlockIndex` | Block number to hash index |

```csharp
using var store = new RocksDbStore("/path/to/data");

// Raw key-value access (byte[] or ReadOnlySpan<byte> overloads)
store.Put(RocksDbStore.CF.State, key, value);
byte[]? data = store.Get(RocksDbStore.CF.State, key);
store.Delete(RocksDbStore.CF.State, key);
bool exists = store.HasKey(RocksDbStore.CF.State, key);

// Iteration
foreach (var (key, value) in store.Iterate(RocksDbStore.CF.State))
    { /* all entries */ }
foreach (var (key, value) in store.IteratePrefix(RocksDbStore.CF.Blocks, prefix))
    { /* entries matching prefix */ }

// Atomic batch writes via WriteBatchScope
using var batch = store.CreateWriteBatch();
batch.Put(cf, key1, val1);
batch.Put(cf, key2, val2);
batch.Delete(cf, key3);
batch.Commit();
```

### BlockStore

Higher-level store for blocks with dual indexing (by hash and by number). Block numbers are stored as 8-byte big-endian keys.

```csharp
var blockStore = new BlockStore(rocksDb);
blockStore.PutBlock(blockData);
blockStore.PutFullBlock(blockData, serializedBlock);  // Also stores raw bytes for peer sync
BlockData? byHash = blockStore.GetByHash(hash);
BlockData? byNum = blockStore.GetByNumber(42);
byte[]? raw = blockStore.GetRawBlock(hash);            // Raw serialized form for peers
byte[]? rawByNum = blockStore.GetRawBlockByNumber(42);
bool exists = blockStore.HasBlock(hash);
ulong? latest = blockStore.GetLatestBlockNumber();
blockStore.SetLatestBlockNumber(number);
```

`BlockData` stores the block header fields (Number, Hash, ParentHash, StateRoot, TransactionsRoot, ReceiptsRoot, Timestamp, Proposer, ChainId, GasUsed, GasLimit, ProtocolVersion, ExtraData) plus an array of `TransactionHashes`. It has `Encode()` and `Decode(byte[])` methods for binary serialization via `BasaltWriter`/`BasaltReader`.

### ReceiptStore

Higher-level store for transaction receipts, indexed by transaction hash.

```csharp
var receiptStore = new ReceiptStore(rocksDb);
receiptStore.PutReceipt(receipt);
receiptStore.PutReceipts(receipts);  // Atomic via WriteBatch
ReceiptData? r = receiptStore.GetReceipt(txHash);
```

`ReceiptData` stores TransactionHash, BlockHash, BlockNumber, TransactionIndex, From, To, GasUsed, Success, ErrorCode, PostStateRoot, and an array of `LogData` entries. Each `LogData` has Contract, EventSignature, Topics, and Data.

### ITrieNodeStore / InMemoryTrieNodeStore

Storage backend interface for trie nodes, mapping `Hash256` to `TrieNode`.

```csharp
public interface ITrieNodeStore
{
    TrieNode? Get(Hash256 hash);
    void Put(Hash256 hash, TrieNode node);
    void Delete(Hash256 hash);
}
```

`InMemoryTrieNodeStore` is a dictionary-backed implementation for testing and proof verification, with a `Count` property.

`RocksDbTrieNodeStore` delegates to `RocksDbStore` using the `trie_nodes` column family.

### NibblePath

Value type representing a path through the trie as a sequence of nibbles (4-bit values). Each byte in a key produces two nibbles. Supports slicing, prefix comparison, compact (hex-prefix) encoding/decoding, and equality comparison.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Hash256, Address, UInt256, AccountState types |
| `Basalt.Codec` | Binary serialization (BasaltWriter/BasaltReader) for trie nodes and block/receipt data |
| `Basalt.Crypto` | BLAKE3 hashing for trie node hashes and state root computation |
| `RocksDB` | Persistent key-value storage engine |
