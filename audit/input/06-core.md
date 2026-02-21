# Basalt Security & Quality Audit — Core Layer

## Scope

Audit the foundational type system, cryptographic primitives, and binary codec that underpin the entire Basalt blockchain:

| Project | Path | Description |
|---|---|---|
| `Basalt.Core` | `src/core/Basalt.Core/` | Fundamental types: `Address`, `Hash256`, `UInt256`, `Signature`, `PublicKey`, `ChainParameters`, error handling, compliance interfaces, `IStakingState` |
| `Basalt.Crypto` | `src/core/Basalt.Crypto/` | Cryptographic operations: BLAKE3, Ed25519, Keccak-256, BLS12-381, X25519, Argon2 keystore |
| `Basalt.Codec` | `src/core/Basalt.Codec/` | Binary serialization: `BasaltWriter`/`BasaltReader` (ref structs), `IBasaltSerializable` |

Corresponding test projects: `tests/Basalt.Core.Tests/`, `tests/Basalt.Crypto.Tests/`, `tests/Basalt.Codec.Tests/`

---

## Files to Audit

### Basalt.Core
- `Address.cs` (~139 lines) — 20-byte address, IEquatable, IComparable
- `Hash256.cs` (~131 lines) — 32-byte hash, IEquatable, IComparable
- `UInt256.cs` (~411 lines) — 256-bit unsigned integer arithmetic
- `Signature.cs` (~256 lines) — `Signature` (64B Ed25519), `PublicKey` (32B), `BlsSignature` (96B), `BlsPublicKey` (48B)
- `ChainParameters.cs` (~139 lines) — Network configuration constants
- `BasaltError.cs` (~148 lines) — `BasaltErrorCode`, `BasaltResult`, `BasaltResult<T>`, `BasaltException`
- `IStakingState.cs` (~42 lines) — Cross-layer staking interface
- `Compliance/ComplianceProof.cs` (~28 lines) — Readonly struct for ZK compliance proofs
- `Compliance/IComplianceVerifier.cs` (~56 lines) — Compliance verification interface
- `Compliance/ProofRequirement.cs` (~18 lines) — Proof requirement specification

### Basalt.Crypto
- `Blake3Hasher.cs` (~79 lines) — BLAKE3 hashing (primary hash function)
- `Ed25519Signer.cs` (~91 lines) — Ed25519 signing/verification
- `KeccakHasher.cs` (~149 lines) — Custom software Keccak-256 (address derivation)
- `BlsSigner.cs` (~196 lines) — BLS12-381 via Nethermind.Crypto.Bls
- `IBlsSigner.cs` (~96 lines) — BLS interface + `StubBlsSigner`
- `Keystore.cs` (~176 lines) — Argon2-encrypted keystore (JSON format)
- `X25519.cs` (~95 lines) — ECDH key exchange

### Basalt.Codec
- `BasaltWriter.cs` (~181 lines) — Zero-copy binary serializer (ref struct)
- `BasaltReader.cs` (~213 lines) — Zero-copy binary deserializer (ref struct)
- `IBasaltSerializable.cs` (~56 lines) — Serialization interface + source-gen attribute

---

## Audit Objectives

### 1. UInt256 Arithmetic Correctness (CRITICAL)
- Verify all arithmetic operations (add, subtract, multiply, divide, modulo) are correct for the full 256-bit range.
- Check for overflow/underflow behavior: operations must not silently wrap around — verify whether checked or unchecked semantics are used and whether this is intentional.
- Verify comparison operators (`<`, `>`, `<=`, `>=`, `==`, `!=`) are correct across all quadrants (both high and low 128-bit halves).
- Check `IsZero`, `IsOne`, and special value constants (`Zero`, `One`, `MaxValue`).
- Verify serialization: little-endian 32-byte encoding must round-trip correctly.
- Check `ToString()`, `Parse()`, and `TryParse()` for edge cases.
- Verify that `UInt256` is used consistently for all monetary amounts throughout the codebase (no residual `ulong` amount fields).

### 2. Address & Hash256 Integrity
- Verify `Address` (20B) and `Hash256` (32B) correctly implement `IEquatable<T>` and `IComparable<T>`.
- Check `GetHashCode()` implementations for collision resistance (important for dictionary keys).
- Verify hex encoding/decoding handles "0x" prefix consistently.
- Check for potential buffer overflows or truncation when converting between byte arrays and fixed-size structs.
- Verify `Address.FromPublicKey()` uses Keccak-256 correctly (rightmost 20 bytes of hash).

