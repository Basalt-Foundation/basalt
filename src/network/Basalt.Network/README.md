# Basalt.Network

Peer-to-peer networking layer for the Basalt blockchain. Implements peer discovery via Kademlia DHT, message propagation via two-tier Episub gossip, and peer reputation scoring.

## Components

### BlockCodec

Static codec for serializing and deserializing `Block` and `Transaction` for network transmission using the Basalt binary wire format.

```csharp
byte[] serialized = BlockCodec.SerializeTransaction(tx);
Transaction tx = BlockCodec.DeserializeTransaction(data);

byte[] blockBytes = BlockCodec.SerializeBlock(block);
Block block = BlockCodec.DeserializeBlock(blockBytes);
```

Wire format (transaction): Type(1) + Nonce(8) + Sender(20) + To(20) + Value(32) + GasLimit(8) + GasPrice(32) + Data(varint+N) + Priority(1) + ChainId(4) + Signature(64) + SenderPublicKey(32) = 222 bytes fixed + variable data.

Wire format (block): Header(188 bytes fixed + ExtraData) + varint tx count + serialized transactions.

### MessageCodec

Serializes and deserializes all Basalt network messages using `BasaltWriter`/`BasaltReader`.

Wire format: `[1 byte MessageType][32 bytes SenderId][8 bytes Timestamp][payload...]`

Header size is 41 bytes. Stack allocation is used for messages under 8,192 bytes; heap allocation for larger messages up to the 65,536-byte maximum buffer size.

```csharp
byte[] data = MessageCodec.Serialize(message);
NetworkMessage msg = MessageCodec.Deserialize(data);
```

### Transport Layer

TCP transport with length-prefixed framing for peer connections.

**TcpTransport** -- manages inbound and outbound TCP connections. Implements `IAsyncDisposable`.

```csharp
var transport = new TcpTransport(logger);
transport.OnMessageReceived = (peerId, data) => { /* handle */ };
transport.OnPeerConnected = (connection) => { /* handshake */ };
transport.OnPeerDisconnected = (peerId) => { /* cleanup */ };

await transport.StartAsync(port, cancellationToken);
PeerConnection conn = await transport.ConnectAsync(host, port);
await transport.SendAsync(peerId, data);
await transport.BroadcastAsync(data, exclude: peerId);
bool updated = transport.UpdatePeerId(tempId, realId);
transport.DisconnectPeer(peerId);
transport.StartReadLoop(connection);
IReadOnlyCollection<PeerId> connected = transport.ConnectedPeerIds;
await transport.StopAsync();
```

Outbound connections get a temporary `PeerId` derived from `BLAKE3(endpoint_string)`. The caller is responsible for performing the handshake and then calling `UpdatePeerId` with the real identity. `OnPeerConnected` is only fired for inbound connections. If `UpdatePeerId` detects a duplicate connection (simultaneous inbound+outbound), it silently disposes the duplicate and returns `false`.

**PeerConnection** -- wraps a single TCP connection with length-prefixed framing (`[4 bytes big-endian length][N bytes payload]`). Thread-safe for concurrent sends via an internal semaphore. Maximum message size: 16 MB.

```csharp
var connection = new PeerConnection(tcpClient, peerId, onMessageReceived);
await connection.SendAsync(data);
byte[]? response = await connection.ReceiveOneAsync(ct);  // For handshake
await connection.StartReadLoopAsync(ct);
bool alive = connection.IsConnected;
```

**HandshakeProtocol** -- exchanges Hello/HelloAck messages after TCP connection, validates chain ID compatibility with a 5-second timeout:

```csharp
var handshake = new HandshakeProtocol(
    chainId, localPublicKey, localPeerId, listenPort,
    getBestBlockNumber, getBestBlockHash, getGenesisHash, logger);

HandshakeResult result = await handshake.InitiateAsync(connection, ct);  // Outbound
HandshakeResult result = await handshake.RespondAsync(connection, ct);   // Inbound

if (result.IsSuccess)
{
    PeerId remotePeerId = result.PeerId;
    PublicKey remotePubKey = result.PeerPublicKey;
    string remoteHost = result.PeerHost;
    int remotePort = result.PeerPort;
    ulong peerBestBlock = result.PeerBestBlock;
    Hash256 peerBestBlockHash = result.PeerBestBlockHash;
}
```

