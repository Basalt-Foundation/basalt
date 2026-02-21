# Core Layer Audit Report

## Executive Summary

The Core layer (`Basalt.Core`, `Basalt.Crypto`, `Basalt.Codec`) forms the cryptographic and type-system foundation of the entire Basalt blockchain. This audit identified **3 critical issues**, **9 high-severity issues**, **14 medium-severity issues**, and **14 low-severity issues** across the three projects. The most urgent finding is an overflow detection bug in `UInt256.CheckedAdd`/`TryAdd` — the "safe" arithmetic path explicitly designed for financial calculations — that silently returns incorrect results for a provable class of inputs. The custom Keccak-256 implementation was verified correct against known test vectors. The Codec layer has remotely-exploitable input validation gaps in `BasaltReader`. Key material lifetime management in `Basalt.Crypto` is the primary systemic weakness.

---

## Critical Issues

### C-01: UInt256.CheckedAdd/TryAdd Fails to Detect Overflow When carry=1 and b.Hi=MaxValue

**Location:** `src/core/Basalt.Core/UInt256.cs:235-243` (CheckedAdd), `262-274` (TryAdd)

**Description:** The overflow detection condition `hi < a.Hi || (carry == 0 && hi < b.Hi)` is incomplete. It misses the case where `a.Hi` is small (e.g., 0), `b.Hi` is `UInt128.MaxValue`, and `carry = 1`:

```
a = new UInt256(1, 0)               // Lo=1, Hi=0
b = new UInt256(UInt128.MaxValue, UInt128.MaxValue)  // MaxValue
lo = 1 + MaxValue = 0               // wraps, carry = 1
hi = 0 + MaxValue + 1 = 0           // wraps to 0
Check: hi < a.Hi  →  0 < 0  →  false
Check: carry == 0 && hi < b.Hi  →  false (carry is 1)
Result: UInt256.Zero returned as valid sum — overflow undetected
```

**Impact:** Silent corruption of monetary calculations in the "safe" arithmetic path. Any balance/gas calculation using `CheckedAdd` or `TryAdd` where the sum overflows through this code path will silently wrap to a small value. Since these are the methods explicitly intended to prevent overflow in financial calculations, callers trust them to throw or return false.

**Recommendation:** Replace with two-stage overflow detection:

```csharp
public static UInt256 CheckedAdd(UInt256 a, UInt256 b)
{
    var lo = a.Lo + b.Lo;
    var carry = lo < a.Lo ? (UInt128)1 : (UInt128)0;
    var hiSum = a.Hi + b.Hi;
    bool hiOverflow = hiSum < a.Hi;
    var hi = hiSum + carry;
    if (hiOverflow || hi < hiSum)
        throw new OverflowException("UInt256 addition overflow.");
    return new UInt256(lo, hi);
}
```

Apply the same fix to `TryAdd`. Add test: `CheckedAdd(new UInt256(1, 0), UInt256.MaxValue)` must throw.

**Severity:** Critical

---

### C-02: UInt256 Unchecked operator+ Silently Wraps All Monetary Amounts

**Location:** `src/core/Basalt.Core/UInt256.cs:91-97`

**Description:** The `operator +` performs wrapping addition without any overflow detection, identical to native integer types. However, `UInt256` is a custom type handling all monetary amounts in the blockchain. The code computes `hi = a.Hi + b.Hi + carry` on line 95, which silently wraps. A grep shows `operator +` is used in places like `BaseFeeCalculator` where overflow is theoretically possible with extreme parameter values.

**Impact:** If any code path uses `a + b` instead of `CheckedAdd(a, b)` for balance calculations, overflow will silently wrap the result. Since `UInt256` replaced `ulong` for all amounts, every call site of `operator +` is a potential wrapping point.

