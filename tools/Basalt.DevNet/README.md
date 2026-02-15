# Basalt.DevNet

Development network configuration and tooling for running a local Basalt blockchain with 4 validators.

## Quick Start

From the repository root:

```bash
docker compose up --build
```

This starts a 4-validator devnet with pre-configured genesis accounts, RocksDB persistent storage, and automatic peer discovery.

## Files

### devnet-genesis.json

Genesis configuration for the development network:

| Parameter | Value |
|-----------|-------|
| Chain ID | 31337 |
| Network name | basalt-devnet |
| Block time | 400ms |
| Validators | 4 |
| Block gas limit | 100,000,000 |
| Max transactions per block | 10,000 |
| Token symbol | BSLT |
| Decimals | 18 |

**Validators:**

| Name | Address | Stake |
|------|---------|-------|
| validator-0 | `0x...0100` | 100,000 BSLT |
| validator-1 | `0x...0101` | 100,000 BSLT |
| validator-2 | `0x...0102` | 100,000 BSLT |
| validator-3 | `0x...0103` | 100,000 BSLT |

**Pre-funded accounts:**

| Address | Balance | Purpose |
|---------|---------|---------|
| `0x...0001` | 1,000,000,000 BSLT | Test account 1 |
| `0x...0002` | 1,000,000,000 BSLT | Test account 2 |
| `0x...0100`-`0103` | 200,000 BSLT each | Validator accounts |
| `0xFAUCET...` | 500,000,000 BSLT | Faucet supply |

**Faucet configuration:**

| Parameter | Value |
|-----------|-------|
| Drip amount | 100 BSLT |
| Cooldown | 60 seconds |

### setup-validator.sh

Automated validator node setup script for standalone deployment:

```bash
chmod +x tools/Basalt.DevNet/setup-validator.sh
./tools/Basalt.DevNet/setup-validator.sh
```

The script:
1. Creates data, config, and log directories under `$BASALT_HOME` (defaults to `~/.basalt`)
2. Checks .NET 9 SDK installation
3. Generates validator keys using the Basalt CLI (if installed)
4. Writes a default `basalt.json` configuration file with testnet defaults

## Docker Compose Topology

```
+---------------+  +---------------+  +---------------+  +---------------+
| validator-0   |--| validator-1   |--| validator-2   |--| validator-3   |
| :5100 (REST)  |  | :5101 (REST)  |  | :5102 (REST)  |  | :5103 (REST)  |
| :30300 (P2P)  |  | :30301 (P2P)  |  | :30302 (P2P)  |  | :30303 (P2P)  |
+---------------+  +---------------+  +---------------+  +---------------+
        +------------------+------------------+-----------------+
                     basalt-devnet bridge network
```

### Port Mapping

Each container runs with internal ports 5000 (HTTP) and 30303 (P2P), mapped to host as follows:

| Validator | Container | Host REST | Host P2P |
|-----------|-----------|-----------|----------|
| validator-0 | `basalt-validator-0` | `localhost:5100` | `localhost:30300` |
| validator-1 | `basalt-validator-1` | `localhost:5101` | `localhost:30301` |
| validator-2 | `basalt-validator-2` | `localhost:5102` | `localhost:30302` |
| validator-3 | `basalt-validator-3` | `localhost:5103` | `localhost:30303` |

### Environment Variables

Each validator is configured with:

| Variable | Value |
|----------|-------|
| `BASALT_VALIDATOR_INDEX` | 0-3 |
| `BASALT_VALIDATOR_ADDRESS` | `0x...0100` through `0x...0103` |
| `BASALT_VALIDATOR_KEY` | Deterministic dev key per validator |
| `BASALT_NETWORK` | `basalt-devnet` |
| `BASALT_CHAIN_ID` | `31337` |
| `ASPNETCORE_URLS` | `http://+:5000` |
| `BASALT_PEERS` | Comma-separated list of the other 3 validators (`validator-N:30303`) |
| `BASALT_DATA_DIR` | `/data/basalt` |

### Persistent Storage

Each validator has a Docker named volume (`validator-N-data`) mounted at `/data/basalt` for RocksDB persistence. Data survives container restarts.

### Health Checks

Only `validator-0` has an explicit health check configured. It polls `GET /v1/status` every 5 seconds with a 3-second timeout, 10 retries, and a 10-second start period. All validators run with `restart: unless-stopped`.

### Networking

All validators are connected via a Docker bridge network named `basalt-devnet`. Each validator's `BASALT_PEERS` environment variable lists all other validators by container hostname (e.g., `validator-1:30303,validator-2:30303,validator-3:30303`).

### Dockerfile

The Docker image uses a two-stage build:
1. **Build stage**: `mcr.microsoft.com/dotnet/sdk:9.0` -- publishes `Basalt.Node` in Release mode with AOT disabled for container compatibility
2. **Runtime stage**: `mcr.microsoft.com/dotnet/aspnet:9.0` -- installs `librocksdb-dev` (the NuGet RocksDB package has an ARM64 stub only) and runs `dotnet Basalt.Node.dll`

Exposed ports: 5000 (REST), 5001 (gRPC), 30303 (P2P).
