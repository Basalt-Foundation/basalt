# Basalt.Bridge.Tests

Unit tests for the Basalt EVM bridge: deposit/withdrawal lifecycle, multisig relayer, Merkle proof verification, and bridge message serialization. **148 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| BridgeState | 42 | Lock, confirm, finalize deposits; unlock with multisig; replay prevention; locked balance tracking; nonce management; withdrawal lifecycle; edge cases |
| MultisigRelayer | 34 | Add/remove relayers, threshold verification, M-of-N signature validation, insufficient signatures rejection, relayer management, quorum calculation |
| BridgeProofVerifier | 31 | Merkle root computation, proof construction, proof verification, single-leaf and multi-leaf trees, tampered proof detection, large tree proofs, edge cases |
| BridgeMessages | 20 | Bridge message serialization/deserialization: deposit, withdrawal, confirmation, finalization message types, field validation |

**Total: 148 tests**

## Test Files

- `BridgeStateTests.cs` -- Bridge state machine: deposit/withdrawal lifecycle, locking, confirmation, finalization, replay prevention
- `MultisigRelayerTests.cs` -- Multisig relayer: relayer management, M-of-N threshold validation, signature collection
- `BridgeProofVerifierTests.cs` -- Merkle proof construction and verification: tree building, proof generation, tamper detection
- `BridgeMessagesTests.cs` -- Bridge message codec: serialization roundtrips for all bridge message types

## Running

```bash
dotnet test tests/Basalt.Bridge.Tests
```
