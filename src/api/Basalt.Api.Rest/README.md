# Basalt.Api.Rest

RESTful HTTP API for the Basalt blockchain node. Provides endpoints for submitting transactions, querying blocks and accounts, a rate-limited faucet, Prometheus metrics, and real-time WebSocket block notifications. Built on ASP.NET Minimal APIs with AOT-compatible source-generated JSON serialization.

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/v1/transactions` | Submit a signed transaction |
| `GET` | `/v1/blocks/latest` | Get the latest block |
| `GET` | `/v1/blocks/{id}` | Get block by number or hash |
| `GET` | `/v1/blocks?page=&pageSize=` | Paginated block list |
| `GET` | `/v1/blocks/{number}/transactions` | Transactions in a block |
| `GET` | `/v1/transactions/recent?count=` | Recent transactions (default 50, max 200) |
| `GET` | `/v1/transactions/{hash}` | Get transaction by hash |
| `GET` | `/v1/accounts/{address}` | Get account balance and nonce |
| `GET` | `/v1/accounts/{address}/transactions?count=` | Account transaction history |
| `GET` | `/v1/status` | Node status (height, mempool, version) |
| `POST` | `/v1/call` | Read-only contract call (eth_call equivalent) |
| `GET` | `/v1/contracts/{address}` | Contract metadata (code size, deployer, deploy tx) |
| `GET` | `/v1/contracts/{address}/storage?key=` | Read contract storage by string key |
| `POST` | `/v1/faucet` | Request test tokens (rate-limited) |
| `GET` | `/v1/faucet/status` | Faucet address and balance |
| `GET` | `/v1/receipts/{hash}` | Get transaction receipt |
| `GET` | `/v1/pools` | Staking pools list |
| `GET` | `/v1/validators` | Validator list with stakes |
| `GET` | `/v1/debug/mempool` | Diagnostic mempool dump |
| `GET` | `/metrics` | Prometheus-format metrics |
| `WS` | `/ws/blocks` | Real-time block notifications |

## Usage

### Setup

```csharp
var app = WebApplication.Create();

var contractRuntime = new ManagedContractRuntime();
RestApiEndpoints.MapBasaltEndpoints(app, chainManager, mempool, validator, stateDb, contractRuntime);
MetricsEndpoint.MapMetricsEndpoint(app, chainManager, mempool);
FaucetEndpoint.MapFaucetEndpoint(app, stateDb);
app.MapWebSocketEndpoint(webSocketHandler);
```

The `contractRuntime` parameter enables the `POST /v1/call`, `GET /v1/contracts/{address}`, and `GET /v1/contracts/{address}/storage` endpoints. Pass `null` to disable contract-related endpoints.

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

### Read-Only Contract Call

```bash
curl -X POST http://localhost:5000/v1/call \
  -H "Content-Type: application/json" \
  -d '{
    "to": "0x...",
    "data": "5DB08A5F...",
    "from": "0x...",
    "gasLimit": 100000
  }'
# {"success":true,"returnData":"00000006626173616C74","gasUsed":900,"error":null}
```

Executes a contract call without modifying state. The `data` field is the ABI-encoded call data (4-byte BLAKE3 selector + arguments). The `from` and `gasLimit` fields are optional.

### Contract Info

```bash
curl http://localhost:5000/v1/contracts/0x...
# {"address":"0x...","codeSize":4,"codeHash":"0x...","deployer":"0x...","deployTxHash":"0x...","deployBlockNumber":5}
```

Returns contract metadata: code size, BLAKE3 code hash, deployer address, deploy transaction hash, and deploy block number. Scans up to 5000 recent blocks to find the deployment transaction.

### Storage Read

```bash
curl "http://localhost:5000/v1/contracts/0x.../storage?key=welcome"
# {"key":"welcome","keyHash":"0x...","found":true,"valueHex":"00000006626173616C74","valueUtf8":"basalt","valueSize":10,"gasUsed":900}
```

Reads a storage value by string key. The server BLAKE3-hashes the key to derive the 32-byte storage key, then executes a read-only `storage_get` call. Returns the value as both hex and decoded UTF-8 (when valid).

### Transaction Receipt

```bash
curl http://localhost:5000/v1/receipts/0x...
# {"transactionHash":"0x...","blockHash":"0x...","blockNumber":42,"transactionIndex":0,"from":"0x...","to":"0x...","gasUsed":21000,"success":true,"errorCode":"None","postStateRoot":"0x...","effectiveGasPrice":"1","logs":[]}
```

Returns the execution receipt for a confirmed transaction including gas used, success/failure status, error code, effective gas price (EIP-1559), and event logs.

Transaction responses now include receipt fields when available: `gasUsed`, `success`, `errorCode`, `effectiveGasPrice`, `logs`, `maxFeePerGas`, `maxPriorityFeePerGas`.

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

- `BasaltApiJsonContext` -- covers ~20 types including `TransactionRequest`, `TransactionResponse`, `BlockResponse`, `AccountResponse`, `StatusResponse`, `ErrorResponse`, `FaucetRequest`, `FaucetResponse`, `TransactionDetailResponse`, `PaginatedBlocksResponse`, `ValidatorInfoResponse`, `CallRequest`, `CallResponse`, `ContractInfoResponse`, `StorageReadResponse`, `ReceiptResponse`, `PoolInfoResponse`.
- `WsJsonContext` -- covers `WebSocketBlockMessage`, `WebSocketBlockData`.

## Dependencies

| Package / Project | Purpose |
|-------------------|---------|
| `Basalt.Core` | Hash256, Address, UInt256, Block, Transaction |
| `Basalt.Crypto` | BLAKE3 hashing (contract code hash, storage key derivation) |
| `Basalt.Codec` | Binary serialization |
| `Basalt.Execution` | ChainManager, Mempool, TransactionValidator, IContractRuntime |
| `Basalt.Storage` | IStateDatabase for account and storage queries |
| `System.Text.Json` | JSON serialization with source generators |
| `Microsoft.AspNetCore.App` | ASP.NET Minimal APIs framework |