**Recommendation:** Audit all call sites of `operator +` and `operator -` on `UInt256` to verify they are either (a) in contexts where overflow is mathematically impossible, or (b) replaced with `CheckedAdd`/`CheckedSub`. Consider making the default operators checked and providing explicit `UncheckedAdd`/`UncheckedSub` for proven-safe hot paths.

**Severity:** Critical

---

### C-03: MemoryMarshal.Cast Alignment Requirement Violated in Signature/Key Types

**Location:** `src/core/Basalt.Core/Signature.cs:23-25` (Signature ctor), `:36-38` (WriteTo), `:86-87,97-98` (PublicKey), `:145-148,160-163` (BlsSignature), `:212-214,225-226` (BlsPublicKey)

**Description:** All four key/signature types use `MemoryMarshal.Cast<byte, ulong>(bytes)` in constructors and `WriteTo` methods. This requires the source span to be 8-byte aligned. When deserialized from `BasaltReader`, the span is a slice of the internal buffer at an arbitrary read position, which may not be aligned. In contrast, `Address` and `Hash256` correctly use `Unsafe.ReadUnaligned<ulong>`, which is safe for any alignment.

**Impact:** On ARM64 (macOS Apple Silicon, Docker ARM64 validators), unaligned memory access through `MemoryMarshal.Cast` can cause `DataMisalignedException` or produce corrupted reads. This affects every P2P message, block, and transaction that contains a signature or public key.

**Recommendation:** Replace all `MemoryMarshal.Cast` usage with the `Unsafe.ReadUnaligned`/`Unsafe.WriteUnaligned` pattern already used in `Address.cs` and `Hash256.cs`:

```csharp
ref var src = ref MemoryMarshal.GetReference(bytes);
_v0 = Unsafe.ReadUnaligned<ulong>(ref src);
_v1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 8));
// ... etc.
```

**Severity:** Critical

---

## High Severity

### H-01: BasaltReader.ReadBytes() Negative Length from VarInt-to-int Cast

**Location:** `src/core/Basalt.Codec/BasaltReader.cs:109`

**Description:** `ReadBytes()` casts the VarInt length to `int` via `var length = (int)ReadVarInt()`. A malicious payload encoding a VarInt length > `int.MaxValue` produces a negative `length` after truncation. A negative `length` passes `EnsureAvailable(length)` (since `_position + negative` underflows) and `_buffer.Slice(_position, length)` with a negative length throws an uncontrolled `ArgumentOutOfRangeException`.

**Impact:** Denial-of-service via crafted P2P messages. Any network peer can send a message with an oversized VarInt length to crash the receiving node.

**Recommendation:** Add validation: `var rawLength = ReadVarInt(); if (rawLength > int.MaxValue) throw new FormatException("Byte array length exceeds maximum."); var length = (int)rawLength;`

**Severity:** High

---

### H-02: BasaltReader.ReadString() Same Negative Length Issue

**Location:** `src/core/Basalt.Codec/BasaltReader.cs:133`

**Description:** Same as H-01. The `(int)ReadVarInt()` cast can produce a negative value that bypasses the `MaxStringLength` check (negative < positive).

**Impact:** Same DoS vector as H-01.

**Recommendation:** Same fix — validate `rawLength <= int.MaxValue` before casting.

**Severity:** High

---

### H-03: BasaltReader.ReadBytes() Has No Maximum Length Limit

**Location:** `src/core/Basalt.Codec/BasaltReader.cs:107-114`

**Description:** Unlike `ReadString()` which has `MaxStringLength = 4096`, `ReadBytes()` has no upper bound on the decoded length. While `EnsureAvailable` prevents out-of-bounds reads from the current buffer, there is no defense-in-depth cap. A crafted message with a valid VarInt encoding a large length could cause unexpected behavior in callers that allocate based on the decoded length.

**Recommendation:** Add a `MaxBytesLength` constant (e.g., 16 MB or an appropriate protocol maximum) and validate against it.

**Severity:** High

---

