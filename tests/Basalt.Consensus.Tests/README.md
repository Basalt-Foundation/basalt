# Basalt.Consensus.Tests

Unit tests for Basalt consensus: BFT state machine, validator set, staking, slashing, view changes, pipelined consensus, weighted leader selection, and epoch management. **154 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| StakingState | 31 | Validator registration, stake addition, delegation, unbonding, minimum stake enforcement, active validator queries, stake info retrieval |
| ValidatorSet | 23 | Construction, quorum threshold, max faults, leader selection, lookup by PeerId/Address, edge cases for small/large sets |
| ViewChange | 20 | View change initiation, vote collection, quorum detection, timeout handling, view number progression, duplicate vote prevention |
| SlashingEngine | 19 | Double-sign (100%), inactivity (5%), invalid block (1%) penalties, slashing history, zero-stake edge cases |
| EpochManager | 12 | Epoch boundary detection, validator set rebuild from staking, identity transfer across epochs, inactive validator exclusion, deterministic sorting, multiple epoch transitions |
| PipelinedConsensus | 9 | Multi-round overlap, finalization, cleanup, concurrent proposal handling |
| WeightedLeaderSelector | 9 | Stake-weighted leader selection, determinism, distribution across validators |
| BasaltBft | 7 | Proposal creation, vote handling across PREPARE/PRE-COMMIT/COMMIT phases, block finalization, view change timeout |
| BLS Aggregation | 24 | BLS signature aggregation, aggregate verification, bitmap encoding |

**Total: 154 tests**

## Test Files

- `StakingTests.cs` -- Staking state management: registration, delegation, unbonding, minimum stakes
- `ValidatorSetTests.cs` -- Validator set construction, quorum calculation, leader rotation, peer lookup
- `ViewChangeTests.cs` -- View change protocol: initiation, voting, quorum, timeout, view progression
- `SlashingTests.cs` -- Slashing engine: double-sign, inactivity, invalid block penalties, slash history
- `EpochManagerTests.cs` -- Epoch transitions: boundary detection, validator set rebuild, identity transfer, inactive exclusion
- `PipelinedConsensusTests.cs` -- Pipelined BFT consensus: overlapping rounds, finalization, cleanup
- `WeightedLeaderSelectorTests.cs` -- Stake-weighted leader selection: determinism, fairness, edge cases
- `BasaltBftTests.cs` -- Core BFT state machine: proposal, voting phases, finalization

## Running

```bash
dotnet test tests/Basalt.Consensus.Tests
```
