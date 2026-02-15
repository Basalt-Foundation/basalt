# Contributing to Basalt

Thank you for your interest in contributing to Basalt! This document provides guidelines and information for contributors.

## Getting Started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Docker](https://www.docker.com/) (for devnet testing)
- A C# IDE (Visual Studio, Rider, or VS Code with C# Dev Kit)

### Building

```bash
dotnet build
```

### Running Tests

```bash
dotnet test
```

### Running the Devnet

```bash
docker compose up --build
```

This starts a 4-validator network with HTTP APIs on ports 5100-5103 and P2P on ports 30300-30303.

## Development Workflow

1. Fork the repository
2. Create a feature branch from `main`
3. Make your changes
4. Ensure all tests pass (`dotnet test`)
5. Ensure the build has zero warnings (`dotnet build`)
6. Submit a pull request

## Code Standards

- Follow existing code conventions and patterns
- All public APIs must have XML documentation
- New features must include unit tests
- Maintain zero build warnings
- Use the central package management (`Directory.Packages.props`) for NuGet versions

## Architecture

The solution is organized into layers:

- **Core** (`src/core/`) — Primitives, cryptography, serialization
- **Storage** (`src/storage/`) — State database, Merkle Patricia Trie, RocksDB
- **Network** (`src/network/`) — P2P transport, gossip, DHT
- **Consensus** (`src/consensus/`) — BFT consensus, staking, slashing
- **Execution** (`src/execution/`) — Transaction processing, smart contract VM
- **API** (`src/api/`) — gRPC, GraphQL, REST endpoints
- **SDK** (`src/sdk/`) — Smart contract SDK, Roslyn analyzers, source generators
- **Compliance** (`src/compliance/`) — Identity, KYC, sanctions
- **Bridge** (`src/bridge/`) — EVM bridge, multisig relayer
- **Confidentiality** (`src/confidentiality/`) — Zero-knowledge proofs, private channels
- **Explorer** (`src/explorer/`) — Blazor WebAssembly block explorer
- **Tools** (`tools/`) — CLI and devnet orchestration

## Reporting Issues

- Use GitHub Issues for bug reports and feature requests
- Include reproduction steps for bugs
- Check existing issues before creating duplicates

## Security

If you discover a security vulnerability, please follow our [Security Policy](SECURITY.md) for responsible disclosure. Do **not** open a public issue for security vulnerabilities.

## License

By contributing to Basalt, you agree that your contributions will be licensed under the [Apache License 2.0](LICENSE).
