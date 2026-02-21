# Node Orchestration Audit Report

## Executive Summary

The Basalt node orchestration layer (`NodeCoordinator`, `Program.cs`, `NodeConfiguration`) is well-architected overall, with correct fork-and-swap state management, proper atomic sync guards, and comprehensive message handling. However, the audit identified **2 critical issues** (CLI signing payload mismatch rendering the CLI non-functional, and devnet validator keys with trivially low entropy that bypass the runtime check), **5 high-severity issues** (sync TCS race condition, key file permissions, missing key zeroing, partial sync state adoption, and `async void` event handler), and several medium/low findings across configuration, concurrency, and test coverage.

---

## Critical Issues

### CRIT-01: CLI Transaction Signing Payload Does Not Match Node Verification

- **Location:** `tools/Basalt.Cli/Program.cs:166-168`
- **Description:** The CLI `tx send` command constructs the signing payload by UTF-8-encoding a string interpolation of selected fields:
  ```csharp
  var txBytes = System.Text.Encoding.UTF8.GetBytes(
      $"{tx.Type}{tx.Nonce}{tx.Sender}{tx.To}{tx.Value}{tx.GasLimit}{tx.GasPrice}{tx.ChainId}");
  ```
  The node's `Transaction.WriteSigningPayload()` (in `Basalt.Execution/Transaction.cs`) uses a binary serialization format with fixed-width fields (1B Type, 8B Nonce, 20B Sender, 20B To, 32B Value as UInt256 LE, 8B GasLimit, 32B GasPrice as UInt256, 32B MaxFeePerGas, 32B MaxPriorityFeePerGas, variable Data, 1B Priority, 4B ChainId, 32B ComplianceProofHash). The CLI omits `MaxFeePerGas`, `MaxPriorityFeePerGas`, `Data`, `Priority`, and `ComplianceProofHash`, and uses text encoding instead of binary.
- **Impact:** Every transaction signed by the CLI will fail `TransactionValidator.Validate()` signature verification. The CLI's `tx send` command is effectively non-functional.
- **Recommendation:** Replace the ad-hoc signing payload with the canonical `Transaction.WriteSigningPayload()` method, or construct a `Transaction` object and call `Transaction.Sign()`.
- **Severity:** Critical

### CRIT-02: Docker Compose Devnet Validator Keys Have Trivially Low Entropy

- **Location:** `docker-compose.yml:13,42,65,88`
- **Description:** The four devnet validator keys are:
  - `0000000000000000000000000000000000000000000000000000000000000000` (all zeros)
  - `0100000000000000000000000000000000000000000000000000000000000000` (1 distinct non-zero byte)
  - `0200000000000000000000000000000000000000000000000000000000000000` (1 distinct non-zero byte)
  - `0300000000000000000000000000000000000000000000000000000000000000` (1 distinct non-zero byte)

  Validator-0's key is literally all zeros. The `ValidateKeyEntropy` check (NodeCoordinator.cs:237) catches all-zero keys and keys with <=2 distinct bytes. However, validators 1-3 have exactly 2 distinct bytes (`0x00` and `0x01`/`0x02`/`0x03`), which means `Distinct().Count() <= 2` is true and they should also be rejected. Validator-0's all-zeros key is explicitly caught. This means **all four devnet keys should fail the entropy check** in production, but if the entropy check is ever relaxed or bypassed, these keys are cryptographically trivial.
- **Impact:** If these keys leak (they are in the public repository) or are reused in any non-local environment, all four validators can be impersonated. The all-zeros key for validator-0 is especially dangerous as it's the most commonly guessed key. Even for devnet, using such weak keys sets a bad precedent and could be accidentally used in deployments.
- **Recommendation:** Generate random devnet keys and store them in a `.env` file that is `.gitignore`d. Add a startup warning if known-weak devnet keys are detected. The existing entropy check is good but should be supplemented with a blocklist of well-known weak keys (all-zeros, all-ones, sequential).
- **Severity:** Critical

---

## High Severity

### HIGH-01: Sync Batch TCS Race Condition

