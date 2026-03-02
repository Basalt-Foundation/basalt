# Basalt.Execution.Tests

Unit tests for Basalt transaction processing: validation, execution, mempool, chain management, block building, VM operations, sandboxed contract runtime, staking transactions, SDK contract execution, and the Caldera Fusion DEX engine. **556 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| AuditRemediation | 45 | Audit trail, remediation workflows, compliance integration |
| ConcentratedPool | 33 | Tick-based concentrated liquidity: position minting/burning, fee collection, tick crossing, swap loops |
| HostStorageProvider | 30 | On-chain storage provider: read/write/delete, tag serialization, UInt256 storage |
| DexEngine | 27 | Pool creation, liquidity management, single swaps, limit orders, BST-20 integration |
| ContractRegistry | 26 | Contract type registration, SDK contract dispatch, type ID lookup |
| DexMath | 23 | Full math library: MulDiv, Sqrt, overflow safety, GetAmountOut/In, fee tier validation |
| DexState | 22 | State reader/writer: pool metadata, reserves, LP balances, order storage, TWAP accumulators |
| DexIntegration | 22 | End-to-end DEX flows: pool lifecycle, multi-user trading, batch settlement, LP transfers |
| SqrtPriceMath | 21 | Price math: GetAmount0Delta, GetAmount1Delta, GetNextSqrtPriceFromInput |
| LpToken | 19 | LP share management: minting, burning, transfers, approvals, allowances |
| SdkContractExecution | 18 | SDK contract deploy/call via ManagedContractRuntime, gas accounting |
| DexFuzz | 17 | Fuzz testing: random pool operations, invariant checking, edge case discovery |
| TransactionValidator | 15 | Signature validation, nonce checks, balance verification, gas limit bounds, data size limits, chain ID enforcement |
| ChainManager | 15 | Chain state management, block appending, genesis initialization, block retrieval, height tracking |
| BatchAuctionSolver | 15 | Batch auction: clearing price computation, volume maximization, partial fills, limit order crossing |
| Mempool | 14 | Transaction pool: add/remove, capacity limits, nonce ordering, duplicate rejection, fee prioritization |
| TickMath | 14 | Tick-to-sqrtPrice conversion, binary decomposition, tick range validation |
| DynamicFees | 13 | Volatility-adjusted fees: threshold, growth factor, clamping, base fee interaction |
| TwapOracle | 13 | Time-weighted average price: accumulator updates, windowed queries, volatility computation |
| LiquidityMath | 13 | Liquidity calculations: GetLiquidityForAmounts, AddDelta, three-case range logic |
| MainnetHardening | 13 | Emergency pause, governance parameters, rate limiting, admin controls |
| MainnetReadiness | 38 | Production readiness: parameter bounds, pool creation limits, TWAP window, solver rewards |
| SandboxedRuntime | 12 | Contract sandboxing: deploy/call, gas metering, memory limits, revert handling |
| FeeTracking | 12 | Concentrated liquidity fee tracking: global/per-tick/per-position fee growth |
| EncryptedIntents | 12 | EC-ElGamal encryption/decryption, threshold reconstruction, AES-256-GCM |
| BlockBuilder | 12 | Block construction, transaction root computation, receipts root, state root updates |
| StakingTransactions | 11 | Validator register/exit, stake deposit/withdraw, balance debits, P2P endpoint |
| VM | 9 | Virtual machine: opcode execution, stack operations, memory access, gas accounting |
| OrderBook | 7 | Limit order matching: per-pool linked lists, crossing order detection, expiry cleanup |
| Transaction | 5 | Transaction creation, signing, hash computation, serialization roundtrip |
| FaucetDiagnostic | 3 | Faucet endpoint and SDK contract diagnostics |

**Total: 556 tests**

## Test Files

- `TransactionValidatorTests.cs` -- Transaction validation rules: signatures, nonces, balances, gas, chain ID
- `ChainManagerTests.cs` -- Chain state: block append, genesis, block retrieval
- `MempoolTests.cs` -- Transaction pool management: ordering, capacity, deduplication
- `Sandbox/SandboxedRuntimeTests.cs` -- Sandboxed contract execution: isolation, gas limits, revert
- `BlockBuilderTests.cs` -- Block assembly: transaction ordering, Merkle roots, state updates
- `StakingTransactionTests.cs` -- Staking transaction execution: register, exit, deposit, withdraw
- `VmTests.cs` -- Basalt VM: opcode execution, stack, memory, gas metering
- `TransactionTests.cs` -- Transaction struct: construction, signing, hashing
- `AuditRemediationTests.cs` -- Audit trail and remediation workflows
- `ContractRegistryTests.cs` -- Contract type registration and dispatch
- `HostStorageProviderTests.cs` -- On-chain storage provider with tag serialization
- `SdkContractExecutionTests.cs` -- SDK contract deploy/call integration
- `FaucetDiagnosticTests.cs` -- Faucet and SDK diagnostics
- `Dex/BatchAuctionSolverTests.cs` -- Batch auction clearing price computation
- `Dex/ConcentratedPoolTests.cs` -- Tick-based concentrated liquidity
- `Dex/DexEngineTests.cs` -- Core DEX engine operations
- `Dex/DexFuzzTests.cs` -- Fuzz testing for DEX invariants
- `Dex/DexMathTests.cs` -- DEX math library
- `Dex/DexStateTests.cs` -- DEX state reader/writer
- `Dex/DynamicFeeTests.cs` -- Volatility-adjusted fee calculations
- `Dex/EncryptedIntentTests.cs` -- EC-ElGamal encrypted swap intents
- `Dex/FeeTrackingTests.cs` -- Concentrated liquidity fee tracking
- `Dex/IntegrationTests.cs` -- End-to-end DEX integration scenarios
- `Dex/LiquidityMathTests.cs` -- Liquidity math calculations
- `Dex/LpTokenTests.cs` -- LP share transfers and approvals
- `Dex/MainnetHardeningTests.cs` -- Emergency pause and admin controls
- `Dex/MainnetReadinessTests.cs` -- Production readiness validation
- `Dex/MainnetReadinessTests2.cs` -- Extended production readiness tests
- `Dex/OrderBookTests.cs` -- Limit order matching and cleanup
- `Dex/SqrtPriceMathTests.cs` -- Square root price math
- `Dex/TickMathTests.cs` -- Tick-to-price conversions
- `Dex/TwapOracleTests.cs` -- TWAP oracle accumulation and queries

## Running

```bash
dotnet test tests/Basalt.Execution.Tests
```
