# Basalt.Crypto.Tests

Unit tests for Basalt cryptographic operations: BLAKE3, Ed25519, BLS12-381, and Keccak-256. **48 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| BLS12-381 | 12 | Sign/verify, aggregate signatures, deterministic output, key generation, invalid key handling |
| Ed25519 | 9 | Key generation, sign/verify, batch verify, tampered signature detection, address derivation determinism |
| BLAKE3 | 6 | Deterministic output, different inputs produce different hashes, `HashPair`, incremental hashing |
| Keccak-256 | 4 | Hash output correctness, address derivation from public keys |

**Total: 48 tests**

## Test Files

- `BlsSignerTests.cs` -- BLS12-381 signing, verification, aggregation, deterministic signatures, public key derivation
- `Ed25519Tests.cs` -- Ed25519 key generation, signing, verification, batch verification, address derivation
- `Blake3Tests.cs` -- BLAKE3 hashing determinism, collision resistance, pair hashing, incremental API
- `KeccakTests.cs` -- Keccak-256 hashing, address derivation from Ed25519 public keys

## Running

```bash
dotnet test tests/Basalt.Crypto.Tests
```
