# Confidentiality Layer Audit Report

## Executive Summary

The Basalt Confidentiality layer provides a well-structured privacy infrastructure with Groth16 ZK-SNARK verification, Pedersen commitments, Sparse Merkle Trees, encrypted channels, and selective disclosure. The cryptographic primitives are generally sound, leveraging established libraries (Nethermind.Crypto.Bls/blst, NSec, .NET System.Security.Cryptography). However, several issues were identified: missing explicit subgroup checks on deserialized BLS12-381 proof elements (critical for Groth16 soundness), an endianness mismatch in the Groth16 codec, thread-safety gaps in PrivateChannel, and an unenforced TimeBoundViewingKey. Test coverage is strong for the happy path but lacks adversarial/edge-case scenarios for the cryptographic core.

---

## Critical Issues

### C-01: No Explicit BLS12-381 Subgroup Checks on Groth16 Proof Elements

- **Location**: `Crypto/PairingEngine.cs:57-67` (ScalarMultG1), `Crypto/PairingEngine.cs:151-167` (ComputeMillerLoop), `Crypto/Groth16Verifier.cs:73-136` (Verify)
- **Description**: When proof elements (A, B, C) and verification key points are deserialized via `Bls.P1Affine.Decode()` / `Bls.P2Affine.Decode()`, the code relies on blst's internal checks. Depending on the blst version bundled by Nethermind.Crypto.Bls 1.0.5, `blst_p1_uncompress` may only verify the point is on the curve without checking subgroup membership in G1 (cofactor h₁ ≈ 2^128) or G2 (cofactor h₂ is even larger). Points not in the correct prime-order subgroup can break Groth16 soundness, allowing a malicious prover to forge proofs.
- **Impact**: **Soundness break** — an attacker could construct proofs using points outside the r-order subgroup that satisfy the pairing equation but correspond to no valid witness. This would allow forged ZK proofs, potentially enabling hidden inflation, fake compliance attestations, or unauthorized confidential transfers.
- **Recommendation**: Add explicit subgroup checks after every `Decode()` call for untrusted input. Either:
  1. Call blst's `InGroup()` on each deserialized `P1Affine`/`P2Affine` before using them in pairings, OR
  2. Multiply each G1 point by the G1 cofactor and check identity (cofactor clearing), and similarly for G2 points, OR
  3. Verify that Nethermind.Crypto.Bls 1.0.5 bundles a blst version (≥ 0.3.11) that includes automatic subgroup checks in `Decode`, and document this dependency explicitly with a regression test.
- **Severity**: **Critical**

### C-02: Groth16 Verifier Accepts Identity Elements in Proof

- **Location**: `Crypto/Groth16Verifier.cs:73-136`
- **Description**: The verifier does not reject proof elements that are the identity point (point at infinity). If `proof.A` is the G1 identity, then `e(A, B) = 1_GT` regardless of B, which simplifies the pairing equation and reduces the degrees of freedom an attacker must satisfy. Similarly, identity in B or C weakens the verification. While not trivially exploitable on its own, accepting identity elements is a well-known footgun in pairing-based protocols.
- **Impact**: Reduces the security margin of the Groth16 verifier. Combined with C-01 (no subgroup checks), this could enable proof forgery.
- **Recommendation**: Add explicit identity checks before the pairing computation:
  ```csharp
  if (PairingEngine.IsG1Identity(proof.A) || PairingEngine.IsG1Identity(proof.C))
      return false;
  // Add IsG2Identity check for proof.B (implement a corresponding helper)
  ```
- **Severity**: **Critical**

---

## High Severity

### H-01: Groth16Codec Endianness Mismatch (Encode vs Decode)

- **Location**: `Crypto/Groth16Codec.cs:97` (encode), `Crypto/Groth16Codec.cs:132` (decode)
- **Description**: The IC count is encoded using `BitConverter.TryWriteBytes(result.AsSpan(offset), vk.IC.Length)` which uses platform-native endianness, but decoded using `BinaryPrimitives.ReadInt32LittleEndian()` which is explicitly little-endian. The F-12 fix was applied to decoding but the encoding was not updated to match. On all currently supported .NET platforms (x64, ARM64) this is harmless because they are little-endian, but this is a correctness bug that would manifest on any future big-endian target.
- **Impact**: Verification keys serialized on a big-endian platform would be deserialized with a corrupted IC count on a little-endian platform (and vice versa), causing either crashes or silent data corruption.
- **Recommendation**: Replace line 97 with:
  ```csharp
  BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), vk.IC.Length);
  ```
