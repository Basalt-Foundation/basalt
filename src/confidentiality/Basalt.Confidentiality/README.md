# Basalt.Confidentiality

Privacy and confidentiality features for the Basalt blockchain. Provides zero-knowledge proofs, confidential transfers, private communication channels, and selective disclosure for enterprise use cases requiring data privacy.

## Status

Implemented — all core subsystems operational with comprehensive test coverage.

## Architecture

### Crypto (`Crypto/`)

- **PairingEngine** -- BLS12-381 pairing operations (scalar multiplication, point addition/negation, hash-to-curve, Miller loop, pairing checks)
  - Constants: `G1CompressedSize = 48`, `G2CompressedSize = 96`, `ScalarSize = 32`
  - Properties: `G1Generator` (48 bytes), `G2Generator` (96 bytes)
  - Methods: `ScalarMultG1`, `AddG1`, `NegG1`, `NegG2`, `HashToG1`, `ComputeMillerLoop`, `PairingCheck`, `IsG1Identity`
- **PedersenCommitment** -- Additively homomorphic commitments on G1: `C = value * G + blindingFactor * H`. Supports commit, open, add, and subtract operations for balance proofs
- **Groth16Verifier** -- ZK-SNARK verification via multi-pairing equation check. Used for range proofs on confidential transfer outputs
- **Groth16Codec** -- Binary serialization for Groth16 proofs and verification keys
  - `ProofSize = 192` (48 + 96 + 48 bytes: A in G1, B in G2, C in G1)
  - Methods: `EncodeProof`, `DecodeProof`, `EncodeVerificationKey`, `DecodeVerificationKey`

### Confidential Transfers (`Transactions/`)

- **ConfidentialTransfer** — Transfer structure with Pedersen-committed input/output amounts, balance proof blinding factor, optional encrypted amounts, and optional Groth16 range proof
- **TransferValidator** — Validates that input and output commitments balance (sum difference opens to zero) and optionally verifies range proofs to prevent wraparound

### Private Channels (`Channels/`)

- **X25519KeyExchange** -- Diffie-Hellman key establishment with HKDF-SHA256 key derivation (info: `basalt-channel-v1`)
- **ChannelEncryption** -- AES-256-GCM authenticated encryption with monotonic nonce construction from sequence numbers
  - Constants: `NonceSize = 12`, `TagSize = 16`, `KeySize = 32`
  - Methods: `Encrypt(key, nonce, plaintext)`, `Decrypt(key, nonce, ciphertextWithTag)`, `BuildNonce(sequenceNumber)`
- **ChannelMessage** -- Encrypted message container with channel binding, sequence number, and Ed25519 signature
- **PrivateChannel** -- Bilateral communication channel with lifecycle management and authenticated encryption
  - `ChannelStatus` enum values: `Open`, `Active`, `Closing`, `Closed`
  - `CreateMessage(byte[] sharedSecret, byte[] payload, byte[] senderPrivateKey)` -- encrypts and signs a message; channel must be `Active`; auto-increments the nonce
  - `VerifyAndDecrypt(ChannelMessage message, byte[] sharedSecret, PublicKey senderPublicKey)` -- verifies the Ed25519 signature and decrypts the payload
  - `DeriveChannelId(ReadOnlySpan<byte> pubKeyA, ReadOnlySpan<byte> pubKeyB)` -- static method; keys are sorted lexicographically before BLAKE3 hashing to ensure both parties derive the same channel ID

### Selective Disclosure (`Disclosure/`)

- **ViewingKey** -- Ephemeral X25519 ECDH + AES-256-GCM encryption enabling auditors to decrypt transaction amounts without on-chain visibility. Forward-secure via ephemeral keys
- **DisclosureProof** -- Simple Pedersen commitment opening proof for compliance scenarios where values can be directly revealed to an auditor
  - `DisclosureProof.Create(UInt256 value, byte[] blindingFactor)` -- static factory; validates blinding factor is 32 bytes
  - `DisclosureProof.Verify(ReadOnlySpan<byte> commitment, DisclosureProof proof)` -- static method; checks that `Commit(proof.Value, proof.BlindingFactor) == commitment`

### Module Entry Point

- **ConfidentialityModule** -- Health check and diagnostics entry point
  - `Name` -- constant `"Basalt.Confidentiality"`
  - `Version` -- constant `"1.0.0"`
  - `IsOperational()` -- returns `true` if BLS12-381 generators, Pedersen H generator, and X25519 key generation are all functional

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Hash256, Address, UInt256 |
| `Basalt.Codec` | Serialization |
| `Basalt.Crypto` | Ed25519, Blake3 |
| `Nethermind.Crypto` | BLS12-381 pairing operations (blst) |
| `NSec.Cryptography` | X25519 key exchange, HKDF-SHA256 |

## Cryptographic Primitives

| Primitive | Curve / Algorithm | Use |
|-----------|-------------------|-----|
| Pedersen commitments | BLS12-381 G1 | Confidential amounts |
| Groth16 ZK-SNARKs | BLS12-381 pairings | Range proofs |
| X25519 ECDH | Curve25519 | Channel key agreement |
| AES-256-GCM | — | Channel message encryption |
| Ed25519 | Ed25519 | Channel message signing |
| BLAKE3 | — | Channel ID derivation |
