# Basalt Security & Quality Audit — Node Orchestration

## Scope

Audit the node entry point, dependency injection, configuration, and the central orchestrator that wires all subsystems together:

| Project | Path | Description |
|---|---|---|
| `Basalt.Node` | `src/node/Basalt.Node/` | Main executable: `Program.cs` (DI/startup), `NodeCoordinator.cs` (orchestration), `NodeConfiguration.cs` (env-var config) |

Corresponding test project: `tests/Basalt.Node.Tests/`

Also review the DevNet tooling:
- `tools/Basalt.DevNet/` — Genesis configuration, validator setup scripts
- `tools/Basalt.Cli/` — CLI tool for node interaction

---

## Files to Audit

### Basalt.Node
- `Program.cs` (~386 lines) — ASP.NET host builder, DI registration, service startup, middleware pipeline
- `NodeCoordinator.cs` (~1,671 lines) — **LARGEST FILE IN CODEBASE** — wires Transport → PeerManager → Gossip → Episub → ChainManager → BlockBuilder → BlockProductionLoop → Consensus → EpochManager → StakingState → SlashingEngine → Compliance → ReceiptStore
- `NodeConfiguration.cs` (~91 lines) — Environment variable-based configuration

### tools/Basalt.Cli
- `Program.cs` (~400 lines) — CLI commands: status, block, account, transfer, faucet, keygen
- `NodeClient.cs` (~153 lines) — HTTP client for CLI-to-node communication

### tools/Basalt.DevNet
- `devnet-genesis.json` — Genesis configuration
- `setup-validator.sh` — Validator setup script

---

## Audit Objectives

### 1. NodeCoordinator Correctness (CRITICAL)
This is the most complex and highest-risk file in the codebase at 1,671 lines. It orchestrates all subsystems.

- **Block Finalization Pipeline**: Verify the flow from consensus agreement → block application → state update → receipt storage → event emission. Check that all code paths (leader path, follower path, sync path) produce identical state.
- **Consensus Sync**: Verify `TrySyncFromPeers()` correctly recovers from being behind. Check the re-entrancy guard. Verify post-sync consensus restart produces correct state.
- **Epoch Transitions**: Verify `ApplyEpochTransition()` correctly: swaps validator set, rewires leader selector, updates consensus, resets activity tracking. Check timing — when exactly during block processing does the epoch transition occur?
- **Receipt Storage**: Verify receipts are persisted in all 3 code paths (leader execution, follower execution, sync recovery).
- **Double-Sign Detection**: Verify `_proposalsByView` dictionary correctly detects double-signing without false positives. Cross-reference with `PipelinedConsensus._minNextView`.
- **Activity Tracking**: Verify `_lastActiveBlock` tracking for inactivity slashing. Check that legitimate validator restarts don't trigger false inactivity penalties.
- **Message Handling**: Verify all network message types are correctly routed and processed. Check that consensus messages from unknown validators are rejected.

### 2. Startup & Initialization Order (CRITICAL)
- Verify that subsystems are initialized in the correct order — dependencies must be ready before dependents.
- Check genesis block creation: verify determinism (no `DateTimeOffset.UtcNow` — use fixed timestamp).
- Verify that `GenesisContractDeployer` deploys all 8 system contracts correctly.
- Check that recovery from stored blocks (`ChainManager.ResumeFromBlock`) happens before consensus starts.
- Verify that state sync (`TrySyncFromPeers()`) completes before the node joins consensus.

### 3. Configuration Security
- Verify `NodeConfiguration` reads environment variables safely.
- Check for sensitive configuration leaks: private keys, bootstrap peer credentials.
- Verify `BASALT_DATA_DIR` is validated and cannot be path-traversed.
- Check that default values are safe for production use.
- Verify that P2P port, HTTP port, and bootstrap peers are properly validated.

