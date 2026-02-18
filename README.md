# Basalt

A high-performance Layer 1 blockchain built on .NET 9 with Native AOT compilation, targeting enterprise use cases including RWA tokenization, supply chain management, energy markets, and decentralized identity with native regulatory compliance (MiCA, GDPR, KYC/AML).

## Key Features

- **Native AOT** -- Single self-contained binary with no JIT overhead, sub-millisecond startup
- **BLAKE3 Hashing** -- 6x faster than SHA-256 for state root computation and Merkle proofs
- **Ed25519 Signatures** -- Fast signing/verification via libsodium, with batch verification support
- **BLS12-381 Aggregation** -- BLS signatures via Nethermind.Crypto.Bls for aggregate consensus certificates
- **Keccak-256** -- Custom software implementation for address derivation (macOS-compatible)
- **BasaltBFT Consensus** -- Pipelined HotStuff-based BFT with 400ms block time, stake-weighted leader selection, BLS aggregation, and epoch-based dynamic validator sets
- **Merkle Patricia Trie** -- Cryptographically verifiable state with RocksDB persistence
- **Smart Contracts** -- C# contracts with gas metering, sandboxed execution, and Roslyn analyzers
- **Built-in Compliance** -- KYC/AML identity registry, sanctions screening, per-token transfer policies
- **EVM Bridge** -- Lock/unlock bridge with multisig relayer and Merkle proof verification
- **Confidentiality** -- Pedersen commitments, Groth16 proofs, private channels, selective disclosure
- **P2P Networking** -- TCP transport with length-prefixed framing, Kademlia DHT, Episub gossip, peer reputation scoring

## Architecture

```
Basalt.Core  (zero external deps)
+-- Basalt.Codec         -- Deterministic binary serialization
+-- Basalt.Crypto        -- BLAKE3, Ed25519, Keccak-256, BLS12-381, AES-256-GCM keystore
+-- Basalt.Storage       -- RocksDB, Merkle Patricia Trie, block/receipt stores
+-- Basalt.Network       -- TCP transport, Kademlia DHT, Episub gossip
|   +-- Basalt.Consensus -- BasaltBFT, staking, slashing, pipelined consensus
|       +-- Basalt.Execution -- Transaction executor, BasaltVM, gas metering
|           +-- Basalt.Api.Rest     -- REST API + WebSocket
|           +-- Basalt.Api.Grpc     -- gRPC services
|           +-- Basalt.Api.GraphQL  -- GraphQL (HotChocolate)
|           +-- Basalt.Compliance   -- Identity, KYC/AML, sanctions, audit trail
|           +-- Basalt.Confidentiality -- Pedersen, Groth16, private channels
|           +-- Basalt.Bridge       -- EVM bridge, multisig relayer
|               +-- Basalt.Node     -- Composition root, single binary
```

## Quick Start

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### Build

```bash
dotnet build
```

### Run Tests

```bash
dotnet test
```

1,698 tests across 16 test projects covering core types, cryptography, codec serialization, storage, networking, consensus, execution, API, compliance, bridge, confidentiality, node configuration, SDK contracts, analyzers, and end-to-end integration.

### Run a Local Node

```bash
dotnet run --project src/node/Basalt.Node
```

The node starts in standalone mode on the devnet (chain ID 31337) with a REST API on port 5000 and timer-based block production at 400ms intervals.

### Run a 4-Validator Devnet

```bash
docker compose up --build
```

Spins up 4 validator nodes with pre-configured genesis accounts, RocksDB persistent storage, and automatic peer discovery via static peer lists.

| Validator | REST API | P2P |
|-----------|----------|-----|
| validator-0 | `localhost:5100` | `30300` |
| validator-1 | `localhost:5101` | `30301` |
| validator-2 | `localhost:5102` | `30302` |
| validator-3 | `localhost:5103` | `30303` |

Each validator has a Docker volume (`validator-N-data`) for RocksDB persistence and connects to all other validators via environment-configured peer lists. Health checks poll `/v1/status` every 5 seconds.

### CLI

