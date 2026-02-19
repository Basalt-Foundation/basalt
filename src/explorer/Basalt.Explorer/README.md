# Basalt.Explorer

Blazor WebAssembly block explorer for the Basalt blockchain. A full-featured, responsive web UI for browsing blocks, viewing transactions, inspecting accounts and contracts, monitoring validators, and interacting with the network in real time via WebSocket streaming.

## Features

- **Dashboard** -- Live network status with WebSocket real-time updates (LIVE/POLLING indicator), block height, mempool size, protocol version, latest block hash, latest block details, and recent transactions table
- **Block List** -- Paginated, sortable block list with gas utilization bars
- **Block Detail** -- Full header fields, gas usage bar, base fee (EIP-1559), and transaction list
- **Transaction List** -- Recent transactions with type badges, from/to addresses, and value
- **Transaction Detail** -- 4-card layout: Overview, Type-Specific Details (transfer flow visualization, deploy/call data), Gas & Technical (EIP-1559 fields: MaxFeePerGas, MaxPriorityFeePerGas), and Execution Receipt (status badge, gas used, effective gas price, error codes, event logs)
- **Validators** -- Column sorting (address, stake, status) with sort indicators, total/self/delegated stake breakdown
- **Account Detail** -- Account info, contract details (code size, deployer, deploy tx), interactive storage explorer, transaction history with direction badges (IN/OUT), receipt status badges, and method name decoding for contract calls
- **Staking Pools** -- Pool list with operator, total stake, and total rewards
- **Mempool** -- Live mempool view with validation diagnostics
- **Faucet** -- Request test tokens with cooldown timer, toast feedback, and transaction link
- **Network Stats** -- Prometheus metrics with auto-refresh, sparkline charts for TPS trend, gas utilization, and mempool size
- **Search** -- Contextual search suggestions (block/account/tx) with recent search history via localStorage
- **Dark/Light Theme** -- Toggle with localStorage persistence and flash-of-wrong-theme prevention via inline script
- **Responsive Design** -- 3 breakpoints (1024px, 768px, 480px), hamburger menu, tables convert to card layout on mobile via `data-label` attributes
- **Toast Notifications** -- Info/success/warning/error levels with auto-dismiss and slide animations
- **SVG Mini Charts** -- Zero-dependency sparkline components for inline metric visualization

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
| `/` | Dashboard with live WebSocket block updates |
| `/blocks` | Paginated block list with gas utilization bars |
| `/block/{Id}` | Block detail with transactions and base fee |
| `/transactions` | Recent transactions list with type badges |
| `/tx/{Hash}` | Transaction detail with execution receipt |
| `/validators` | Sortable validator list with stake breakdown |
| `/account/{Address}` | Account/contract detail with storage explorer |
| `/pools` | Staking pools list |
| `/mempool` | Live mempool diagnostics |
| `/faucet` | Testnet token faucet |
| `/stats` | Network statistics with sparkline charts |

## Architecture

### API Client

**BasaltApiClient** -- HTTP client wrapping the REST API with 16 methods:

- `GetStatusAsync()` -- `/v1/status`
- `GetLatestBlockAsync()` -- `/v1/blocks/latest`
- `GetBlockAsync(id)` -- `/v1/blocks/{id}`
- `GetAccountAsync(address)` -- `/v1/accounts/{address}`
- `GetRecentTransactionsAsync()` -- fetches latest block, then `/v1/blocks/{number}/transactions`
- `GetTransactionAsync(hash)` -- `/v1/transactions/{hash}`
- `GetValidatorsAsync()` -- `/v1/validators`
- `GetContractInfoAsync(address)` -- `/v1/contracts/{address}`
- `ReadContractStorageAsync(address, key)` -- `/v1/contracts/{address}/storage?key=`
- `GetReceiptAsync(hash)` -- `/v1/receipts/{hash}`
- `RequestFaucetAsync(address)` -- `/v1/faucet`
- `GetFaucetStatusAsync()` -- `/v1/faucet/status`
- `CallContractAsync(address, data)` -- `/v1/contracts/{address}/call`
- `GetPoolsAsync()` -- `/v1/pools`
- `GetMempoolAsync()` -- `/v1/mempool`
- `GetMetricsRawAsync()` -- `/metrics` (Prometheus)

### Formatting

**FormatHelper** -- Formatting and display utilities:

- `FormatBslt(rawValue)` -- converts raw base-unit strings to human-readable BSLT
- `GetTxTypeBadgeClass(type)` / `GetTxTypeLabel(type)` -- transaction type badge styling
- `DecodeMethodName(txData)` -- decodes 4-byte BLAKE3 method selectors to human-readable names
- `FormatMethodBadge(txData)` -- formatted method label for contract call transactions
- `TruncateHash(hash)` / `TruncateData(hex)` / `FormatBytes(bytes)` -- display helpers
- `FormatUptime(seconds)` -- human-readable uptime string
- `GetReceiptStatusBadgeClass(status)` / `GetReceiptStatusLabel(status)` -- receipt status badge styling
- `ParsePrometheusMetrics(raw)` -- parses Prometheus text format into key-value pairs

### Serialization

**ExplorerJsonContext** -- Source-generated `JsonSerializerContext` for AOT-compatible JSON deserialization of all 20 DTO types.

### DTOs

`NodeStatusDto`, `BlockDto` (with `BaseFee`), `TransactionDto` (with `MaxFeePerGas`, `MaxPriorityFeePerGas`, `GasUsed`, `Success`, `ErrorCode`, `EffectiveGasPrice`, `Logs`, `ComplianceProofCount`), `ValidatorDto`, `AccountDto`, `PoolDto`, `MempoolResponseDto`, `MempoolTransactionDto`, `FaucetStatusDto`, `ContractInfoDto`, `StorageReadResponseDto`, `LogDto`, `ReceiptDto`, `FaucetRequestDto`, `FaucetResponseDto`, `ContractCallRequestDto`, `ContractCallResponseDto`, `WebSocketEnvelopeDto`, `WebSocketBlockEvent`

All DTOs use `[JsonPropertyName]` attributes for explicit JSON mapping.

### Components

- **LoadingSkeleton** -- Shimmer animation placeholder for loading states
- **ToastContainer** -- Renders stacked toast notifications with slide animations
- **MiniChart** -- SVG sparkline component for inline metric visualization (zero external dependencies)

### Services

- **ToastService** -- Scoped, event-based notification service supporting info/success/warning/error levels with configurable auto-dismiss
- **BlockWebSocketService** -- `ClientWebSocket` wrapper with automatic reconnection for streaming block events to the dashboard

### Layout and CSS

- **MainLayout** -- Hamburger menu for mobile, theme toggle (sun/moon icons), search bar with contextual suggestions and recent search history, toast container
- **CSS** -- Design tokens, dark/light theme via `[data-theme]` attribute, 3 responsive breakpoints (1024px, 768px, 480px), mobile table-to-card conversion using `data-label` attributes
- **Theme persistence** -- localStorage-backed with an inline `<script>` to prevent flash of wrong theme on initial load

### AOT Notes

`IsAotCompatible`, `EnableTrimAnalyzer`, `EnableSingleFileAnalyzer`, and `EnableAotAnalyzer` are all set to `false` since Blazor WASM uses reflection internally and these analyzers produce false positives in the Blazor context.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.AspNetCore.Components.WebAssembly` | Blazor WASM runtime |
| `Microsoft.AspNetCore.Components.WebAssembly.DevServer` | Dev server (private assets) |
| `System.Text.Json` | AOT-friendly JSON serialization |