### H-04: Ed25519 GenerateKeyPair Returns Unprotected Private Key byte[]

**Location:** `src/core/Basalt.Crypto/Ed25519Signer.cs:18-24`

**Description:** `GenerateKeyPair()` returns the private key as a raw `byte[]`. The NSec `Key` object is properly disposed, but the exported `privateKeyBytes` array persists in managed memory indefinitely. There is no documentation requiring callers to zero it, and the `ZeroPrivateKey` helper at line 87 is a separate, easily forgotten call.

**Impact:** Private key material lingers in process memory, recoverable from crash dumps, memory forensics, or swap files. In a validator context, compromised keys could lead to unauthorized signing and slashing.

**Recommendation:** Document that callers MUST zero the returned key. Consider returning a disposable wrapper that zeros on disposal. Audit all call sites.

**Severity:** High

---

### H-05: Keystore.Decrypt Returns Plaintext Private Key Without Zeroing at Call Sites

**Location:** `src/core/Basalt.Crypto/Keystore.cs:72-99`

**Description:** `Decrypt` correctly zeros the derived key (line 97), but the returned `plaintext` byte[] (the raw private key) is handed to callers with no zeroing. In `KeystoreManager.LoadAsync`, the decrypted key is passed directly to `Account.FromPrivateKey(decryptedKey)` with no subsequent zeroing.

**Impact:** Decrypted private keys remain in managed memory after use, subject to memory forensics.

**Recommendation:** Zero `decryptedKey` after `Account.FromPrivateKey` returns using `CryptographicOperations.ZeroMemory`. Wrap in `try/finally`.

**Severity:** High

---

### H-06: BLS Private Key Not Zeroed After Use in Sign() and GetPublicKeyStatic()

**Location:** `src/core/Basalt.Crypto/BlsSigner.cs:29-31` (Sign), `:157-159` (GetPublicKeyStatic)

**Description:** Both methods copy the private key into a `stackalloc` span, apply the `0x3F` mask, and pass it to `SecretKey.FromBendian()`. The `masked` span is stack-allocated but not explicitly zeroed before the method returns. The `SecretKey` struct's internal state is not zeroed either.

**Impact:** BLS private key material lingers on the stack and in the native struct's memory.

**Recommendation:** Add `CryptographicOperations.ZeroMemory(masked)` after use.

**Severity:** High

---

### H-07: ChainParameters Allows Degenerate Zero Values for Critical Divisors

**Location:** `src/core/Basalt.Core/ChainParameters.cs:36,39,57`

**Description:** `BaseFeeChangeDenominator`, `ElasticityMultiplier`, and `EpochLength` are `uint` init properties with no validation. Setting any to zero causes division-by-zero exceptions at runtime:
- `ElasticityMultiplier = 0` → `DivisionByZeroException` in `BaseFeeCalculator`
- `BaseFeeChangeDenominator = 0` → `DivideByZeroException` via `UInt256.operator /`
- `EpochLength = 0` → potential infinite loop or division by zero
- `BlockTimeMs = 0` → infinite loop in block pacing

**Impact:** A misconfigured node crashes with an unhandled exception during block production. DoS through configuration.

**Recommendation:** Add validation in init setters: `value > 0 ? value : throw new ArgumentOutOfRangeException(...)`. Alternatively, add a `Validate()` method called at startup.

**Severity:** High

---

### H-08: ComplianceProof Nullable Fields Not Marked Required

**Location:** `src/core/Basalt.Core/Compliance/ComplianceProof.cs:14,17`

**Description:** `ComplianceProof` is a `readonly struct` with `byte[]` init properties (`Proof`, `PublicInputs`) that are not `required` and have no defaults. `default(ComplianceProof)` produces null `Proof` and `PublicInputs` despite non-nullable type declarations.

**Impact:** `NullReferenceException` in transaction validation if a default-constructed or incompletely-initialized `ComplianceProof` reaches the hot path.

