# Basalt Security & Quality Audit — Execution Layer

## Scope

Audit the transaction processing, smart contract execution runtime, gas metering, block building, and chain management:

| Project | Path | Description |
|---|---|---|
| `Basalt.Execution` | `src/execution/Basalt.Execution/` | Transaction executor, mempool, block builder, VM runtime, contract sandbox, EIP-1559 base fee |

Corresponding test project: `tests/Basalt.Execution.Tests/`

---

## Files to Audit

### Transaction Processing
- `Transaction.cs` (~220 lines) — `TransactionType` enum, `Transaction`, `TransactionReceipt`, `EventLog`
- `TransactionExecutor.cs` (~532 lines) — All 7 transaction type handlers with EIP-1559 pricing
- `TransactionValidator.cs` (~80 lines) — Pre-execution validation
- `Mempool.cs` (~239 lines) — Transaction pool with EIP-1559 sorting
- `BaseFeeCalculator.cs` (~57 lines) — EIP-1559 dynamic base fee formula

### Block Production
- `Block.cs` (~81 lines) — `BlockHeader`, `Block`
- `BlockBuilder.cs` (~190 lines) — Block assembly from mempool transactions
- `BlockProductionLoop.cs` (~109 lines) — Timed block production
- `ChainManager.cs` (~156 lines) — Chain state, block application, recovery

### Contract Runtime
- `GenesisContractDeployer.cs` (~126 lines) — System contract deployment at genesis
- `VM/ExecutionContext.cs` (~28 lines) — `VmExecutionContext`
- `VM/GasMeter.cs` (~78 lines) — Gas accounting and `OutOfGasException`
- `VM/GasTable.cs` (~67 lines) — Gas cost constants
- `VM/HostInterface.cs` (~143 lines) — Host functions exposed to contracts
- `VM/HostStorageProvider.cs` (~154 lines) — On-chain storage with tagged serialization (`TagUInt256 = 0x0A`)
- `VM/ContractBridge.cs` (~125 lines) — Wires SDK `Context.*` from `VmExecutionContext`
- `VM/ContractRegistry.cs` (~211 lines) — Contract type registration (0x0001-0x0007 standards + 0x0100-0x0107 system)
- `VM/IContractRuntime.cs` (~44 lines) — Runtime interface
- `VM/ManagedContractRuntime.cs` (~296 lines) — SDK contract execution

### Contract Sandbox
- `VM/Sandbox/ContractAssemblyContext.cs` (~104 lines) — Assembly allow-list for isolation
- `VM/Sandbox/ResourceLimiter.cs` (~83 lines) — Memory and CPU resource limits
- `VM/Sandbox/SandboxConfiguration.cs` (~25 lines) — Sandbox parameters
- `VM/Sandbox/SandboxedContractRuntime.cs` (~349 lines) — Sandboxed execution environment
- `VM/Sandbox/SandboxedHostBridge.cs` (~236 lines) — Sandboxed host function bridge
- `VM/Sandbox/SandboxExceptions.cs` (~45 lines) — Timeout, memory limit, isolation exceptions

---

## Audit Objectives

### 1. Transaction Execution Correctness (CRITICAL)
- Verify all 7 transaction types are correctly handled: Transfer, ContractDeploy, ContractCall, StakeDeposit, StakeWithdraw, ValidatorRegister, ValidatorExit.
- For each type, verify:
  - Nonce is checked and incremented atomically
  - Sufficient balance for value + gas is verified upfront
  - State changes are applied correctly on success
  - State changes are reverted on failure (atomicity)
  - Gas is consumed correctly and refunded on completion
  - Receipts are generated with correct status and gas usage

### 2. EIP-1559 Gas Pricing (CRITICAL)
- Verify `BaseFeeCalculator.Calculate()` implements the EIP-1559 formula correctly:
  - Target gas = gasLimit / ElasticityMultiplier (default 2)
  - Max 12.5% change per block (denominator 8)
  - Base fee increases when blocks are above target, decreases when below
- Verify `Transaction.EffectiveGasPrice(baseFee)` = `min(MaxFeePerGas, BaseFee + MaxPriorityFeePerGas)`.
- Verify upfront debit uses `EffectiveMaxFee * gasLimit` and refund uses `effectiveGasPrice`.
- Verify tip = `(effectiveGasPrice - baseFee) * gasUsed` is credited to proposer.
- Verify base fee portion is burned (not credited to anyone).
- Check that legacy transactions (`GasPrice` field, non-EIP-1559) are handled correctly.

