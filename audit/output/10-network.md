# Network Layer Audit Report

## Executive Summary

The network layer demonstrates strong security fundamentals: mutual Ed25519 challenge-response authentication, ephemeral X25519 key exchange with AES-256-GCM transport encryption, per-IP and total connection limits, message length validation, timestamp drift checks, and anti-replay nonce enforcement. No critical vulnerabilities were found. The primary concerns are: outbound connections bypass per-IP/total connection limits, `BlockCodec` deserialization lacks bounds checking on array counts (enabling OOM), `SenderId` in deserialized messages is never verified against the authenticated peer identity, and the `HandshakeResult.SharedSecret` is not zeroed after use. Test coverage is solid (73 tests) but lacks handshake protocol, PeerManager banning, and malformed message boundary tests.

---

## Critical Issues

No critical issues found.

---

## High Severity

### H-1: Outbound Connections Bypass Per-IP and Total Connection Limits

- **Location**: `src/network/Basalt.Network/Transport/TcpTransport.cs:97-131` (ConnectAsync)
- **Description**: `AcceptLoopAsync` (line 341-358) enforces both `MaxTotalConnections` (200) and `MaxConnectionsPerIp` (3) for inbound connections. However, `ConnectAsync` performs no such checks. It also does not call `_connectionsPerIp.AddOrUpdate` or populate `_peerIpMap`, so outbound connections are invisible to the per-IP counter. A compromised or misconfigured node initiating many outbound connections to the same target could bypass the intended limits.
- **Impact**: The per-IP limit can be circumvented for outbound connections, reducing the effectiveness of the DoS protection. Additionally, when outbound connections disconnect, `RemoveConnection` will not find them in `_peerIpMap`, so the IP counter decrement is skipped (harmless but inconsistent).
- **Recommendation**: Add per-IP and total connection limit checks to `ConnectAsync`. After successful TCP connect, extract the remote IP, check limits, and populate `_connectionsPerIp` and `_peerIpMap`:
  ```csharp
  if (_connections.Count >= MaxTotalConnections)
  {
      client.Dispose();
      throw new InvalidOperationException("Total connection limit reached");
  }
  var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
  var currentCount = _connectionsPerIp.GetOrAdd(remoteIp, 0);
  if (currentCount >= MaxConnectionsPerIp)
  {
      client.Dispose();
      throw new InvalidOperationException($"Per-IP limit reached for {remoteIp}");
  }
  _connectionsPerIp.AddOrUpdate(remoteIp, 1, (_, c) => c + 1);
  _peerIpMap[tempId] = remoteIp;
  ```
- **Severity**: High

### H-2: `BlockCodec.DeserializeBlock` Does Not Validate Transaction Count

- **Location**: `src/network/Basalt.Network/BlockCodec.cs:131`
- **Description**: `DeserializeBlock` reads the transaction count as `(int)reader.ReadVarInt()` with no bounds check. `MessageCodec` applies `MaxArrayCount` (10,000) to all deserialized arrays, but `BlockCodec` is a separate codec and does not apply any limit. A crafted block payload with a varint encoding a very large count (e.g., 2 billion) causes `new List<Transaction>(txCount)` to attempt a massive heap allocation.
- **Impact**: A malicious peer sending a `BlockPayloadMessage` or `SyncResponseMessage` with a crafted block blob can cause an out-of-memory crash on the receiving node.
- **Recommendation**: Add a `MaxTransactionCount` constant and validate:
  ```csharp
  int txCount = (int)reader.ReadVarInt();
  if (txCount < 0 || txCount > MaxTransactionCount)
      throw new InvalidOperationException($"Transaction count {txCount} exceeds limit");
  ```
  A reasonable limit is 10,000 (matching `MessageCodec.MaxArrayCount`).
- **Severity**: High

### H-3: `BlockCodec.ReadTransaction` Does Not Validate ComplianceProof Count

- **Location**: `src/network/Basalt.Network/BlockCodec.cs:200`
- **Description**: `ReadTransaction` reads `proofCount` as `(int)reader.ReadVarInt()` without bounds checking. A crafted transaction with a varint encoding a large proof count causes `new ComplianceProof[proofCount]` to attempt a massive allocation.
- **Impact**: Same as H-2 — OOM crash from a crafted transaction within a block payload.
- **Recommendation**: Add a `MaxProofCount` constant (e.g., 32) and validate before allocation.
- **Severity**: High

