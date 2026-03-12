# Basalt.Node.Tests

Unit tests for the Basalt node: configuration, message handling, validator setup, slashing integration, mainnet guards, solver manager, and solver scoring. **96 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| SolverManager | 18 | Solver registration (max 32), solution submission, Ed25519 signature verification, solution window, best-solution selection, reward computation |
| MessageHandling | 16 | Serialization roundtrips for all node-level message types: TxAnnounce, BlockAnnounce, BlockRequest, TxRequest, SyncRequest, Ping, Pong, ConsensusProposal, ConsensusVote, ViewChange, IHave, IWant, Graft, Prune, BlockPayload, TxPayload |
| SolverScoring | 13 | Surplus computation, feasibility validation, constant-product invariant check, fill count limits, scoring edge cases |
| MainnetGuard | 9 | Production safety checks: chain ID validation, admin address requirements, parameter bounds, DKG configuration |
| ValidatorSetup | 8 | ValidatorSet construction (quorum, leader cycling, peer lookup), PeerId determinism, Ed25519/BLS sign/verify |
| NodeConfiguration | 8 | Default values, IsConsensusMode (requires both peers and validator index), property initialization, environment variable handling |
| SlashingIntegration | 5 | Double-sign slashing (100% penalty), inactivity slashing (5% penalty), staking state registration, active validator queries, weighted leader selection |

**Total: 96 tests**

## Test Files

- `MessageHandlingTests.cs` -- Node-level message serialization: roundtrip tests for all 16 P2P message types
- `ValidatorSetupTests.cs` -- Validator infrastructure: ValidatorSet, PeerId, Ed25519/BLS signing, consensus mode
- `NodeConfigurationTests.cs` -- Node configuration defaults and consensus mode detection
- `SlashingIntegrationTests.cs` -- Staking/slashing integration: double-sign penalties, inactivity penalties
- `MainnetGuardTests.cs` -- Production safety checks: chain ID, admin addresses, parameter bounds
- `Solver/SolverManagerTests.cs` -- Solver registration, solution submission, reward distribution
- `Solver/SolverScoringTests.cs` -- Solution scoring: surplus, feasibility, invariant checks

## Running

```bash
dotnet test tests/Basalt.Node.Tests
```
