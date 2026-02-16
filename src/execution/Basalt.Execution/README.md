# Basalt.Execution

Transaction processing and smart contract execution engine for the Basalt blockchain. Handles transaction validation, block building, mempool management, chain state, and a contract runtime with gas metering and sandboxing.

## Components

### Transaction

Core transaction type supporting transfers, contract deployment, contract calls, and staking operations.

```csharp
var tx = Transaction.Sign(new Transaction
{
    Type = TransactionType.Transfer,
    Nonce = 0,
    Sender = senderAddr,
    To = recipientAddr,
    Value = new UInt256(1000),
    GasLimit = 21_000,
    GasPrice = new UInt256(1),
    Data = [],            // byte[]: calldata or deploy code
    Priority = 0,         // byte: transaction priority
    ChainId = 31337,
}, privateKey);

bool valid = tx.VerifySignature();
Hash256 hash = tx.Hash;                // BLAKE3 of the signing payload
PublicKey senderPubKey = tx.SenderPublicKey;  // Set automatically by Transaction.Sign()
```

Transaction types: `Transfer` (0), `ContractDeploy` (1), `ContractCall` (2), `StakeDeposit` (3), `StakeWithdraw` (4), `ValidatorRegister` (5).

### TransactionReceipt

Generated after execution. The `ErrorCode` field is of type `BasaltErrorCode` (not a plain int).

```csharp
public sealed class TransactionReceipt
{
    public Hash256 TransactionHash { get; init; }
    public Hash256 BlockHash { get; init; }
    public ulong BlockNumber { get; init; }
    public int TransactionIndex { get; init; }
    public Address From { get; init; }
    public Address To { get; init; }
    public ulong GasUsed { get; init; }
    public bool Success { get; init; }
    public BasaltErrorCode ErrorCode { get; init; }
    public Hash256 PostStateRoot { get; init; }
    public List<EventLog> Logs { get; init; }
}
```

### TransactionValidator

Seven-step validation pipeline: signature verification, sender-address-matches-public-key check, chain ID, nonce, balance (value + gas cost), gas limits (per-tx and minimum gas price), and data size.

```csharp
var validator = new TransactionValidator(chainParams);
BasaltResult result = validator.Validate(tx, stateDb);
if (!result.IsSuccess)
    Console.WriteLine($"{result.ErrorCode}: {result.Message}");
```

### TransactionExecutor

Executes validated transactions against state, producing receipts with gas accounting. Supports `Transfer`, `ContractDeploy`, and `ContractCall` types. Other transaction types return an `InvalidTransactionType` error.

```csharp
var executor = new TransactionExecutor(chainParams);
// Or with a custom contract runtime:
var executor = new TransactionExecutor(chainParams, contractRuntime);

TransactionReceipt receipt = executor.Execute(tx, stateDb, blockHeader, txIndex);
```

Key behaviors:

- **Transfer**: Debits sender (value + gas fee), credits recipient, increments nonce. Gas cost is `ChainParameters.TransferGasCost`.
- **ContractDeploy**: Derives contract address from `BLAKE3(sender || nonce)` (last 20 bytes). Debits sender for max gas fee + value upfront. Creates the contract account with `AccountType.Contract` and the code hash. Delegates to `IContractRuntime.Deploy()`. Refunds unused gas (capped at 50% via `GasMeter.EffectiveGasUsed()`). Reverts contract creation on failure.
- **ContractCall**: Loads contract code from storage under the well-known key `0xFF01` (a 32-byte `Hash256` with `[0xFF, 0x01, 0x00, ...]`). Debits sender for max gas fee + value, transfers value to contract, delegates to `IContractRuntime.Execute()`, then refunds unused gas.

### Mempool

Priority queue of pending transactions ordered by gas price (highest first), then nonce (lowest first), with deterministic hash-based tiebreaking. Rejects duplicates and enforces a configurable size limit (default 10,000). Thread-safe via internal locking.

