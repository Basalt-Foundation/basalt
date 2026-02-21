# Basalt Security & Quality Audit — Explorer (Blazor WASM)

## Scope

Audit the Blazor WebAssembly block explorer frontend application:

| Project | Path | Description |
|---|---|---|
| `Basalt.Explorer` | `src/explorer/Basalt.Explorer/` | Blazor WASM client — block explorer, transaction viewer, validator dashboard, faucet UI |

**Note:** No dedicated test project exists for the Explorer. Part of this audit is assessing whether tests should be added.

---

## Files to Audit

### Application Core
- `Program.cs` (~20 lines) — Blazor WASM entry point, DI registration
- `BasaltApiClient.cs` (~180 lines) — Typed HTTP client consuming the REST API
- `FormatHelper.cs` (~60 lines) — Display formatting utilities

### Services
- `Services/BlockWebSocketService.cs` (~80 lines) — WebSocket connection for live block updates
- `Services/ToastService.cs` (~40 lines) — Toast notification service

### Pages (Razor components)
- `Pages/Dashboard.razor` — Main dashboard
- `Pages/Blocks.razor` — Block list view
- `Pages/BlockDetail.razor` — Single block details
- `Pages/Transactions.razor` — Transaction list view
- `Pages/TransactionDetail.razor` — Single transaction details
- `Pages/Validators.razor` — Validator status page
- `Pages/AccountDetail.razor` — Account balance and history
- `Pages/Faucet.razor` — Faucet request UI
- `Pages/Mempool.razor` — Mempool viewer
- `Pages/Pools.razor` — Staking pool viewer
- `Pages/Stats.razor` — Chain statistics

### Components
- `Components/LoadingSkeleton.razor` — Skeleton loading indicator
- `Components/MiniChart.razor` — Inline chart component
- `Components/ToastContainer.razor` — Toast notification container

### Layout
- `Layout/MainLayout.razor` — Application shell layout
- `App.razor` — Router configuration
- `_Imports.razor` — Global using directives

---

## Audit Objectives

### 1. Cross-Site Scripting (XSS) Prevention
- Verify that all user-supplied data displayed in Razor components is properly sanitized.
- Check that addresses, transaction hashes, and other hex data are validated before rendering.
- Verify that `@((MarkupString)...)` or `@Html.Raw()` patterns are not used with untrusted data.
- Check that `FormatHelper` does not introduce XSS vectors in formatted output.
- Verify that route parameters (block numbers, transaction hashes, addresses) are validated.

### 2. API Client Security
- Verify `BasaltApiClient` validates responses and handles errors gracefully.
- Check that the API base URL is properly configured and not injectable.
- Verify that no sensitive data (private keys, seeds) is sent from the explorer to the API.
- Check CORS configuration implications — the explorer is a separate WASM app.

### 3. WebSocket Security
- Verify `BlockWebSocketService` handles connection failures, reconnection, and message validation.
- Check for message deserialization vulnerabilities (malformed JSON from WebSocket).
- Verify that the WebSocket connection is properly disposed on page navigation.
- Check for memory leaks from long-lived WebSocket connections.

### 4. Faucet UI Security
- Verify the faucet page validates user input (address format, amount bounds).
- Check that the faucet UI cannot be used to bypass server-side rate limiting.
- Verify that faucet responses are properly handled (success, error, rate-limited).

### 5. Client-Side Data Handling
- Check that no sensitive data is stored in browser local storage, session storage, or cookies.
- Verify that the application does not cache sensitive information in JavaScript interop.
- Check that pagination and data loading do not cause memory leaks.

### 6. Error Handling & User Experience
- Verify that API failures show user-friendly error messages (not raw exceptions).
- Check that loading states (skeleton loaders) are properly shown and hidden.
- Verify that invalid routes are handled gracefully (404 equivalent).

### 7. Content Security
- Check that external resources (fonts, CSS, scripts) are loaded from trusted sources with integrity hashes.
- Verify that inline styles/scripts are minimized.
- Check for potential clickjacking (frame-ancestors CSP).

### 8. AOT/Trim Safety
- The project has `IsAotCompatible=false` — verify this is necessary and document why.
- Check that trim-unsafe patterns are identified.
- Verify that `System.Text.Json` usage is AOT-safe with source-generated serialization contexts.

### 9. Razor Component Patterns
- Verify that `$"..."` string interpolation is not used directly in `onclick` handlers (known Blazor issue).
- Check that event handlers properly prevent double-submission.
- Verify component disposal (IDisposable) is implemented where needed.

### 10. Test Coverage Assessment
- There is no dedicated test project for the Explorer.
- Assess which components/services would benefit from tests.
- Recommend a testing strategy (bUnit for component tests, integration tests for API client).

---

## Key Context

- Blazor WASM runs entirely in the browser — all code is downloadable by users.
- The Explorer has no project references to the blockchain stack — it consumes the REST API only.
- AOT/trim analyzers are disabled: `IsAotCompatible=false`.
- Known issue: Razor files cannot use `$"..."` in onclick handlers — must extract to methods.
- Known issue: Raw string literals with `$"""`: brace escaping issues — use concatenation instead.
- NuGet dependencies: `Microsoft.AspNetCore.Components.WebAssembly 9.0.0`, `Microsoft.AspNetCore.Components.WebAssembly.DevServer 9.0.0`, `System.Text.Json 9.0.0`.

---

## Output Format

Write your findings to `audit/output/08-explorer.md` with the following structure:

```markdown
# Explorer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[XSS vulnerabilities, sensitive data exposure]

## High Severity
[Significant security or reliability issues]

## Medium Severity
[Issues to address]

## Low Severity / Recommendations
[Code quality, UX improvements, best practices]

## Test Coverage Gaps
[Components/services that need tests, recommended testing strategy]

## Positive Findings
[Well-implemented patterns]
```

For each finding, include:
1. **Location**: File path and line number(s)
2. **Description**: What the issue is
3. **Impact**: What could go wrong
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
