# Execution Layer Audit Report

## Executive Summary

The Execution Layer contains **6 critical**, **9 high**, **14 medium**, and **13 low** severity findings across transaction processing, contract execution, sandbox isolation, block building, and chain management. The most urgent issues are: (1) no state rollback on contract call/deploy failure — storage mutations persist even when execution reverts; (2) `BlockProductionLoop` mutates canonical state without forking, causing permanent state corruption if `AddBlock` fails; (3) static mutable `Context` class is fundamentally thread-unsafe; (4) `GasMeter` overflow can bypass gas limits; and (5) failed transactions in several tx types don't increment nonce or charge gas, enabling block-space griefing. EIP-1559 `BaseFeeCalculator` has zero test coverage.

## Critical Issues

### C-1: No State Rollback on Contract Call/Deploy Failure — Storage Mutations Persist

**Location:** `TransactionExecutor.cs:208-211,322-325` and `ManagedContractRuntime.cs:94-156`

**Description:** When a contract call or deploy fails (including `OutOfGasException`), the `TransactionExecutor` only reverts the value transfer. It does **not** fork the state database before execution, and does **not** roll back any storage mutations the contract made before failing. There is no `stateDb.Fork()` call anywhere in the execution path — zero matches for fork/rollback in the execution directory.

When an `OutOfGasException` is thrown during contract execution, at the point of the throw, the following state mutations have already been committed with no rollback:
- Sender balance debited by `maxGasFee + value`, nonce incremented
- Contract account created with transferred value (deploy) or value transferred to contract (call)
- Any contract storage writes made before the gas ran out

The catch block only returns a receipt reporting `GasUsed = tx.GasLimit` — it does not revert state changes, refund unused gas, or clean up partially-created contracts.

**Impact:** Complete loss of sender funds on any OOG contract execution. Contract storage is permanently corrupted by partially-executed operations. This breaks the fundamental atomicity guarantee of smart contract execution.

**Recommendation:** Wrap contract execution in `stateDb.Fork()` — only merge on success. On failure, revert to the fork point, then apply nonce increment + full gas charge to the original state.

**Severity:** Critical

---

### C-2: Failed Transactions Don't Increment Nonce or Charge Gas (Transfer + Staking Types)

**Location:** `TransactionExecutor.cs:71-105,328-364,366-400,402-432,434-463`

**Description:** When a Transfer, ValidatorRegister, ValidatorExit, StakeDeposit, or StakeWithdraw transaction fails validation within the executor (e.g., `InsufficientBalance`, `StakeBelowMinimum`), the function returns a failed receipt **without incrementing the nonce and without charging gas**. The sender's nonce remains unchanged.

