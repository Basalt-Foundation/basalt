# Basalt.Crypto

Cryptographic primitives for the Basalt blockchain: hashing, signing, key derivation, and encrypted key storage.

## Components

### Blake3Hasher

Primary hash function (`static class`) for state roots, Merkle trees, and content addressing. Uses the [Blake3](https://github.com/xoofx/Blake3.NET) native binding.

```csharp
Hash256 hash = Blake3Hasher.Hash(data);                     // ReadOnlySpan<byte> -> Hash256
Blake3Hasher.Hash(data, output);                            // ReadOnlySpan<byte> -> Span<byte>
Hash256 node = Blake3Hasher.HashPair(left, right);          // Merkle tree nodes

using var incremental = Blake3Hasher.CreateIncremental();   // Returns IncrementalHasher
incremental.Update(part1);
incremental.Update(part2);
Hash256 result = incremental.Finalize();                    // Returns Hash256
incremental.FinalizeInto(outputSpan);                       // Write to Span<byte>
```

Note: `Blake3Hasher.Hash(ReadOnlySpan<byte>)` returns `Hash256`, not `byte[]`. Use `.ToArray()` on the result if you need a byte array.

### Ed25519Signer

Digital signatures (`static class`) via [NSec.Cryptography](https://nsec.rocks/) (libsodium).

```csharp
// Key generation: returns (byte[] PrivateKey, PublicKey PublicKey)
var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();

// Signing: privateKey and message are ReadOnlySpan<byte>, returns Signature
Signature sig = Ed25519Signer.Sign(privateKey, message);

// Verification: publicKey is PublicKey, message is ReadOnlySpan<byte>, sig is Signature
bool valid = Ed25519Signer.Verify(publicKey, message, sig);

// Public key derivation: privateKey is ReadOnlySpan<byte>, returns PublicKey
PublicKey pub = Ed25519Signer.GetPublicKey(privateKey);

// Address derivation: delegates to KeccakHasher.DeriveAddress
Address addr = Ed25519Signer.DeriveAddress(publicKey);

// Batch verification: validates each independently, returns false if any fail
bool allValid = Ed25519Signer.BatchVerify(publicKeys, messages, signatures);
// publicKeys: ReadOnlySpan<PublicKey>
// messages:   ReadOnlySpan<byte[]>
// signatures: ReadOnlySpan<Signature>
```

### KeccakHasher

Software Keccak-256 implementation (`static class`) for address derivation. Platform-independent (no OS SHA-3 dependency).

```csharp
byte[] hash = KeccakHasher.Hash(data);                      // ReadOnlySpan<byte> -> byte[]
KeccakHasher.Hash(data, destination);                       // ReadOnlySpan<byte> -> Span<byte>
Address addr = KeccakHasher.DeriveAddress(publicKey);       // PublicKey -> Address (last 20 bytes)
Address addr = KeccakHasher.DeriveAddress(pubKeyBytes);     // ReadOnlySpan<byte> -> Address
```

Note: `KeccakHasher.Hash` returns `byte[]`, unlike `Blake3Hasher.Hash` which returns `Hash256`.

### Keystore

Encrypted private key storage (`static class`) using AES-256-GCM with Argon2id key derivation.

```csharp
KeystoreFile encrypted = Keystore.Encrypt(privateKey, "password");   // ReadOnlySpan<byte>, string
string json = Keystore.ToJson(encrypted);

KeystoreFile? loaded = Keystore.FromJson(json);                      // Returns nullable
byte[] decrypted = Keystore.Decrypt(loaded!, "password");
```

Parameters: Argon2id with 3 iterations, 64 MB memory, parallelism 4 (OWASP recommended). The `KeystoreFile` stores version, cipher info, nonce, tag, ciphertext, and KDF parameters -- all as hex strings in JSON.

### IBlsSigner

Interface for BLS12-381 aggregate signatures used in consensus vote aggregation.

```csharp
public interface IBlsSigner
{
    byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> message);
    bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature);
    byte[] AggregateSignatures(ReadOnlySpan<byte[]> signatures);
    bool VerifyAggregate(ReadOnlySpan<byte[]> publicKeys, ReadOnlySpan<byte> message, ReadOnlySpan<byte> aggregateSignature);
    byte[] GetPublicKey(ReadOnlySpan<byte> privateKey);
}
```

### BlsSigner

Real BLS12-381 implementation (`sealed class`) using `Nethermind.Crypto.Bls` (wraps the `blst` native library). Key sizes: 32-byte private keys, 48-byte public keys (compressed G1 point), 96-byte signatures (compressed G2 point). Uses the Ethereum domain separation tag `BLS_SIG_BLS12381G2_XMD:SHA-256_SSWU_RO_POP_`.

```csharp
IBlsSigner bls = new BlsSigner();

byte[] sig = bls.Sign(privateKey, message);                         // Returns byte[96]
bool valid = bls.Verify(publicKey, message, sig);                   // All ReadOnlySpan<byte>
byte[] aggSig = bls.AggregateSignatures(signatures);                // ReadOnlySpan<byte[]>
bool aggValid = bls.VerifyAggregate(publicKeys, message, aggSig);   // ReadOnlySpan<byte[]>, ...

// Static public key derivation (preferred over interface method)
byte[] pubKey = BlsSigner.GetPublicKeyStatic(privateKey);           // Returns byte[48]
```

Note: `GetPublicKey` is an explicit interface implementation on `BlsSigner`. For direct access without casting to `IBlsSigner`, use `BlsSigner.GetPublicKeyStatic(privateKey)`. Verification uses manual pairing: `MillerLoop` + `FinalExp().IsEqual()` rather than `Pairing.Aggregate` + `FinalVerify`.

### StubBlsSigner [Obsolete]

`StubBlsSigner` is deprecated (`[Obsolete]`) and retained only for backward compatibility. It wraps Ed25519, padding signatures (64 -> 96 bytes) and public keys (32 -> 48 bytes) to BLS sizes. Use `BlsSigner` for all new code.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Hash256, Address, PublicKey, Signature, BlsSignature, BlsPublicKey |
| `Blake3` | BLAKE3 native binding |
| `NSec.Cryptography` | Ed25519 via libsodium |
| `Konscious.Security.Cryptography.Argon2` | Argon2id key derivation |
| `Nethermind.Crypto.Bls` | BLS12-381 via blst native library |