The `HelloAckMessage` includes the responder's identity (`NodePublicKey`, `ListenPort`, `BestBlockNumber`, `BestBlockHash`) so the initiator can derive the correct remote `PeerId`. The `InitiateAsync` method also handles simultaneous-connect (both sides send Hello): it validates the peer's Hello and responds with an Ack.

Validation rules: chain ID must match, protocol version must be >= 1.

### PeerManager

Central peer registry and lifecycle management.

```csharp
var manager = new PeerManager(logger, maxPeers: 50);

PeerInfo peer = manager.AddStaticPeer(peerId, publicKey, host, port);
bool accepted = manager.RegisterPeer(peerInfo);   // returns false if max peers reached
PeerInfo? info = manager.GetPeer(peerId);
manager.UpdatePeerBestBlock(peerId, blockNumber, blockHash);
manager.DisconnectPeer(peerId, "protocol violation");
manager.BanPeer(peerId, "double sign detected");
int pruned = manager.PruneInactivePeers(TimeSpan.FromMinutes(5));

IReadOnlyCollection<PeerInfo> connected = manager.ConnectedPeers;
IReadOnlyCollection<PeerInfo> all = manager.AllPeers;
int count = manager.ConnectedCount;
```

Peer states: `Disconnected` -> `Connecting` -> `Handshaking` -> `Connected` (or `Banned`).

### PeerId

256-bit peer identifier derived from the node's public key via BLAKE3.

```csharp
PeerId id = PeerId.FromPublicKey(publicKey);
string hex = id.ToHexString();
Hash256 raw = id.AsHash256();
```

Implements `IEquatable<PeerId>` and `IComparable<PeerId>`.

### Message Protocol

All messages inherit from `NetworkMessage` and carry a `MessageType` discriminator, a `SenderId` (`PeerId`), and a `Timestamp` (Unix milliseconds).

| Type ID | Category | Messages |
|---------|----------|----------|
| 0x01-0x02 | Handshake | `Hello`, `HelloAck` |
| 0x03-0x04 | Heartbeat | `Ping`, `Pong` |
| 0x10-0x12 | Transactions | `TxAnnounce`, `TxRequest`, `TxPayload` |
| 0x20-0x22 | Blocks | `BlockAnnounce`, `BlockRequest`, `BlockPayload` |
| 0x30-0x32 | Consensus | `ConsensusProposal`, `ConsensusVote`, `ConsensusViewChange` |
| 0x40-0x41 | Sync | `SyncRequest`, `SyncResponse` |
| 0x50-0x53 | Gossip | `IHave`, `IWant`, `Graft`, `Prune` |
| 0x60-0x61 | DHT | `FindNode`, `FindNodeResponse` |

Consensus messages use BLS signatures: `ConsensusProposalMessage` carries a `BlsSignature ProposerSignature`; `ConsensusVoteMessage` carries `BlsSignature VoterSignature` and `BlsPublicKey VoterPublicKey` along with a `VotePhase` (Prepare=0, PreCommit=1, Commit=2); `ViewChangeMessage` carries `BlsSignature VoterSignature` and `BlsPublicKey VoterPublicKey`.

### GossipService

Eager-push gossip for transaction and block propagation. Deduplicates messages with a 60-second seen cache (100K capacity). Communicates via events rather than direct transport access.

```csharp
var gossip = new GossipService(peerManager, logger);
gossip.OnSendMessage += (peerId, data) => { /* wire to transport */ };
gossip.OnMessageReceived += (peerId, message) => { /* handle */ };

gossip.BroadcastTransaction(txHash, excludePeer: sourcePeerId);
gossip.BroadcastBlock(number, hash, parentHash, excludePeer: sourcePeerId);
gossip.BroadcastConsensusMessage(voteMessage);
gossip.HandleMessage(sender, message);
gossip.SendToPeer(peerId, message);
```

### EpisubService

Two-tier gossip optimization: priority eager push for high-value messages (consensus, blocks), lazy IHAVE/IWANT for standard traffic.