- **Severity**: **High**

### H-02: TimeBoundViewingKey Has No Enforcement Mechanism

- **Location**: `Disclosure/ViewingKey.cs:146-164`
- **Description**: `TimeBoundViewingKey` stores `ValidFrom`/`ValidUntil` timestamps and provides an `IsValid(currentTimestamp)` check, but:
  1. Nothing in `ViewingKey.DecryptWithViewingKey` consults these time bounds
  2. The class holds only a public key, not a private key, so it cannot enforce anything
  3. There is no signing mechanism to prevent construction of `TimeBoundViewingKey` with arbitrary validity windows
  4. No code in the codebase actually calls `IsValid()`
- **Impact**: Time-bounded access controls are purely advisory and trivially bypassable. An auditor/viewer who obtains the private key can decrypt indefinitely, defeating the purpose of time-bounded viewing.
- **Recommendation**: Either:
  1. Create an `EncryptForTimeBoundViewer` method that encrypts with a time-locked key derivation (e.g., including the time window in the HKDF info), or
  2. Have the sender include the time bounds in authenticated associated data so the decryption function can enforce them, or
  3. Document that `TimeBoundViewingKey` is an application-level policy hint and not a cryptographic enforcement mechanism.
- **Severity**: **High**

### H-03: PrivateChannel Is Not Thread-Safe

- **Location**: `Channels/PrivateChannel.cs:51` (`Nonce`), `Channels/PrivateChannel.cs:60` (`_lastReceivedNonce`)
- **Description**: `PrivateChannel.Nonce` and `_lastReceivedNonce` are accessed and mutated without synchronization. If `CreateMessage` is called concurrently, two messages could be assigned the same nonce, resulting in AES-GCM nonce reuse (catastrophic for confidentiality). Similarly, concurrent `VerifyAndDecrypt` calls could bypass replay protection.
- **Impact**: **Nonce reuse** in AES-GCM leaks the XOR of two plaintexts and destroys authentication guarantees. Replay protection bypass allows message replay.
- **Recommendation**: Add `lock` synchronization (or use `Interlocked` for the nonce counter) to `CreateMessage` and `VerifyAndDecrypt`, similar to the locking pattern already used in `SparseMerkleTree`.
- **Severity**: **High**

### H-04: PrivateChannel Status Has Unrestricted Public Setter

- **Location**: `Channels/PrivateChannel.cs:54`
- **Description**: `Status` is a public get/set property with no state-machine validation. Any code can set `Status = ChannelStatus.Active` on a `Closed` channel, bypassing the intended lifecycle. The tests demonstrate this pattern directly (`channel.Status = ChannelStatus.Active`).
- **Impact**: Channel lifecycle controls can be circumvented, allowing messaging on channels that should be closed. In a protocol context, this could be exploited to continue communicating on a channel after one party has explicitly closed it.
- **Recommendation**: Replace the public setter with explicit transition methods (e.g., `Activate()`, `InitiateClose()`, `Close()`) that enforce valid transitions and throw on invalid ones.
- **Severity**: **High**

---

## Medium Severity

### M-01: PedersenCommitment.Open Uses Variable-Time Comparison

- **Location**: `Crypto/PedersenCommitment.cs:86`
- **Description**: `commitment.SequenceEqual(recomputed)` is a short-circuiting comparison that returns `false` as soon as it finds a differing byte. While the committed values are typically public curve points, in a confidential transfer context, timing differences could theoretically leak partial information about the commitment structure.
- **Impact**: Minor side-channel information leak. Practical exploitability is low since the committed point is typically public, but constant-time comparison is cryptographic best practice.
- **Recommendation**: Use `CryptographicOperations.FixedTimeEquals(commitment, recomputed)` for constant-time comparison.
- **Severity**: **Medium**

### M-02: Directional Key Material Not Zeroed After Use