### H-4: `BlockRequestMessage.Count` Not Validated During Deserialization

- **Location**: `src/network/Basalt.Network/MessageCodec.cs:466-475` (ReadBlockRequest)
- **Description**: `ReadBlockRequest` reads `Count` as `reader.ReadInt32()` without validation. Unlike `ReadSyncRequest` (line 548-554) which validates against `MaxSyncBlocks`, `ReadBlockRequest` allows any int value including negative numbers or extremely large counts. The handler serving this request could attempt to read millions of blocks from storage.
- **Impact**: A peer sending a `BlockRequest` with `Count = Int32.MaxValue` could cause the serving node to attempt loading ~2 billion blocks, exhausting CPU and memory.
- **Recommendation**: Add validation matching `ReadSyncRequest`:
  ```csharp
  if (count <= 0 || count > MaxSyncBlocks)
      throw new InvalidOperationException($"Invalid block request count: {count}");
  ```
- **Severity**: High

---

## Medium Severity

### M-1: `SenderId` in Message Header Not Verified Against Authenticated Peer

- **Location**: `src/network/Basalt.Network/MessageCodec.cs:211`, `src/network/Basalt.Network/Transport/TcpTransport.cs:405-408`
- **Description**: `MessageCodec.Deserialize` reads the 32-byte `SenderId` from the message header, but nowhere in the stack is this verified against the authenticated `PeerId` of the TCP connection that delivered the message. `TcpTransport.HandleMessageReceived` passes the connection's PeerId, not the header SenderId. However, higher-level handlers that deserialize the message get a SenderId that could be forged by the sending peer.
- **Impact**: A peer could set a different `SenderId` in their messages to impersonate another peer. The impact depends on how higher-level code uses `message.SenderId` vs. the connection PeerId. For consensus messages, this could be used to attribute votes to the wrong validator (though BLS signature verification would catch this).
- **Recommendation**: After deserialization, verify `message.SenderId == connectionPeerId` and reject mismatches:
  ```csharp
  if (message.SenderId != connectionPeerId)
      throw new InvalidOperationException("SenderId mismatch: message sender does not match authenticated peer");
  ```
- **Severity**: Medium

### M-2: `HandshakeResult.SharedSecret` Not Zeroed After Use

- **Location**: `src/network/Basalt.Network/Transport/HandshakeProtocol.cs:432-472` (HandshakeResult class)
- **Description**: `HandshakeResult.SharedSecret` is a `byte[]` that persists on the heap after the `TransportEncryption` instance is created from it. The `HandshakeProtocol` correctly zeros its ephemeral X25519 private key in `ZeroEphemeralKeys()` (line 401-409), but the derived shared secret in the `HandshakeResult` is never zeroed. The caller uses it to create `TransportEncryption`, but the `HandshakeResult` object and its `SharedSecret` remain in memory.
- **Impact**: If a memory dump or heap inspection occurs, the shared secret could be recovered, potentially compromising the encrypted transport session.
- **Recommendation**: Zero `SharedSecret` after `TransportEncryption` is created, or make `HandshakeResult` implement `IDisposable` with a `ZeroMemory` call. The caller should zero it after use:
  ```csharp
  CryptographicOperations.ZeroMemory(result.SharedSecret);
  ```
- **Severity**: Medium

### M-3: `TransportEncryption` Nonce Anti-Replay Has No Gap Tolerance

- **Location**: `src/network/Basalt.Network/Transport/TransportEncryption.cs:114-118`
- **Description**: The anti-replay check requires `receivedCounter >= expectedMin` (strictly monotonically increasing). If a message is encrypted with nonce N+2 and arrives before nonce N+1 (due to reordering), nonce N+1 is permanently rejected. While TCP guarantees ordering for a single connection, there's an edge case: if `Encrypt()` is called concurrently from multiple threads (using `Interlocked.Increment`), the resulting encrypted frames could be written to the socket out of order if the thread scheduling causes frame N+2 to be sent before N+1.
- **Impact**: Under high concurrency with many threads encrypting simultaneously, out-of-order nonce delivery could cause decryption failures and connection drops. `PeerConnection.SendAsync` uses a `SemaphoreSlim` which serializes writes, so this is mitigated — but only because `Encrypt()` happens inside the send lock. If `Encrypt()` were called before acquiring the send lock, this would be an active bug.
- **Recommendation**: Document that `Encrypt()` must be called within the send lock's critical section to maintain nonce ordering. Alternatively, consider a sliding window approach for the receiver.
- **Severity**: Medium