**Recommendation:** Mark as `required`: `public required byte[] Proof { get; init; }` or add defaults: `= Array.Empty<byte>()`.

**Severity:** High

---

### H-09: Signature/BlsSignature/BlsPublicKey GetHashCode() Only Uses Subset of Fields

**Location:** `src/core/Basalt.Core/Signature.cs:56` (Signature), `:182` (BlsSignature), `:244` (BlsPublicKey)

**Description:** `Signature.GetHashCode()` uses `HashCode.Combine(_v0, _v1, _v2, _v3)` — only the first 32 of 64 bytes. `BlsSignature` uses first 32 of 96 bytes (33%). `BlsPublicKey` uses first 24 of 48 bytes (50%).

**Impact:** Severe hash collision rate when used as dictionary keys. Two signatures differing only in the last 32 bytes hash identically. While `Equals` prevents incorrect behavior, hash table lookups degrade to O(n).

**Recommendation:** Use `HashCode` builder pattern to include all fields:

```csharp
var h = new HashCode();
h.Add(_v0); h.Add(_v1); h.Add(_v2); h.Add(_v3);
h.Add(_v4); h.Add(_v5); h.Add(_v6); h.Add(_v7);
return h.ToHashCode();
```

**Severity:** High

---

## Medium Severity

### M-01: UInt256.ToString() Inconsistent Format Across 2^128 Boundary

**Location:** `src/core/Basalt.Core/UInt256.cs:353-360`

**Description:** `ToString()` returns decimal for values where `Hi == 0` but `"0x" + hex` for values where `Hi != 0`. The string representation changes format at the 2^128 boundary.

**Impact:** UI/API display inconsistencies. Code that assumes a single format will break.

**Recommendation:** Use consistent decimal format via `BigInteger` for all values.

**Severity:** Medium

---

### M-02: UInt256.ToHexString() Returns Empty String for Zero

**Location:** `src/core/Basalt.Core/UInt256.cs:362-367`

**Description:** `TrimStart('0')` on all-zero hex produces `""`. Direct callers of `ToHexString()` on zero get an empty string.

**Recommendation:** Add zero guard: `if (IsZero) return "0";`

**Severity:** Medium

---

### M-03: UInt256 Missing TryParse Method

**Location:** `src/core/Basalt.Core/UInt256.cs` (absent)

**Description:** `Address` and `Hash256` provide `TryFromHexString`/`TryParse`, but `UInt256` only has `Parse()` which throws on invalid input. Since `UInt256` handles user-supplied monetary amounts, parsing failures force exception-based control flow.

**Recommendation:** Add `TryParse(string s, out UInt256 result)`.

**Severity:** Medium

---

### M-04: BasaltReader.ReadVarInt() Accepts Non-Minimal LEB128 Encodings

**Location:** `src/core/Basalt.Codec/BasaltReader.cs:84-102`

**Description:** The LEB128 decoder accepts non-minimal encodings (e.g., `0x80 0x00` for value 0 instead of `0x00`). The same logical value can have multiple wire representations, creating a canonicalization issue.

**Impact:** If VarInt-encoded values appear in structures that are hashed for signing, an attacker could create two different byte sequences decoding to the same transaction, potentially bypassing deduplication or enabling signature malleability.

**Recommendation:** Reject non-minimal encodings by verifying the last continuation byte's payload bits are non-zero (unless it's the only byte).

**Severity:** Medium

---

### M-05: Argon2id Instance Not Disposed in Keystore.DeriveKey

**Location:** `src/core/Basalt.Crypto/Keystore.cs:118-127`

**Description:** The `Argon2id` object (which implements `IDisposable` via `HashAlgorithm`) is never disposed. Its internal state may contain password-derived material.

**Recommendation:** Use `using var argon2 = ...`

**Severity:** Medium

---

### M-06: Keystore.DeriveKey Does Not Zero Password Byte Array

