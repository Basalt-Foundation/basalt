# Basalt.Api.GraphQL

GraphQL API for the Basalt blockchain node. Provides a flexible query interface for block explorers, dashboards, and data analysis tools. Built on HotChocolate 14.3.0.

## Schema

### Queries

Five query resolvers are available on the `Query` type:

| Field | Arguments | Return Type | Description |
|-------|-----------|-------------|-------------|
| `getStatus` | -- | `StatusResult` | Current block height, latest block hash, mempool size, and protocol version. Injects `ChainManager` and `Mempool`. |
| `getBlock` | `id: String!` | `BlockResult?` | Retrieves a block by number (parsed as `ulong`) or by hex hash string. Returns `null` if not found. Injects `ChainManager`. |
| `getLatestBlock` | -- | `BlockResult?` | Returns the most recently finalized block, or `null` if the chain is empty. Injects `ChainManager`. |
| `getBlocks` | `last: Int!` | `[BlockResult]` | Returns up to `last` recent blocks (capped at 100), ordered from newest to oldest. Injects `ChainManager`. |
| `getAccount` | `address: String!` | `AccountResult?` | Looks up an account by hex address. Returns `null` for invalid addresses or unknown accounts. Injects `IStateDatabase`. |

### Mutations

| Field | Arguments | Return Type | Description |
|-------|-----------|-------------|-------------|
| `submitTransaction` | `input: TransactionInput!` | `TransactionResult` | Submits a signed transaction. Validates via `TransactionValidator`, adds to `Mempool`. Returns success/failure with hash or error message. Injects `ChainManager`, `Mempool`, `TransactionValidator`, and `IStateDatabase`. |

## Result Types

**StatusResult** -- `blockHeight: UInt64`, `latestBlockHash: String`, `mempoolSize: Int`, `protocolVersion: UInt32`.

**BlockResult** -- `number: UInt64`, `hash: String`, `parentHash: String`, `stateRoot: String`, `timestamp: Int64`, `proposer: String`, `gasUsed: UInt64`, `gasLimit: UInt64`, `transactionCount: Int`. Constructed via the static `BlockResult.FromBlock(Block)` factory.

**AccountResult** -- `address: String`, `balance: String`, `nonce: UInt64`, `accountType: String`.

**TransactionResult** -- `success: Boolean`, `hash: String?`, `status: String?`, `errorMessage: String?`.

**TransactionInput** -- input object with fields: `type: Byte`, `nonce: UInt64`, `sender: String`, `to: String`, `value: String` (decimal or hex), `gasLimit: UInt64`, `gasPrice: String`, `data: String?` (hex, optional `0x` prefix), `priority: Byte`, `chainId: UInt32`, `signature: String` (hex), `senderPublicKey: String` (hex). Provides a `ToTransaction()` method that parses all fields into a `Transaction` instance.

## Setup

Two extension methods in `GraphQLSetup` handle registration:

```csharp
// In Program.cs or Startup.cs:
builder.Services.AddBasaltGraphQL();   // Registers HotChocolate server, Query, and Mutation types
// ...
app.MapBasaltGraphQL();                // Maps the /graphql endpoint
```

`AddBasaltGraphQL` calls `AddGraphQLServer().AddQueryType<Query>().AddMutationType<Mutation>()`.

`MapBasaltGraphQL` calls `MapGraphQL("/graphql")`.

## Dependencies

| Package / Project | Purpose |
|-------------------|---------|
| `Basalt.Core` | Hash256, Address, UInt256, Block, Transaction |
| `Basalt.Execution` | ChainManager, Mempool, TransactionValidator |
| `Basalt.Storage` | IStateDatabase |
| `HotChocolate.AspNetCore` | GraphQL server framework |
| `Microsoft.AspNetCore.App` | ASP.NET framework reference |