```csharp
var episub = new EpisubService(peerManager, logger);
episub.OnSendMessage += (peerId, data) => { /* wire to transport */ };
episub.OnMessageReceived += (peerId, message) => { /* handle */ };

// Broadcasting
episub.BroadcastPriority(msgId, consensusMsg, excludePeer: source);  // Eager push to eager peers + IHAVE to lazy
episub.BroadcastStandard(msgId, txAnnounce, excludePeer: source);    // Lazy IHAVE to all connected peers

// Message handling
episub.HandleIHave(sender, messageId);               // Process IHAVE, send IWANT if unseen
episub.HandleFullMessage(sender, messageId, message); // Process full message; promotes lazy sender to eager
IEnumerable<(Hash256, byte[])> responses = episub.HandleIWant(sender, messageIds); // Respond to IWANT

// Message cache
episub.CacheMessage(messageId, serializedData);
bool found = episub.TryGetCachedMessage(messageId, out byte[]? data);

// Peer tier management
episub.GraftPeer(peerId);   // Promote lazy -> eager
episub.PrunePeer(peerId);   // Demote eager -> lazy
episub.RebalanceTiers();    // Periodic rebalancing (promotes best-reputation lazy, prunes worst eager)
episub.OnPeerConnected(peerId);
episub.OnPeerDisconnected(peerId);
episub.CleanupSeenMessages();  // Remove expired entries + stale cache/IHAVE sources

// Properties
int eager = episub.EagerPeerCount;    // Target: 6
int lazy = episub.LazyPeerCount;      // Target: 12
int cached = episub.CachedMessageCount; // Max: 50,000
```

Seen message cache: 200,000 capacity, 2-minute TTL.

### Kademlia DHT

Distributed hash table for peer discovery with 256-bit ID space.

```csharp
var table = new KademliaTable(localPeerId);
bool added = table.AddOrUpdate(peerInfo);
bool removed = table.Remove(peerId);
List<PeerInfo> closest = table.FindClosest(target, count: 20);
List<PeerInfo> all = table.GetAllPeers();
int total = table.PeerCount;
int bucket = table.GetBucketIndex(remotePeerId);

var lookup = new NodeLookup(table, logger);
lookup.QueryPeer = (from, target) => /* ask peer */;
List<PeerInfo> found = lookup.Lookup(targetId);
```

Parameters: k=20 (bucket size), alpha=3 (concurrent lookups), 256 buckets. Bucket eviction is reputation-aware: a new peer replaces the least-recently-seen entry only if its reputation score is higher.

### ReputationScorer

Tracks peer behavior and automatically disconnects or bans misbehaving peers.

```csharp
var scorer = new ReputationScorer(peerManager, logger);

// Block events
scorer.RecordValidBlock(peerId);        // +5
scorer.RecordInvalidBlock(peerId);      // -50

// Transaction events
scorer.RecordValidTransaction(peerId);    // +1
scorer.RecordInvalidTransaction(peerId);  // -10

// Consensus events
scorer.RecordValidConsensusVote(peerId);    // +3
scorer.RecordInvalidConsensusVote(peerId);  // -30

// Network events
scorer.RecordTimelyResponse(peerId);   // +2
scorer.RecordTimeout(peerId);          // -5
scorer.RecordProtocolViolation(peerId); // -20

// Queries
bool shouldDrop = scorer.ShouldDisconnect(peerId);  // Score <= BanThreshold (10)
bool lowRep = scorer.IsLowReputation(peerId);        // Score <= LowRepThreshold (30)
List<PeerInfo> ranked = scorer.GetPeersByReputation(); // Best first
scorer.DecayScores();  // Periodic decay toward DefaultScore (100)
```

Constants: `MaxScore = 200`, `DefaultScore = 100`, `BanThreshold = 10`, `LowRepThreshold = 30`.

All delta values are defined in `ReputationScorer.Deltas`: `ValidBlock (+5)`, `InvalidBlock (-50)`, `ValidTransaction (+1)`, `InvalidTransaction (-10)`, `ValidConsensusVote (+3)`, `InvalidConsensusVote (-30)`, `TimelyResponse (+2)`, `SlowResponse (-1)`, `Timeout (-5)`, `ProtocolViolation (-20)`, `DuplicateMessage (-1)`, `SuccessfulHandshake (+10)`, `FailedHandshake (-15)`, `HeartbeatSuccess (+1)`, `HeartbeatFailure (-3)`.

When a peer's score drops to or below `BanThreshold`, the scorer automatically calls `PeerManager.BanPeer`.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Hash256, Address, PublicKey, Signature, BlsPublicKey, BlsSignature |
| `Basalt.Crypto` | BLAKE3 for peer ID derivation |
| `Basalt.Codec` | BasaltWriter/BasaltReader for message and block serialization |
| `Basalt.Execution` | Block, Transaction, BlockHeader types used by BlockCodec |
| `Microsoft.Extensions.Logging.Abstractions` | Structured logging |
