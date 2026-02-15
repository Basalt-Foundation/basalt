# Basalt.Api.Grpc

gRPC API for the Basalt blockchain node. Provides high-performance binary RPC access for programmatic clients, wallets, and inter-service communication. Includes a server-streaming endpoint for real-time block subscriptions.

## Service Definition

The `BasaltNode` gRPC service is defined in `Protos/basalt.proto` (package `basalt`, C# namespace `Basalt.Api.Grpc`) and exposes five RPCs:

| RPC | Type | Description |
|-----|------|-------------|
| `GetStatus` | Unary | Returns current block height, latest block hash, mempool size, and protocol version. |
| `GetBlock` | Unary | Retrieves a block by number or hash (oneof `identifier`). Returns `NOT_FOUND` if the block does not exist. |
| `GetAccount` | Unary | Looks up an account by hex address. Returns balance, nonce, and account type. Returns `INVALID_ARGUMENT` for malformed addresses, `NOT_FOUND` for unknown accounts. |
| `SubmitTransaction` | Unary | Submits a fully signed transaction. Validates via `TransactionValidator`, then adds to the `Mempool`. Returns the transaction hash and `"pending"` status on success. Returns `INVALID_ARGUMENT` for validation failures, `ALREADY_EXISTS` if the mempool rejects the transaction, or `INTERNAL` for unexpected errors. |
| `SubscribeBlocks` | Server streaming | Streams `BlockReply` messages as new blocks are finalized. Sends the current latest block immediately, then polls every 200 ms for new blocks until the client disconnects. |

## Proto Messages

**Requests:**

- `GetStatusRequest` -- empty message.
- `GetBlockRequest` -- contains a `oneof identifier { uint64 number; string hash; }`.
- `GetAccountRequest` -- contains a `string address`.
- `SubmitTransactionRequest` -- contains `uint32 type`, `uint64 nonce`, `string sender`, `string to`, `string value`, `uint64 gas_limit`, `string gas_price`, `bytes data`, `uint32 priority`, `uint32 chain_id`, `bytes signature`, `bytes sender_public_key`.
- `SubscribeBlocksRequest` -- empty message.

**Replies:**

- `StatusReply` -- `uint64 block_height`, `string latest_block_hash`, `int32 mempool_size`, `uint32 protocol_version`.
- `BlockReply` -- `uint64 number`, `string hash`, `string parent_hash`, `string state_root`, `int64 timestamp`, `string proposer`, `uint64 gas_used`, `uint64 gas_limit`, `int32 transaction_count`.
- `AccountReply` -- `string address`, `string balance`, `uint64 nonce`, `string account_type`.
- `TransactionReply` -- `string hash`, `string status`.

## Implementation

`BasaltNodeService` extends the generated `BasaltNode.BasaltNodeBase` and is wired via constructor injection with four dependencies:

- `ChainManager` -- block retrieval and latest block tracking.
- `Mempool` -- transaction pool.
- `TransactionValidator` -- signature and state validation.
- `IStateDatabase` -- account state lookups (from `Basalt.Storage`, resolved transitively).

### Registration

Register the gRPC service in your ASP.NET `Program.cs`:

```csharp
builder.Services.AddGrpc();
// ...
app.MapGrpcService<BasaltNodeService>();
```

## Dependencies

| Package / Project | Purpose |
|-------------------|---------|
| `Basalt.Core` | Hash256, Address, UInt256, Signature, PublicKey, Transaction, Block |
| `Basalt.Codec` | Binary serialization |
| `Basalt.Execution` | ChainManager, Mempool, TransactionValidator |
| `Basalt.Storage` | IStateDatabase (transitive via Execution) |
| `Grpc.AspNetCore` | gRPC server hosting |
| `Grpc.Tools` | Proto compilation (build-time only, PrivateAssets="All") |
| `Google.Protobuf` | Protocol Buffers runtime |