### 3. Signature & Key Type Safety
- Verify `Signature` (64B) correctly stores Ed25519 signatures.
- Verify `PublicKey` (32B) correctly stores Ed25519 public keys.
- Verify `BlsSignature` (96B) and `BlsPublicKey` (48B) correctly store BLS12-381 values.
- Check that type conversions (`ToArray()`, `FromBytes()`) handle size mismatches safely.
- Verify that signature/key types cannot be confused (e.g., an Ed25519 signature interpreted as BLS).

### 4. Cryptographic Operations (CRITICAL)
- **BLAKE3**: Verify correct usage of the BLAKE3 NuGet binding. Check that `IncrementalHasher` is properly disposed.
- **Ed25519**: Verify `Sign()` and `Verify()` are correct. Check `GenerateKeyPair()` randomness source. Verify signatures are verified in canonical form.
- **Keccak-256**: Audit the custom software implementation for correctness against NIST test vectors. This is critical since macOS lacks system SHA3_256 support.
- **BLS12-381**: Verify private key masking (`privateKey[0] &= 0x3F` for scalar < field modulus). Verify `GetPublicKey`/`Sign`/`Verify`/`AggregateVerify`. Check that `Pairing.Aggregate + FinalVerify` is NOT used (known broken — manual pairing via `MillerLoop` is required).
- **X25519**: Verify ECDH key exchange handles edge cases (low-order points, identity).
- **Keystore**: Verify Argon2 parameters are sufficient. Check key derivation, encryption/decryption, and that private keys are zeroed after use.

### 5. Binary Codec Safety
- Verify `BasaltWriter` and `BasaltReader` (ref structs) correctly handle boundary conditions:
  - Writing/reading at buffer capacity
  - Reading past end of buffer
  - Zero-length writes/reads
  - Maximum-size values
- Verify endianness is consistent (little-endian for all multi-byte values).
- Check that `ref struct` usage prevents accidental heap allocation and lambda capture (CA2014).
- Verify `IBasaltSerializable` round-trip: serialize → deserialize must produce identical objects.

### 6. ChainParameters Validation
- Verify all parameter values are reasonable (block time, epoch length, gas limits, minimum stakes).
- Check that parameter values cannot create degenerate network behavior (e.g., epoch length of 0, block time of 0).
- Verify `InitialBaseFee = 1` is appropriate for devnet backward compatibility.

### 7. Error Handling
- Verify `BasaltResult<T>` correctly propagates errors without exceptions in hot paths.
- Check that `BasaltException` is only thrown in exceptional conditions, not for control flow.
- Verify error codes are unique and meaningful.

### 8. AOT Safety
- Verify all code is AOT-compatible: no reflection, no `dynamic`, no `Assembly.Load`.
- Check that `readonly struct` types are correctly used (no hidden copies).

### 9. Test Coverage
- Review test projects for:
  - UInt256: boundary arithmetic, overflow, serialization round-trips
  - Address/Hash256: equality, comparison, hex encoding, dictionary usage
  - BLAKE3/Ed25519/Keccak: known test vectors from standards
  - BLS: sign/verify, aggregate, key derivation
  - Codec: round-trip tests for all serializable types

---

## Key Context

- `Basalt.Core` has ZERO external dependencies — it is the foundation of the entire stack.
- `Basalt.Crypto` depends on: `Blake3 1.1.0`, `NSec.Cryptography 24.4.0`, `Konscious.Security.Cryptography.Argon2 1.3.1`, `Nethermind.Crypto.Bls 1.0.5`.
- The custom Keccak-256 implementation exists because macOS lacks native SHA3_256 support — correctness is paramount.
- `PublicKey` has `ToArray()` but NO `.Bytes` property.
- `Blake3Hasher.Hash()` returns `Hash256`, NOT `byte[]` — call `.ToArray()` for byte arrays.
- `ref struct` types (`BasaltWriter`/`BasaltReader`) cannot be captured in lambdas or used in async methods.
- `UInt256` handles all monetary amounts in the system — arithmetic bugs here would affect every financial operation.

---

## Output Format

Write your findings to `audit/output/06-core.md` with the following structure:

```markdown
# Core Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Arithmetic bugs, cryptographic flaws, codec corruption]

## High Severity
[Significant correctness or security issues]

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
