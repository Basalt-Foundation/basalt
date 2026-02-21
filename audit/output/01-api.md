# API Layer Audit Report

## Executive Summary

The API layer (REST, gRPC, GraphQL) demonstrates solid security fundamentals including input validation, state forking for read-only calls, AOT-safe serialization, and connection limits. No critical vulnerabilities were found. The primary concerns are: several endpoints perform unbounded or O(n) chain scans that can be abused for DoS, the gRPC surface is missing EIP-1559 fields creating data inconsistency, and the gRPC `SubmitTransaction` lacks the signature/public-key length validation present in REST. Test coverage is minimal, with zero tests for REST endpoints, gRPC, WebSocket, faucet, and metrics.

---

## Critical Issues

No critical issues found.

---

## High Severity

### H-1: `GET /v1/transactions/recent` Scans Entire Chain (DoS)

- **Location**: `src/api/Basalt.Api.Rest/RestApiEndpoints.cs:299`
- **Description**: The loop `for (ulong i = 0; i <= latestNum && ...)` iterates from the latest block all the way back to genesis with **no scan depth cap**. Other similar endpoints (`/v1/transactions/{hash}`, `/v1/accounts/{address}/transactions`, `LookupReceipt`) cap at 1,000 blocks; this one does not.
- **Impact**: On a chain with millions of blocks but sparse transactions, a single request triggers an O(chain_height) scan. An attacker can repeatedly call this endpoint to exhaust CPU.
- **Recommendation**: Add a `scanDepth` cap identical to the other scan endpoints:
  ```csharp
  var scanDepth = Math.Min(latestNum + 1, 1000UL);
  for (ulong i = 0; i < scanDepth && transactions.Count < maxTxs; i++)
  ```
- **Severity**: High

### H-2: gRPC `SubmitTransaction` Does Not Validate Signature/PublicKey Byte Lengths

- **Location**: `src/api/Basalt.Api.Grpc/BasaltNodeService.cs:96-97`
- **Description**: The gRPC endpoint constructs `new Signature(request.Signature.ToByteArray())` and `new PublicKey(request.SenderPublicKey.ToByteArray())` without checking that the byte arrays are exactly 64 and 32 bytes respectively. The REST equivalent (`RestApiEndpoints.cs:752-760`) performs explicit length validation.
- **Impact**: Malformed signature/public-key data could cause unexpected behavior in downstream Ed25519 verification, potential panics, or buffer-related issues depending on how the `Signature` and `PublicKey` constructors handle wrong-sized input.
- **Recommendation**: Add length validation before constructing the domain types:
  ```csharp
  if (request.Signature.Length != 64)
      throw new RpcException(new Status(StatusCode.InvalidArgument, "Signature must be exactly 64 bytes"));
  if (request.SenderPublicKey.Length != 32)
      throw new RpcException(new Status(StatusCode.InvalidArgument, "SenderPublicKey must be exactly 32 bytes"));
  ```
- **Severity**: High

### H-3: gRPC `SubscribeBlocks` Has No Connection Limit

- **Location**: `src/api/Basalt.Api.Grpc/BasaltNodeService.cs:127-157`
- **Description**: The server-streaming `SubscribeBlocks` RPC polls every 200ms indefinitely with no limit on concurrent subscriptions. The WebSocket handler properly caps at 1,000 connections (`WebSocketEndpoint.cs:24`), but gRPC has no equivalent guard.
- **Impact**: An attacker can open thousands of streaming subscriptions, each consuming a server thread and polling the chain every 200ms, leading to CPU and memory exhaustion.
- **Recommendation**: Add an `AtomicInteger` connection counter with a max limit, rejecting new subscriptions with `StatusCode.ResourceExhausted` when the limit is reached. Alternatively, use the gRPC middleware-level concurrency limiter.
- **Severity**: High

### H-4: Multiple Chain-Scan Endpoints Are Expensive Per Request

- **Location**: `src/api/Basalt.Api.Rest/RestApiEndpoints.cs:217-227` (account txs), `322-336` (tx by hash), `38-72` (LookupReceipt), `488-507` (contract deployer scan)
- **Description**: Several endpoints scan up to 1,000 blocks (or 5,000 for contract info) backward through the chain, iterating every transaction in each block. With no rate limiting, caching, or request timeout, these are O(blocks * txs_per_block) per request.
- **Impact**: Targeted abuse of these endpoints under load can degrade node performance. The contract-info scan at 5,000 blocks is particularly expensive.
- **Recommendation**: Consider (a) adding an index for tx-hash and address-based lookups, (b) implementing per-IP rate limiting on expensive endpoints, or (c) adding request-scoped `CancellationToken` timeouts.
- **Severity**: High

---

## Medium Severity