**Location:** `src/core/Basalt.Crypto/Keystore.cs:118`

**Description:** `Encoding.UTF8.GetBytes(password)` creates a byte array that is passed to Argon2id and then abandoned without zeroing.

**Recommendation:** Store in a local, zero after KDF completes with `CryptographicOperations.ZeroMemory`.

**Severity:** Medium

---

### M-07: No Input Validation on Keystore.Decrypt for Tampered KDF Parameters

**Location:** `src/core/Basalt.Crypto/Keystore.cs:72-99`

**Description:** `Decrypt` reads KDF parameters (iterations, memory, parallelism) from the `KeystoreFile` without validating minimums. An attacker modifying the keystore JSON could set `Iterations=1, MemoryKB=1`, weakening brute-force resistance.

**Recommendation:** Validate parameters meet security minimums before proceeding.

**Severity:** Medium

---

### M-08: BLS AggregateSignatures Returns Empty Array for Empty Input

**Location:** `src/core/Basalt.Crypto/BlsSigner.cs:84-85`

**Description:** When called with zero signatures, returns `[]` — not a valid 96-byte G2 point. Passing this to `Verify` would trigger an exception inside `Decode`.

**Recommendation:** Throw `ArgumentException` for empty input or return the G2 identity.

**Severity:** Medium

---

### M-09: BlsSigner.Verify Swallows All Exceptions

**Location:** `src/core/Basalt.Crypto/BlsSigner.cs:73-76,144-147`

**Description:** `Verify` and `VerifyAggregate` catch all exceptions with `catch {}` and return `false`. This silences programming errors (`NullReferenceException`, `OutOfMemoryException`), making debugging difficult.

**Recommendation:** Catch only specific exceptions from the blst library. Let unexpected exceptions propagate.

**Severity:** Medium

---

### M-10: StubBlsSigner Still Exists and Is DI-Injectable

**Location:** `src/core/Basalt.Crypto/IBlsSigner.cs:41-96`

**Description:** `StubBlsSigner` uses Ed25519 padded to BLS sizes. Marked `[Obsolete]` but still present. If accidentally injected via DI misconfiguration, BLS signatures become silently incompatible across nodes.

**Recommendation:** Verify no production code resolves `IBlsSigner` to `StubBlsSigner`. Consider removing entirely or making methods throw `NotSupportedException`.

**Severity:** Medium

---

### M-11: Blake3 IncrementalHasher Usable After Dispose

**Location:** `src/core/Basalt.Crypto/Blake3Hasher.cs:49-78`

**Description:** `IncrementalHasher` has no `_disposed` flag. Methods can be called after `Dispose()`, with behavior depending on the underlying library.

**Recommendation:** Add `_disposed` flag, throw `ObjectDisposedException` after disposal.

**Severity:** Medium

---

### M-12: BasaltWriter.WriteString() Does Not Handle Null Input

**Location:** `src/core/Basalt.Codec/BasaltWriter.cs:113-119`

**Description:** Passing `null` causes `NullReferenceException` at `Encoding.UTF8.GetByteCount(value)`. No explicit guard.

**Recommendation:** Add `ArgumentNullException.ThrowIfNull(value)`.

**Severity:** Medium

---

### M-13: ComplianceCheckOutcome.Reason Non-Nullable Without Default

**Location:** `src/core/Basalt.Core/Compliance/IComplianceVerifier.cs:41`

**Description:** `public string Reason { get; init; }` — `default(ComplianceCheckOutcome)` has `Reason = null` despite non-nullable declaration.

**Recommendation:** Add default: `public string Reason { get; init; } = "";`

**Severity:** Medium

---

### M-14: BasaltResult<T>.Value Accessible Without Checking IsSuccess

**Location:** `src/core/Basalt.Core/BasaltError.cs:116-134`

**Description:** `Value` returns `default(T)` on error. For `UInt256`, this is `Zero` — a valid monetary value. Callers who forget `IsSuccess` checks silently use zero.

