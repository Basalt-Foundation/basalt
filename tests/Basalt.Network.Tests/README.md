# Basalt.Network.Tests

Unit tests for Basalt P2P networking: message serialization, Kademlia DHT, TCP transport, Episub gossip protocol, and reputation scoring. **110 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| MessageCodec | 27 | Serialization roundtrips for all message types: Hello, HelloAck, Ping, Pong, consensus messages, block/tx announce, sync, Episub (IHave, IWant, Graft, Prune) |
| KademliaTable | 10 | Insert, `FindClosest`, bucket indexing, removal, capacity limits, distance calculation |
| ReputationScorer | 6 | Score adjustments, ban threshold, decay, peer ranking |
| EpisubIWant | 6 | IWant request/response flow, message ID tracking, deduplication, timeout handling |
| TcpTransport | 6 | TCP connection lifecycle, length-prefixed framing, peer connection management, async disposal |
| Episub | 5 | Eager/lazy tier management, graft/prune, priority vs standard broadcast, rebalancing |

**Total: 110 tests**

## Test Files

- `MessageCodecTests.cs` -- Binary serialization roundtrips for all P2P message types
- `KademliaTests.cs` -- Kademlia DHT routing table: insertion, closest-node lookup, bucket management
- `ReputationTests.cs` -- Peer reputation scoring, ban detection, score decay
- `EpisubIWantTests.cs` -- Episub IWant message handling and lazy-push recovery
- `TcpTransportTests.cs` -- TCP transport layer with length-prefixed framing
- `EpisubTests.cs` -- Episub gossip protocol: eager/lazy push, mesh management

## Running

```bash
dotnet test tests/Basalt.Network.Tests
```
