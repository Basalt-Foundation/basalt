# Basalt Security & Quality Audit — Network Layer

## Scope

Audit the peer-to-peer networking infrastructure including transport, message encoding, gossip protocols, DHT, peer management, and reputation scoring:

| Project | Path | Description |
|---|---|---|
| `Basalt.Network` | `src/network/Basalt.Network/` | TCP transport, message codec, gossip (Episub), Kademlia DHT, peer management, reputation, encryption |

Corresponding test project: `tests/Basalt.Network.Tests/`

---

## Files to Audit

### Transport
- `Transport/TcpTransport.cs` (~431 lines) — TCP connections with length-prefixed framing (4-byte big-endian)
- `Transport/PeerConnection.cs` (~274 lines) — Individual peer connection lifecycle
- `Transport/HandshakeProtocol.cs` (~473 lines) — Hello/HelloAck handshake with chain ID validation, 5s timeout
- `Transport/TransportEncryption.cs` (~137 lines) — Encrypted transport layer

### Message Protocol
- `Messages.cs` (~326 lines) — All message types (Hello, TxAnnounce, BlockAnnounce, ConsensusProposal, ConsensusVote, ViewChange, AggregateVote, IHave, IWant, Graft, Prune, FindNode, FindNodeResponse)
- `MessageCodec.cs` (~719 lines) — Binary encoding/decoding: `[1B type][32B senderId][8B timestamp][payload]`
- `BlockCodec.cs` (~303 lines) — Block serialization for network transmission

### Gossip
- `GossipService.cs` (~221 lines) — Transaction and block gossip
- `Gossip/EpisubService.cs` (~441 lines) — Episub pub/sub mesh with IHave/IWant/Graft/Prune

### Peer Management
- `PeerId.cs` (~37 lines) — Peer identifier (readonly struct)
- `PeerInfo.cs` (~99 lines) — Peer metadata and state
- `PeerManager.cs` (~201 lines) — Peer discovery, connection management
- `ReputationScorer.cs` (~306 lines) — Peer reputation scoring with penalty deltas

### DHT
- `DHT/KademliaTable.cs` (~324 lines) — Kademlia routing table with XOR distance
- `DHT/KBucket.cs` (~102 lines) — K-bucket for Kademlia
- `DHT/NodeLookup.cs` (~123 lines) — Iterative Kademlia node lookup

---

## Audit Objectives

### 1. Transport Security (CRITICAL)
- Verify TCP framing: 4-byte big-endian length prefix must be validated against maximum message size to prevent memory exhaustion.
- Check for slowloris-style attacks: incomplete messages that hold connections open indefinitely.
- Verify the 5-second handshake timeout is enforced and cannot be bypassed.
- Check that `TransportEncryption` uses authenticated encryption and handles key compromise correctly.
- Verify connection limits per IP and total to prevent connection exhaustion.
- Check for TCP reset attacks and connection hijacking mitigations.

### 2. Handshake Protocol Security (CRITICAL)
- Verify chain ID validation prevents cross-chain message injection.
- Check that HelloAck includes responder identity (NodePublicKey, ListenPort) and the initiator correctly derives PeerId.
- Verify the handshake is resistant to replay attacks.
- Check the dual TCP connection race condition fix: simultaneous connections from both sides must resolve correctly without both dying.
- Verify that `TcpTransport.UpdatePeerId` doesn't set PeerId until TryAdd succeeds and doesn't fire OnPeerDisconnected for duplicates.
- Check that per-connection `HandshakeProtocol` instances (not shared) prevent the X25519 race condition.

### 3. Message Codec Security
- Verify `MessageCodec` correctly validates all message fields during deserialization.
- Check that malformed messages cannot cause:
  - Buffer overflows or out-of-bounds reads
  - Excessive memory allocation
  - Type confusion (wrong message type for payload)
- Verify that `SenderId` in the message header matches the authenticated peer identity.
- Check timestamp validation: reject messages with timestamps too far in the future or past.
- Verify that unknown message types are handled gracefully (not crash the node).