- **Location:** `NodeCoordinator.cs:1461-1472` (TrySyncFromPeers) and `NodeCoordinator.cs:1134` (HandleSyncResponse)
- **Description:** The `_syncBatchTcs` field is written and read without synchronization between the sync loop thread and the network message handler thread:
  ```csharp
  // Sync thread (line 1461):
  _syncBatchTcs = new TaskCompletionSource<bool>();
  // ... send request (line 1469) ...
  // ... await response (line 1472) ...

  // Network thread (line 1134):
  _syncBatchTcs?.TrySetResult(applied > 0);
  ```
  If the network response arrives between line 1461 (TCS creation) and line 1472 (await), the `TrySetResult` fires on the newly created TCS before the sync thread is waiting on it. While this is benign (the Task will be immediately completed when awaited), there is a more subtle race: if a **stale** response from a previous batch arrives after a new TCS is created but before the new request is sent, it completes the wrong TCS.
- **Impact:** A validator could get stuck in sync if a stale response completes the wrong TCS, causing the sync loop to think progress was made (or wasn't) incorrectly. The 10-second timeout mitigates this but introduces unnecessary latency.
- **Recommendation:** Use a `lock` to coordinate TCS creation and consumption, or pass a batch sequence number through the sync request/response to match responses to their requests.
- **Severity:** High

### HIGH-02: CLI Key File Written World-Readable

- **Location:** `tools/Basalt.Cli/Program.cs:91-95`
- **Description:** The `account create --output` command writes the private key to a file using `File.WriteAllTextAsync()` which creates files with the default umask (typically `0644` on Unix), making the private key readable by any user on the system:
  ```csharp
  var content = $"address={addrHex}\npublicKey={pubHex}\nprivateKey=0x{privHex}\n";
  await File.WriteAllTextAsync(output, content);
  ```
- **Impact:** Any user on the system can read the exported private key. On shared servers or CI environments, this is a direct key compromise.
- **Recommendation:** Set file permissions to `0600` (owner-only) after creation. On Unix: `File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite)`. Also consider encrypting the key file with a passphrase.
- **Severity:** High

### HIGH-03: Private Key Material Not Zeroed in CLI

- **Location:** `tools/Basalt.Cli/Program.cs:142` (tx send) and `tools/Basalt.Cli/Program.cs:77,80` (account create)
- **Description:** In the `tx send` handler, the private key bytes (`privateKey` at line 142) and hex string (`privKeyHex` at line 141) are never zeroed after use. In `account create`, the `privateKey` byte array (line 77) and `privHex` string (line 80) persist in memory. The crypto library provides `Ed25519Signer.ZeroPrivateKey()` but it is not called.
- **Impact:** Private key material remains in process memory and can be recovered via memory dumps, core files, or swap. While .NET's GC may relocate the data making zeroing imperfect, failing to even attempt zeroing is a security hygiene failure.
- **Recommendation:** Call `CryptographicOperations.ZeroMemory(privateKey)` or `Ed25519Signer.ZeroPrivateKey(privateKey)` in a `finally` block after signing completes.
- **Severity:** High

### HIGH-04: Partial Sync State Adoption in HandleSyncResponse

- **Location:** `NodeCoordinator.cs:1123-1131`
- **Description:** When a sync batch partially applies (some blocks succeed, some fail), the code correctly logs a warning and discards the forked state. However, the blocks that were successfully added to `ChainManager.AddBlock()` (line 1095) remain in the chain, while the corresponding state mutations in `forkedState` are discarded (line 1130). This creates an inconsistency: the chain knows about blocks whose state transitions were computed on a now-discarded fork.
  ```csharp
  if (applied == blocksToApply.Count && applied > 0)
  {
      _stateDb.Swap(forkedState);  // Only on full success
  }
  else if (applied > 0)
  {
      _logger.LogWarning("Partial sync: ...");
      // forkedState is discarded, but blocks are already in ChainManager!
  }
  ```
- **Impact:** After a partial sync, `ChainManager.LatestBlockNumber` reflects blocks whose state is not in the canonical state DB. Subsequent block execution will operate on stale state, potentially accepting invalid transactions or rejecting valid ones. The next successful sync will likely fix this, but there is a window of inconsistency.
- **Recommendation:** Either (a) rollback `ChainManager` to the last consistent block on partial failure, or (b) apply state to canonical DB incrementally per block rather than as an all-or-nothing batch.
- **Severity:** High

### HIGH-05: `async void` Event Handler for Peer Connections

- **Location:** `NodeCoordinator.cs:646`
- **Description:** `HandleNewConnection` is declared `async void` and is wired as an event handler for `_transport.OnPeerConnected`. While it has a try-catch covering the body, `async void` methods have fundamentally different exception semantics: any exception that escapes (e.g., from a `ConfigureAwait` continuation or an unobserved task) propagates to the `SynchronizationContext` and can crash the process.
- **Impact:** An unhandled exception in any async continuation within the handler will terminate the node process without a clean shutdown.
- **Recommendation:** Convert to `async Task` and use a fire-and-forget wrapper: `_transport.OnPeerConnected += conn => _ = HandleNewConnectionAsync(conn);`. This ensures exceptions are captured by the Task rather than propagating to the synchronization context.
- **Severity:** High

---

## Medium Severity

### MED-01: DataDir Path Traversal Validation Incomplete

- **Location:** `NodeConfiguration.cs:79-90`
- **Description:** The `ValidateDataDir` method rejects paths under specific system directories (`/etc`, `/usr`, `/bin`, etc.) but the blocklist is Unix-specific and incomplete:
  1. No Windows system path checks (e.g., `C:\Windows`, `C:\Program Files`)
  2. Missing `/boot`, `/dev`, `/lib`, `/root`, `/home` (other users' directories)
  3. Symlink resolution: `Path.GetFullPath` does not resolve symlinks, so `/tmp/link-to-etc` would bypass the check
  4. The check uses `StartsWith` with `StringComparison.Ordinal`, which is correct for case-sensitive filesystems but wrong for macOS (case-insensitive HFS+)
- **Impact:** On macOS, a path like `/ETC/passwd` bypasses the check. Symlinks can bypass all path-prefix checks.
- **Recommendation:** Use `Path.GetFullPath(new FileInfo(dataDir).FullName)` to resolve symlinks. Add case-insensitive comparison on macOS/Windows. Consider an allowlist approach (only allow paths under `/data`, `$HOME/.basalt`, or similar) rather than a denylist.
- **Severity:** Medium

### MED-02: No Validation of Peer Endpoint Format

- **Location:** `NodeCoordinator.cs:1211`
- **Description:** Peer endpoints from `BASALT_PEERS` are parsed with a simple `Split(':')` without validation:
  ```csharp
  var parts = peerEndpoint.Split(':');
  var host = parts[0];
  var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 30303;
  ```
  No validation that `host` is a valid hostname/IP, no range check on `port` (0-65535), no protection against SSRF if the peer list is user-controlled.
- **Impact:** Malformed peer entries silently fail. An attacker who controls `BASALT_PEERS` could direct the node to connect to arbitrary internal services (SSRF).
- **Recommendation:** Validate host format (hostname regex or IP parse), validate port range (1-65535), and consider restricting to non-privileged ports (>1024).
- **Severity:** Medium

### MED-03: No Rate Limiting on Sync Requests

- **Location:** `NodeCoordinator.cs:1008-1046` (HandleSyncRequest)
- **Description:** Any connected peer can send unlimited `SyncRequestMessage` messages, each of which triggers up to 100 blocks of serialization and transmission. There is no per-peer rate limiting.
- **Impact:** A malicious peer could DoS a validator by flooding it with sync requests, consuming CPU (serialization) and bandwidth (block data transmission) without limit.
- **Recommendation:** Add per-peer rate limiting for sync requests (e.g., max 1 sync request per second per peer). Use the `PeerManager` scoring system to penalize peers that send excessive requests.
- **Severity:** Medium

### MED-04: Block Time Check in Health Endpoint Uses Wall Clock

- **Location:** `Program.cs:213-216`
- **Description:** The `/v1/health` endpoint calculates block age using `DateTimeOffset.UtcNow`:
  ```csharp
  var blockAge = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastBlock.Header.Timestamp) / 1000.0;
  var healthy = blockAge >= 0 && blockAge < 60;
  ```
  If the system clock is skewed, the health check reports incorrect status. A node that is producing blocks correctly but has a clock drift >60 seconds will report as unhealthy.
- **Impact:** False health alerts, or worse, a load balancer removing a healthy node from rotation due to clock skew.
- **Recommendation:** Add a secondary health signal based on block height progress (e.g., has the block number increased in the last N seconds?) rather than relying solely on clock comparison.
- **Severity:** Medium

### MED-05: devnet-genesis.json Placeholder Faucet Address

- **Location:** `tools/Basalt.DevNet/devnet-genesis.json:39`
- **Description:** The genesis JSON contains a placeholder faucet address `0xFAUCET0000000000000000000000000000000000` which is not a valid hex address (contains letters 'U' and 'E' which are not hex-safe — actually FAUCET is valid hex: F-A-U... wait, 'U' is not a valid hex character). This address cannot be parsed by `Address.FromHexString()`.
- **Impact:** The devnet genesis JSON is not usable as-is — it would fail at address parsing. However, this file is not actually consumed by the node (Program.cs hardcodes balances), so it's a documentation/tooling issue only.
- **Recommendation:** Fix the placeholder address to use valid hex, or better, document that this file is aspirational configuration and not yet consumed by the node.
- **Severity:** Medium

### MED-06: No Consensus Message Authentication

- **Location:** `NodeCoordinator.cs:1137-1179` (HandleConsensusProposal)
- **Description:** When a consensus proposal arrives, the code checks for double-signing but does not verify that the `proposal.SenderId` corresponds to a known validator in the current validator set before processing it. The proposal is passed directly to `_consensus.HandleProposal()` or `_pipelinedConsensus.HandleProposal()`. If the consensus engine internally validates the sender, this is fine — but the NodeCoordinator should reject messages from unknown peers early to avoid unnecessary processing.
- **Impact:** Unknown or expelled validators' messages are processed (likely rejected internally by consensus), wasting CPU cycles. In the worst case, if consensus doesn't validate the sender, a non-validator peer could influence consensus.
- **Recommendation:** Add an early check: `if (_validatorSet.GetByPeerId(sender) == null) return;` before processing consensus messages.
- **Severity:** Medium

---

## Low Severity / Recommendations

### LOW-01: NodeCoordinator Is 1,671 Lines — Decomposition Recommended

- **Location:** `NodeCoordinator.cs` (entire file)
- **Description:** This file handles identity setup, validator set management, networking, consensus wiring, block production, sync logic, message routing, epoch transitions, persistence, and receipt storage. It is the single largest file in the codebase and mixes concerns that could be separated.
- **Recommendation:** Extract into focused components: `SyncManager`, `MessageRouter`, `EpochTransitionHandler`, `PersistenceManager`. This would improve testability (most of these paths are untested at the integration level) and reduce cognitive load.
- **Severity:** Low

### LOW-02: `_lastBlockFinalizedAtMs` Not Volatile

- **Location:** `NodeCoordinator.cs:88,473,579,604`
- **Description:** `_lastBlockFinalizedAtMs` is a `long` field written by the consensus finalization callback (line 473) and read by the consensus loop thread (lines 579, 604). On x86-64, 64-bit reads/writes are atomic, but this is not guaranteed on all architectures. The field is not declared `volatile` and no `Interlocked` operations are used.
- **Impact:** On ARM64 (which Basalt targets via Docker), torn reads of a `long` are theoretically possible, though extremely unlikely in practice.
- **Recommendation:** Use `Volatile.Write`/`Volatile.Read` or `Interlocked.Exchange`/`Interlocked.Read` for cross-thread long access.
- **Severity:** Low

### LOW-03: CLI `tx send` Hardcodes ChainId Default to 1

- **Location:** `tools/Basalt.Cli/Program.cs:131`
- **Description:** The `--chain-id` option defaults to `1`, but the devnet chain ID is `31337`. Users who forget to specify `--chain-id 31337` will have their transactions rejected by chain ID validation.
- **Recommendation:** Change the default to `31337` to match devnet, or auto-detect from the node's `/v1/status` endpoint.
- **Severity:** Low

### LOW-04: CLI `tx status` Command Uses String Replace for Explorer URL

- **Location:** `tools/Basalt.Cli/Program.cs:196`
- **Description:** `nodeUrl.Replace("5000", "5001")` is a fragile heuristic to derive the explorer URL from the node URL. It would fail for non-standard ports or if `5000` appears in the hostname.
- **Recommendation:** Accept an `--explorer` option or query the node for its explorer URL.
- **Severity:** Low

### LOW-05: No Timeout on `NodeClient` HTTP Requests

- **Location:** `tools/Basalt.Cli/NodeClient.cs:14-16`
- **Description:** The `HttpClient` is created without a `Timeout` setting. By default, `HttpClient.Timeout` is 100 seconds, which is excessive for status checks and block queries against a local node.
- **Recommendation:** Set `_http.Timeout = TimeSpan.FromSeconds(10)` for responsive CLI UX.
- **Severity:** Low

### LOW-06: Standalone Mode Does Not Persist Blocks

- **Location:** `Program.cs:308-339`
- **Description:** In standalone mode (no consensus peers), `BlockProductionLoop` produces blocks but there is no `blockStore` or receipt persistence wired. The `OnBlockProduced` event only records metrics and broadcasts WebSocket events. If the standalone node restarts, all state is lost.
- **Impact:** Expected for development, but worth a log warning if `DataDir` is set in standalone mode (data directory is created but blocks are not persisted).
- **Recommendation:** Log a warning if `DataDir` is set but the node is in standalone mode.
- **Severity:** Low

### LOW-07: `setup-validator.sh` Does Not Set Key File Permissions

- **Location:** `tools/Basalt.DevNet/setup-validator.sh:40`
- **Description:** The script invokes `basalt account create --output "$BASALT_CONFIG/validator.key"` but does not `chmod 600` the resulting key file.
- **Recommendation:** Add `chmod 600 "$BASALT_CONFIG/validator.key"` after key generation.
- **Severity:** Low

### LOW-08: Genesis Balances Mismatch Between Program.cs and devnet-genesis.json

- **Location:** `Program.cs:56-65` vs `tools/Basalt.DevNet/devnet-genesis.json:33-39`
- **Description:** The hardcoded genesis balances in `Program.cs` differ from those in `devnet-genesis.json`:
  - Program.cs: `0x...0001` gets `1,000,000 * 10^18`, validators get `200,000 * 10^18`
  - devnet-genesis.json: `0x...0001` gets `1,000,000,000 * 10^18`, validators get `200,000 * 10^18`

  The genesis JSON is not consumed by the node, so this is a documentation inconsistency rather than a bug.
- **Recommendation:** Keep the files in sync or remove the JSON if it's not consumed.
- **Severity:** Low

---

## Test Coverage Gaps

### GAP-01: No Integration Tests for NodeCoordinator

- **Description:** There are **zero** tests for `NodeCoordinator` itself. The test project (`Basalt.Node.Tests/`) contains:
  - `NodeConfigurationTests` — 4 tests for configuration parsing
  - `ValidatorSetupTests` — 7 tests for ValidatorSet, PeerId, crypto
  - `MessageHandlingTests` — 16 tests for message round-trip serialization
  - `SlashingIntegrationTests` — 5 tests for staking/slashing

  None of these test `NodeCoordinator`'s orchestration logic: block finalization, sync, epoch transitions, message routing, or the interaction between subsystems.

### GAP-02: Untested NodeCoordinator Paths

The following critical code paths have no test coverage:

| Path | Lines | Risk |
|------|-------|------|
| `HandleBlockFinalized` | 438-533 | Block application, receipt storage, epoch transition |
| `TrySyncFromPeers` | 1429-1535 | Sync guard, batch sync, consensus restart |
| `HandleSyncResponse` | 1048-1135 | Fork-and-swap, partial failure handling |
| `HandleConsensusProposal` | 1137-1179 | Double-sign detection |
| `ApplyEpochTransition` | 1564-1592 | Validator set swap |
| `HandleBlockPayload` | 950-1006 | Anti-injection guard |
| `HandleBlockAnnounce` | 937-948 | Sync trigger |
| `SetupIdentity` | 205-232 | Key validation, entropy check |
| `TryProposeBlockPipelined` | 601-635 | Pipelined proposal logic |
| `PersistBlock` / `PersistReceipts` | 1594-1661 | Persistence error handling |

### GAP-03: No Shutdown/Recovery Tests

- No tests verify graceful shutdown (`StopAsync`, `DisposeAsync`)
- No tests verify recovery from stored blocks (RocksDB restart path in Program.cs:90-128)
- No tests verify state root consistency check (Program.cs:115-122)

### GAP-04: No Configuration Edge Case Tests

- No tests for `FromEnvironment()` with actual environment variables
- No tests for `ValidateDataDir` with path traversal attempts, symlinks, or system paths
- No tests for invalid `BASALT_VALIDATOR_KEY` hex (odd-length, invalid characters)

### GAP-05: No Concurrent Message Processing Tests

- No tests verify that concurrent network messages are handled correctly
- No tests for the `_isSyncing` guard under concurrent access
- No tests for the `_syncBatchTcs` race condition

---

## Positive Findings

### POS-01: Fork-and-Swap State Management (N-05)
The `StateDbRef` pattern for sharing canonical state across API and consensus layers is elegant. Proposals use forked state, sync uses forked state with atomic swap on success, and the `volatile` reference ensures visibility. This prevents speculative state mutation and provides crash consistency.

### POS-02: Atomic Sync Guard (N-09)
The `Interlocked.CompareExchange` pattern for `_isSyncing` (line 1432) correctly prevents TOCTOU races on sync entry. Combined with `Volatile.Read` in the consensus loop, this ensures only one sync operation runs at a time.

### POS-03: Double-Sign Detection with Sliding Window (N-10, N-17)
The `_proposalsByView` dictionary uses a `(View, Block, Proposer)` composite key to avoid false positives from view number reuse across blocks. The sliding window (retain last 10 views) bounds memory growth while preserving recent evidence.

### POS-04: Per-Connection Handshake Isolation
The `CreateHandshake()` factory method (line 60) creates a fresh `HandshakeProtocol` per connection, preventing the X25519 ephemeral key race condition that was previously a known bug.

### POS-05: Transport Encryption with Key Zeroing (NET-C02)
After establishing transport encryption (lines 692-698, 1280-1286), the shared secret is immediately zeroed with `CryptographicOperations.ZeroMemory()`. This is proper cryptographic hygiene.

### POS-06: Private Key Zeroing on Disposal (N-16)
`NodeCoordinator.DisposeAsync()` (line 1669) zeros the validator private key on shutdown, preventing post-process memory scraping.

### POS-07: Block Request Count Caps (N-03, N-14)
Both `HandleSyncRequest` (MaxSyncResponseBlocks=100) and `HandleBlockRequest` (MaxBlockRequestCount=100) cap the number of blocks served per request, preventing resource exhaustion from malicious peers.

### POS-08: Unsolicited Block Payload Rejection (N-04)
`HandleBlockPayload` (line 952) rejects block payloads when not in sync mode, preventing unsolicited block injection attacks.

### POS-09: Kestrel Request Body Size Limit (INFO-2)
`Program.cs:168-171` limits request bodies to 512KB, preventing large payload attacks against the REST API.

### POS-10: State Root Consistency Verification (N-11)
On recovery from RocksDB (Program.cs:115-122), the node recomputes the state root and compares it to the stored block header. This detects database corruption before the node joins consensus with invalid state.

### POS-11: Genesis Chain ID Verification (N-20)
On recovery (Program.cs:103-108), the stored genesis block's chain ID is verified against the configured chain ID, preventing accidental data directory reuse across networks.

### POS-12: Deterministic Genesis Timestamp
The genesis block creation uses `ChainManager.CreateGenesisBlock()` which uses a fixed timestamp (not `DateTimeOffset.UtcNow`), ensuring all nodes produce the same genesis hash — a lesson learned from a previous bug.

### POS-13: AOT-Safe JSON Serialization
Both the node (`BasaltApiJsonContext`, `WsJsonContext`) and the CLI (`CliJsonContext`) use source-generated JSON serializers, ensuring AOT compatibility without reflection.

### POS-14: Structured Logging with Context
Logging throughout `NodeCoordinator` includes structured parameters (block numbers, peer IDs, hash prefixes) enabling effective log analysis. Private keys are never logged.

### POS-15: Health Endpoint with Degraded Status
The `/v1/health` endpoint (Program.cs:210-224) returns both status and diagnostic information (block age, chain ID) and uses HTTP 503 for degraded state, enabling proper load balancer integration.

### POS-16: Mempool Event Firing Outside Lock
The `Mempool.Add()` method fires `OnTransactionAdded` outside the lock, preventing deadlocks when event handlers (like tx gossip) need to access other locked resources.

### POS-17: Shutdown Timeout (N-18)
Both consensus and standalone modes have a 10-second shutdown timeout (Program.cs:302, 334) to prevent shutdown deadlocks from blocking process termination.
