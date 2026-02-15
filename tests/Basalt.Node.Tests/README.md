# Basalt.Node.Tests

Unit tests for the Basalt node: configuration, message handling, validator setup, and slashing integration. **34 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| MessageHandling | 16 | Serialization roundtrips for all node-level message types: TxAnnounce, BlockAnnounce, BlockRequest, TxRequest, SyncRequest, Ping, Pong, ConsensusProposal, ConsensusVote, ViewChange, IHave, IWant, Graft, Prune, BlockPayload, TxPayload |
| ValidatorSetup | 8 | ValidatorSet construction (quorum, leader cycling, peer lookup), PeerId determinism, Ed25519 sign/verify, BLS sign/verify, NodeConfiguration consensus mode |
| NodeConfiguration | 5 | Default values, IsConsensusMode (requires both peers and validator index), property initialization |
| SlashingIntegration | 5 | Double-sign slashing (100% penalty), inactivity slashing (5% penalty), staking state registration, active validator queries, weighted leader selection |

**Total: 34 tests**

## Test Files

- `MessageHandlingTests.cs` -- Node-level message serialization: roundtrip tests for all 16 P2P message types
- `ValidatorSetupTests.cs` -- Validator infrastructure: ValidatorSet, PeerId, Ed25519/BLS signing, consensus mode configuration
- `NodeConfigurationTests.cs` -- Node configuration defaults and consensus mode detection
- `SlashingIntegrationTests.cs` -- Staking/slashing integration: double-sign penalties, inactivity penalties, weighted leader selection

## Running

```bash
dotnet test tests/Basalt.Node.Tests
```
