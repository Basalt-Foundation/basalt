# Basalt.Execution.Tests

Unit tests for Basalt transaction processing: validation, execution, mempool, chain management, block building, VM operations, sandboxed contract runtime, and staking transactions. **207 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| TransactionValidator | 15 | Signature validation, nonce checks, balance verification, gas limit bounds, data size limits, chain ID enforcement, duplicate detection |
| ChainManager | 15 | Chain state management, block appending, fork handling, genesis initialization, block retrieval, height tracking |
| Mempool | 14 | Transaction pool: add/remove, capacity limits, nonce ordering, expiry, duplicate rejection, fee prioritization |
| SandboxedRuntime | 12 | Contract sandboxing: deploy/call, gas metering, memory limits, revert handling, forbidden operations |
| BlockBuilder | 12 | Block construction, transaction root computation, receipts root, state root updates, gas usage tracking |
| StakingTransactions | 11 | Validator register/exit, stake deposit/withdraw, balance debits, P2P endpoint, error cases (below minimum, insufficient balance, not registered, no staking state) |
| VM | 9 | Virtual machine: opcode execution, stack operations, memory access, gas accounting, contract calls |
| Transaction | 5 | Transaction creation, signing, hash computation, serialization roundtrip |
| FaucetDiagnostic | Various | Faucet endpoint and SDK contract tests |

**Total: 207 tests**

## Test Files

- `TransactionValidatorTests.cs` -- Transaction validation rules: signatures, nonces, balances, gas, chain ID
- `ChainManagerTests.cs` -- Chain state: block append, fork detection, genesis, block retrieval
- `MempoolTests.cs` -- Transaction pool management: ordering, capacity, expiry, deduplication
- `Sandbox/SandboxedRuntimeTests.cs` -- Sandboxed contract execution: isolation, gas limits, revert
- `BlockBuilderTests.cs` -- Block assembly: transaction ordering, Merkle roots, state updates
- `StakingTransactionTests.cs` -- Staking transaction execution: register, exit, deposit, withdraw, edge cases
- `VmTests.cs` -- Basalt VM: opcode execution, stack, memory, gas metering
- `TransactionTests.cs` -- Transaction struct: construction, signing, hashing

## Running

```bash
dotnet test tests/Basalt.Execution.Tests
```
