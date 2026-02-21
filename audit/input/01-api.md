# Basalt Security & Quality Audit — API Layer

## Scope

Audit the three API surface projects that expose the Basalt blockchain node to external consumers:

| Project | Path | Description |
|---|---|---|
| `Basalt.Api.Rest` | `src/api/Basalt.Api.Rest/` | Minimal API (ASP.NET) — REST endpoints, faucet, metrics, WebSocket |
| `Basalt.Api.Grpc` | `src/api/Basalt.Api.Grpc/` | gRPC service (`BasaltNodeService`) |
| `Basalt.Api.GraphQL` | `src/api/Basalt.Api.GraphQL/` | HotChocolate 14 GraphQL API |

Corresponding test project: `tests/Basalt.Api.Tests/`

---

## Files to Audit

### Basalt.Api.Rest
- `RestApiEndpoints.cs` (~1065 lines) — all REST route mappings and DTO types
- `FaucetEndpoint.cs` (~220 lines) — faucet request handling
- `MetricsEndpoint.cs` (~109 lines) — Prometheus-style metrics
- `WebSocketEndpoint.cs` (~233 lines) — live block streaming

### Basalt.Api.Grpc
- `BasaltNodeService.cs` (~174 lines) — gRPC service implementation
- `Protos/basalt.proto` — protobuf schema

### Basalt.Api.GraphQL
- `GraphQLSetup.cs` (~26 lines)
- `Query.cs` (~234 lines)
- `Mutation.cs` (~55 lines)
- `Types/ResultTypes.cs` (~142 lines)

---

## Audit Objectives

### 1. Input Validation & Injection
- Verify all user-supplied parameters (addresses, hashes, transaction data, block numbers, pagination offsets/limits) are validated before use.
- Check for potential injection vectors: malformed hex strings, oversized payloads, boundary values for pagination (`offset`, `limit`).
- Verify GraphQL query depth/complexity limits are configured to prevent DoS via deeply nested queries.
- Verify the faucet has rate-limiting and cannot be abused (amount draining, replay).

### 2. Authentication & Authorization
- Document which endpoints are public vs. should be restricted.
- Assess whether any endpoints expose internal state that should not be publicly available (e.g., validator private keys, mempool internals, configuration secrets).
- Check that the metrics endpoint does not leak sensitive operational data.

### 3. Error Handling & Information Disclosure
- Ensure error responses do not leak stack traces, internal paths, or implementation details.
- Verify all error paths return structured `ErrorResponse` objects with appropriate HTTP status codes.
- Check that gRPC status codes are correctly mapped.
- Ensure GraphQL errors do not expose resolver internals.

### 4. Serialization Safety
- Verify `BasaltApiJsonContext` and `WsJsonContext` are properly configured for AOT-safe `System.Text.Json` source generation.
- Check that `NoWarn IL2026;IL3050` suppressions in `Basalt.Api.Rest.csproj` are justified and do not mask real AOT issues.
- Verify protobuf message sizes are bounded.

### 5. WebSocket Security
- Check for proper connection lifecycle management (max connections, idle timeout, graceful shutdown).
- Verify message framing prevents memory exhaustion from large or infinite streams.
- Check that `WebSocketHandler` properly disposes resources on disconnect.

### 6. Denial of Service Resilience
- Assess pagination endpoints for unbounded query sizes.
- Check whether block/transaction list endpoints can be abused to dump entire chain history.
- Verify GraphQL does not allow unlimited result sets.
- Check faucet for rate limiting.

### 7. Data Consistency
- Verify REST, gRPC, and GraphQL return consistent representations of the same underlying data (blocks, transactions, accounts).
- Check that hex encoding/decoding of addresses, hashes, and signatures is consistent across all three APIs.
- Verify `ReceiptResponse`/`ReceiptResult` fields match actual `ReceiptData` storage format.

### 8. Test Coverage Assessment
- Review `tests/Basalt.Api.Tests/` for coverage gaps.
- Identify untested endpoints, error paths, and edge cases.
- Assess whether WebSocket behavior is tested.

---

## Key Context

- The REST API uses ASP.NET Minimal APIs with delegate-based route handlers.
- The node is intended to run as Native AOT (`PublishAot=true` on `Basalt.Node`).
- Transaction submission flows: REST `POST /v1/transactions` → `Mempool.Add()` → gossip. GraphQL `submitTransaction` mutation → same path.
- The faucet mints native tokens from a pre-funded genesis account.
- WebSocket streams new blocks as they are finalized.
- EIP-1559 fields (`BaseFee`, `EffectiveGasPrice`, `MaxFeePerGas`, `MaxPriorityFeePerGas`) should be present in transaction/receipt responses.

---

## Output Format

Write your findings to `audit/output/01-api.md` with the following structure:

```markdown
# API Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Issues that must be fixed before production — security vulnerabilities, data corruption risks]

## High Severity
[Issues that significantly impact security, reliability, or correctness]

## Medium Severity
[Issues that should be addressed but do not pose immediate risk]

## Low Severity / Recommendations
[Code quality, best practices, minor improvements]

## Test Coverage Gaps
[Specific untested scenarios that should have tests]

## Positive Findings
[Well-implemented patterns worth noting]
```

For each finding, include:
1. **Location**: File path and line number(s)
2. **Description**: What the issue is
3. **Impact**: What could go wrong
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