### 3. Double-Spend Prevention
- Verify nonce management prevents replay attacks.
- Check that the same transaction cannot be executed twice in the same block or across blocks.
- Verify that mempool rejects transactions with nonces already seen.

### 4. Contract Execution Security (CRITICAL)
- Verify `ManagedContractRuntime` correctly dispatches calls to SDK contracts.
- Check that `ContractBridge.Setup()` correctly wires `Context.TxValue` as `UInt256` without truncation.
- Verify that contract storage isolation is enforced — one contract cannot access another's storage.
- Check that `HostStorageProvider.TagUInt256` (0x0A) correctly serializes/deserializes UInt256 values.
- Verify cross-contract call depth limits to prevent stack overflow.
- Verify that contract revert correctly unwinds all state changes.

### 5. Sandbox Security
- Verify `ContractAssemblyContext` correctly restricts which assemblies contracts can load.
- Check `ResourceLimiter` for memory limit enforcement and timeout handling.
- Verify that sandbox escape is not possible via:
  - Reflection (should be blocked by AOT + analyzer)
  - File system access
  - Network access
  - Environment variable reads
  - Process spawning
- Check that `SandboxedHostBridge` correctly mediates all host function calls.
- Verify timeout handling does not leave dangling threads or inconsistent state.

### 6. Mempool Security
- Verify mempool size limits to prevent memory exhaustion.
- Check that `MempoolEntryComparer` correctly sorts by `EffectiveMaxFee` (highest first).
- Verify that low-fee transactions are correctly evicted when the pool is full.
- Check for transaction pinning attacks (where a low-fee parent prevents a high-fee child from being included).
- Verify that `OnTransactionAdded` event fires correctly for gossip.

### 7. Block Builder
- Verify `BlockBuilder.BuildBlock()` correctly selects transactions from mempool.
- Check that the block gas limit is enforced.
- Verify that `BuildBlock()` mutates `stateDb` — confirm this is intentional and only the leader does this.
- Check that invalid transactions in a block are correctly skipped without aborting the entire block.

### 8. Chain Manager & Recovery
- Verify `ChainManager.ResumeFromBlock()` correctly recovers from stored blocks.
- Check that state root validation ensures chain integrity after recovery.
- Verify that `ApplyBlock()` and `RevertBlock()` (if exists) are inverse operations.

### 9. Genesis Contract Deployment
- Verify `GenesisContractDeployer` correctly deploys all 8 system contracts (0x...1001 through 0x...1008).
- Check that genesis state is deterministic — same parameters always produce the same genesis block.

### 10. Test Coverage
- Review `tests/Basalt.Execution.Tests/` for:
  - All 7 transaction types
  - EIP-1559 base fee calculation edge cases
  - Gas metering: exact gas, out-of-gas, refund
  - Contract deploy and call flows
  - Sandbox resource limit enforcement
  - Mempool ordering, eviction, size limits
  - Block builder with mixed valid/invalid transactions
  - Chain recovery from stored blocks

---

## Key Context

- Two selector schemes: BLAKE3 (built-in methods), FNV-1a (SDK contracts via source generator).
- Magic bytes `[0xBA, 0x5A]` identify SDK contracts.
- `TransactionType` enum: Transfer=0, ContractDeploy=1, ContractCall=2, StakeDeposit=3, StakeWithdraw=4, ValidatorRegister=5, ValidatorExit=6.
- `AccountState` is a readonly struct — updates require `SetAccount(addr, new AccountState { ... })`.
- `GasMeter` constructor: `new GasMeter(ulong gasLimit)` — NOT init properties.
- `BlockBuilder.BuildBlock()` mutates `stateDb` — the leader executes during proposal; non-leaders execute in `OnBlockFinalized`.
- Devnet `InitialBaseFee = 1` for backward compat.
- `VmExecutionContext` was renamed from `ExecutionContext` to avoid `System.Threading.ExecutionContext` conflict.

---

## Output Format

Write your findings to `audit/output/07-execution.md` with the following structure:

```markdown
# Execution Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Double-spend, gas manipulation, sandbox escape, fund loss]

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
3. **Impact**: What could go wrong
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
