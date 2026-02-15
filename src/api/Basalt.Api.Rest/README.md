# Basalt.Api.Rest

RESTful HTTP API for the Basalt blockchain node. Provides endpoints for submitting transactions, querying blocks and accounts, a rate-limited faucet, Prometheus metrics, and real-time WebSocket block notifications. Built on ASP.NET Minimal APIs with AOT-compatible source-generated JSON serialization.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/v1/transactions` | Submit a signed transaction |
| `GET` | `/v1/blocks/latest` | Get the latest block |
| `GET` | `/v1/blocks/{id}` | Get block by number or hash |
| `GET` | `/v1/accounts/{address}` | Get account balance and nonce |
| `GET` | `/v1/status` | Node status (height, mempool, version) |
| `POST` | `/v1/faucet` | Request test tokens (rate-limited) |
| `GET` | `/metrics` | Prometheus-format metrics |
| `WS` | `/ws/blocks` | Real-time block notifications |

## Usage

### Setup

```csharp
var app = WebApplication.Create();

RestApiEndpoints.MapBasaltEndpoints(app, chainManager, mempool, validator, stateDb);
MetricsEndpoint.MapMetricsEndpoint(app, chainManager, mempool);
FaucetEndpoint.MapFaucetEndpoint(app, stateDb);
app.MapWebSocketEndpoint(webSocketHandler);
```

### Submit a Transaction

```bash
curl -X POST http://localhost:5000/v1/transactions \
  -H "Content-Type: application/json" \
  -d '{
    "type": 0,
    "nonce": 0,
    "sender": "0x...",
    "to": "0x...",
    "value": "1000",
    "gasLimit": 21000,
    "gasPrice": "1",
    "data": "0x...",
    "priority": 0,
    "chainId": 31337,
    "signature": "0x...",
    "senderPublicKey": "0x..."
  }'
```

The `priority` field (byte, 0-255) controls transaction ordering in the mempool. The `data` field is optional and accepts hex strings with or without a `0x` prefix.

On success returns `{"hash":"0x...","status":"pending"}`. On failure returns `{"code":<int>,"message":"..."}`.

### Query Status

```bash
curl http://localhost:5000/v1/status
# {"blockHeight":42,"latestBlockHash":"0x...","mempoolSize":3,"protocolVersion":1}
```

### Faucet

```bash
curl -X POST http://localhost:5000/v1/faucet \
  -H "Content-Type: application/json" \
  -d '{"address":"0x..."}'
```

The faucet directly debits a configurable faucet address and credits the recipient in the state database. Configurable via static properties on `FaucetEndpoint`:

- `DripAmount` -- amount in base units (default: 100 BSLT).
- `CooldownSeconds` -- per-address cooldown (default: 60 seconds).
- `FaucetAddress` -- source address (default: `Address.Zero`).

Returns `{"success":true,"message":"Sent 100 BSLT to 0x...","txHash":"0x0000..."}` on success. The `txHash` field is a placeholder (`Hash256.Zero`) since the faucet modifies state directly rather than creating a transaction.

### WebSocket

Connect to `/ws/blocks` for real-time block notifications. Non-WebSocket requests to this path receive a 400 response.

On initial connection, the server sends the current latest block as a `current_block` message:

```json
{"type":"current_block","block":{"number":42,"hash":"0x...","parentHash":"0x...","stateRoot":"0x...","timestamp":1700000000,"proposer":"0x...","gasUsed":0,"gasLimit":10000000,"transactionCount":0}}
```

Subsequent blocks are pushed as `new_block` messages via `WebSocketHandler.BroadcastNewBlock(block)`:

```json
{"type":"new_block","block":{"number":43,"hash":"0x...","parentHash":"0x...","stateRoot":"0x...","timestamp":1700000001,"proposer":"0x...","gasUsed":21000,"gasLimit":10000000,"transactionCount":1}}
```

The `WebSocketHandler` class manages all active connections, automatically cleaning up disconnected clients during broadcast. It exposes a `ConnectionCount` property for monitoring.

### Prometheus Metrics

```
GET /metrics

# HELP basalt_block_height Current block height.
basalt_block_height 42
# HELP basalt_tps Current transactions per second.
basalt_tps 150.50
# HELP basalt_transactions_total Total transactions processed.
basalt_transactions_total 5000
# HELP basalt_mempool_size Number of pending transactions in mempool.
basalt_mempool_size 3
# HELP basalt_block_gas_used Gas used in last block.
basalt_block_gas_used 63000
# HELP basalt_block_gas_limit Gas limit of last block.
basalt_block_gas_limit 10000000
# HELP basalt_block_tx_count Transactions in last block.
basalt_block_tx_count 3
# HELP basalt_uptime_seconds Node uptime in seconds.
basalt_uptime_seconds 3600
```

TPS is calculated per-block via `MetricsEndpoint.RecordBlock(txCount, timestampMs)`, which should be called by the node when a block is produced. Gas and transaction count metrics for the last block are only emitted when a latest block exists.

## JSON Serialization

Two AOT-compatible source-generated `JsonSerializerContext` classes are defined:

- `BasaltApiJsonContext` -- covers `TransactionRequest`, `TransactionResponse`, `BlockResponse`, `AccountResponse`, `StatusResponse`, `ErrorResponse`, `FaucetRequest`, `FaucetResponse`.
- `WsJsonContext` -- covers `WebSocketBlockMessage`, `WebSocketBlockData`.

## Dependencies

| Package / Project | Purpose |
|-------------------|---------|
| `Basalt.Core` | Hash256, Address, UInt256, Block, Transaction |
| `Basalt.Codec` | Binary serialization |
| `Basalt.Execution` | ChainManager, Mempool, TransactionValidator |
| `Basalt.Storage` | IStateDatabase for account queries |
| `System.Text.Json` | JSON serialization with source generators |
| `Microsoft.AspNetCore.App` | ASP.NET Minimal APIs framework |