### 4. Gossip Protocol Security
- Verify `GossipService` prevents message amplification (gossip storms).
- Check that seen-message caching prevents infinite re-gossip without consuming unbounded memory.
- Verify that `EpisubService` IHave/IWant/Graft/Prune follow the Episub spec:
  - IHave messages are bounded in size
  - IWant requests are rate-limited
  - Graft/Prune correctly maintain mesh connectivity
- Check for eclipse attacks: a malicious peer shouldn't be able to isolate a node from the gossip mesh.
- Verify that transaction and block announcements are deduplicated correctly.

### 5. Kademlia DHT Security
- Verify XOR distance calculation is correct.
- Check K-bucket management: eviction policy, bucket splitting, stale node detection.
- Verify `NodeLookup` terminates and doesn't loop infinitely.
- Check for Sybil attacks: many fake nodes filling the routing table.
- Verify that DHT responses are validated (a node shouldn't return itself as the closest to a query).
- Check for routing table poisoning via malicious FindNodeResponse messages.

### 6. Reputation System
- Verify `ReputationScorer` correctly tracks peer behavior.
- Check that penalty deltas are proportional and cannot be manipulated (e.g., a peer recovering reputation too quickly).
- Verify that zero or negative reputation peers are disconnected or deprioritized.
- Check for reputation gaming: can a peer alternate good/bad behavior to avoid eviction?

### 7. Peer Manager
- Verify connection lifecycle: discovery → connect → handshake → active → disconnect.
- Check peer eviction when connection limits are reached.
- Verify that bootstrap peer configuration is secure.
- Check that peer state transitions are atomic and consistent.

### 8. Block Codec
- Verify `BlockCodec` serialization/deserialization is consistent with the execution layer's block format.
- Check for malleability: can a block be deserialized and re-serialized to produce a different hash?
- Verify that oversized blocks are rejected before full deserialization.

### 9. Concurrency & Resource Management
- Check for race conditions in concurrent message handling.
- Verify that `TcpTransport : IAsyncDisposable` correctly cleans up all connections on shutdown.
- Check for file descriptor leaks from unclosed sockets.
- Verify that async operations have proper cancellation token support.

### 10. Test Coverage
- Review `tests/Basalt.Network.Tests/` for:
  - TCP transport: connect, disconnect, message send/receive, timeout
  - Handshake: valid handshake, chain ID mismatch, timeout, replay
  - MessageCodec: all message types, malformed messages, boundary sizes
  - Episub: mesh formation, IHave/IWant exchange, Graft/Prune
  - Kademlia: routing table operations, node lookup, XOR distance
  - Reputation: scoring, penalties, recovery, eviction threshold
  - Encryption: key exchange, encrypt/decrypt, nonce handling

---

## Key Context

- TCP transport with length-prefixed framing (4-byte big-endian length header).
- MessageCodec format: `[1 byte MessageType][32 bytes SenderId][8 bytes Timestamp][payload]`.
- Hello/HelloAck handshake with chain ID validation, 5s timeout.
- Known pitfall: dual TCP connection race — both sides connect simultaneously, each keeps different conn → both die. Fix: reconnect loop.
- Known pitfall: `HandshakeProtocol` shared instance had ephemeral keys as instance fields — concurrent connections corrupted X25519 state. Fix: per-connection instances via factory method.
- Known pitfall: `TcpTransport.UpdatePeerId` — don't set PeerId until TryAdd succeeds; don't fire OnPeerDisconnected for dups.
- Known pitfall: HelloAck must include responder identity for initiator to derive correct PeerId.
- Docker devnet: 4 validators, ports 30300-30303 (P2P).

---

## Output Format

Write your findings to `audit/output/10-network.md` with the following structure:

```markdown
# Network Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Remote code execution, connection hijacking, message forgery]

## High Severity
[DoS vectors, protocol violations, data corruption]

## Medium Severity
[Issues to address]

## Low Severity / Recommendations
[Code quality, best practices]

## Test Coverage Gaps
[Untested scenarios]

## Positive Findings
[Well-implemented patterns]
```

For each finding, include:
1. **Location**: File path and line number(s)
2. **Description**: What the issue is
3. **Impact**: What could go wrong (node crash, network partition, consensus manipulation)
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