### M-1: Faucet Rate Limit Bypass via 0x Prefix Toggle

- **Location**: `src/api/Basalt.Api.Rest/FaucetEndpoint.cs:83`
- **Description**: The rate-limit key is `request.Address.ToUpperInvariant()`, which normalizes case. However, the `0x` prefix is not stripped before normalization. Sending `"0xABC...123"` and `"ABC...123"` would hash to different rate-limit keys for the same address.
- **Impact**: An attacker can double the faucet drip rate by alternating the `0x` prefix.
- **Recommendation**: Strip the `0x` prefix before normalizing:
  ```csharp
  var normalized = request.Address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? request.Address[2..] : request.Address;
  var addrKey = normalized.ToUpperInvariant();
  ```
- **Severity**: Medium

### M-2: Missing `BaseFee` Field in gRPC `BlockReply` (Data Inconsistency)

- **Location**: `src/api/Basalt.Api.Grpc/Protos/basalt.proto:40-50`
- **Description**: The `BlockReply` proto message has no `base_fee` field. REST (`BlockResponse.BaseFee`, line 799) and GraphQL (`BlockResult.BaseFee`) both include it.
- **Impact**: gRPC clients cannot see EIP-1559 base fees, creating an inconsistent API surface. This also affects the `SubscribeBlocks` streaming output.
- **Recommendation**: Add `string base_fee = 10;` to `BlockReply` and populate it in `ToBlockReply()`.
- **Severity**: Medium

### M-3: Missing EIP-1559 Fields in gRPC `SubmitTransactionRequest`

- **Location**: `src/api/Basalt.Api.Grpc/Protos/basalt.proto:63-76`
- **Description**: The `SubmitTransactionRequest` message has no `max_fee_per_gas` or `max_priority_fee_per_gas` fields. REST and GraphQL both support these fields. Transactions submitted via gRPC will always have these set to `UInt256.Zero`.
- **Impact**: gRPC clients cannot submit EIP-1559 transactions, making them pay legacy gas pricing.
- **Recommendation**: Add `string max_fee_per_gas = 13;` and `string max_priority_fee_per_gas = 14;` to the proto and wire them in `BasaltNodeService.SubmitTransaction`.
- **Severity**: Medium

### M-4: No GraphQL Complexity/Cost Analysis

- **Location**: `src/api/Basalt.Api.GraphQL/GraphQLSetup.cs:15-16`
- **Description**: Only `AddMaxExecutionDepthRule(10)` and a 10-second execution timeout are configured. There is no query complexity/cost analysis. A query like `{ blocks(last: 100) { hash parentHash stateRoot ... } }` with all fields is permitted and triggers 100 `GetBlockByNumber` calls.
- **Impact**: Wide queries (many fields, max pagination) can be expensive even without deep nesting.
- **Recommendation**: Add `AddMaxComplexityRule(200)` or similar cost-based limiting. HotChocolate supports `IQueryComplexityAnalyzer`.
- **Severity**: Medium

### M-5: WebSocket Initial Message Has No Send Timeout

- **Location**: `src/api/Basalt.Api.Rest/WebSocketEndpoint.cs:172`
- **Description**: `BroadcastToSingle` calls `ws.SendAsync(bytes, ..., CancellationToken.None)` with no timeout for the initial "current_block" message sent on connection. The broadcast path correctly uses a 5-second timeout (`SendWithTimeout`, line 89-101).
- **Impact**: A malicious slow client could stall during connection setup, tying up a server thread.
- **Recommendation**: Use the same `BroadcastTimeout` with a `CancellationTokenSource` for the initial send:
  ```csharp
  using var cts = new CancellationTokenSource(BroadcastTimeout);
  await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token);
  ```
- **Severity**: Medium

### M-6: GraphQL `GetBlocks` Excludes Genesis Block

- **Location**: `src/api/Basalt.Api.GraphQL/Query.cs:205`
- **Description**: The loop condition `for (ulong i = latest.Number; i > 0 && ...)` stops at block 1, never including block 0 (genesis). REST `/v1/blocks` pagination (`RestApiEndpoints.cs:256`) does include block 0.
- **Impact**: Data inconsistency between API surfaces. Clients using GraphQL cannot retrieve the genesis block via `getBlocks`.
- **Recommendation**: Change the condition to `i > 0` with a post-loop check, or use `i >= 0` with a `ulong` underflow guard (e.g., decrement inside the loop body).
- **Severity**: Medium

### M-7: `IL2026`/`IL3050` Warnings Suppressed Project-Wide

