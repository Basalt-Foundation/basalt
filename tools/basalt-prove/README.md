# basalt-prove

A Go tool (gnark) for the Basalt compliance circuit: golden-vector generation and a real prover.

The compliance circuit proves, in zero knowledge, that the sender holds a credential that is a member of
a trusted issuer's Merkle tree, has not expired, meets an issuer tier, is not revoked, and yields a
per-epoch nullifier. All five public inputs are bound to the credential: `issuerRoot`, `nullifier`,
`expiry`, `issuerTier`, `revocationRoot`. The verifier side lives in `Basalt.Confidentiality` (blst) and
`Basalt.Compliance` (COMPL-C03 enforcement of expiry, tier, and the nullifier binding).

## Modes

- `go run . encoding` — golden vector for the gnark to blst cross-encoding test (trivial circuit).
- `go run . compliance` — golden vector for the frozen 5-input layout (v1, inputs exposed not bound).
- `go run . membership` — golden vector for the full circuit (all inputs bound).
- `go run . setup` — trusted setup. Writes `membership.pk` and `membership.vk`, and prints the
  Basalt-layout `vk_hex` to register on-chain in the SchemaRegistry. Run ONCE per circuit.
- `go run . prove` — reads a credential JSON on stdin, loads `membership.pk`, produces a proof,
  self-verifies it against `membership.vk`, and prints the pieces a Basalt `ComplianceProof` carries.

## Prove input (stdin JSON)

```json
{
  "secret": 42,
  "epoch": 7,
  "expiry": 1893456000,
  "tier": 3,
  "leafIndex": 5,
  "leaves": ["0x...", "..."]
}
```

`leaves` is the issuer's published tree (`2^MerkleDepth` hex field elements); the prover's leaf at
`leafIndex` must equal `MiMC(secret, expiry, tier)`. Omit `leaves` to synthesize a demo issuer tree.

## Prove output

```json
{
  "proof_hex": "...",          // 192 bytes: A(48) B(96) C(48)
  "public_inputs_hex": "...",  // 160 bytes: five 32-byte big-endian public inputs
  "nullifier_hex": "..."       // 32 bytes, equals public input index 1
}
```

## Flow

1. Issuer/authority runs `setup` once, registers `vk_hex` on-chain, distributes `membership.pk`.
2. A holder runs `prove` with their credential to get a `ComplianceProof`, attaches it to a transaction.
3. Basalt verifies it with the registered vk (blst pairing) and enforces expiry/tier/nullifier (C03).

`membership.pk` and `membership.vk` are large and generated, so they are git-ignored. The setup is not a
production ceremony, a real deployment would use a multi-party trusted setup.