### M-4: `GossipService.HandleMessage` Does Not Mark Messages As Seen

- **Location**: `src/network/Basalt.Network/GossipService.cs:109-115`
- **Description**: `HandleMessage` invokes `OnMessageReceived` without calling `MarkMessageSeen`. This means if the same message arrives from multiple peers (which is expected in gossip), the callback fires for each delivery. The deduplication at the gossip level only happens when `Broadcast*` is called.
- **Impact**: Higher-level handlers (e.g., NodeCoordinator) receive duplicate consensus votes, transaction announcements, and block announcements from different peers. While the consensus layer likely handles this via its own dedup, it creates unnecessary CPU overhead for deserialization and validation.
- **Recommendation**: Mark messages as seen in `HandleMessage` to suppress duplicate deliveries:
  ```csharp
  var serialized = SerializeMessage(message);
  var msgId = Blake3Hasher.Hash(serialized);
  if (IsMessageSeen(msgId))
      return;
  MarkMessageSeen(msgId);
  OnMessageReceived?.Invoke(sender, message);
  ```
  Note: This requires computing the message hash, which adds some cost. An alternative is to accept the duplication and rely on higher-level dedup.
- **Severity**: Medium

### M-5: `ReputationScorer.IsRewardCapped` Has TOCTOU Race

- **Location**: `src/network/Basalt.Network/ReputationScorer.cs:227-252`
- **Description**: `IsRewardCapped` reads the current count from `ConcurrentDictionary`, checks against the cap, and increments — all non-atomically. Two threads processing rewards for the same peer simultaneously could both read `Count = 9`, both decide it's under `MaxTxRewardsPerWindow = 10`, and both write `Count = 10`, effectively awarding 11 rewards.
- **Impact**: Minor — the reward cap is best-effort, and exceeding it by 1-2 rewards per window has negligible impact on reputation scores. The race window is small.
- **Recommendation**: Accept as-is (the impact is negligible) or use a lock per peer for the reward window logic.
- **Severity**: Medium

### M-6: `EpisubService.GraftPeer` Does Not Enforce `MaxEagerPeers` Cap

- **Location**: `src/network/Basalt.Network/Gossip/EpisubService.cs:217-224`
- **Description**: `GraftPeer` unconditionally moves a peer from lazy to eager tier without checking `MaxEagerPeers` (12). The `HandleFullMessage` method (line 198-212) correctly checks the cap, and `RebalanceTiers` prunes excess eager peers. However, a malicious peer sending GRAFT messages directly would be promoted to the eager tier without the cap check.
- **Impact**: An attacker sending GRAFT messages could force many of their Sybil nodes into the eager tier, exceeding the intended cap. This increases the attacker's share of eager bandwidth.
- **Recommendation**: Add `MaxEagerPeers` check to `GraftPeer`:
  ```csharp
  public void GraftPeer(PeerId peerId)
  {
      if (_lazyPeers.TryRemove(peerId, out _))
      {
          if (_eagerPeers.Count < MaxEagerPeers)
          {
              _eagerPeers.TryAdd(peerId, 0);
              _logger.LogDebug("Grafted {PeerId} to eager tier", peerId);
          }
          else
          {
              _lazyPeers.TryAdd(peerId, 0); // Re-add to lazy
              _logger.LogDebug("Graft rejected for {PeerId}: eager tier full", peerId);
          }
      }
  }
  ```
- **Severity**: Medium

### M-7: `KademliaTable.AddOrUpdate` IP Diversity Check Outside Write Lock