- **Location**: `src/api/Basalt.Api.Rest/Basalt.Api.Rest.csproj:6`
- **Description**: `<NoWarn>$(NoWarn);IL2026;IL3050</NoWarn>` suppresses AOT trim/dynamic-code warnings across the entire project. While the comment explains this is for minimal API delegate overloads, it also masks any genuine AOT issues in the DTO serialization code or other areas.
- **Impact**: Real AOT problems could go undetected until runtime in a native-AOT deployment.
- **Recommendation**: Remove the project-wide suppression and use targeted `#pragma warning disable IL2026, IL3050` only around the `MapGet`/`MapPost` registration calls in the endpoint setup methods.
- **Severity**: Medium

### M-8: Faucet Status Endpoint Exposes Internal State

- **Location**: `src/api/Basalt.Api.Rest/FaucetEndpoint.cs:194-206`
- **Description**: `GET /v1/faucet/status` exposes `pendingNonce`, `nonceInitialized`, and `mempoolSize` with no authentication.
- **Impact**: Low-risk information disclosure. The `pendingNonce` reveals the internal nonce tracking state, which could help an attacker time faucet abuse attempts.
- **Recommendation**: Either gate behind `BASALT_DEBUG` like the mempool debug endpoint, or remove `pendingNonce`/`nonceInitialized` from the response.
- **Severity**: Medium

### M-9: No Request Body Size Limits on Transaction/Call Endpoints

- **Location**: `src/api/Basalt.Api.Rest/RestApiEndpoints.cs:110` (POST /v1/transactions), `362` (POST /v1/call)
- **Description**: The `data` field in `TransactionRequest` and `CallRequest` accepts arbitrarily large hex strings, bounded only by ASP.NET's default 30MB request body limit. Similarly, `basalt.proto` `bytes data = 8` has no `max_size`.
- **Impact**: A single request with a multi-megabyte `data` payload causes memory pressure and slow hex decoding.
- **Recommendation**: Add explicit `Data` length validation (e.g., max 128KB decoded) in `ToTransaction()` and the call endpoint. For gRPC, set `MaxReceiveMessageSize` in server options.
- **Severity**: Medium

---

## Low Severity / Recommendations

### L-1: Generic `catch (Exception)` Blocks Without Logging

- **Location**: `src/api/Basalt.Api.Rest/RestApiEndpoints.cs:140, 438, 519, 633`
- **Description**: Four catch blocks swallow all exceptions and return a generic "Internal error" or "Transaction submission failed" with no logging. The faucet endpoint (line 165) demonstrates the correct pattern with `_logger?.LogWarning(...)`.
- **Impact**: Debugging production issues is significantly harder when errors are silently swallowed.
- **Recommendation**: Add `ILogger` parameter to `MapBasaltEndpoints` and log caught exceptions at `Warning` or `Error` level.
- **Severity**: Low

### L-2: `GET /v1/pools` Directly Reads Contract Storage With Hardcoded Tags

- **Location**: `src/api/Basalt.Api.Rest/RestApiEndpoints.cs:644-692`
- **Description**: The pools endpoint reads raw contract storage with hardcoded tag bytes (`0x01` for ulong, `0x07` for string, `0x0A` for UInt256) and key prefixes (`sp_next`, `sp_ops:`, `sp_total:`, `sp_rewards:`). This creates tight coupling to the `StakingPool` contract's internal storage format.
- **Impact**: If the contract storage schema changes, this endpoint silently returns incorrect data.
- **Recommendation**: Use the read-only contract call path (`/v1/call`) instead, calling a dedicated view method on the StakingPool contract.
- **Severity**: Low

### L-3: Faucet Nonce Race Condition Window

- **Location**: `src/api/Basalt.Api.Rest/FaucetEndpoint.cs:127-177`
- **Description**: The nonce is read at line 128-137 under lock, but the lock is released before `mempool.Add()` (line 163) and re-acquired for increment (line 174). If two requests for different addresses arrive simultaneously (bypassing the rate limiter), both could get the same nonce. One succeeds in mempool, the other is rejected.
- **Impact**: Low in practice due to rate limiting, but the rejected request returns a misleading "Transaction rejected by mempool" error instead of retrying with an incremented nonce.
- **Recommendation**: Hold the nonce lock across the entire create-sign-submit sequence, or implement a retry loop with nonce increment on mempool rejection.
- **Severity**: Low

### L-4: Inconsistent `0x` Prefix Handling Across Endpoints

- **Location**: `src/api/Basalt.Api.Rest/RestApiEndpoints.cs:313` vs `165`
- **Description**: `GET /v1/transactions/{hash}` explicitly strips the `0x` prefix (line 313-314) before calling `TryFromHexString`. `GET /v1/blocks/{id}` at line 165 passes the raw string to `Hash256.TryFromHexString` without stripping. Whether `TryFromHexString` handles `0x` internally is implementation-dependent.
- **Impact**: Potential inconsistency where some endpoints accept `0x`-prefixed hashes and others don't.
- **Recommendation**: Normalize `0x` prefix stripping consistently across all endpoints, or verify that all `TryFromHexString` methods handle both forms.
- **Severity**: Low

