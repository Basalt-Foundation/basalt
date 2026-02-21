# Explorer Audit Report

## Executive Summary

The Blazor WASM block explorer is well-structured with good separation of concerns and AOT-safe JSON serialization. No critical XSS vulnerabilities were found — Blazor's default Razor rendering auto-encodes all `@` expressions, and no `MarkupString`/`Html.Raw` patterns are used. The primary concerns are: a JS `eval()` call in the theme toggle, missing input validation on route parameters and the faucet address field, WebSocket message handling gaps (multi-frame messages), missing `IDisposable` on pages with timers, and the absence of any test coverage.

---

## Critical Issues

_None found._

---

## High Severity

### H-1: `eval()` Used for Theme Toggle — DOM XSS Vector

**Location:** `Layout/MainLayout.razor:114`
**Description:** The theme toggle uses `JS.InvokeVoidAsync("eval", ...)` to set a DOM attribute:
```csharp
await JS.InvokeVoidAsync("eval", $"document.documentElement.setAttribute('data-theme','{theme}')");
```
While `theme` is currently constrained to `"dark"` or `"light"` (hardcoded toggle), using `eval()` is a dangerous pattern. If any future code path allows user-controlled input to reach this string interpolation, it becomes a DOM XSS vulnerability. Additionally, CSP policies that block `eval` (which they should) will break this functionality.

**Impact:** Potential DOM XSS if the `theme` variable is ever derived from user input; incompatible with strict CSP (`unsafe-eval` required).

**Recommendation:** Replace with a direct JS interop function:
```javascript
// In index.html or a separate .js file
window.setTheme = function(theme) {
    document.documentElement.setAttribute('data-theme', theme);
};
```
```csharp
await JS.InvokeVoidAsync("setTheme", theme);
```

### H-2: WebSocket Receive Buffer Does Not Handle Multi-Frame Messages

**Location:** `Services/BlockWebSocketService.cs:40-55`
**Description:** The receive loop reads into a fixed 4096-byte buffer and processes the message from a single `ReceiveAsync` call without checking `result.EndOfMessage`. If a WebSocket message exceeds 4096 bytes (or is fragmented into multiple frames), the code will deserialize an incomplete JSON string, silently failing or producing corrupt data.

```csharp
var result = await _ws.ReceiveAsync(buffer, ct);
// ...
var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
var envelope = JsonSerializer.Deserialize(json, ...);
```

**Impact:** Silently dropped or corrupted block notifications if messages exceed buffer size or arrive fragmented. Could cause the dashboard to miss blocks or display stale data.

**Recommendation:** Accumulate frames into a `MemoryStream` until `result.EndOfMessage` is true, then deserialize:
```csharp
using var ms = new MemoryStream();
WebSocketReceiveResult result;
do {
    result = await _ws.ReceiveAsync(buffer, ct);
    ms.Write(buffer, 0, result.Count);
} while (!result.EndOfMessage);
var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
```

---

## Medium Severity

### M-1: No Input Validation on Faucet Address

**Location:** `Pages/Faucet.razor:92-101`
**Description:** The faucet request sends whatever the user types as the address after only trimming whitespace. There is no client-side validation for:
- Correct hex format (should be 40 hex chars or `0x` + 40 hex chars)
- Valid character set (only `[0-9a-fA-F]`)
- Minimum/maximum length

While server-side validation should catch this, the client provides no feedback before sending the request, leading to poor UX and unnecessary API calls.

**Impact:** Poor UX (users get unhelpful "failed" messages for malformed addresses); unnecessary network traffic.

**Recommendation:** Add client-side validation before submitting:
```csharp
private static bool IsValidAddress(string addr)
{
    if (addr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        addr = addr[2..];
    return addr.Length == 40 && addr.All(c => char.IsAsciiHexDigit(c));
}
```

### M-2: No Route Parameter Validation on Detail Pages

**Location:** `Pages/BlockDetail.razor:100`, `Pages/TransactionDetail.razor:269`, `Pages/AccountDetail.razor:236`
**Description:** Route parameters (`Id`, `Hash`, `Address`) are passed directly to API calls without any client-side validation:
- `BlockDetail.razor`: `Id` is a string that could be anything — passed directly to `GetBlockAsync(Id)`
- `TransactionDetail.razor`: `Hash` could contain non-hex characters
- `AccountDetail.razor`: `Address` could be arbitrary text