- **Location**: `src/network/Basalt.Network/DHT/KademliaTable.cs:53-82`
- **Description**: The IP diversity check (lines 60-71) reads existing peers from the bucket via `_buckets[bucket].GetPeers()` before acquiring the `_rwLock` write lock. Between the check and the write lock acquisition, another thread could add a peer from the same subnet, causing the count to exceed `MaxPeersPerSubnetPerBucket`.
- **Impact**: The /24 subnet limit (2 per bucket) could be exceeded by 1 under concurrent adds. This slightly weakens the anti-Sybil protection.
- **Recommendation**: Move the IP diversity check inside the write lock:
  ```csharp
  _rwLock.EnterWriteLock();
  try
  {
      string peerSubnet = GetSubnet24(peer.Host);
      var existingPeers = _buckets[bucket].GetPeers();
      bool isExisting = existingPeers.Any(p => p.Id == peer.Id);
      if (!isExisting)
      {
          int subnetCount = existingPeers.Count(p => GetSubnet24(p.Host) == peerSubnet);
          if (subnetCount >= MaxPeersPerSubnetPerBucket)
              return false;
      }
      return _buckets[bucket].InsertOrUpdate(peer);
  }
  finally { _rwLock.ExitWriteLock(); }
  ```
- **Severity**: Medium

---

## Low Severity / Recommendations

### L-1: `BroadcastAsync` Sends Sequentially, Not in Parallel

- **Location**: `src/network/Basalt.Network/Transport/TcpTransport.cs:166-192`
- **Description**: `BroadcastAsync` iterates all connections sequentially, calling `SendAsync` on each. If one peer is slow (TCP buffer full, congested link), all subsequent peers are delayed. The 120s frame read timeout on the remote side means a stalled send could block the broadcast loop for a long time.
- **Impact**: A single slow or malicious peer can delay broadcast delivery to all other peers. For consensus messages, this increases message latency.
- **Recommendation**: Use `Task.WhenAll` with per-send timeouts for parallel broadcast:
  ```csharp
  var tasks = snapshot.Select(async (kvp) => {
      try { await connection.SendAsync(data).ConfigureAwait(false); }
      catch { RemoveConnection(peerId); }
  });
  await Task.WhenAll(tasks);
  ```
- **Severity**: Low

### L-2: `PeerConnection` Allocates `new byte[]` Per Received Frame

- **Location**: `src/network/Basalt.Network/Transport/PeerConnection.cs:100-102`
- **Description**: Every received frame allocates `new byte[messageLength]` on the heap. For high-throughput scenarios (e.g., sync with hundreds of blocks), this creates significant GC pressure. The code has a comment noting `NET-L04: Consider ArrayPool for large payloads in future optimization`.
- **Impact**: Under high message rates, GC pauses could cause latency spikes. This is a performance concern, not a correctness issue.
- **Recommendation**: Use `ArrayPool<byte>.Shared.Rent()` for payloads above a threshold (e.g., 4KB), with a wrapper that returns the buffer after processing.
- **Severity**: Low

### L-3: `GossipService` and `EpisubService` Duplicate Seen-Message Logic

- **Location**: `src/network/Basalt.Network/GossipService.cs:183-215`, `src/network/Basalt.Network/Gossip/EpisubService.cs:398-405`
- **Description**: Both services maintain independent `ConcurrentDictionary<Hash256, long>` seen-message caches with identical cleanup logic. The `GossipService` uses a 100K cap with 60s TTL; `EpisubService` uses a 200K cap with 120s TTL. A message seen by one service is not visible to the other.
- **Impact**: Code duplication and potential inconsistency. A message could be accepted by one gossip path and rejected by the other due to different TTLs.
- **Recommendation**: Extract seen-message tracking into a shared `MessageDeduplicator` class used by both services.
- **Severity**: Low

### L-4: `NodeLookup` Queries Are Synchronous

- **Location**: `src/network/Basalt.Network/DHT/NodeLookup.cs:38-107`
- **Description**: The `Lookup` method is synchronous and queries peers sequentially within each round (line 70-100). Kademlia specifies querying `alpha` (3) peers in parallel, but this implementation processes them in a `foreach` loop. The `QueryPeer` delegate is also synchronous (`Func<PeerId, PeerId, List<PeerInfo>>?`).
- **Impact**: Lookup latency is `O(rounds * alpha * query_time)` instead of `O(rounds * query_time)`. In a live network with network latency, this significantly slows peer discovery.
- **Recommendation**: Make `QueryPeer` async and use `Task.WhenAll` for parallel queries within each round.
- **Severity**: Low

### L-5: `FindNodeResponseMessage.Host` Not Validated