**Recommendation:** Throw in `Value` getter when `!IsSuccess`, or rename to `ValueOrDefault` and add a throwing `Value`.

**Severity:** Medium

---

## Low Severity / Recommendations

### L-01: Address/Hash256 TryFromHexString Uses Exception-Based Control Flow

**Location:** `src/core/Basalt.Core/Address.cs:95-107`, `src/core/Basalt.Core/Hash256.cs:106-118`

**Description:** Both `TryFromHexString` methods catch all exceptions from `FromHexString` instead of validating input first.

**Recommendation:** Use `Convert.TryFromHexString` with length validation before parsing.

**Severity:** Low

---

### L-02: StakingOperationResult Uses String Errors Instead of Typed Error Codes

**Location:** `src/core/Basalt.Core/IStakingState.cs:29-42`

**Description:** Uses free-form `string? ErrorMessage` while the rest of the codebase uses `BasaltErrorCode` enum (8001-8004).

**Recommendation:** Add `BasaltErrorCode` field to `StakingOperationResult`.

**Severity:** Low

---

### L-03: UInt256 Implicit Conversion from int Accepts Negative Literals at Compile Time

**Location:** `src/core/Basalt.Core/UInt256.cs:329-333`

**Description:** `implicit operator UInt256(int value)` allows `UInt256 x = -1;` to compile but throw at runtime.

**Recommendation:** Change to `explicit operator` for `int`.

**Severity:** Low

---

### L-04: BasaltErrorCode Enum Has Out-of-Order Groupings

**Location:** `src/core/Basalt.Core/BasaltError.cs:69-84`

**Description:** Staking errors (8xxx) appear before compliance errors (7xxx), breaking ascending numerical order.

**Recommendation:** Reorder for consistency.

**Severity:** Low

---

### L-05: ChainParameters.Mainnet/Testnet Properties Create New Instance Per Access

**Location:** `src/core/Basalt.Core/ChainParameters.cs:78-89`

**Description:** `=> new()` means each access creates a distinct object. Reference equality fails between two reads.

**Recommendation:** Use `static readonly` fields or override `Equals`/`GetHashCode`.

**Severity:** Low

---

### L-06: IComplianceVerifier.GetRequirements Takes byte[] Instead of Address

**Location:** `src/core/Basalt.Core/Compliance/IComplianceVerifier.cs:25`

**Description:** Raw `byte[]` instead of strongly-typed `Address` for contract address parameter.

**Recommendation:** Change to `Address contractAddress`.

**Severity:** Low

---

### L-07: Nullifier Reset at Block Boundaries Allows Cross-Block Replay

**Location:** `src/core/Basalt.Core/Compliance/IComplianceVerifier.cs:27-32`

**Description:** `ResetNullifiers` per block boundary means the same compliance proof can be reused across blocks. Only prevents intra-block replay.

**Recommendation:** Document the threat model. If cross-block replay prevention is required, use persistent/windowed nullifier store.

**Severity:** Low

---

### L-08: Ed25519 BatchVerify Is Sequential, Not True Batch

**Location:** `src/core/Basalt.Crypto/Ed25519Signer.cs:50-64`

**Description:** Loops through signatures individually. True batch verification uses randomized linear combination for significantly lower constant factor.

**Recommendation:** Use NSec's batch verification if available, or rename to `VerifyAll`.

**Severity:** Low

---

### L-09: X25519 Low-Order Point Rejection Not Documented

**Location:** `src/core/Basalt.Crypto/X25519.cs:42-64`

**Description:** Relies on NSec/libsodium to reject low-order public keys (returning `null`), which is correct but undocumented.

**Recommendation:** Add a comment noting the protection.

**Severity:** Low

---

### L-10: Keccak-256 State Array Not Cleared After Hashing

**Location:** `src/core/Basalt.Crypto/KeccakHasher.cs:56-92`