These values are interpolated into URL paths in `BasaltApiClient` (e.g., `$"v1/blocks/{id}"`). While `HttpClient` handles URL encoding, there is no validation that the values match expected formats.

**Impact:** Unnecessary API calls with invalid parameters; error messages may expose internal details (e.g., "Block not found: <script>alert(1)</script>" — though Blazor auto-encodes this, the raw text is still displayed).

**Recommendation:** Validate route parameters before API calls. For blocks, verify numeric or 64-char hex. For transactions, verify 64-char hex. For accounts, verify 40-char hex (with optional `0x` prefix).

### M-3: Faucet Page Does Not Implement `IDisposable` for Cooldown Timer

**Location:** `Pages/Faucet.razor:126-135`
**Description:** The faucet page creates a `System.Threading.Timer` (`_cooldownTimer`) for the cooldown countdown but does not implement `IDisposable` or `IAsyncDisposable`. If the user navigates away during the cooldown, the timer continues running and calling `InvokeAsync(StateHasChanged)` on a disposed component, which can throw `ObjectDisposedException`.

**Impact:** `ObjectDisposedException` in the background after navigation; potential memory leak from undisposed timer.

**Recommendation:** Add `@implements IDisposable` and dispose the timer:
```csharp
public void Dispose()
{
    _cooldownTimer?.Dispose();
}
```

### M-4: `async void` Event Handlers Without Exception Handling

**Location:**
- `Pages/Dashboard.razor:147` — `HandleNewBlock`
- `Layout/MainLayout.razor:177` — `HandleSearchBlur`
- `Components/ToastContainer.razor:22` — `HandleToast`

**Description:** Several `async void` event handlers do not wrap their bodies in try-catch. In `async void` methods, unhandled exceptions crash the application (they propagate to the synchronization context and are unobserved).

**Impact:** An exception in any of these handlers (e.g., a network error during `LoadData()` in `HandleNewBlock`) will crash the Blazor application silently.

**Recommendation:** Wrap `async void` handler bodies in try-catch:
```csharp
private async void HandleNewBlock(WebSocketBlockEvent block)
{
    try
    {
        await InvokeAsync(async () => { ... });
    }
    catch { /* log or ignore */ }
}
```

### M-5: Dashboard Disposes Shared `BlockWebSocketService`