- **Location**: `src/network/Basalt.Network/MessageCodec.cs:574-581`
- **Description**: `ReadFindNodeResponse` deserializes `Host` as a raw string without validation. A malicious peer could return hostnames pointing to internal services (SSRF-like), extremely long strings, or format-string-like content that could interfere with logging.
- **Impact**: Low — the host is stored in `PeerInfo` and logged, but the `NodeLookup` comment at line 88-90 notes that lookup results are NOT added to the routing table. Connection attempts would be needed for exploitation.
- **Recommendation**: Validate host string length and format:
  ```csharp
  if (host.Length > 256)
      throw new InvalidOperationException("Host string too long in FindNodeResponse");
  ```
- **Severity**: Low

### L-6: `PeerInfo.LastSeen` and `ConnectedAt` Lack Thread-Safety

- **Location**: `src/network/Basalt.Network/PeerInfo.cs:60-61`
- **Description**: `LastSeen` and `ConnectedAt` are regular auto-properties accessed from multiple threads without synchronization. `BestBlockNumber` and `BestBlockHash` were given a lock (`_bestBlockLock`) for atomic updates, but `LastSeen` (written in `UpdatePeerBestBlock` at `PeerManager.cs:163`) has no such protection.
- **Impact**: Torn reads on `DateTimeOffset` (16 bytes, not atomic on all platforms). On x64, `DateTimeOffset` is likely written atomically due to 8-byte alignment, but this is not guaranteed by the runtime.
- **Recommendation**: Use `Volatile.Write`/`Volatile.Read` for `LastSeen`, or protect with the existing `_bestBlockLock` since it's often updated alongside best block info.
- **Severity**: Low

### L-7: `KademliaTable.GetSubnet24` Returns Full String for IPv6

- **Location**: `src/network/Basalt.Network/DHT/KademliaTable.cs:253-278`
- **Description**: For non-IPv4 addresses (IPv6, hostnames), `GetSubnet24` returns the full string as the "subnet key." Two IPv6 addresses from the same /48 subnet would be treated as different subnets, making the IP diversity check ineffective for IPv6 peers.
- **Impact**: The Sybil protection via IP diversity is only effective for IPv4 peers. An attacker with IPv6 addresses could bypass the per-subnet limit.
- **Recommendation**: Add IPv6 support by extracting the /48 prefix for IPv6 addresses:
  ```csharp
  if (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6)
      return string.Join(":", host.Split(':').Take(3));
  ```
- **Severity**: Low

---

## Test Coverage Gaps

| Gap | Description | Priority |
|-----|-------------|----------|
| **No HandshakeProtocol tests** | No integration tests for the challenge-response authentication flow, chain ID mismatch rejection, genesis hash validation, X25519 key exchange, or simultaneous-connect handling. | High |
| **No PeerManager banning tests** | Ban/unban lifecycle, ban expiry, `OnPeerBanned` callback, banned peer rejection — all untested via `PeerManager`. | High |
| **No malformed message tests** | Oversized arrays (exceeding `MaxArrayCount`), truncated payloads, invalid `VotePhase` values, negative `BlockRequest.Count`, stale timestamps — none tested. | High |
| **No GossipService tests** | Fan-out limit, consensus message dedup, seen message cleanup, reputation reward integration — all untested. | Medium |
| **No connection limit tests** | `MaxTotalConnections` rejection, `MaxConnectionsPerIp` rejection, IP counter decrement on disconnect — untested. | Medium |
| **No BlockCodec tests** | Block and transaction serialization/deserialization roundtrips, oversized data, malformed inputs — untested in the network test project. | Medium |
| **No PeerManager.PruneInactivePeers tests** | Timeout-based pruning, ban-expiry pruning — untested. | Low |
| **No concurrent message handling tests** | Race conditions in broadcast, concurrent sends/receives, PeerId updates during active read loops — untested. | Low |
| **No NodeLookup with remote query tests** | Lookup with `QueryPeer` delegate set, convergence behavior, max-round termination — only local-table lookup tested. | Low |
| **No Episub RebalanceTiers tests** | Promotion/demotion based on reputation, tier rebalancing with many peers — untested. | Low |

---

## Positive Findings