- **Location**: `Channels/PrivateChannel.cs:150` (CreateMessage), `Channels/PrivateChannel.cs:211` (VerifyAndDecrypt)
- **Description**: The `directionalKey` derived in `DeriveDirectionalKey` is used for AES-GCM encryption/decryption but is never zeroed after use. In contrast, `X25519KeyExchange.DeriveSharedSecret` (F-14) and `ViewingKey.EncryptForViewer`/`DecryptWithViewingKey` correctly zero their key material.
- **Impact**: Sensitive key material persists in memory longer than necessary, increasing the window for memory disclosure attacks (cold boot, heap dumps, swap file).
- **Recommendation**: Wrap the directional key usage in a `try/finally` block with `CryptographicOperations.ZeroMemory(directionalKey)`.
- **Severity**: **Medium**

### M-03: TransferValidator.ValidateTransfer API Design Is Misleading

- **Location**: `Transactions/TransferValidator.cs:114-123`
- **Description**: `ValidateTransfer(transfer, rangeProofVk = null)` has a default `null` parameter for the verification key, but `ValidateRangeProof` returns `false` when the VK is null (F-02 enforcement). This means calling `ValidateTransfer(transfer)` with a balanced transfer will always return `false`, which is correct per F-02 but surprising. The API suggests range proofs are optional (parameter defaults to null) while the implementation makes them mandatory.
- **Impact**: Developer confusion leading to incorrect integration. Callers might incorrectly conclude their balanced transfer is invalid.
- **Recommendation**: Either remove the default value (`VerificationKey rangeProofVk` without `= null`) to force callers to be explicit, or add XML doc clarifying that `null` always results in `false`.
- **Severity**: **Medium**

### M-04: SparseMerkleTree Uses String Dictionary Keys — Performance Concern

- **Location**: `Crypto/SparseMerkleTree.cs:91,400-432`
- **Description**: Node identifiers are constructed via string concatenation (`"L" + level + ":" + hex`) for dictionary lookups. For a depth-256 tree, each insert/delete creates ~512 string allocations (256 for the path + 256 for siblings) involving hex encoding. The `Convert.ToHexString` calls plus string concatenation generate significant GC pressure.
- **Impact**: No correctness issue, but poor performance under high-throughput scenarios (e.g., bulk credential issuance/revocation). Could contribute to GC pauses in a consensus-critical path.
- **Recommendation**: Replace string keys with a struct key (e.g., `(int level, Hash256 prefix)`) or use a `byte[]`-keyed dictionary with a custom comparer to avoid string allocations.
- **Severity**: **Medium**

### M-05: PrivateChannel Legacy 3-Parameter Overloads Default to PartyA Direction

- **Location**: `Channels/PrivateChannel.cs:176-180` (CreateMessage), `Channels/PrivateChannel.cs:223-227` (VerifyAndDecrypt)
- **Description**: The 3-parameter `CreateMessage` and `VerifyAndDecrypt` overloads always use `PartyAPublicKey` as the sender direction. If PartyB calls the 3-parameter `CreateMessage`, the message will be encrypted with PartyA's directional key, which means the receiver must also use PartyA's direction to decrypt — breaking the bidirectional protocol.
- **Impact**: Incorrect encryption direction when PartyB uses the legacy overload, causing decryption failures or, worse, key/nonce reuse if both parties use the same directional key.
- **Recommendation**: Mark the 3-parameter overloads with `[Obsolete("Use the 4-parameter overload with explicit senderX25519PublicKey")]` to guide callers, or remove them entirely.
- **Severity**: **Medium**

---

## Low Severity / Recommendations

### L-01: Groth16Verifier Computes GT Identity on Every Call

- **Location**: `Crypto/Groth16Verifier.cs:126-131`
- **Description**: The GT identity element (`e(0, G2)` after `FinalExp`) is recomputed for every `Verify` call. This involves a MillerLoop + FinalExp on the identity point, which is unnecessary overhead.
- **Recommendation**: Cache the GT identity as a static field, computed once in a static constructor.
- **Severity**: **Low**

### L-02: No IsG2Identity Helper

- **Location**: `Crypto/PairingEngine.cs`
- **Description**: `IsG1Identity` exists but there is no corresponding `IsG2Identity` method. The G2 compressed identity is `0xC0` followed by 95 zero bytes. This is needed for the C-02 fix (rejecting identity proof elements).
- **Recommendation**: Add `IsG2Identity(ReadOnlySpan<byte>)` analogous to `IsG1Identity`.
- **Severity**: **Low**

### L-03: PedersenCommitment.CommitRandom Doesn't Zero Blinding on Failure