**Location:** `Pages/Dashboard.razor:188-193`
**Description:** The `DisposeAsync` method calls `await WsService.DisposeAsync()`, which fully disposes the `BlockWebSocketService` (cancels CTS, closes WebSocket). However, `BlockWebSocketService` is registered as `Scoped` in DI (`Program.cs:14`). In Blazor WASM, Scoped = Singleton (there's only one scope for the app lifetime). This means navigating away from the Dashboard and back will use a disposed WebSocket service.

**Impact:** After navigating away from Dashboard and back, the WebSocket service is disposed and will not reconnect. Live block updates stop working permanently until page refresh.

**Recommendation:** Either:
1. Don't dispose the service in the component — only unsubscribe from the event.
2. Make the service resilient to re-connection after disposal (add a `ConnectAsync` that re-creates internal state).
3. Change to `Transient` registration (but this has other implications for shared state).

The simplest fix is to only unsubscribe:
```csharp
public async ValueTask DisposeAsync()
{
    _timer?.Dispose();
    WsService.OnNewBlock -= HandleNewBlock;
    // Do NOT dispose WsService — it's shared (Scoped = Singleton in WASM)
}
```

---

## Low Severity / Recommendations

### L-1: Recent Searches Stored in `localStorage` Without Size Limits on Values

**Location:** `Layout/MainLayout.razor:162-168`
**Description:** Search queries are stored in `localStorage` under `basalt_recent_searches`. While the list is capped at 10 entries, there is no limit on the length of individual search values. A very long search query could waste localStorage space.

**Impact:** Minimal — localStorage has a 5-10MB quota, and this is unlikely to be exploited.

**Recommendation:** Truncate stored search values to a reasonable length (e.g., 128 characters).

### L-2: `BasaltApiClient` Swallows All Exceptions

**Location:** `BasaltApiClient.cs:13-138` (every method)
**Description:** Every API method catches all exceptions and returns `null` or empty arrays. This makes it impossible for callers to distinguish between "not found", "network error", and "server error" (500). Malformed responses are also silently swallowed.

**Impact:** Poor diagnostics — users see generic "cannot connect" messages for all failure modes. Developers cannot debug API issues without network inspection tools.

**Recommendation:** Consider at minimum logging the exception type, or returning a result type that distinguishes error kinds:
```csharp
// Option 1: Let HttpRequestException through, only catch JsonException
// Option 2: Return a Result<T> with error details
```

### L-3: Validator Status Rendered Without Sanitization

**Location:** `Pages/Validators.razor:30`
**Description:** `v.Status` from the API is rendered directly and also used as a CSS class via `v.Status.ToLowerInvariant()`. While Blazor auto-encodes the text content (no XSS), an unexpected status value from the API would produce an invalid CSS class name, though this has no security impact.

**Impact:** Cosmetic — unexpected status values may not have matching CSS styles.

**Recommendation:** Map status values to a known set of CSS classes (similar to `FormatHelper.GetTxTypeBadgeClass`).

### L-4: No Content Security Policy (CSP) Headers

**Location:** `wwwroot/index.html`
**Description:** The HTML page does not include a `Content-Security-Policy` meta tag or HTTP header. This means:
- No `frame-ancestors` directive (clickjacking possible)
- No `script-src` restrictions (though Blazor WASM requires `unsafe-eval` for some features)
- The `eval()` call in `MainLayout.razor` would be blocked by a strict CSP

**Impact:** No defense-in-depth against XSS or clickjacking attacks.

**Recommendation:** Add a CSP meta tag. At minimum:
```html
<meta http-equiv="Content-Security-Policy"
      content="default-src 'self'; script-src 'self' 'unsafe-eval' 'sha256-...'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none';">
```
Note: The `eval()` in MainLayout should be removed first (see H-1), which would allow dropping `unsafe-eval`.

### L-5: No External Resource Integrity Hashes

**Location:** `wwwroot/index.html:8-9`
**Description:** CSS and JS resources are loaded without Subresource Integrity (SRI) hashes. Currently all resources are first-party (`css/app.css`, `_framework/blazor.webassembly.js`), so the risk is low. However, if CDN-hosted resources are added in the future, SRI should be used.

**Impact:** Low — all resources are currently self-hosted.

**Recommendation:** Document the policy that any future external resources must include `integrity` attributes.

### L-6: `LoadingSkeleton` Component Is Defined But Never Used

**Location:** `Components/LoadingSkeleton.razor`
**Description:** The `LoadingSkeleton` component is defined but not used in any page. All pages use simple `<div class="loading">` text instead.

**Impact:** Dead code.

**Recommendation:** Either integrate the skeleton loader into pages that would benefit (Blocks, Transactions, Dashboard) or remove the unused component.

### L-7: `MiniChart` SVG Points Use Floating-Point Locale

**Location:** `Components/MiniChart.razor:40`
**Description:** The SVG point coordinates use `$"{x:F1},{y:F1}"` which depends on the current culture for decimal separator formatting. In locales that use `,` as a decimal separator, the SVG `points` attribute would be malformed (e.g., `1,2,3,4` instead of `1.2,3.4`).

However, `F1` format specifier in .NET always uses `.` as decimal separator regardless of culture, so this is actually safe. No action needed.

### L-8: Duplicate `TruncateHash` Implementation

**Location:** `Pages/Blocks.razor:92-96` vs `FormatHelper.cs:95-99`
**Description:** `Blocks.razor` has its own local `TruncateHash` method that duplicates `FormatHelper.TruncateHash`. Other pages use the shared `FormatHelper` version.

**Impact:** Code duplication; potential inconsistency if one is updated and not the other.

**Recommendation:** Remove the local `TruncateHash` in `Blocks.razor` and use `FormatHelper.TruncateHash` instead.

---

## Test Coverage Gaps

### No Test Project Exists

The Explorer has zero test coverage. There are no unit tests, component tests, or integration tests.

### Recommended Testing Strategy

1. **Unit Tests for `FormatHelper`** (highest priority, easiest):
   - `FormatBslt` — test with "0", very large numbers, edge cases around decimal precision
   - `TruncateHash` — short strings, empty strings, exact boundary lengths
   - `ParsePrometheusMetrics` — various input formats, comments, empty lines, malformed values
   - `GetTxTypeBadgeClass` / `GetTxTypeLabel` — all known types plus unknown
   - `FormatBytes` — boundary values at KB/MB thresholds
   - These are pure functions with no dependencies — straightforward xUnit tests

2. **Unit Tests for `BlockWebSocketService`** (medium priority):
   - Test connection lifecycle (connect, receive, disconnect, reconnect)
   - Test message deserialization with valid/invalid JSON
   - Test disposal behavior
   - Requires a mock WebSocket server or abstracting the WebSocket dependency

3. **bUnit Component Tests** (medium priority):
   - Test that pages display loading states, error states, and data states correctly
   - Test navigation behavior (clicking rows navigates to detail pages)
   - Test the faucet cooldown mechanism
   - Test the search bar routing logic (block number → /block/, 40-char hex → /account/, 64-char hex → /tx/)

4. **Integration Tests for `BasaltApiClient`** (lower priority):
   - Verify correct URL construction
   - Verify correct JSON deserialization with sample responses
   - Test error handling (network errors, 404s, malformed JSON)
   - Can use `MockHttpMessageHandler`

### Recommended Test Project Setup
```
tests/Basalt.Explorer.Tests/
├── FormatHelperTests.cs          # Pure function unit tests
├── BlockWebSocketServiceTests.cs # Service lifecycle tests
├── BasaltApiClientTests.cs       # HTTP client integration tests
└── Pages/
    ├── DashboardTests.cs         # bUnit component tests
    ├── FaucetTests.cs
    └── SearchTests.cs            # MainLayout search routing logic
```

NuGet packages needed: `bunit`, `Moq` or `NSubstitute`, `RichardSzalay.MockHttp`

---

## Positive Findings

1. **No XSS Vulnerabilities:** All Razor `@` expressions use Blazor's default HTML encoding. No `MarkupString`, `Html.Raw`, or `@((MarkupString)...)` patterns are used anywhere in the codebase. All user-supplied data (addresses, hashes, search queries) flows through auto-encoded Razor rendering.

2. **AOT-Safe JSON Serialization:** The `ExplorerJsonContext` source-generated serializer context covers all DTO types. All `GetFromJsonAsync` / `PostAsJsonAsync` calls use the source-generated context, avoiding reflection-based serialization.

3. **No Sensitive Data Handling:** The explorer is read-only (except for the faucet, which only sends an address). No private keys, seeds, or authentication tokens are handled. The only `localStorage` usage is for theme preference and recent searches — both non-sensitive.

4. **Clean Separation of Concerns:** `BasaltApiClient` cleanly encapsulates all API calls. `FormatHelper` centralizes display formatting. DTOs are well-defined with `JsonPropertyName` attributes.

5. **Proper 404 Handling:** `App.razor` has a `NotFound` template that renders a user-friendly "Page not found" message within the main layout.

6. **No External Dependencies:** All CSS is self-hosted. No external fonts, CDNs, or third-party scripts. The only JavaScript is the theme initializer inline script and the Blazor framework runtime.

7. **Proper Event Unsubscription:** The Dashboard properly unsubscribes from `WsService.OnNewBlock` in `DisposeAsync`. The `ToastContainer` properly unsubscribes from `Toasts.OnToast` in `Dispose`. The Mempool page disposes its polling timer.

8. **Good Pagination Implementation:** The Blocks page uses server-side pagination with proper bounds checking on page navigation.

9. **Robust Metrics Parsing:** `FormatHelper.ParsePrometheusMetrics` uses `InvariantCulture` for number parsing, correctly handling Prometheus text format with comments and edge cases.

10. **No `$"..."` in onclick Handlers:** The known Blazor issue with string interpolation in `onclick` is properly avoided throughout — all onclick handlers use lambda expressions with method calls.
