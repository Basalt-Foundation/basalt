# Basalt.Cli

Command-line interface for interacting with the Basalt blockchain. Provides account management, transaction submission, block queries, faucet access, and contract development tools.

## Installation

```bash
# Run directly
dotnet run --project tools/Basalt.Cli -- <command>

# Or install as a global tool
dotnet pack tools/Basalt.Cli
dotnet tool install --global --add-source ./nupkg Basalt.Cli
basalt <command>
```

## Commands

### `init` -- Initialize a Contract Project

```bash
basalt init <name>
```

Creates a new Basalt smart contract project directory with:
- `<name>.csproj` referencing `Basalt.Sdk.Contracts`
- `Contracts/MyToken.cs` scaffold (BST20Token with `Initialize` and `Mint` methods)
- `Tests/` directory

### `account create` -- Create a New Account

```bash
basalt account create [--output keyfile.txt]
```

Generates an Ed25519 key pair and derives the Basalt address (Keccak-256 of public key, truncated to 20 bytes). Prints the address, public key, and private key to the console. If `--output` is provided, the key material is written to the specified file.

### `account balance` -- Check Account Balance

```bash
basalt account balance <address> [--node http://localhost:5000]
```

Queries `GET /v1/accounts/{address}` and displays the address, balance, nonce, and account type.

### `tx send` -- Send a Transfer Transaction

```bash
basalt tx send \
  --to <recipient-address> \
  --value <amount> \
  --key <private-key-hex> \
  [--gas 21000] \
  [--chain-id 1] \
  [--node http://localhost:5000]
```

The sender address is derived from the private key. The nonce is auto-fetched from the node via `GET /v1/accounts/{sender}`. The transaction is signed using BLAKE3 hashing of the transaction fields and Ed25519 signing, then submitted via `POST /v1/transactions`.

### `tx status` -- Check Transaction Status

```bash
basalt tx status <hash> [--node http://localhost:5000]
```

Prints the transaction hash and directs users to the block explorer for detailed status. Does not query the node directly.

### `block latest` -- Get Latest Block

```bash
basalt block latest [--node http://localhost:5000]
```

Queries `GET /v1/blocks/latest` and displays block number, hash, timestamp, transaction count, and gas usage.

### `block get` -- Get Block by Number or Hash

```bash
basalt block get <id> [--node http://localhost:5000]
```

Queries `GET /v1/blocks/{id}` where `id` is a block number or hex hash. Displays block number, hash, parent hash, state root, timestamp, proposer, gas usage, and transaction count.

### `node status` -- Get Node Status

```bash
basalt node status [--node http://localhost:5000]
```

Queries `GET /v1/status` and displays block height, latest block hash, mempool size, and protocol version.

### `faucet` -- Request Test Tokens

```bash
basalt faucet <address> [--node http://localhost:5000]
```

Sends a `POST /v1/faucet` request with the given address and displays the result message and transaction hash.

### `compile` -- Compile a Contract Project

```bash
basalt compile [<path>]
```

Runs `dotnet build -c Release` on the `.csproj` file in the specified directory (defaults to the current directory).

### `test` -- Run Contract Tests

```bash
basalt test [<path>]
```

Runs `dotnet test --verbosity normal` on the specified directory (defaults to the current directory).

## Global Options

| Option | Default | Description |
|--------|---------|-------------|
| `--node` | `http://localhost:5000` | Basalt node REST API URL |

## API Communication

The CLI uses `NodeClient`, an HTTP client wrapper that communicates with the node's REST API. All JSON serialization uses a source-generated `CliJsonContext` for AOT compatibility. The following endpoints are used:

| Operation | HTTP Method | Endpoint |
|-----------|-------------|----------|
| Get status | `GET` | `/v1/status` |
| Get latest block | `GET` | `/v1/blocks/latest` |
| Get block by ID | `GET` | `/v1/blocks/{id}` |
| Get account | `GET` | `/v1/accounts/{address}` |
| Send transaction | `POST` | `/v1/transactions` |
| Request faucet | `POST` | `/v1/faucet` |

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Address, Hash256, UInt256 |
| `Basalt.Crypto` | Ed25519 key generation, signing, BLAKE3 hashing, address derivation |
| `System.CommandLine` | CLI argument parsing |
| `System.Text.Json` | API communication (with source-generated serialization context) |
