# Basalt.Consensus.Tests

Unit tests for Basalt consensus: BFT state machine, validator set, staking, slashing, view changes, pipelined consensus, weighted leader selection, epoch management, DKG protocol, and threshold cryptography. **221 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| ViewChange | 36 | View change initiation, vote collection, quorum detection, timeout handling, view number progression, duplicate vote prevention |
| StakingState | 31 | Validator registration, stake addition, delegation, unbonding, minimum stake enforcement, active validator queries, stake info retrieval |
| BLS Aggregation | 24 | BLS signature aggregation, aggregate verification, bitmap encoding |
| ValidatorSet | 23 | Construction, quorum threshold, max faults, leader selection, lookup by PeerId/Address, edge cases |
| ThresholdCrypto | 22 | Polynomial evaluation, Lagrange interpolation, share encryption, BLS12-381 scalar field operations |
| DkgProtocol | 21 | Feldman VSS state machine: Deal, Complaint, Justify, Finalize; group public key generation, share verification |
| EpochManager | 20 | Epoch boundary detection, validator set rebuild from staking, identity transfer, inactive validator exclusion, deterministic sorting, multiple transitions |
| SlashingEngine | 19 | Double-sign (100%), inactivity (5%), invalid block (1%) penalties, slashing history, zero-stake edge cases |
| PipelinedConsensus | 14 | Multi-round overlap, finalization, cleanup, concurrent proposal handling, view advancement |
| WeightedLeaderSelector | 9 | Stake-weighted leader selection, determinism, distribution across validators |
| StakingPersistence | 8 | Staking state serialization, RocksDB persistence, round-trip validation |
| BasaltBft | 7 | Proposal creation, vote handling across PREPARE/PRE-COMMIT/COMMIT phases, block finalization, view change timeout |

**Total: 221 tests**

## Test Files

- `StakingTests.cs` -- Staking state management: registration, delegation, unbonding, minimum stakes
- `ValidatorSetTests.cs` -- Validator set construction, quorum calculation, leader rotation, peer lookup
- `ViewChangeTests.cs` -- View change protocol: initiation, voting, quorum, timeout, view progression
- `SlashingTests.cs` -- Slashing engine: double-sign, inactivity, invalid block penalties, slash history
- `EpochManagerTests.cs` -- Epoch transitions: boundary detection, validator set rebuild, identity transfer, inactive exclusion
- `PipelinedConsensusTests.cs` -- Pipelined BFT consensus: overlapping rounds, finalization, cleanup
- `WeightedLeaderSelectorTests.cs` -- Stake-weighted leader selection: determinism, fairness, edge cases
- `BasaltBftTests.cs` -- Core BFT state machine: proposal, voting phases, finalization
- `Dkg/DkgProtocolTests.cs` -- Distributed key generation: Feldman VSS, group public key, share verification
- `Dkg/ThresholdCryptoTests.cs` -- Threshold cryptography: polynomial evaluation, Lagrange interpolation, share encryption
- `Staking/StakingPersistenceTests.cs` -- Staking state persistence and recovery

## Running

```bash
dotnet test tests/Basalt.Consensus.Tests
```