### 4. Dependency Injection & Service Lifecycle
- Verify that all services are registered with correct lifetimes (Singleton, Scoped, Transient).
- Check for service disposal: all `IDisposable`/`IAsyncDisposable` services must be cleaned up.
- Verify that `NodeCoordinator : IAsyncDisposable` properly shuts down all subsystems.
- Check for circular dependency risks in the DI container.

### 5. Error Recovery & Graceful Shutdown
- Verify that the node can recover from:
  - Database corruption (RocksDB)
  - Network partition (all peers unreachable)
  - Consensus stall (no blocks produced)
  - Out-of-memory conditions
- Check that `SIGTERM`/`SIGINT` triggers graceful shutdown (flush state, close connections, stop consensus).
- Verify that partial failures don't leave the node in an inconsistent state.

### 6. Logging & Observability
- Verify logging uses structured logging (Serilog) and includes relevant context (block number, peer ID, tx hash).
- Check that sensitive data (private keys, raw transaction data) is not logged.
- Verify log levels are appropriate (Info for normal operations, Warn for recoverable issues, Error for failures).
- Check that the metrics endpoint exposes useful operational metrics.

### 7. AOT Compatibility
- Verify `PublishAot=true` is respected and no AOT-unsafe patterns exist in the node code.
- Check that DI registration uses AOT-safe patterns.
- Verify that Serilog configuration is AOT-compatible.

### 8. CLI Tool Security
- Verify `NodeClient` validates API responses and handles errors.
- Check that the `keygen` command generates keys securely and handles key material safely.
- Verify that the `transfer` command doesn't log or display private keys.
- Check that the CLI properly validates user input (addresses, amounts).

### 9. DevNet Configuration
- Verify `devnet-genesis.json` contains safe defaults.
- Check `setup-validator.sh` for shell injection vulnerabilities.
- Verify Docker Compose configuration (ports, volumes, environment variables).
- Check that devnet key material is not reused in production.

### 10. Concurrency & Thread Safety
- `NodeCoordinator` is accessed from multiple threads (network handlers, consensus timer, API handlers).
- Verify that shared state is protected by appropriate synchronization.
- Check for deadlock risks in the interaction between consensus, network, and block production.
- Verify that async operations use proper cancellation tokens.

### 11. Test Coverage
- Review `tests/Basalt.Node.Tests/` for:
  - Message handling for all message types
  - Configuration parsing with valid and invalid inputs
  - Slashing integration scenarios
  - Validator setup and epoch transitions
  - Startup and shutdown sequences
  - Recovery from stored state
  - Concurrent message processing

---

## Key Context

- `NodeCoordinator` at 1,671 lines is the single largest file — it's the integration hub where all subsystems meet.
- Known pitfall: Genesis nondeterminism — `DateTimeOffset.UtcNow` in genesis creates different hashes per node; must use fixed timestamp.
- Known pitfall: Dual TCP connection race — both sides connect simultaneously, each keeps different conn.
- Known pitfall: BFT self-votes — nodes must count own votes locally before broadcasting.
- Docker Compose: 4 validators, ports 5100-5103 (HTTP), 30300-30303 (P2P).
- RocksDB volumes: `validator-N-data:/data/basalt` for each validator.
- `BASALT_DATA_DIR` env var controls data directory.
- NuGet: `Microsoft.Extensions.Hosting 9.0.0`, `Serilog 4.2.0`, `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File`.

---

## Output Format

Write your findings to `audit/output/11-node.md` with the following structure:

```markdown
# Node Orchestration Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Initialization order bugs, state inconsistency, security vulnerabilities]

## High Severity
[Significant issues affecting reliability or security]

## Medium Severity
[Issues to address]

## Low Severity / Recommendations
[Code quality, refactoring suggestions, best practices]

## Test Coverage Gaps
[Untested scenarios]

## Positive Findings
[Well-implemented patterns]
```

For each finding, include:
1. **Location**: File path and line number(s)
2. **Description**: What the issue is
3. **Impact**: What could go wrong
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