```csharp
var mempool = new Mempool(maxSize: 10_000);
mempool.OnTransactionAdded += (tx) => { /* trigger gossip */ };

bool added = mempool.Add(tx);                      // raiseEvent defaults to true
bool added = mempool.Add(tx, raiseEvent: false);   // Suppress event (peer-received txs)
List<Transaction> pending = mempool.GetPending(maxCount: 100);
mempool.RemoveConfirmed(confirmedTxs);

bool exists = mempool.Contains(txHash);
Transaction? tx = mempool.Get(txHash);
bool removed = mempool.Remove(txHash);
int count = mempool.Count;
```

### BlockBuilder

Builds blocks by selecting transactions from a pool, validating and executing them, and computing the state/transactions/receipts roots.

```csharp
var builder = new BlockBuilder(chainParams);
Block block = builder.BuildBlock(pendingTxs, stateDb, parentHeader, proposerAddr);
```

Internally creates a `TransactionValidator` and `TransactionExecutor`. Skips transactions that fail validation or would exceed the block gas limit. Computes Merkle roots for transactions and receipts using a binary BLAKE3 Merkle tree (odd leaves are promoted unpaired).

Static helpers: `BlockBuilder.ComputeTransactionsRoot(txList)` and `BlockBuilder.ComputeReceiptsRoot(receiptList)`.

### Block / BlockHeader

```csharp
public sealed class BlockHeader
{
    public ulong Number { get; init; }
    public Hash256 ParentHash { get; init; }
    public Hash256 StateRoot { get; init; }
    public Hash256 TransactionsRoot { get; init; }
    public Hash256 ReceiptsRoot { get; init; }
    public long Timestamp { get; init; }
    public Address Proposer { get; init; }
    public uint ChainId { get; init; }
    public ulong GasUsed { get; init; }
    public ulong GasLimit { get; init; }
    public uint ProtocolVersion { get; init; }  // default 1
    public byte[] ExtraData { get; init; }      // default []
    public Hash256 Hash { get; }                // BLAKE3 of serialized header
}

public sealed class Block
{
    public BlockHeader Header { get; init; }
    public List<Transaction> Transactions { get; init; }
    public List<TransactionReceipt>? Receipts { get; set; }
    public Hash256 Hash { get; }    // delegates to Header.Hash
    public ulong Number { get; }    // delegates to Header.Number
}
```

### ChainManager

Canonical chain state with block lookup by hash or number, genesis creation, and recovery from persistent storage.

```csharp
var chain = new ChainManager();
Block genesis = chain.CreateGenesisBlock(chainParams, initialBalances, stateDb, genesisTimestamp: 0);
// genesisTimestamp is optional (long?); use a fixed value for deterministic genesis across nodes
BasaltResult result = chain.AddBlock(block);
Block? block = chain.GetBlockByNumber(42);
Block? block = chain.GetBlockByHash(hash);
Block? latest = chain.LatestBlock;
ulong latestNum = chain.LatestBlockNumber;

// Recovery from persistent storage
chain.ResumeFromBlock(genesisBlock, latestBlock);
```

`AddBlock` validates that the block's parent hash matches the current chain tip and the block number is sequential. Thread-safe via internal locking.

### BlockProductionLoop

Background service that drains the mempool and produces blocks on a timer (`ChainParameters.BlockTimeMs`).

```csharp
var loop = new BlockProductionLoop(chainParams, chainManager, mempool, stateDb, proposer, logger);
loop.OnBlockProduced += (block) => { /* broadcast */ };
loop.Start();
await loop.StopAsync();
```

## Smart Contract Runtime

### IContractRuntime

Interface for contract runtime implementations. Two implementations are provided:

```csharp
public interface IContractRuntime
{
    ContractDeployResult Deploy(byte[] code, byte[] constructorArgs, VmExecutionContext ctx);
    ContractCallResult Execute(byte[] code, byte[] callData, VmExecutionContext ctx);
}
```

- `ContractDeployResult`: `Success`, `Code`, `AbiMetadata`, `ErrorMessage`, `Logs`
- `ContractCallResult`: `Success`, `ReturnData`, `ErrorMessage`, `Logs`

### ManagedContractRuntime