### L-5: `WsJsonContext` Registration Is Minimal

- **Location**: `src/api/Basalt.Api.Rest/WebSocketEndpoint.cs:231-233`
- **Description**: `WsJsonContext` only registers `WebSocketBlockMessage` and `WebSocketBlockData`. This is currently correct since those are the only WS payloads. However, adding new WebSocket message types without updating the context will cause runtime serialization failures in AOT mode.
- **Impact**: Maintenance risk for future development.
- **Recommendation**: Add a code comment noting that any new WS message types must be registered here.
- **Severity**: Low

### L-6: GraphQL `GetReceipt` and `GetTransaction` Duplicate Receipt-Scanning Logic

- **Location**: `src/api/Basalt.Api.GraphQL/Query.cs:10-79` and `81-164`
- **Description**: Both methods contain nearly identical receipt-scanning code with the 1,000-block fallback. The REST layer has a shared `LookupReceipt` helper and a `GetReceiptForTx` helper; the GraphQL layer duplicates this logic inline.
- **Impact**: Code duplication increases maintenance burden and risk of divergent behavior.
- **Recommendation**: Extract shared receipt-lookup logic into a service class that both REST and GraphQL can use.
- **Severity**: Low

---

## Test Coverage Gaps

| Gap | Description | Priority |
|-----|-------------|----------|
| **No REST endpoint tests** | All 31 tests call GraphQL resolvers/DTOs directly. Zero HTTP integration tests for any REST endpoint. | High |
| **No gRPC service tests** | `BasaltNodeService` has zero test coverage. | High |
| **No WebSocket tests** | Connection lifecycle, broadcast, max connection rejection, slow client timeout, dispose — all untested. | High |
| **No faucet tests** | Rate limiting, nonce management, balance checks, concurrent requests — all untested. | High |
| **No metrics tests** | `RecordBlock()`, TPS calculation, Prometheus output format — all untested. | Medium |
| **No negative input validation tests** | Oversized `data` payloads, malformed hex strings, boundary pagination values (`page=0`, `count=-1`) — untested. | Medium |
| **No concurrent access tests** | Race conditions in mempool submission, faucet nonce handling, WebSocket broadcast — untested. | Medium |
| **No contract call/storage read tests** | `POST /v1/call` and `GET /v1/contracts/{address}/storage` — untested. | Medium |
| **No receipt lookup tests through API** | `GET /v1/receipts/{hash}` and GraphQL `getReceipt` — untested. | Low |
| **No error response format tests** | Verifying `ErrorResponse` JSON structure, HTTP status codes, gRPC status code mapping — untested. | Low |

---

## Positive Findings

1. **Consistent input validation**: All endpoints validate addresses and hashes using `TryFromHexString` before use, returning structured `400 Bad Request` errors on failure.
2. **State fork for read-only calls**: `POST /v1/call` and `GET /v1/contracts/{address}/storage` fork the state database (`stateDb.Fork()`) to prevent read-only queries from mutating canonical state. This is a strong safety pattern.
3. **Faucet rate limiting with eviction**: Per-address cooldown with stale entry eviction at 100,000 entries prevents unbounded memory growth.
4. **WebSocket connection limits**: `MaxConnections = 1000` with per-client 5-second broadcast timeouts prevents slow-client blocking.
5. **Debug endpoint isolation**: `GET /v1/debug/mempool` is gated behind `BASALT_DEBUG=1` environment variable.
6. **AOT-safe serialization**: Both `BasaltApiJsonContext` and `WsJsonContext` use source-generated `JsonSerializerContext` covering all DTO types.
7. **Structured error responses**: All REST endpoints return consistent `ErrorResponse { Code, Message }` objects with appropriate HTTP status codes.
8. **EIP-1559 data in REST/GraphQL**: Block responses include `BaseFee`, transaction responses include `MaxFeePerGas`, `MaxPriorityFeePerGas`, and `EffectiveGasPrice`.
9. **GraphQL depth + timeout limits**: `AddMaxExecutionDepthRule(10)` and 10-second execution timeout are configured.
10. **Thread-safe metrics**: All `MetricsEndpoint` shared fields use `Interlocked` operations.
11. **gRPC enum validation**: `BasaltNodeService.SubmitTransaction` validates the `TransactionType` enum with `Enum.IsDefined()` (line 81).
12. **Proper gRPC exception handling**: The `catch (RpcException) { throw; }` pattern at line 117-119 preserves specific gRPC error codes while catching other exceptions as `Internal`.