- **Location**: `Crypto/PedersenCommitment.cs:104-110`
- **Description**: If `Commit(value, blindingFactor)` throws (e.g., due to a blst error), the generated `blindingFactor` is not zeroed. The random bytes persist in memory.
- **Recommendation**: Wrap in `try/catch` with `CryptographicOperations.ZeroMemory(blindingFactor)` in the catch block, then rethrow.
- **Severity**: **Low**

### L-04: Groth16Codec IC Count Upper Bound Is Arbitrary

- **Location**: `Crypto/Groth16Codec.cs:136`
- **Description**: The IC count is capped at 1024, which is a reasonable defense against large allocations. However, the bound is undocumented and has no relationship to any system parameter. For Basalt's use cases (compliance proofs, range proofs), the expected IC count is likely < 10.
- **Recommendation**: Document the rationale for the 1024 bound and consider lowering it to match actual circuit sizes (e.g., 64 or 128).
- **Severity**: **Low**

### L-05: ViewingKey Plaintext Not Zeroed After Decryption

- **Location**: `Disclosure/ViewingKey.cs:125-128`
- **Description**: In `DecryptWithViewingKey`, the decrypted plaintext (containing the value and blinding factor) is not zeroed after parsing. The blinding factor is especially sensitive.
- **Recommendation**: Zero the `plaintext` array in a `finally` block after extracting the value and blinding factor.
- **Severity**: **Low**

### L-06: ChannelEncryption BuildNonce Wastes 4 Bytes of Nonce Space

- **Location**: `Channels/ChannelEncryption.cs:80-85`
- **Description**: `BuildNonce` uses a 12-byte nonce where the first 4 bytes are always zero, with only the last 8 bytes carrying the sequence number. While 2^64 messages before nonce exhaustion is practically infinite, the leading zeros are wasted. This is fine for single-use channel keys but reduces nonce entropy if the same key were reused across contexts.
- **Impact**: No practical issue given that channel keys are unique per session. The 4 zero bytes could be used for a channel-specific prefix in future multi-channel designs.
- **Recommendation**: Consider using the first 4 bytes as a channel identifier or salt in a future version. No action needed currently.
- **Severity**: **Low**

### L-07: AOT Safety — All Code Passes

- **Location**: All files in scope
- **Description**: No reflection, dynamic code generation, or AOT-unsafe patterns were found. All cryptographic operations use direct method calls. The Nethermind.Crypto.Bls native interop uses P/Invoke which is AOT-compatible. NSec also uses P/Invoke.
- **Severity**: Informational (positive finding)

---

## Test Coverage Gaps

### T-01: No Tests with Adversarial/Malformed BLS12-381 Points

- **Location**: `tests/Basalt.Confidentiality.Tests/Groth16Tests.cs`, `PairingEngineTests.cs`
- **Description**: All tests use well-formed points (generators, scalar multiples of generators). There are no tests with:
  - Points not on the BLS12-381 curve (random 48/96-byte arrays)
  - Points on the curve but not in the prime-order subgroup
  - The G1/G2 identity point as proof elements (A, B, or C)
  - Proof elements from a different curve
- **Impact**: The C-01 and C-02 findings are not covered by tests, meaning regressions would go undetected.

### T-02: No Tests for TimeBoundViewingKey

- **Location**: `tests/Basalt.Confidentiality.Tests/SelectiveDisclosureTests.cs`
- **Description**: `TimeBoundViewingKey` has zero test coverage. No tests verify:
  - `IsValid()` returns true within the window
  - `IsValid()` returns false outside the window
  - Edge cases: `ValidFrom == ValidUntil`, `ValidFrom > ValidUntil`, `currentTimestamp == ValidFrom/ValidUntil`

### T-03: No Concurrent Access Tests for PrivateChannel

- **Location**: `tests/Basalt.Confidentiality.Tests/PrivateChannelTests.cs`
- **Description**: All tests are single-threaded. No tests verify thread safety of `CreateMessage` or `VerifyAndDecrypt` under concurrent access, which would expose H-03.

### T-04: No SparseMerkleTree Stress/Adversarial Tests

- **Location**: `tests/Basalt.Confidentiality.Tests/SparseMerkleTreeTests.cs`
- **Description**: Missing:
  - Large tree tests (1000+ insertions) to verify performance doesn't degrade
  - Keys with common prefixes (adversarial key selection)
  - Full insert-delete-reinsert cycles to verify sparse storage cleanup
  - Proof verification with tampered sibling hashes