**Description:** The `ulong[25]` state array is heap-allocated and not cleared after hashing. Contains intermediate computation data.

**Recommendation:** `Array.Clear(state)` after the squeeze step, or use `stackalloc` (625 bytes).

**Severity:** Low

---

### L-11: BLS Private Key 0x3F Masking Wastes Key Space

**Location:** `src/core/Basalt.Crypto/BlsSigner.cs:31,159`

**Description:** `masked[0] &= 0x3F` restricts to < 2^254, excluding valid keys 0x40–0x73. Two inputs differing only in bits 254-255 produce the same BLS key pair.

**Recommendation:** Document this behavior. Consider using `blst`'s `KeyGen` for proper scalar reduction.

**Severity:** Low

---

### L-12: X25519 TransportKeyDomain Contains Hidden Null Terminator

**Location:** `src/core/Basalt.Crypto/X25519.cs:21`

**Description:** `"basalt-transport-key-v1\0"u8.ToArray()` includes `\0`. Interoperability risk with other implementations.

**Recommendation:** Document the null byte is intentional or remove it.

**Severity:** Low

---

### L-13: Keystore Version Not Validated on Decrypt

**Location:** `src/core/Basalt.Crypto/Keystore.cs:72-99`

**Description:** Does not check `keystore.Version`. A future v2 format would be silently processed with v1 logic.

**Recommendation:** Add `if (keystore.Version != 1) throw new NotSupportedException(...)`.

**Severity:** Low

---

### L-14: IBlsSigner Missing ProofOfPossession Methods

**Location:** `src/core/Basalt.Crypto/IBlsSigner.cs:8-34`

**Description:** PoP methods only on concrete `BlsSigner`, not in `IBlsSigner` interface. Consumers must downcast.

**Recommendation:** Add `GenerateProofOfPossession`/`VerifyProofOfPossession` to interface.

**Severity:** Low

---

## Test Coverage Gaps

### UInt256 Tests

| Gap | Missing Test |
|-----|-------------|
| U-01 | `Parse()` with invalid input (non-hex without `0x`, empty string, negative, > 256 bits) |
| U-02 | `ToString()` format verification across Hi==0 / Hi!=0 boundary |
| U-03 | `explicit operator ulong` overflow (Hi != 0 → should throw) |
| U-04 | Bitwise operators (`&`, `\|`, `^`, `~`) |
| U-05 | `DivRem` with large values exercising bit-by-bit division |
| U-06 | `CheckedMul` boundary: `b == MaxValue / a` (pass) vs `b == MaxValue / a + 1` (throw) |
| U-07 | Unchecked subtraction wrapping (`0 - 1` → `MaxValue`) |
| U-08 | Shift edge cases: shift by 0, 128, 256+, negative values |
| U-09 | `CheckedAdd` with `carry=1` and `b.Hi=UInt128.MaxValue` (the C-01 bug) |

### Address & Hash256 Tests

| Gap | Missing Test |
|-----|-------------|
| AH-01 | `TryFromHexString` success and failure paths |
| AH-02 | `CompareTo` ordering |
| AH-03 | Use as `Dictionary<T, _>` key (GetHashCode + Equals contract) |
| AH-04 | `FromHexString` with invalid inputs (wrong length, non-hex characters) |

### Crypto Tests

| Gap | Missing Test |
|-----|-------------|
| CR-01 | Ed25519 RFC 8032 Section 7.1 known test vectors |
| CR-02 | Ed25519 empty message and very large message sign/verify |
| CR-03 | Ed25519 malformed public key/signature inputs (wrong length) |
| CR-04 | BLS aggregate of zero signatures |
| CR-05 | BLS empty message signing/verification |
| CR-06 | BLS rogue key attack resistance |
| CR-07 | BLS cross-implementation test vectors from spec |
| CR-08 | BLAKE3 official test vectors beyond empty input (length 1, 1024, 65536) |
| CR-09 | Keccak-256 input at exactly `Rate - 1 = 135` bytes (padding edge case) |

