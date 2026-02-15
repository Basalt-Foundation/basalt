# Basalt.Confidentiality.Tests

Unit tests for Basalt confidentiality features: Pedersen commitments, Groth16 zero-knowledge proofs, confidential transfers, private channels, selective disclosure, channel encryption, and BLS12-381 pairing engine. **185 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| PairingEngine | 34 | G1/G2 generator properties, scalar multiplication, point addition (commutativity, associativity), negation, identity element, hash-to-curve, pairing checks (bilinearity), Miller loop, argument validation |
| Groth16 | 28 | Proof codec roundtrips, verification key codec, valid proof verification, tampered proof/VK detection, null input handling, wrong-size field detection, identity point handling (split across Groth16CodecTests and Groth16VerifierTests) |
| SelectiveDisclosure | 27 | Viewing key generation, encrypt/decrypt roundtrip, wrong key detection, disclosure proof create/verify, auditor verification workflow, argument validation, edge cases (zero value, large value, non-deterministic encryption, multiple viewers) |
| ConfidentialTransfer | 25 | Balanced/unbalanced transfer validation, multi-input/multi-output, wrong blinding factor, tampered commitments, null/empty inputs, range proof handling, zero-amount transfers |
| ChannelEncryption | 25 | AES-GCM encrypt/decrypt, argument validation (key/nonce/ciphertext size), wrong key/nonce, tampered ciphertext/tag, empty plaintext, nonce construction, X25519 key exchange (generation, derivation, commutativity, determinism) |
| PedersenCommitment | 23 | Commit/open, different blinding factors, homomorphic addition/subtraction, H generator properties, zero value, argument validation, determinism, multi-commitment addition |
| PrivateChannel | 23 | X25519 key exchange, channel ID derivation (deterministic, order-independent), message create/verify/decrypt, bidirectional communication, nonce management, status transitions, wrong signature/channel/secret detection, empty/large payloads |

**Total: 185 tests**

## Test Files

- `PairingEngineTests.cs` -- BLS12-381 pairing engine: G1/G2 generators, scalar multiplication, point addition, negation, hash-to-curve, pairing checks, argument validation
- `Groth16Tests.cs` -- Groth16 zero-knowledge proof system: proof/VK codec, proof verification, tamper detection, field size validation (contains Groth16CodecTests and Groth16VerifierTests classes)
- `SelectiveDisclosureTests.cs` -- Selective disclosure: viewing key encryption, disclosure proofs, auditor verification, argument validation
- `ConfidentialTransferTests.cs` -- Confidential transfer validation: balance proofs via Pedersen commitments, multi-input/output, range proofs
- `ChannelEncryptionTests.cs` -- AES-GCM channel encryption and X25519 key exchange: encrypt/decrypt, nonce management, argument validation (contains ChannelEncryptionTests and X25519KeyExchangeTests classes)
- `PedersenCommitmentTests.cs` -- Pedersen commitments: commit/open, homomorphic properties, H generator, argument validation
- `PrivateChannelTests.cs` -- Private channels: key exchange, channel ID derivation, encrypted messaging, signature verification, status management

## Running

```bash
dotnet test tests/Basalt.Confidentiality.Tests
```