Phase 1 in-process contract runtime. Stores contract code under the well-known storage key `0xFF01` during deployment. Call dispatch uses the first 4 bytes of `callData` as a method selector.

Built-in selectors (BLAKE3-based, first 4 bytes of `BLAKE3(method_name)`):
- `0x3E927582` -- `storage_set(key, value)`: write to contract storage
- `0x5DB08A5F` -- `storage_get(key)`: read from contract storage
- `0x758EC466` -- `storage_del(key)`: delete from contract storage
- `0x81359626` -- `emit_event(signature, data)`: emit a log event

If `callData` is shorter than 4 bytes, it is treated as a fallback/receive call (accepts value only).

Static helper: `ManagedContractRuntime.ComputeSelector(string methodSignature)` computes a 4-byte BLAKE3-based method selector.

### SandboxedContractRuntime

AOT-sandbox-aware runtime that wraps each invocation with:
- A `ContractAssemblyContext` (collectible `AssemblyLoadContext` for isolated assembly loading)
- A `ResourceLimiter` for memory tracking
- A `CancellationTokenSource` for wall-clock timeout enforcement

Configured via `SandboxConfiguration`:
- `ExecutionTimeout`: max wall-clock time (default 5s)
- `MemoryLimitBytes`: max memory per invocation (default 100 MB)
- `EnableMemoryTracking`: toggle memory metering (disable for trusted system contracts)

Uses the same built-in selector dispatch as `ManagedContractRuntime` in Phase 1. Phase 2 will load AOT-compiled contract assemblies into the ALC.

```csharp
var config = new SandboxConfiguration { ExecutionTimeout = TimeSpan.FromSeconds(5) };
var runtime = new SandboxedContractRuntime(config);
var executor = new TransactionExecutor(chainParams, runtime);
```

### SandboxedHostBridge

Byte-array-based bridge between isolated contract assemblies and the `HostInterface`. All methods use only primitive types and byte arrays so contracts in a separate ALC do not need direct references to core value types. Tracks allocations through the `ResourceLimiter`.

### ContractAssemblyContext

Collectible `AssemblyLoadContext` for contract isolation. Validates that loaded assemblies only reference an allow-list of assemblies: `Basalt.Core`, `Basalt.Sdk.Contracts`, `Basalt.Codec`, `System.Runtime`, `System.Private.CoreLib`, `netstandard`.

### VmExecutionContext

Execution context passed to contract runtimes. Renamed from `ExecutionContext` to avoid conflicts with `System.Threading.ExecutionContext`.

```csharp
public sealed class VmExecutionContext
{
    public Address Caller { get; init; }
    public Address ContractAddress { get; init; }
    public UInt256 Value { get; init; }
    public ulong BlockTimestamp { get; init; }
    public ulong BlockNumber { get; init; }
    public Address BlockProposer { get; init; }
    public uint ChainId { get; init; }
    public GasMeter GasMeter { get; init; }
    public IStateDatabase StateDb { get; init; }
    public int CallDepth { get; init; }
    public List<EventLog> EmittedLogs { get; }

    public const int MaxCallDepth = 1024;
}
```

### HostInterface

System calls available to contracts, with per-operation gas metering:

- **Storage**: `StorageRead(Hash256)`, `StorageWrite(Hash256, byte[])`, `StorageDelete(Hash256)` -- charges for new vs existing slots; delete grants a refund
- **Crypto**: `Blake3Hash(ReadOnlySpan<byte>)`, `Keccak256(ReadOnlySpan<byte>)`, `Ed25519Verify(PublicKey, ReadOnlySpan<byte>, Signature)`
- **Context**: `GetCaller()`, `GetValue()`, `GetBlockTimestamp()`, `GetBlockNumber()`, `GetBalance(Address)`
- **Events**: `EmitEvent(Hash256 eventSignature, Hash256[] topics, byte[] data)`
- **Control**: `Revert(string)`, `Require(bool, string)` -- both throw `ContractRevertException`

### GasTable

Gas costs for all operations:

| Category | Operation | Constant | Gas Cost |
|----------|-----------|----------|----------|
| Base | Transaction base | `TxBase` | 21,000 |
| Base | Data zero byte | `TxDataZeroByte` | 4 |
| Base | Data non-zero byte | `TxDataNonZeroByte` | 16 |
| Base | Contract creation | `ContractCreation` | 32,000 |
| Storage | Read | `StorageRead` | 200 |
| Storage | Write (existing) | `StorageWrite` | 5,000 |
| Storage | Write (new slot) | `StorageWriteNew` | 20,000 |
| Storage | Delete | `StorageDelete` | 5,000 |
| Storage | Delete refund | `StorageDeleteRefund` | 15,000 |
| Crypto | BLAKE3 hash | `Blake3Hash` | 30 + 6/word |
| Crypto | Keccak-256 | `Keccak256` | 30 + 6/word |
| Crypto | Ed25519 verify | `Ed25519Verify` | 3,000 |
| Memory | Per byte | `MemoryPerByte` | 3 |
| Memory | Copy per byte | `CopyPerByte` | 3 |
| Calls | Call | `Call` | 700 |
| Calls | Value transfer | `CallValueTransfer` | 9,000 |
| Calls | New account | `CallNewAccount` | 25,000 |
| Events | Log base | `Log` | 375 |
| Events | Per topic | `LogTopic` | 375 |
| Events | Per data byte | `LogDataPerByte` | 8 |
| ZK | Pedersen commit | `PedersenCommit` | 10,000 |
| ZK | Groth16 verify | `Groth16Verify` | 300,000 |
| ZK | G1 scalar mult | `G1ScalarMult` | 5,000 |
| ZK | G1 add | `G1Add` | 500 |
| ZK | Pairing | `Pairing` | 75,000 |
| System | Balance query | `Balance` | 400 |
| System | Block hash | `BlockHash` | 20 |
| System | Timestamp | `Timestamp` | 2 |
| System | Block number | `BlockNumber` | 2 |
| System | Caller | `Caller` | 2 |

Static helper: `GasTable.ComputeDataGas(ReadOnlySpan<byte> data)` sums per-byte costs (4 for zero bytes, 16 for non-zero).

### GasMeter

Tracks gas consumption and enforces limits. Supports refunds (e.g., from storage deletion) capped at 50% of total gas used.

```csharp
var meter = new GasMeter(gasLimit: 100_000);
meter.Consume(21_000);              // throws OutOfGasException if insufficient
bool ok = meter.TryConsume(5_000);  // returns false if insufficient (no throw)
meter.AddRefund(15_000);            // storage delete refund

ulong remaining = meter.GasRemaining;
ulong used = meter.GasUsed;
ulong refund = meter.GasRefund;
ulong effective = meter.EffectiveGasUsed();  // GasUsed - min(GasRefund, GasUsed/2)
```

### Exceptions

- `OutOfGasException` (BasaltErrorCode.OutOfGas) -- includes `GasUsed`, `GasRequested`, `GasLimit`
- `ContractRevertException` (BasaltErrorCode.ContractReverted) -- explicit revert with message
- `SandboxTimeoutException` (BasaltErrorCode.CpuTimeLimitExceeded) -- wall-clock timeout
- `SandboxMemoryLimitException` (BasaltErrorCode.MemoryLimitExceeded) -- memory budget exceeded
- `SandboxIsolationException` (BasaltErrorCode.ContractCallFailed) -- disallowed assembly or API access

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Hash256, Address, UInt256, ChainParameters, BasaltResult, error codes |
| `Basalt.Codec` | Block/transaction serialization (BasaltWriter/BasaltReader) |
| `Basalt.Crypto` | BLAKE3, Keccak-256, Ed25519 for hashing, address derivation, and verification |
| `Basalt.Storage` | IStateDatabase for state access |
| `Microsoft.Extensions.Logging.Abstractions` | Structured logging (BlockProductionLoop) |

Note: The `Basalt.Consensus` project reference was removed to avoid a circular dependency (Execution -> Consensus -> Network -> Execution). Consensus-related interfaces live in `Basalt.Core` if needed.