### T-05: No Groth16 End-to-End Test with Real Circuit

- **Location**: `tests/Basalt.Confidentiality.Tests/Groth16Tests.cs`
- **Description**: All Groth16 tests use a trivially constructed proof (A=G1, B=G2, C=-G1 with alpha=G1, beta=G2, gamma=G2, delta=G2). There are no tests with:
  - A proof generated by an actual Groth16 prover from a real circuit
  - Multiple public inputs (the current test uses zero public inputs)
  - Known test vectors from reference implementations (e.g., snarkjs, bellman)

### T-06: No Tests for DisclosureProof.CommitmentRef Binding (F-15)

- **Location**: `tests/Basalt.Confidentiality.Tests/SelectiveDisclosureTests.cs`
- **Description**: While `DisclosureProof.Create` accepts a `commitmentRef` parameter and `Verify` checks it, no test exercises the scenario where `CommitmentRef` is provided and must match the commitment being verified.

### T-07: No Tests for RatchetKey Integration with Directional Keys

- **Location**: `tests/Basalt.Confidentiality.Tests/PrivateChannelTests.cs`
- **Description**: `F08_RatchetKey_ChainedRatcheting_Works` tests ratcheting with the legacy 3-parameter overload (PartyA direction only). No test verifies that ratcheting works correctly in a true bidirectional scenario with F-01 directional keys.

---

## Positive Findings

1. **Pedersen Commitment Scheme Is Correctly Implemented**: The H generator is derived via hash-to-curve with a domain separation tag (`BASALT_PEDERSEN_DST`), ensuring it is provably independent of G. The commitment `C = v*G + r*H` correctly implements the binding and hiding properties. Homomorphic addition and subtraction are correct.

2. **Sparse Merkle Tree Has Proper Domain Separation (F-09)**: Leaf hashes use `BLAKE3(0x00 || key)` and internal nodes use `BLAKE3(0x01 || left || right)`, preventing second-preimage attacks where a leaf could be confused with an internal node.

3. **AES-256-GCM Is Used Correctly for Channel Encryption**: The AEAD construction uses .NET's `AesGcm` class with proper 12-byte nonces and 16-byte tags. The format is clean (ciphertext || tag).

4. **X25519 Key Exchange Has Strong Identity Binding (F-06)**: The HKDF derivation binds both parties' public keys into the info parameter, preventing unknown-key-share attacks. The keys are sorted before binding to ensure both parties derive the same secret.

5. **MITM Protection via Signed Key Exchange (F-07)**: The `SignKeyExchange`/`VerifyKeyExchange` methods bind X25519 ephemeral keys to Ed25519 identities with a domain-separated message prefix (`basalt-key-exchange-v1`), preventing man-in-the-middle key substitution.

6. **Directional Encryption Keys (F-01)**: Each direction (A→B, B→A) derives a unique encryption key via HKDF with sender/receiver public keys in the info parameter. This prevents nonce reuse when both parties start their counters at 0.

7. **Replay Protection (F-05)**: `VerifyAndDecrypt` enforces strictly increasing nonces, preventing message replay. The sentinel value `ulong.MaxValue` for the initial state is handled correctly.

8. **Key Ratcheting (F-08)**: The `RatchetKey` method uses HKDF with the nonce as salt, providing forward secrecy. Compromise of the current key does not reveal past message keys.

9. **Intermediate Secret Zeroing (F-14)**: `X25519KeyExchange.DeriveSharedSecret`, `ViewingKey.EncryptForViewer`, and `ViewingKey.DecryptWithViewingKey` all zero intermediate secret material in `finally` blocks.

10. **Comprehensive Test Coverage for Normal Operations**: The test suite covers round-trips, argument validation, algebraic properties (commutativity, associativity, distributivity), bidirectional communication, and various edge cases (empty payloads, zero values, max nonces).

11. **SparseMerkleTree Thread Safety (F-17)**: All public methods (`Insert`, `Delete`, `Contains`, `GenerateProof`) use `lock` synchronization, preventing race conditions in concurrent access scenarios.

12. **Payload Size Limits (F-18)**: `PrivateChannel.CreateMessage` enforces a 1 MB maximum payload size, preventing memory exhaustion from oversized messages.