There is a TOCTOU window: state can change between `TransactionValidator.Validate()` and `TransactionExecutor.Execute()` (e.g., another tx in the same block reduces the sender's balance). When this happens, the executor returns a failed receipt with zero state change.

**Impact:** Combined with C-3, a transaction can be included in a block, appear as failed, but cost the sender nothing. The same transaction (same nonce) can be replayed indefinitely in subsequent blocks, consuming block space for free. This is a griefing/DoS vector.

**Recommendation:** Always increment nonce and charge base gas fee on execution failure (Ethereum semantics). Move the nonce increment and gas charge before any value-transfer logic.

**Severity:** Critical

---

### C-3: BlockBuilder Includes Failed Transactions Without Advancing Nonce

**Location:** `BlockBuilder.cs:78-81`

**Description:** The `BlockBuilder` unconditionally adds every executed transaction to the block regardless of receipt success:
```csharp
var receipt = _executor.Execute(tx, stateDb, preliminaryHeader, validTxs.Count);
validTxs.Add(tx);  // Added regardless of receipt.Success
```
Combined with C-2, failed transactions don't advance the nonce, so the same transaction passes validation repeatedly.

**Impact:** Block space griefing. An attacker submits a transaction that always fails at execution time (e.g., transfers more than balance minus gas). It will be included in every block, consuming space and validation resources, while never paying gas fees.

**Recommendation:** Either (a) skip failed transactions from the block entirely, or (b) fix C-2 to always charge gas on failure so the cost deters abuse.

**Severity:** Critical

---

### C-4: BlockProductionLoop State Corruption — No Fork Before BuildBlock

**Location:** `BlockProductionLoop.cs:78-108`

**Description:** `ProduceBlock()` calls `_blockBuilder.BuildBlock(pendingTxs, _stateDb, ...)` directly on the canonical state database without forking. `BuildBlock` mutates `_stateDb` during transaction execution. If `_chainManager.AddBlock()` subsequently fails (e.g., a concurrent consensus finalization advanced the chain tip), the state database is permanently corrupted — it contains the effects of transactions from a block that was never added to the chain.

The consensus path in `NodeCoordinator` correctly uses `_stateDb.Fork()` before building, but `BlockProductionLoop` does not.

**Impact:** In solo block production mode, if block addition fails due to a race condition, the canonical state is permanently corrupted. All subsequent blocks are built on incorrect state.

**Recommendation:** Fork the state before building:
```csharp
var proposalState = _stateDb.Fork();
var block = _blockBuilder.BuildBlock(pendingTxs, proposalState, ...);
```

**Severity:** Critical

---

### C-5: Static Mutable Context Class is Not Thread-Safe

**Location:** `Basalt.Sdk.Contracts/Context.cs:1-178`, `ContractBridge.cs:18-94`

**Description:** The `Context` class uses static mutable fields (`Caller`, `Self`, `TxValue`, `BlockTimestamp`, `CallDepth`, `EventEmitted`, `NativeTransferHandler`, etc.). `ContractBridge.Setup()` saves and restores these via a disposable scope. `ContractStorage.Provider` is also a single static field.

If any two transactions execute concurrently (even partially overlapping), one contract execution will read/write another contract's `Caller`, `Self`, `TxValue`, and storage, leading to unauthorized fund transfers, privilege escalation, and total state corruption.

**Impact:** Complete state corruption under concurrent execution. Currently mitigated if execution is strictly single-threaded, but this invariant is not documented or enforced.

**Recommendation:** Either (a) enforce single-threaded execution with a documented invariant and a runtime check, or (b) refactor `Context` and `ContractStorage` to use `AsyncLocal<T>` or pass context through parameters.

**Severity:** Critical

---

### C-6: MaxCallDepth Declared But Never Enforced

**Location:** `VmExecutionContext.cs:27`, `ManagedContractRuntime.cs`, `SandboxedContractRuntime.cs`

**Description:** `VmExecutionContext.MaxCallDepth = 1024` is declared but never checked. Neither `ManagedContractRuntime.Execute()` nor `SandboxedContractRuntime.Execute()` validates `ctx.CallDepth < MaxCallDepth`. The `TransactionExecutor` always sets `CallDepth = 0` and never increments it. The SDK-level `Context.MaxCallDepth = 8` is only checked in `Context.CallContract()`, but `CrossContractCallHandler` is never wired by the production runtime.

**Impact:** Becomes immediately exploitable when cross-contract calls are wired — enables stack overflow attacks. Currently, cross-contract calls via the SDK path silently fail ("Cross-contract calls not available"), which is itself a correctness issue.

**Recommendation:** Enforce `MaxCallDepth` at the VM level. Wire `CrossContractCallHandler` if cross-contract calls are intended functionality, or remove/disable the SDK `CallContract` API entirely.

**Severity:** Critical

---

## High Severity

### H-1: GasMeter Arithmetic Overflow Bypasses Gas Limits

**Location:** `GasMeter.cs:24-28,34-40`

**Description:** The check `GasUsed + amount > GasLimit` can overflow. If `GasUsed = ulong.MaxValue - 10` and `amount = 20`, the sum wraps to `9`, passing the check incorrectly.

**Impact:** An attacker could craft a transaction that causes `GasUsed + amount` to overflow, bypassing gas limits entirely and enabling unbounded computation.

**Recommendation:** Change to `amount > GasLimit - GasUsed` (after verifying `GasLimit >= GasUsed`).

**Severity:** High

---

### H-2: Missing MaxPriorityFeePerGas <= MaxFeePerGas Validation

**Location:** `TransactionValidator.cs:59-63`

**Description:** For EIP-1559 transactions, the validator checks `MaxFeePerGas >= baseFee` but never verifies `MaxPriorityFeePerGas <= MaxFeePerGas`. Per EIP-1559, this invariant must hold. When violated, the tip calculation produces unexpected results (capped at `MaxFeePerGas - baseFee` instead of `MaxPriorityFeePerGas`).

**Impact:** Spec violation; confusing fee calculations for wallets and tooling.

**Recommendation:** Add `if (tx.MaxPriorityFeePerGas > tx.MaxFeePerGas) return Error(...)`.

**Severity:** High

---

### H-3: VarInt Size Mismatch in Transaction Signing Payload

**Location:** `Transaction.cs:95`

**Description:** `GetSigningPayloadSize()` allocates a constant `4` bytes for the VarInt length prefix of `Data`, but `BasaltWriter.WriteBytes()` uses LEB128 encoding (1-5 bytes depending on length). The hash includes trailing zero bytes that aren't part of the actual payload. For Data > 256 MB (unlikely given validation cap), the buffer would be too small, causing a buffer overrun.

**Impact:** Cross-implementation verification would fail — any alternate implementation computing the signing payload correctly would produce a different hash. Interoperability is broken.

**Recommendation:** Replace hardcoded `4` with proper VarInt size calculation. This is a consensus-breaking change requiring a hard fork if blocks have been finalized.

**Severity:** High

---

### H-4: UInt256 Unchecked Arithmetic in Transfer/Staking Balance Operations

**Location:** `TransactionExecutor.cs:79,97,255,337,411,509`

**Description:** Several balance operations use unchecked `+` operator instead of `CheckedAdd`:
- `tx.Value + gasFee` (Transfer, ValidatorRegister, StakeDeposit)
- `recipientState.Balance + tx.Value` (Transfer)
- `proposerState.Balance + totalTip` (CreditProposerTip)

The `+` operator on `UInt256` silently wraps on overflow. ContractDeploy/ContractCall paths correctly use `CheckedAdd`, but Transfer/Staking paths do not.

**Impact:** Theoretical balance wrap-around causing funds loss. Inconsistent with checked arithmetic used elsewhere.

**Recommendation:** Use `UInt256.CheckedAdd` consistently across all balance arithmetic.

**Severity:** High

---

### H-5: No Minimum GasLimit Validation

**Location:** `TransactionValidator.cs`

**Description:** The validator checks `tx.GasLimit <= BlockGasLimit` but never checks `GasLimit > 0` or `GasLimit >= intrinsicGas`. A zero-GasLimit contract deploy would: create a `GasMeter(0)`, immediately throw `OutOfGasException`, and trigger C-1 (partially committed state).

**Impact:** Zero-GasLimit transactions bypass economic constraints and interact badly with the OOG state corruption bug.

**Recommendation:** Add `if (tx.GasLimit < GasTable.TxBase) return Error(...)`.

**Severity:** High

---

### H-6: Unbounded ExtraData Causes Stack Overflow in Block Hash Computation

**Location:** `Block.cs:39-41`

**Description:** `BlockHeader.ExtraData` has no enforced size limit. `ComputeHash()` uses `stackalloc byte[size]` with the serialized size. A malicious proposer setting ExtraData to 1 MB+ would cause a stack overflow crash on any node computing the hash.

**Impact:** A malicious block proposer can crash all validators by proposing a block with oversized ExtraData. DoS vector.

**Recommendation:** Add `MaxExtraDataBytes` to `ChainParameters` and validate in `ChainManager.AddBlock()`. Use heap allocation for large sizes.

**Severity:** High

---

### H-7: ChainManager Does Not Validate State Root by Default

**Location:** `ChainManager.cs:28,62`

**Description:** The single-argument `AddBlock(block)` passes `null` for `computedStateRoot`, skipping the state root check entirely. Both `BlockProductionLoop` and the consensus path use this no-validation overload.

**Impact:** Blocks with incorrect state roots are accepted, potentially leading to state divergence between nodes or acceptance of invalid state transitions.

**Recommendation:** Always validate state root by computing and passing it to `AddBlock`.

**Severity:** High

---

### H-8: ChainManager Missing Header Field Validation

**Location:** `ChainManager.cs:34-70`

**Description:** `AddBlock` validates parent hash, block number, and timestamp, but does NOT validate:
- `ChainId` matches expected chain ID (cross-chain replay)
- `GasUsed <= GasLimit` (gas limit violation)
- `GasLimit == ChainParameters.BlockGasLimit` (gas cap enforcement)
- `BaseFee` is correctly derived from parent block (fee market integrity)
- `ProtocolVersion` is supported

**Impact:** A malicious proposer can submit blocks with incorrect chain IDs, exceeding gas limits, or incorrect base fees.

**Recommendation:** Add validation for all header fields.

**Severity:** High

---

### H-9: Unbounded In-Memory Block Storage — No Eviction

**Location:** `ChainManager.cs:66-67`

**Description:** `ChainManager` stores every block in two in-memory dictionaries with no eviction policy. At 2-second block times, that's ~43,200 blocks/day. With even 10 KB per block, that's ~420 MB/day of in-memory data.

**Impact:** Eventual OOM crash. Long-term liveness issue.

**Recommendation:** Implement sliding window or LRU cache retaining only the last N blocks in memory, with older blocks served from RocksDB.

**Severity:** High

---

### H-10: NativeTransferHandler Uses Unchecked Balance Addition

**Location:** `ContractBridge.cs:75-87`

**Description:** The recipient balance credit in the `NativeTransferHandler` lambda uses unchecked addition: `recipientBalance + amount`. The `TransactionExecutor` uses `CheckedAdd` everywhere else, but this code path does not.

**Impact:** Potential silent balance overflow in native transfers initiated from contracts.

**Recommendation:** Use `UInt256.CheckedAdd`.

**Severity:** High

---

### H-11: Integer Overflow in Host Function Gas Cost Calculation

**Location:** `HostInterface.cs:55,61`

**Description:** The gas formula `(ulong)(data.Length + 31) / 32 * GasTable.Blake3HashPerWord` — if `data.Length` is `int.MaxValue`, `data.Length + 31` overflows the `int` before the cast to `ulong`, producing an incorrect gas cost.

**Impact:** Incorrectly computed gas for edge-case large inputs.

**Recommendation:** Cast before addition: `((ulong)data.Length + 31) / 32 * ...`.

**Severity:** High

---

### H-12: Sandbox Timeout Not Applied to SDK Contract Execution

**Location:** `SandboxedContractRuntime.cs:144-156`

**Description:** The SDK contract path runs `ContractBridge.Setup()`, `_registry.CreateInstance()`, and `contract.Dispatch()` **outside** the `CancellationTokenSource` timeout. Timeout only applies to the built-in contract dispatch path.

**Impact:** A malicious SDK contract can run an infinite loop, causing node DoS.

**Recommendation:** Wrap SDK contract execution in the same timeout scope.

**Severity:** High

---

### H-13: Assembly Allow-List Not Enforced for Transitive Resolution

**Location:** `ContractAssemblyContext.cs:54-59`

**Description:** The `Load(AssemblyName)` override returns `null`, falling through to the default `AssemblyLoadContext` resolution. The allow-list only checks directly loaded assemblies via `LoadAndValidate()`, not transitive dependencies resolved at runtime. A contract's allowed assemblies can transitively load `System.IO.FileSystem`, `System.Net.Http`, `System.Diagnostics.Process`, etc.

**Impact:** Contracts can potentially bypass the sandbox by using transitively-resolved disallowed assemblies.

**Recommendation:** Change `Load()` to actively reject non-allowed assemblies instead of returning null.

**Severity:** High

---

## Medium Severity

### M-1: Mempool Has No Per-Sender Transaction Limit

**Location:** `Mempool.cs`

**Description:** Global size limit (10,000) but no per-sender limit. A single sender can fill the entire mempool, preventing all other users from submitting transactions.

**Impact:** DoS against other users.

**Recommendation:** Add per-sender cap (e.g., 64 txs).

**Severity:** Medium

---

### M-2: Mempool Does Not Validate Transactions Before Admission

**Location:** `Mempool.cs:40-61`

**Description:** `Add()` only checks for duplicate hashes and size limits. No signature, chain ID, nonce, or balance validation. Invalid transactions are stored, gossiped, and only rejected at block building time.

**Impact:** Resource exhaustion from invalid transaction spam.

**Recommendation:** Call `TransactionValidator.Validate()` before admission.

**Severity:** Medium

---

### M-3: Mempool Full Rejection With No Low-Fee Eviction

**Location:** `Mempool.cs:48-49`

**Description:** When the mempool is full, new transactions are unconditionally rejected. No comparison against the lowest-fee transaction. An attacker fills the pool with minimum-fee transactions, blocking all legitimate submissions.

**Impact:** Mempool pinning attack.

**Recommendation:** Evict the lowest-fee transaction when a higher-fee transaction arrives and the pool is full.

**Severity:** Medium

---

### M-4: GetPending Re-Sorting Breaks Per-Sender Nonce Ordering

**Location:** `Mempool.cs:127-138`

**Description:** After filtering for contiguous nonce sequences per sender, the result is re-sorted globally by fee. For a single sender with nonces [0, 1, 2], they might be reordered as [2, 0, 1] if tx 2 has the highest fee. Only the first-nonce tx per sender will succeed; others fail validation.

**Impact:** Reduced throughput — at most one transaction per sender per block for multi-tx senders.

**Recommendation:** Sort by fee between senders but maintain nonce order within each sender (group-then-interleave).

**Severity:** Medium

---

### M-5: Inconsistent Gas Accounting in Block Builder (GasLimit vs GasUsed)

**Location:** `BlockBuilder.cs:67,81`

**Description:** The gas limit check uses `tx.GasLimit` (maximum possible), but `totalGasUsed` accumulates `receipt.GasUsed` (actual). A tx with `GasLimit=50M` that uses `21K` gas will "fill" the block for the next check, but `totalGasUsed` only records `21K`, leading to suboptimal block utilization.

**Impact:** Block space underutilization. High-GasLimit transactions block smaller ones.

**Recommendation:** Track both `totalGasReserved` (for the limit check) and `totalGasUsed` (for the header).

**Severity:** Medium

---

### M-6: Receipts Have Wrong BlockHash From Preliminary Header

**Location:** `BlockBuilder.cs:47-60`

**Description:** The preliminary header has `Hash256.Zero` for state/tx/receipts roots. Receipts generated during block building contain a block hash computed from this incomplete header. In the consensus path, receipts are regenerated; in `BlockProductionLoop`, these incorrect receipts are stored.

**Impact:** API consumers relying on receipt `BlockHash` get incorrect values in solo block production mode.

**Recommendation:** Update receipt `BlockHash` after computing the final header.

**Severity:** Medium

---

### M-7: PostStateRoot Computed Per Receipt — O(n²) State Root Computation

**Location:** `TransactionExecutor.cs:528`

**Description:** `stateDb.ComputeStateRoot()` is called for every receipt. For blocks with many transactions, this is O(n²) — each computation is O(modified nodes × log(trie depth)).

**Impact:** Severe performance bottleneck during block building, directly limiting throughput.

**Recommendation:** Remove per-receipt state roots (Ethereum removed this post-Byzantium) or compute only once at block end.

**Severity:** Medium

---

### M-8: Nonce Gap Filtering Disabled in BlockProductionLoop

**Location:** `BlockProductionLoop.cs:87`

**Description:** `GetPending` is called without the `stateDb` parameter. When `stateDb` is null, nonce-contiguity filtering is completely bypassed. Transactions with nonce gaps will be included and fail during execution, wasting block space.

**Impact:** Blocks may contain guaranteed-to-fail transactions in solo production mode.

**Recommendation:** Pass `_stateDb` to `GetPending()`.

**Severity:** Medium

---

### M-9: ResumeFromBlock Leaves Gap in Block Lookup

**Location:** `ChainManager.cs:90-105`

**Description:** After recovery, only genesis and the latest block are in memory. All intermediate blocks return `null` from `GetBlockByNumber`/`GetBlockByHash`. API queries for historical blocks fail after restart.

**Impact:** Broken historical block queries after recovery.

**Recommendation:** Load a window of recent blocks from RocksDB during recovery, or fall through to persistent storage on cache miss.

**Severity:** Medium

---

### M-10: CreateGenesisBlock Ignores AddBlock Result

**Location:** `ChainManager.cs:153`

**Description:** `AddBlock(genesis)` return value is discarded. If genesis was already added (double-call bug), the second call silently fails.

**Impact:** Silent failure during genesis initialization.

**Recommendation:** Check result and throw on failure.

**Severity:** Medium

---

### M-11: Genesis Deployer Silently Ignores Constructor Failures

**Location:** `GenesisContractDeployer.cs:119-121`

**Description:** No explicit error handling for constructor failures. `GasMeter` at line 104 has a 10M gas limit — complex constructors could OOG, partially deploying some contracts and leaving genesis state inconsistent.

**Impact:** Partially initialized system contracts at genesis.

**Recommendation:** Wrap each deployment in try-catch, use `ulong.MaxValue` for genesis gas (no economic constraint at genesis).

**Severity:** Medium

---

### M-12: Compliance Check Uses `tx.To` for Staking Transactions

**Location:** `TransactionExecutor.cs:44-56`

**Description:** Compliance verification calls `_complianceVerifier.GetRequirements(tx.To.ToArray())`. For staking transactions, `To` may be the sender's own address or zero, not a meaningful recipient. Compliance requirements are looked up on an irrelevant address.

**Impact:** Compliance checks may incorrectly block or allow staking operations.

**Recommendation:** Skip compliance check for staking transaction types, or use a dedicated staking compliance path.

**Severity:** Medium

---

### M-13: SandboxedContractRuntime.Deploy Skips SDK Contract Constructor

**Location:** `SandboxedContractRuntime.cs:46-112`

**Description:** Unlike `ManagedContractRuntime.Deploy()` which runs SDK contract constructors, `SandboxedContractRuntime.Deploy()` only stores code and returns success. SDK contracts deployed via the sandboxed runtime have uninitialized storage.

**Impact:** SDK contracts deployed through the sandbox will behave incorrectly on first call.

**Recommendation:** Run the SDK constructor in the deploy path.

**Severity:** Medium

---

### M-14: HostStorageProvider Missing Payload Length Validation

**Location:** `HostStorageProvider.cs:130-153`

**Description:** `DeserializeValue<T>` does not validate payload length for each type tag. Corrupted storage (e.g., `[TagULong, 0x00]` — only 1 byte instead of 8) causes `ArgumentOutOfRangeException` or incorrect deserialization. Different nodes handling the exception differently causes consensus divergence.

**Impact:** Corrupted storage causes unhandled exceptions or consensus divergence.

**Recommendation:** Validate payload length matches expected size for each tag before reading.

**Severity:** Medium

---

## Low Severity / Recommendations

### L-1: Mutable Arrays in Cached-Hash Transaction Type

**Location:** `Transaction.cs:35,48`

**Description:** `Data` and `ComplianceProofs` are `byte[]` with `init` setters. While `init` prevents reassignment, array contents can still be mutated after hash computation, invalidating the cached hash.

**Recommendation:** Defensive copy arrays in the property getter, or document immutability requirements.

**Severity:** Low

---

### L-2: Block Header VarInt Size Mismatch

**Location:** `Block.cs:46-49`

**Description:** `GetSerializedSize()` reserves 4 bytes for ExtraData VarInt prefix, but actual VarInt encoding is 1-5 bytes. Buffer is oversized (harmless, since `WrittenSpan` is used for hashing), but breaks if `GetSerializedSize()` is used for wire-format allocation.

**Recommendation:** Use proper VarInt size calculation.

**Severity:** Low

---

### L-3: Unused `Priority` Field in Signing Payload

**Location:** `Transaction.cs:36,111`

**Description:** `Priority` byte is included in the signing payload but never read by executor, validator, or mempool. Dead code in the wire format.

**Recommendation:** Remove or document intended use.

**Severity:** Low

---

### L-4: Nonce Overflow at ulong.MaxValue

**Location:** `TransactionExecutor.cs:89,135,247,357,393,425,456`

**Description:** `senderState.Nonce + 1` uses unchecked `ulong` arithmetic. At `ulong.MaxValue`, wraps to 0, enabling replay of entire tx history. Practically unreachable (2^64 txs from one address).

**Recommendation:** Add overflow check if paranoid.

**Severity:** Low

---

### L-5: Redundant Signature Verification in Block Building

**Location:** `BlockBuilder.cs:70`

**Description:** `Validate()` re-verifies Ed25519 signatures for every transaction during block building. Signatures were already verified at mempool admission (if M-2 is fixed). Each verify costs ~100µs, adding ~1 second for 10K-tx blocks.

**Recommendation:** Cache signature verification results.

**Severity:** Low

---

### L-6: Merkle Tree Odd-Leaf Promotion Without Re-Hashing

**Location:** `BlockBuilder.cs:183`

**Description:** When transaction count is odd, the last hash is promoted to the next level without re-hashing. This is a known "second pre-image" weakness. Computationally infeasible to exploit with BLAKE3's 256-bit security.

**Recommendation:** Re-hash promoted leaves or use domain separation between leaf and internal node hashes.

**Severity:** Low

---

### L-7: StopAsync Has No Timeout

**Location:** `BlockProductionLoop.cs:50-56`

**Description:** `StopAsync` awaits the loop task with no timeout. If `ProduceBlock()` hangs, node shutdown hangs indefinitely.

**Recommendation:** Add timeout: `await Task.WhenAny(_loopTask, Task.Delay(30_000))`.

**Severity:** Low

---

### L-8: Wrong Error Code for Timestamp Validation

**Location:** `ChainManager.cs:49-51`

**Description:** Timestamp monotonicity check uses `BasaltErrorCode.InvalidBlockNumber` instead of a timestamp-specific code. Misleading in logs.

**Recommendation:** Use dedicated `InvalidTimestamp` error code.

**Severity:** Low

---

### L-9: System Address Collision Risk

**Location:** `GenesisContractDeployer.cs:28-34`

**Description:** System contracts use `0x000...XXYY` addresses. User contract addresses are `BLAKE3(sender||nonce)[12..32]`. Collision probability is ~2^-144 — negligible, but no explicit check exists.

**Recommendation:** Add a system address check in `ExecuteContractDeploy`.

**Severity:** Low

---

### L-10: Context.GasRemaining is a Stale Snapshot

**Location:** `ContractBridge.cs:42`

**Description:** `Context.GasRemaining` is set once at bridge setup. As gas is consumed via host calls, the value becomes stale. Contracts making gas-aware decisions see incorrect values.

**Recommendation:** Make `GasRemaining` a live delegate: `Context.GasRemaining = () => ctx.GasMeter.GasRemaining`.

**Severity:** Low

---

### L-11: ResourceLimiter.Allocate TOCTOU Race

**Location:** `ResourceLimiter.cs:46-62`

**Description:** `Interlocked.Add` then check-and-rollback pattern allows momentary over-allocation and spurious failures under contention.

**Recommendation:** Use `Interlocked.CompareExchange` loop for atomic check-and-allocate.

**Severity:** Low

---

### L-12: HostStorageProvider ComputeStorageKey Allocates on Every Access

**Location:** `HostStorageProvider.cs:69`

**Description:** `Encoding.UTF8.GetBytes(key)` allocates a new byte array on every storage access. High GC pressure on hot paths.

**Recommendation:** Use `stackalloc` + `Encoding.UTF8.GetBytes(key, buffer)`.

**Severity:** Low

---

### L-13: EmittedLogs List Unbounded Per Transaction

**Location:** `VmExecutionContext.cs:22`

**Description:** `EmittedLogs` has no size cap. A contract with sufficient gas can emit thousands of events, creating large in-memory lists and receipts.

**Recommendation:** Cap `EmittedLogs` count (e.g., 256 per tx).

**Severity:** Low

---

## Test Coverage Gaps

### Gap 1: EIP-1559 BaseFeeCalculator — ZERO TESTS (High Priority)

`BaseFeeCalculator.Calculate()` has no tests whatsoever. Missing:
- Fee unchanged when `parentGasUsed == targetGas`
- Fee increase when above target, decrease when below
- Maximum 12.5% change per block
- Minimum increase of 1 when rounding to 0
- Floor at zero; reset from zero to `InitialBaseFee`
- `EffectiveGasPrice` calculation
- Proposer tip credit and base fee burn

### Gap 2: State Rollback on Failed Contract Deploy/Call — NOT TESTED (High Priority)

The `TransactionExecutor` has explicit rollback code (lines 171-176, 281-289) that is never exercised by any test. Missing:
- Deploy failure: verify sender balance restored, contract account deleted
- Call failure with value: verify value returned to sender
- Multiple txs in block where one fails: verify no state corruption for subsequent txs

### Gap 3: Mempool.PruneStale — NOT TESTED (High Priority)

Production-critical method for removing stale-nonce and below-base-fee transactions has zero tests.

### Gap 4: Mempool.GetPending With stateDb (Nonce-Gap Filtering) — NOT TESTED (High Priority)

Complex nonce-contiguity filtering logic (Mempool.cs:84-138) is entirely untested. Tests only use the no-stateDb path.

### Gap 5: ContractCall Through TransactionExecutor (End-to-End) — NOT TESTED (Medium Priority)

No test exercises `ExecuteContractCall()` for value transfer, gas refund, and failure revert paths.

### Gap 6: Self-Transfer (sender == recipient) — NOT TESTED (Medium Priority)

Zero tests where sender equals recipient. Incorrect handling could duplicate funds.

### Gap 7: EIP-1559 Transaction Ordering in Mempool — NOT TESTED (Medium Priority)

No test creates transactions with `MaxFeePerGas`/`MaxPriorityFeePerGas` to verify correct ordering.

### Gap 8: Sandbox Timeout Enforcement — NOT TESTED (Medium Priority)

`SandboxTimeoutException` is never tested. No test verifies that a contract exceeding `ExecutionTimeout` is terminated.

### Gap 9: GenesisContractDeployer — NOT TESTED (Low Priority)

No tests anywhere for this class. System contract deployment at genesis is untested.

### Gap 10: BlockProductionLoop — NOT TESTED (Low Priority)

No tests anywhere for this class. Timed block production is untested.

---

## Positive Findings

### P-1: EIP-1559 Formula Implementation is Correct

`BaseFeeCalculator.Calculate()` correctly implements the EIP-1559 formula with target gas, elasticity multiplier, change denominator, minimum increase of 1, and zero-base-fee reset. The math is sound despite lacking tests.

### P-2: Consistent Receipt Generation

`CreateReceipt()` generates receipts with all necessary fields including `PostStateRoot`, `CumulativeGasUsed`, `EffectiveGasPrice`, and `EventLogs`. Receipt data is sufficient for full transaction tracing.

### P-3: Comprehensive HostStorageProvider Tag System

The tagged serialization system (`TagUInt256 = 0x0A`) with 10 type tags covers all necessary storage types. The `HostStorageProviderTests` (30 tests) provide excellent roundtrip coverage for all types.

### P-4: Correct SDK Contract Dispatch Architecture

The two-selector scheme (BLAKE3 for built-in, FNV-1a for SDK via source generator) with `0xBA5A` magic bytes is well-designed and AOT-safe. `ContractRegistry` tests (26 tests) provide thorough coverage.

### P-5: Robust Transaction Validation Pipeline

`TransactionValidator` checks 7 validation rules in the correct order: signature, sender match, balance, nonce, gas limit, gas price, chain ID, and data size. The 15 validator tests cover all paths.

### P-6: Sound Merkle Root Computation

`ComputeMerkleRoot` produces correct BLAKE3-based Merkle roots. Empty set returns `Hash256.Zero`. The implementation handles powers of two and odd counts correctly (despite the minor odd-leaf promotion pattern noted in L-6).

### P-7: Clean Separation of Concerns

The execution layer has clear separation: `Transaction` (data), `TransactionValidator` (pre-checks), `TransactionExecutor` (state mutation), `BlockBuilder` (assembly), `ChainManager` (chain state). This makes the code auditable and maintainable.
