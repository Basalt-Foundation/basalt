# Basalt Security & Quality Audit — Confidentiality Layer

## Scope

Audit the cryptographic privacy infrastructure providing zero-knowledge proofs, confidential transfers, encrypted channels, and selective disclosure:

| Project | Path | Description |
|---|---|---|
| `Basalt.Confidentiality` | `src/confidentiality/Basalt.Confidentiality/` | ZK proofs (Groth16), encrypted channels (X25519), Pedersen commitments, Sparse Merkle Trees, confidential transfers |

Corresponding test project: `tests/Basalt.Confidentiality.Tests/`

---

## Files to Audit

### Cryptographic Primitives
- `Crypto/Groth16Verifier.cs` (~162 lines) — `VerificationKey`, `Groth16Proof`, Groth16 zero-knowledge proof verification
- `Crypto/Groth16Codec.cs` (~159 lines) — Encoding/decoding of Groth16 proofs and verification keys
- `Crypto/PairingEngine.cs` (~221 lines) — BLS12-381 pairing operations for Groth16
- `Crypto/PedersenCommitment.cs` (~164 lines) — Pedersen commitment scheme for value hiding
- `Crypto/SparseMerkleTree.cs` (~517 lines) — Sparse Merkle Tree for set membership proofs

### Encrypted Channels
- `Channels/ChannelEncryption.cs` (~86 lines) — Symmetric encryption for private channels
- `Channels/ChannelMessage.cs` (~29 lines) — Encrypted message structure
- `Channels/PrivateChannel.cs` (~261 lines) — Private communication channel with lifecycle management
- `Channels/X25519KeyExchange.cs` (~184 lines) — ECDH key exchange for channel establishment

### Selective Disclosure
- `Disclosure/DisclosureProof.cs` (~67 lines) — Selective disclosure proof structure
- `Disclosure/ViewingKey.cs` (~164 lines) — Viewing keys and time-bounded access

### Confidential Transfers
- `Transactions/ConfidentialTransfer.cs` (~49 lines) — Confidential transfer structure
- `Transactions/TransferValidator.cs` (~124 lines) — Validates confidential transfer proofs

### Module Entry
- `ConfidentialityModule.cs` (~61 lines) — Module registration and initialization

---

## Audit Objectives

### 1. Groth16 Proof Verification (CRITICAL)
- Verify the pairing equation `e(A, B) = e(alpha, beta) * e(sum(vk_i * input_i), gamma) * e(C, delta)` is correctly implemented.
- Check that `PairingEngine` correctly implements BLS12-381 pairings using `MillerLoop` + `FinalExp`.
- Verify that proof deserialization (`Groth16Codec`) correctly handles compressed G1/G2 points.
- Check for subgroup checks: proof elements must lie on the correct subgroup of BLS12-381.
- Verify that malformed proofs (wrong curve, identity elements, points at infinity) are rejected.
- Check that the verification key cannot be substituted or tampered with at runtime.

### 2. Sparse Merkle Tree Correctness (CRITICAL)
- Verify inclusion and exclusion proof generation and verification are correct.
- Check for second-preimage resistance: the hash function must domain-separate leaf nodes from internal nodes.
- Verify empty tree, single-element tree, and full tree edge cases.
- Check proof size bounds — a 256-bit sparse Merkle tree proof should be at most 256 hashes.
- Verify that tree updates are atomic and consistent.
- Check for potential DoS via adversarial key selection causing deep tree traversals.

### 3. Pedersen Commitments
- Verify the commitment scheme is binding and hiding: `C = v*G + r*H` where G and H are independent generators.
- Check that the blinding factor `r` is generated with sufficient randomness.
- Verify that `H` is provably independent of `G` (nothing-up-my-sleeve construction).
- Check homomorphic properties are correctly leveraged for range proofs or balance verification.
- Verify that commitment opening cannot be forged.

### 4. Channel Encryption Security
- Verify `ChannelEncryption` uses authenticated encryption (AEAD) — check algorithm choice and nonce handling.
- Check for nonce reuse vulnerabilities in long-lived channels.
- Verify `X25519KeyExchange` correctly derives shared secrets and handles edge cases (low-order points, identity).
- Check key material lifecycle: keys should be zeroed after use.
- Verify channel establishment is resistant to MITM attacks.

### 5. Viewing Key Security
- Verify `ViewingKey` grants read-only access and cannot be escalated to write/spend capability.
- Check `TimeBoundViewingKey` expiry enforcement — verify time-boundary checks are correct and use consistent time sources.
- Verify viewing keys cannot be used to derive spending keys.

### 6. Confidential Transfer Validation
- Verify `TransferValidator` correctly validates that inputs equal outputs (conservation of value).
- Check that range proofs prevent negative values or overflow.
- Verify that confidential transfers interact correctly with the base fee mechanism.

### 7. Side-Channel Resistance
- Check for timing-dependent comparisons in proof verification (use constant-time comparison for cryptographic values).
- Verify that error messages from failed verification do not reveal information about why the proof failed.
- Check for memory leaks of sensitive cryptographic material.

### 8. AOT Compatibility
- Verify all cryptographic code is AOT-safe — no reflection, no dynamic code generation.
- Check that `Nethermind.Crypto.Bls` native interop works correctly under AOT.

### 9. Test Coverage
- Review `tests/Basalt.Confidentiality.Tests/` for:
  - Valid and invalid Groth16 proofs
  - SparseMerkleTree: insert, delete, proof generation, proof verification, empty tree
  - Pedersen commitment: create, open, verify, homomorphic addition
  - Channel: establish, encrypt/decrypt, key rotation, expiry
  - Edge cases: zero values, maximum values, malformed inputs

---

## Key Context

- BLS12-381 via `Nethermind.Crypto.Bls` (1.0.5) wrapping the blst native library.
- Known issue: `Pairing.Aggregate + FinalVerify` does NOT work — manual pairing via `MillerLoop` + `FinalExp` + `IsEqual` is used instead.
- Key sizes: BLS private 32B, public 48B (compressed G1), signature 96B (compressed G2).
- DST for BLS: `BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_`
- SparseMerkleTree is also used by `ZkComplianceVerifier` in the Compliance layer.
- This layer has no external NuGet dependencies beyond those pulled transitively through `Basalt.Crypto`.

---

## Output Format

Write your findings to `audit/output/04-confidentiality.md` with the following structure:

```markdown
# Confidentiality Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Cryptographic vulnerabilities, proof system flaws]

## High Severity
[Significant security issues]

## Medium Severity
[Issues to address]

## Low Severity / Recommendations
[Code quality, cryptographic best practices]

## Test Coverage Gaps
[Untested scenarios]

## Positive Findings
[Well-implemented patterns]
```

For each finding, include:
1. **Location**: File path and line number(s)
2. **Description**: What the issue is
3. **Impact**: What could go wrong (soundness break, privacy leak, fund loss)
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