```bash
# Create an account
dotnet run --project tools/Basalt.Cli -- account create

# Check balance
dotnet run --project tools/Basalt.Cli -- account balance 0x...

# Send a transfer
dotnet run --project tools/Basalt.Cli -- tx send --to 0x... --value 1000 --key <hex>

# Get node status
dotnet run --project tools/Basalt.Cli -- node status

# Request faucet tokens
dotnet run --project tools/Basalt.Cli -- faucet 0x...

# Initialize a contract project
dotnet run --project tools/Basalt.Cli -- init MyToken
```

## Project Structure

```
Basalt.sln                              (38 C# projects)
+-- src/
|   +-- core/
|   |   +-- Basalt.Core/               # Hash256, Address, UInt256, chain parameters
|   |   +-- Basalt.Crypto/             # BLAKE3, Ed25519, Keccak-256, BLS12-381, keystore
|   |   +-- Basalt.Codec/              # BasaltWriter/Reader, varint, serialization
|   +-- storage/
|   |   +-- Basalt.Storage/            # RocksDB, MPT, block/receipt stores
|   +-- network/
|   |   +-- Basalt.Network/            # TCP transport, Kademlia DHT, Episub, peer reputation
|   +-- consensus/
|   |   +-- Basalt.Consensus/          # BasaltBFT, pipelined consensus, staking, slashing, epoch manager
|   +-- execution/
|   |   +-- Basalt.Execution/          # Transaction executor, BasaltVM, gas metering, sandbox
|   +-- api/
|   |   +-- Basalt.Api.Rest/           # REST endpoints, faucet, WebSocket, Prometheus metrics
|   |   +-- Basalt.Api.Grpc/           # gRPC services (BasaltNodeService)
|   |   +-- Basalt.Api.GraphQL/        # GraphQL queries and mutations (HotChocolate 14.3.0)
|   +-- compliance/
|   |   +-- Basalt.Compliance/         # Identity registry, KYC/AML, sanctions
|   +-- confidentiality/
|   |   +-- Basalt.Confidentiality/    # Pedersen commitments, Groth16 proofs, private channels, selective disclosure
|   +-- bridge/
|   |   +-- Basalt.Bridge/             # EVM bridge, multisig relayer, Merkle proofs
|   +-- sdk/
|   |   +-- Basalt.Sdk.Contracts/      # Contract attributes, storage primitives, BST20Token base
|   |   +-- Basalt.Sdk.Analyzers/      # Roslyn analyzers for contract safety
|   |   +-- Basalt.Sdk.Testing/        # BasaltTestHost in-process emulator
|   +-- generators/
|   |   +-- Basalt.Generators.Codec/   # Codec source generator (planned)
|   |   +-- Basalt.Generators.Json/    # JSON source generator (planned)
|   |   +-- Basalt.Generators.Contracts/ # ABI dispatch source generator (planned)
|   +-- explorer/
|   |   +-- Basalt.Explorer/           # Blazor WebAssembly block explorer
|   +-- node/
|       +-- Basalt.Node/               # Composition root, single binary
+-- tests/                             # 16 test projects, 1,698 tests
|   +-- Basalt.Core.Tests/
|   +-- Basalt.Crypto.Tests/
|   +-- Basalt.Codec.Tests/
|   +-- Basalt.Storage.Tests/
|   +-- Basalt.Network.Tests/
|   +-- Basalt.Consensus.Tests/
|   +-- Basalt.Execution.Tests/
|   +-- Basalt.Api.Tests/
|   +-- Basalt.Compliance.Tests/
|   +-- Basalt.Bridge.Tests/
|   +-- Basalt.Confidentiality.Tests/
|   +-- Basalt.Sdk.Tests/
|   +-- Basalt.Sdk.Analyzers.Tests/
|   +-- Basalt.Node.Tests/
|   +-- Basalt.Integration.Tests/
+-- benchmarks/
|   +-- Basalt.Benchmarks/             # BenchmarkDotNet microbenchmarks + TPS macro
+-- tools/
|   +-- Basalt.Cli/                    # CLI tool (account, tx, block, faucet, contract init/compile/test)
|   +-- Basalt.DevNet/                 # Docker devnet genesis config + validator setup script
+-- contracts/                         # Solidity bridge contracts (BasaltBridge, WBST)
+-- docs/                              # Design plan, technical specification
```

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/v1/transactions` | Submit a signed transaction |
| `GET` | `/v1/blocks/latest` | Get the latest block |
| `GET` | `/v1/blocks/{id}` | Get block by number or hash |
| `GET` | `/v1/accounts/{address}` | Get account balance and nonce |
| `GET` | `/v1/validators` | List active validators with stake info |
| `GET` | `/v1/status` | Node status (block height, mempool, etc.) |
| `POST` | `/v1/faucet` | Request devnet test tokens |
| `GET` | `/metrics` | Prometheus metrics |
| `WS` | `/ws/blocks` | Real-time block notifications |

## Configuration

The node is configured via environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `BASALT_CHAIN_ID` | `31337` | Chain identifier |
| `BASALT_NETWORK` | `basalt-devnet` | Network name |
| `BASALT_VALIDATOR_INDEX` | `-1` | Validator index in the set (enables consensus mode when >= 0 and peers are set) |
| `BASALT_VALIDATOR_ADDRESS` | -- | Validator account address (hex) |
| `BASALT_PEERS` | -- | Comma-separated peer endpoints (`host:port`) |
| `BASALT_VALIDATOR_KEY` | -- | Validator Ed25519 private key (hex). If unset, a random key is generated (dev mode) |
| `HTTP_PORT` | `5000` | HTTP API listen port |
| `P2P_PORT` | `30303` | P2P TCP listen port |
| `BASALT_DATA_DIR` | -- | Data directory for RocksDB persistence. If unset, state is in-memory only |
| `BASALT_USE_PIPELINING` | `false` | Enable pipelined consensus |
| `BASALT_USE_SANDBOX` | `false` | Enable sandboxed contract execution via AssemblyLoadContext isolation |
| `ASPNETCORE_URLS` | `http://+:5000` | REST API listen address |

