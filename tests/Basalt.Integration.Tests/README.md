# Basalt.Integration.Tests

End-to-end integration tests spanning multiple Basalt modules. Verifies the full transaction lifecycle from key generation through execution and state verification. **27 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| ChainLifecycleTests | 6 | Genesis block creation with initial balances, state root computation, invalid parent hash rejection, invalid block number rejection, valid block sequence acceptance, block lookup by number and hash |
| CryptoIntegrationTests | 6 | Key pair address derivation determinism, transaction signature roundtrip, tampered transaction detection, BLAKE3 hash determinism, different data produces different hashes, transaction hash determinism |
| TransferE2ETests | 5 | Full transfer lifecycle (genesis -> sign -> validate -> mempool -> execute -> verify), multiple sequential transfers, insufficient balance rejection, wrong nonce rejection, wrong chain ID rejection |
| ComplianceBridgeTests | 4 | KYC-verified user bridge deposit, sanctioned address blocked by compliance, full bridge flow with 2-of-3 multisig relayer, Merkle proof verification for multiple deposits |
| MempoolIntegrationTests | 3 | Gas price ordering (highest first), duplicate transaction rejection, confirmed transaction removal |
| StateConsistencyTests | 3 | Identical transactions produce identical state roots, different recipients produce different roots, state root changes after each transaction (4 unique roots) |

**Total: 27 tests**

## Test Files

- `ChainLifecycleTests.cs` -- Chain lifecycle: genesis creation, state roots, block validation, block sequence acceptance, block lookup
- `CryptoIntegrationTests.cs` -- Cryptographic operations across the stack: key derivation, signing, verification, hashing
- `TransferE2ETests.cs` -- End-to-end transfers: sign, validate, execute, verify state changes
- `ComplianceBridgeTests.cs` -- Cross-module tests: compliance + bridge interaction, multisig relayer, Merkle proofs
- `MempoolTests.cs` -- Mempool integration: gas price ordering, deduplication, confirmed removal
- `StateConsistencyTests.cs` -- State root determinism: identical/different transactions, per-transaction root changes

## Running

```bash
dotnet test tests/Basalt.Integration.Tests
```
