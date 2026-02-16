# Basalt.Explorer

Blazor WebAssembly block explorer for the Basalt blockchain. Provides a web-based UI for browsing blocks, viewing transactions, inspecting accounts, and monitoring network status in real time.

## Features

- **Dashboard** -- Live network status with auto-refresh (2-second polling): block height, latest block hash, mempool size, protocol version
- **Block List** -- Paginated list of recent blocks (up to 20) with navigation
- **Block Detail** -- Individual block view with header fields, gas usage, and transaction count
- **Transaction List** -- Recent transactions from latest blocks with hash, type, sender, recipient, and value
- **Transaction Detail** -- Individual transaction view with hash, type, nonce, sender, to, value, gas limit, gas price, and priority
- **Validators** -- Validator list showing address, stake, public key, and status
- **Account Detail** -- Account balance, nonce, and type lookup
- **Contract Detail** -- Enhanced view for contract accounts: code size, code hash, deployer (clickable), deploy transaction, deploy block, interactive storage explorer, and decoded method names in transaction history
- **Search** -- Search by block number, transaction hash (64/66 chars), account address (40/42 chars), or block hash (fallback)

## Running

### Development

```bash
dotnet run --project src/explorer/Basalt.Explorer
```

The explorer runs as a Blazor WebAssembly SPA and connects to a Basalt node REST API. Configure the node URL via the `NodeUrl` configuration key (default: `http://localhost:5000`).

### With Docker Devnet

Start the devnet, then point the explorer at any validator's REST API:

```bash
docker compose up -d
dotnet run --project src/explorer/Basalt.Explorer
# Explorer connects to http://localhost:5100 (validator-0)
```

## Pages

| Route | Description |
|-------|-------------|
| `/` | Dashboard with live status |
| `/blocks` | Block list with navigation |
| `/block/{Id}` | Block detail view |
| `/transactions` | Recent transactions list |
| `/tx/{Hash}` | Transaction detail view |
| `/validators` | Validator list with address, stake, public key, and status |
| `/account/{Address}` | Account / contract detail view |

## Architecture

- **BasaltApiClient** -- HTTP client wrapping the REST API:
  - `GetStatusAsync()` -- `/v1/status`
  - `GetLatestBlockAsync()` -- `/v1/blocks/latest`
  - `GetBlockAsync(id)` -- `/v1/blocks/{id}`
  - `GetAccountAsync(address)` -- `/v1/accounts/{address}`
  - `GetRecentTransactionsAsync()` -- fetches latest block, then `/v1/blocks/{number}/transactions`
  - `GetTransactionAsync(hash)` -- `/v1/transactions/{hash}`
  - `GetValidatorsAsync()` -- `/v1/validators`
  - `GetContractInfoAsync(address)` -- `/v1/contracts/{address}` (contract metadata: code size, deployer, deploy tx)
  - `ReadContractStorageAsync(address, key)` -- `/v1/contracts/{address}/storage?key=` (server-side BLAKE3 key hashing)
- **FormatHelper** -- Formatting utilities:
  - `FormatBslt(rawValue)` -- converts raw base-unit strings to human-readable BSLT
  - `GetTxTypeBadgeClass(type)` / `GetTxTypeLabel(type)` -- transaction type badge styling
  - `DecodeMethodName(txData)` -- decodes 4-byte BLAKE3 method selectors to names (storage_set, storage_get, storage_del, emit_event)
  - `FormatMethodBadge(txData)` -- formatted method label for contract call transactions
  - `TruncateHash(hash)` / `TruncateData(hex)` / `FormatBytes(bytes)` -- display helpers
- **ExplorerJsonContext** -- Source-generated `JsonSerializerContext` for AOT-compatible JSON deserialization of all DTO types
- **DTOs** -- `NodeStatusDto`, `BlockDto`, `AccountDto`, `TransactionDto`, `ValidatorDto`, `ContractInfoDto`, `StorageReadResponseDto`, `MempoolResponseDto`, `FaucetStatusDto` with `[JsonPropertyName]` attributes for explicit JSON mapping
- **MainLayout** -- Top navbar with navigation links (Dashboard, Blocks, Transactions, Validators) and a search bar
- **Dark theme** -- Custom CSS with system font stack (`-apple-system, BlinkMacSystemFont, Segoe UI, Roboto`) and monospace styling for hashes/addresses
- **AOT notes** -- `IsAotCompatible`, `EnableTrimAnalyzer`, `EnableSingleFileAnalyzer`, and `EnableAotAnalyzer` are all set to `false` since Blazor WASM uses reflection internally

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.AspNetCore.Components.WebAssembly` | Blazor WASM runtime |
| `Microsoft.AspNetCore.Components.WebAssembly.DevServer` | Dev server (private assets) |
| `System.Text.Json` | AOT-friendly JSON serialization |
