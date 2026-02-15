# Basalt.Node

Composition root and entry point for the Basalt blockchain node. Assembles all modules into a single self-contained binary with REST API, gRPC, block production, P2P consensus, and structured logging.

## Running

```bash
# Development
dotnet run --project src/node/Basalt.Node

# Production (Native AOT)
dotnet publish src/node/Basalt.Node -c Release -r linux-x64
./bin/Release/net9.0/linux-x64/publish/Basalt.Node

# Docker
docker build -t basalt-node .
docker run -p 5000:5000 -p 30303:30303 basalt-node
```

## Runtime Modes

The node operates in one of two modes, determined by the `BASALT_VALIDATOR_INDEX` environment variable (along with `BASALT_PEERS`). If both are set (index >= 0 and at least one peer), the node runs in **consensus mode**; otherwise it falls back to **standalone mode**.

### Standalone Mode

1. Initializes Serilog structured logging
2. Creates an in-memory state database
3. Configures chain parameters (devnet by default)
4. Creates genesis block with initial account balances
5. Registers genesis validators in StakingState
6. Starts the REST API, gRPC (`BasaltNodeService`), faucet, WebSocket, and Prometheus metrics
7. Creates a `BlockProductionLoop` for timer-based block production (400ms block time)
8. Wires metrics and WebSocket notifications to the block production loop
9. Handles graceful shutdown on SIGINT/SIGTERM

### Consensus Mode

1. Initializes Serilog structured logging
2. Initializes RocksDB persistent storage (when `BASALT_DATA_DIR` is set) with `BlockStore` and `ReceiptStore`, or in-memory state
3. Recovers chain state from persistent storage if available (deserializes genesis and latest block, rebuilds state from `TrieStateDb`)
4. Registers genesis validators with `StakingState` (4 validators, 200,000 BSLT each)
5. Creates `SlashingEngine` for double-sign and inactivity penalties
6. Starts the REST API, gRPC (`BasaltNodeService`), faucet, WebSocket, and Prometheus metrics
7. Launches `NodeCoordinator`, which wires:
   - `TcpTransport` for P2P connections (length-prefixed framing, 4-byte big-endian length header)
   - `HandshakeProtocol` with chain ID validation and 5s timeout
   - `PeerManager` for peer lifecycle management
   - `EpisubService` for gossip-based message dissemination (with IHave/IWant, Graft/Prune)
   - `GossipService` for transaction and consensus message broadcasting
   - `BasaltBft` (sequential) or `PipelinedConsensus` depending on configuration
   - `WeightedLeaderSelector` for stake-weighted leader rotation
8. Connects to static peers and performs state sync from peers if behind
9. Runs BFT consensus loop with 200ms tick interval, block-time pacing, and view-change timeouts
10. Subscribes to mempool events for transaction gossip (peer-received transactions use `raiseEvent: false` to avoid double-gossip)
11. Tracks validator activity for inactivity slashing (threshold: 100 blocks) and detects double-signing
12. Persists finalized blocks to RocksDB
13. Runs a peer reconnection loop (checks every 5 seconds)

## NodeCoordinator

`NodeCoordinator` is the central orchestrator for multi-node operation. It implements `IAsyncDisposable` and coordinates:

- **Identity**: Ed25519 key pair, `PeerId` derivation, BLS key pair
- **Validator Set**: Built from peer configuration with stake-weighted leader selection
- **Networking**: TCP transport, handshake protocol (Hello/HelloAck), peer manager, Episub gossip
- **Consensus**: Sequential (`BasaltBft`) or pipelined (`PipelinedConsensus`) mode
- **Block Production**: On-demand by the leader, finalized through BFT (no `BlockProductionLoop` in consensus mode)
- **State Sync**: Batch-based sync via `SyncRequestMessage`/`SyncResponseMessage` (batch size: 50 blocks)
- **Slashing**: Double-sign detection via `_proposalsByView` dictionary, inactivity tracking via `_lastActiveBlock` per validator

### Message Types Handled

| Message Type | Handler |
|---|---|
| `TxAnnounceMessage` | Request missing transactions from sender |
| `TxPayloadMessage` | Validate and add transactions to mempool |
| `TxRequestMessage` | Respond with requested transactions |
| `BlockAnnounceMessage` | Request blocks we do not have |
| `BlockPayloadMessage` | Apply blocks from peers |
| `SyncRequestMessage` | Serve blocks to syncing peers |
| `SyncResponseMessage` | Apply synced blocks in batch |
| `ConsensusProposalMessage` | Double-sign detection, then delegate to consensus engine |
| `ConsensusVoteMessage` | Track validator activity, then delegate to consensus engine |
| `ViewChangeMessage` | Delegate to consensus engine |
| `IHaveMessage` / `IWantMessage` | Episub gossip protocol |
| `GraftMessage` / `PruneMessage` | Episub mesh management |
| `PingMessage` | Reply with `PongMessage` |

## Configuration

Environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `BASALT_CHAIN_ID` | `31337` | Chain identifier |
| `BASALT_NETWORK` | `basalt-devnet` | Network name |
| `BASALT_VALIDATOR_INDEX` | `-1` | Validator index (enables consensus mode when >= 0 and `BASALT_PEERS` is set) |
| `BASALT_VALIDATOR_ADDRESS` | -- | Validator address (hex) |
| `BASALT_VALIDATOR_KEY` | -- | Validator Ed25519 private key (hex). If unset, a random key is generated (dev mode) |
| `BASALT_PEERS` | -- | Comma-separated peer endpoints (`host:port`) |
| `HTTP_PORT` | `5000` | HTTP listen port for REST/gRPC |
| `P2P_PORT` | `30303` | TCP listen port for P2P networking |
| `BASALT_DATA_DIR` | -- | Path to RocksDB data directory. If unset, state is in-memory only |
| `BASALT_USE_PIPELINING` | `false` | Enable pipelined consensus (`true`/`false`) |
| `BASALT_USE_SANDBOX` | `false` | Enable sandboxed contract execution via AssemblyLoadContext isolation (`true`/`false`) |
| `ASPNETCORE_URLS` | `http://+:5000` | Listen address |

## Ports

| Port | Protocol | Service |
|------|----------|---------|
| 5000 | HTTP | REST API, faucet, Prometheus metrics, WebSocket, gRPC (via `MapGrpcService`) |
| 30303 | TCP | P2P networking (length-prefixed framing) |

## Dependencies

References the following Basalt modules:

- `Basalt.Core`, `Basalt.Crypto`, `Basalt.Codec`
- `Basalt.Storage`, `Basalt.Network`, `Basalt.Consensus`
- `Basalt.Execution`, `Basalt.Compliance`
- `Basalt.Api.Rest`, `Basalt.Api.Grpc`

External packages:

- `Microsoft.Extensions.Hosting` -- Generic host
- `Serilog`, `Serilog.Extensions.Hosting`, `Serilog.Sinks.Console`, `Serilog.Sinks.File` -- Structured logging
- `Microsoft.Extensions.Configuration.Json`, `.EnvironmentVariables`, `.CommandLine` -- Configuration