## Native AOT Compatibility

`Basalt.Node` is the primary AOT publication target (`PublishAot=true`). All production dependencies are AOT-safe:

| Layer | AOT Status | Notes |
|-------|-----------|-------|
| Core, Crypto, Codec | Safe | Zero reflection, value types, P/Invoke wrappers |
| Storage (RocksDB, MPT) | Safe | Native interop via P/Invoke |
| Network, Consensus | Safe | Standard delegate patterns, no dynamic dispatch |
| Execution (ManagedContractRuntime) | Safe | Selector-based dispatch, no reflection |
| Execution (SandboxedContractRuntime) | **JIT only** | `AssemblyLoadContext.LoadFromStream` requires JIT |
| Api.Rest, Api.Grpc | Safe | Source-generated JSON (`BasaltApiJsonContext`), Protobuf codegen |
| Api.GraphQL (HotChocolate) | **JIT only** | Not referenced by Basalt.Node; isolated in separate library |
| Sdk.Contracts | Safe | Source-generated dispatch via `IDispatchable` |
| Sdk.Testing | N/A | Test-only, not in production |

**Key constraint:** `BASALT_USE_SANDBOX=true` is incompatible with Native AOT. The `ContractAssemblyContext` loads IL at runtime via `LoadFromStream`, which requires a JIT compiler. The default `ManagedContractRuntime` (used when sandbox is disabled) is fully AOT-safe. The Docker container build uses framework-dependent publish (`-p:PublishAot=false`) for full runtime support.

JSON serialization uses source-generated `JsonSerializerContext` types throughout — no reflection-based serialization. Custom converters exist for blockchain types (`Hash256`, `Address`, `UInt256`, `Signature`, `PublicKey`, `BlsSignature`, `BlsPublicKey`).

## Token Economics

| Parameter | Value |
|-----------|-------|
| Symbol | BSLT |
| Decimals | 18 |
| Block time | 400ms |
| Block gas limit | 100,000,000 |
| Max transactions per block | 10,000 |
| Transfer gas cost | 21,000 |
| Min validator stake | 100,000 BSLT |
| Epoch length | 1,000 blocks (100 on devnet) |
| Unbonding period | ~21 days |

## License

Copyright (c) 2025-2026 Basalt Foundation