### Codec Tests

| Gap | Missing Test |
|-----|-------------|
| CD-01 | Non-minimal VarInt encoding (crafted manually, verify rejection or acceptance) |
| CD-02 | Truncated multi-byte VarInt (e.g., buffer = `0x80` with no following byte) |
| CD-03 | `ReadString()` exceeding `MaxStringLength` |
| CD-04 | `BasaltSerializer.Deserialize()` with trailing data |
| CD-05 | `BasaltSerializer.Serialize()` output length matches `GetSerializedSize()` |

---

## Positive Findings

- **Keccak-256 is correct**: Line-by-line audit verified round constants, rotation offsets, pi lane permutation, theta/rho/pi/chi/iota steps all match the NIST Keccak specification. The padding byte `0x01` (not SHA-3's `0x06`) is correct. Rate = 136 bytes is correct for Keccak-256. Five known-answer test vectors pass, including the Ethereum empty hash.

- **Address and Hash256 are well-implemented**: Correct `IEquatable<T>`, `IComparable<T>`, `GetHashCode()`, and serialization. Proper use of `Unsafe.ReadUnaligned` for safe unaligned access.

- **UInt256 multiplication is correct**: Schoolbook 4-limb multiplication with proper carry tracking, including `accHi` for overflow beyond 128 bits in intermediate accumulators. The result construction correctly maps limbs to the 256-bit value.

- **UInt256 division (DivRem) is correct**: Bit-by-bit long division algorithm is straightforward and correct.

- **UInt256 shift operators are correct**: Both `<<` and `>>` handle all ranges (0, 1-127, 128-255, 256+) correctly.

- **BasaltResult<T> design is sound**: Using `readonly struct` avoids heap allocation on the hot path. Error propagation without exceptions is correct for transaction validation.

- **Full AOT safety**: No reflection, `dynamic`, or `Assembly.Load` in any audited file. All types are `readonly struct` or sealed classes.

- **No Ed25519/BLS type confusion**: Separate `Signature` (64B), `BlsSignature` (96B), `PublicKey` (32B), `BlsPublicKey` (48B) with strict size validation in constructors.

- **Codec boundary checking**: `BasaltWriter` correctly validates buffer capacity before every write. `BasaltReader.EnsureAvailable` prevents reads past end of buffer. `ref struct` usage prevents accidental heap allocation.

- **Endianness consistency**: All multi-byte values use little-endian encoding throughout the codec.

- **Test coverage breadth**: 2,105 tests across 16 projects with 0 failures. The `BasaltWriterReaderTests` provide excellent round-trip coverage for all primitive types including boundary values. `UInt256Tests` cover arithmetic, carry propagation, and checked overflow. `KeccakTests` include 5 known-answer vectors and block-boundary tests.

---

## Summary

| Severity | Count | Most Urgent |
|----------|-------|-------------|
| Critical | 3 | C-01: UInt256 CheckedAdd overflow miss — fix immediately |
| High | 9 | H-01/H-02: BasaltReader remote DoS — fix before mainnet |
| Medium | 14 | M-04: VarInt canonicalization — evaluate malleability impact |
| Low | 14 | Best-effort improvements |

**Priority order:**
1. **Immediate**: C-01 (CheckedAdd overflow) — provable silent monetary corruption
2. **Immediate**: C-03 (MemoryMarshal alignment) — ARM64 crashes in production
3. **Urgent**: C-02 (unchecked operators audit) — identify all wrapping call sites
4. **Urgent**: H-01/H-02 (BasaltReader negative length) — remote DoS by any peer
5. **Soon**: H-04/H-05/H-06 (key material zeroing) — defense-in-depth for validators
6. **Soon**: H-07 (ChainParameters validation) — prevent misconfiguration DoS