1. **Strong mutual authentication**: The handshake protocol implements mutual Ed25519 challenge-response authentication with domain-separated BLAKE3 hashes (`basalt-hello-v1`, `basalt-ack-v1`), 32-byte random nonces, and chain ID binding. This prevents identity spoofing and cross-chain injection.

2. **Ephemeral X25519 key exchange with identity binding**: X25519 ephemeral keys are Ed25519-signed, binding them to the authenticated node identity and preventing MITM key substitution. The `ZeroEphemeralKeys()` in a `finally` block ensures forward secrecy.

3. **AES-256-GCM with directional keys**: Transport encryption uses HKDF-SHA256 to derive separate send/receive keys from the shared secret with distinct info strings (`basalt-transport-v1-init/resp`). The anti-replay nonce counter prevents message replay. Derived key material is zeroed after cipher creation.

4. **Robust connection limits**: Per-IP (3) and total (200) connection limits for inbound connections prevent resource exhaustion. The IP counter tracks mapping via `_peerIpMap` and properly decrements on disconnect.

5. **Thread-safe framing**: `PeerConnection` uses `Interlocked.Exchange` for atomic dispose, `SemaphoreSlim` for serialized sends to prevent interleaved frames, and combined header+payload writes to avoid TCP fragmentation.

6. **Message size validation**: Both the 4-byte length prefix (`MaxMessageSize = 16MB`) and per-array `MaxArrayCount = 10,000` prevent memory exhaustion from oversized messages. `ReadHashArray` also validates against remaining bytes.

7. **120-second frame read timeout**: Per-frame `CancellationTokenSource.CancelAfter(FrameReadTimeout)` detects stale connections that would otherwise hold resources indefinitely (slowloris mitigation).

8. **Timestamp drift validation**: `MessageCodec.IsTimestampValid` rejects messages with timestamps more than 30 seconds from current time, preventing replay of old messages and blocking future-dated messages.

9. **Standard Kademlia eviction policy**: `KBucket.InsertOrUpdate` (NET-H11) correctly rejects newcomers when the bucket is full, preferring long-lived nodes. This is the standard Sybil-resistant eviction policy.

10. **Outbound-protected DHT slots**: `KademliaTable` reserves up to 4 outbound-protected peer slots (NET-C05) that cannot be removed, preventing eclipse attacks from displacing critical peers.

11. **IP diversity in routing table**: The /24 subnet limit (`MaxPeersPerSubnetPerBucket = 2`) in `KademliaTable` limits the effectiveness of Sybil attacks from a single IP range.

12. **Bounded node lookup**: `NodeLookup` enforces `MaxLookupRounds = 20` and `MaxCandidates = 60` to prevent infinite iteration and memory exhaustion. Lookup results are NOT added to the routing table.

13. **Episub IWANT rate limiting and correlation**: IWANT requests are rate-limited (10/second per peer), truncated (max 100 IDs), and correlated against previously sent IHAVEs (NET-M16) to prevent cache probing and amplification.

14. **Reputation system with abuse resistance**: Diminishing returns (capped rewards per time window), minor penalty floor (cannot drop below `LowRepThreshold` from minor penalties alone), instant ban for severe violations, and active-recovery-only decay prevent both reputation grinding and false-positive bans.

15. **Events instead of public delegates**: All transport/peer callbacks use `event` (NET-L02) instead of public `Action?` fields, preventing accidental overwrite of handlers.

16. **Peer banning with transport disconnect**: `PeerManager.BanPeer` sets a `BannedUntil` timestamp (1 hour default) and fires `OnPeerBanned` to trigger transport-level disconnect. `RegisterPeer` checks ban status before allowing reconnection.

17. **Thread-safe reputation updates**: `PeerInfo.AdjustReputation` uses `Interlocked.CompareExchange` loop (NET-M23) for atomic score adjustment with clamping to [0, 200].

18. **Comprehensive VotePhase validation**: `MessageCodec` validates the `VotePhase` byte during deserialization (NET-M08), rejecting values outside the valid 0-2 range.

19. **Sync request bounds**: `ReadSyncRequest` validates `MaxBlocks` is positive and within `MaxSyncBlocks = 200`, preventing unbounded sync responses.

20. **Shutdown cleanup**: `TcpTransport.StopAsync` awaits all tracked read loop tasks (NET-M04), snapshots connections before disposal (NET-M07), and properly cleans up all resources including the `CancellationTokenSource`.
