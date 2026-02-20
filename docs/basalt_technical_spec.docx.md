

**BASALT**

Technical Specification

The Enterprise-Grade Blockchain on .NET 9 AOT

Version 1.0  —  February 2026

**CONFIDENTIAL — Internal Use Only**

Based on Design Plan v1.0  —  .NET 9 AOT Only

# **Revision History**

| Version | Date | Author | Description |
| :---- | :---- | :---- | :---- |
| 0.1 | 2026-02-14 | Architecture Team | Initial draft from design plan |
| 1.0 | 2026-02-14 | Architecture Team | Complete specification v1.0 |

# **Table of Contents**

[**Revision History	2**](#heading=)

[**Table of Contents	3**](#heading=)

[**1\. Introduction	4**](#heading=)

[1.1 Purpose	4](#heading=)

[1.2 Scope	4](#heading=)

[1.3 Definitions and Abbreviations	4](#heading=)

[1.4 Reference Documents	5](#heading=)

[1.5 Design Constraints	5](#heading=)

[**2\. Cryptographic Primitives	6**](#heading=)

[2.1 Hash Functions	6](#heading=)

[2.2 Digital Signatures	6](#heading=)

[2.2.1 Transaction Signatures	6](#heading=)

[2.2.2 Consensus Signatures (BLS)	6](#heading=)

[2.3 Key Derivation and Address Format	7](#heading=)

[2.3.1 Address Derivation	7](#heading=)

[2.3.2 Key Storage	7](#heading=)

[2.4 Serialization	7](#heading=)

[**3\. Network Layer (P2P)	8**](#heading=)

[3.1 Transport	8](#heading=)

[3.1.1 Primary Transport: QUIC	8](#heading=)

[3.1.2 Fallback Transport: TCP	8](#heading=)

[3.2 Peer Discovery	8](#heading=)

[3.2.1 Kademlia DHT	8](#heading=)

[3.2.2 Bootstrap Nodes	9](#heading=)

[3.2.3 Peer Reputation	9](#heading=)

[3.3 Gossip Protocol (Episub)	9](#heading=)

[3.3.1 Two-Tier Propagation	9](#heading=)

[3.3.2 Backpressure and Flood Protection	9](#heading=)

[3.4 Message Types	10](#heading=)

[**4\. Consensus Layer — BasaltBFT	11**](#heading=)

[4.1 Protocol Overview	11](#heading=)

[4.2 Block Structure	11](#heading=)

[4.3 Consensus Phases	12](#heading=)

[4.3.1 PREPARE Phase	12](#heading=)

[4.3.2 PRE-COMMIT Phase	12](#heading=)

[4.3.3 COMMIT Phase	12](#heading=)

[4.3.4 Pipelining	12](#heading=)

[4.4 Leader Selection	12](#heading=)

[4.5 View Change	13](#heading=)

[4.6 Validator Management	13](#heading=)

[4.6.1 Staking Requirements	13](#heading=)

[4.6.2 Slashing Conditions	13](#heading=)

[4.6.3 Key Rotation	13](#heading=)

[4.7 Enterprise Subnets	14](#heading=)

[**5\. Execution Layer — BasaltVM	15**](#heading=)

[5.1 Transaction Structure	15](#heading=)

[5.2 Transaction Validation	15](#heading=)

[5.3 Execution Mode 1: Native C\# AOT	15](#heading=)

[5.3.1 Compilation Pipeline	15](#heading=)

[5.3.2 Sandbox Specification	16](#heading=)

[5.3.3 Host Interface	16](#heading=)

[5.4 Execution Mode 2: WASM	17](#heading=)

[5.5 Gas Model	17](#heading=)

[5.6 Gas Sponsoring (Enterprise)	17](#heading=)

[**6\. Storage Layer	19**](#heading=)

[6.1 State Database (MPT)	19](#heading=)

[6.1.1 Account State	19](#heading=)

[6.1.2 Trie Node Types	19](#heading=)

[6.1.3 RocksDB Configuration	19](#heading=)

[6.2 Block Database	19](#heading=)

[6.3 Receipt Database	20](#heading=)

[6.4 State Pruning and Expiry	20](#heading=)

[**7\. API Layer	21**](#heading=)

[7.1 gRPC API (High Performance)	21](#heading=)

[7.1.1 Service Definitions	21](#heading=)

[7.2 REST API (Compatibility)	21](#heading=)

[7.3 GraphQL API (Complex Queries)	22](#heading=)

[7.4 Rate Limiting and Authentication	22](#heading=)

[**8\. Smart Contract SDK	23**](#heading=)

[8.1 Contract Model	23](#heading=)

[8.1.1 Contract Anatomy	23](#heading=)

[8.1.2 Storage Primitives	23](#heading=)

[8.2 Roslyn Analyzers	24](#heading=)

[8.3 Token Standards	24](#heading=)

[8.4 Testing Framework	25](#heading=)

[**9\. Compliance Module	26**](#heading=)

[9.1 Identity Registry	26](#heading=)

[9.1.1 Data Model	26](#heading=)

[9.1.2 KYC Providers	26](#heading=)

[9.2 Compliance Engine	26](#heading=)

[9.3 GDPR Module	27](#heading=)

[9.4 MiCA Compliance	27](#heading=)

[9.5 Audit Trail	27](#heading=)

[**10\. Confidentiality	28**](#heading=)

[10.1 Confidential Transactions (ZK-SNARKs)	28](#heading=)

[10.1.1 Proof Circuit	28](#heading=)

[10.1.2 Performance	28](#heading=)

[10.2 Private State Channels	28](#heading=)

[10.3 TEE Enclaves (Optional)	28](#heading=)

[**11\. Tokenomics Protocol	30**](#heading=)

[11.1 Fee Mechanism	30](#heading=)

[11.2 Fee Distribution and Burn	30](#heading=)

[11.3 Staking Rewards	30](#heading=)

[11.4 Gas Abstraction	31](#heading=)

[**12\. Non-Functional Requirements	32**](#heading=)

[12.1 Performance Targets	32](#heading=)

[12.2 Availability and Reliability	32](#heading=)

[12.3 Security Requirements	32](#heading=)

[12.4 Observability	33](#heading=)

[**13\. Acceptance Criteria	34**](#heading=)

[13.1 Network Layer	34](#heading=)

[13.2 Consensus	34](#heading=)

[13.3 Execution	34](#heading=)

[13.4 Storage	34](#heading=)

[13.5 API	34](#heading=)

[13.6 Smart Contracts	35](#heading=)

[13.7 End-to-End	35](#heading=)

[**14\. Appendices	36**](#heading=)

[Appendix A: Chain Parameters	36](#heading=)

[Appendix B: System Contract Addresses	36](#heading=)

[Appendix C: Error Codes	37](#heading=)

# **1\. Introduction**

## **1.1 Purpose**

This document defines the technical specification for Basalt, a Layer 1 blockchain built entirely on .NET 9 with Native AOT compilation. It translates the Basalt Design Plan v1.0 into implementable specifications covering protocol behavior, data structures, interfaces, algorithms, and acceptance criteria.

This specification serves as the authoritative reference for all implementation work across Phases 1 through 3 of the project roadmap.

## **1.2 Scope**

This specification covers the following subsystems:

* Network Layer (P2P transport, peer discovery, gossip protocol)

* Consensus Layer (BasaltBFT protocol, validator management, finality)

* Execution Layer (BasaltVM, C\# AOT sandbox, WASM runtime, gas metering)

* Storage Layer (state database, block store, receipt indexing, pruning)

* API Layer (gRPC, REST, GraphQL interfaces)

* Smart Contract SDK (contract model, token standards, Roslyn analyzers)

* Compliance Module (identity registry, compliance engine, GDPR, MiCA)

* Confidentiality (ZK-SNARKs, private state channels, TEE enclaves)

* Cryptographic Primitives (hashing, signatures, key management)

* Tokenomics Protocol (fee model, burn mechanism, staking economics)

## **1.3 Definitions and Abbreviations**

| Term | Definition |
| :---- | :---- |
| AOT | Ahead-of-Time compilation. .NET Native AOT compiles IL to native machine code at build time. |
| BFT | Byzantine Fault Tolerant. Consensus protocols that tolerate up to f \= (n−1)/3 malicious nodes. |
| BLS | Boneh–Lynn–Shacham signature scheme on BLS12-381 curve. Used for aggregated signatures. |
| BST | Basalt Token. The native token of the Basalt network. |
| DXA | Device-independent units (1/20 of a point, 1440 \= 1 inch). |
| Gas | Unit of computation cost for executing transactions on BasaltVM. |
| MPT | Merkle Patricia Trie. Authenticated data structure for state storage. |
| QUIC | UDP-based multiplexed transport protocol with built-in TLS 1.3. |
| RWA | Real-World Assets. Physical assets represented as on-chain tokens. |
| TEE | Trusted Execution Environment. Hardware-isolated secure computation (SGX, TrustZone). |
| TPS | Transactions Per Second. |
| WASM | WebAssembly. Portable bytecode format for sandboxed execution. |
| ZK-SNARK | Zero-Knowledge Succinct Non-Interactive Argument of Knowledge. |

## **1.4 Reference Documents**

* **Basalt Design Plan v1.0** — February 2026\. Source document for this specification.

* **HotStuff: BFT Consensus Optimally Responsive** — Yin et al., 2019\. Basis for BasaltBFT.

* **.NET 9 Native AOT Documentation** — Microsoft. Runtime constraints and capabilities.

* **libp2p Specification** — Protocol Labs. Peer-to-peer networking primitives.

* **MiCA Regulation (EU) 2023/1114** — European Parliament. Markets in Crypto-Assets.

* **eIDAS 2.0 (EU) 2024/1183** — European digital identity framework.

## **1.5 Design Constraints**

All components MUST adhere to the following constraints:

* **AOT-Only:** Zero runtime reflection, zero dynamically generated code, zero System.Reflection.Emit. All polymorphism via source generators or compile-time generics.

* **Deterministic Execution:** No GC pauses on consensus-critical paths. Object pooling, Span\<T\>, stackalloc, and arena allocators on all hot paths. All floating-point operations MUST use fixed-point arithmetic.

* **Target Runtime:** .NET 9.0 with PublishAot=true. Target RIDs: linux-x64, linux-arm64. Windows support is secondary (Phase 2+).

* **No External Process Dependencies:** The node binary MUST be a single self-contained executable. No JVM, no Python, no Node.js at runtime.

# **2\. Cryptographic Primitives**

This section specifies all cryptographic algorithms, key formats, and serialization used throughout the Basalt protocol.

## **2.1 Hash Functions**

| Usage | Algorithm | Output Size | Library |
| :---- | :---- | :---- | :---- |
| Transaction hash | BLAKE3 | 256 bits (32 bytes) | BLAKE3 .NET (native binding) |
| State trie nodes | BLAKE3 | 256 bits (32 bytes) | BLAKE3 .NET (native binding) |
| Block hash | BLAKE3 | 256 bits (32 bytes) | BLAKE3 .NET (native binding) |
| Address derivation | Keccak-256 | 256 bits → rightmost 160 bits | SHA3 .NET |
| Merkle proof leaves | BLAKE3 | 256 bits (32 bytes) | BLAKE3 .NET (native binding) |
| Content addressing | SHA-256 | 256 bits (32 bytes) | System.Security.Cryptography |

**Rationale:** BLAKE3 is chosen as the primary hash for its superior throughput on x64/ARM64 (3–4 GB/s single-threaded) and its tree-hashing capability for parallel verification. Keccak-256 is retained for address derivation to maintain EVM-compatible addressing for bridge interoperability.

## **2.2 Digital Signatures**

### **2.2.1 Transaction Signatures**

All user transactions MUST be signed using Ed25519 (RFC 8032).

* **Key size:** 256-bit private key, 256-bit public key.

* **Signature size:** 512 bits (64 bytes).

* **Encoding:** Signatures are serialized as 64-byte arrays: R (32 bytes) || S (32 bytes).

* **Library:** NSec.Cryptography (AOT-compatible, libsodium binding).

* **Batch verification:** Nodes MUST support Ed25519 batch verification for transaction validation, achieving 2–3x throughput over individual verification.

### **2.2.2 Consensus Signatures (BLS)**

Consensus votes use BLS signatures on the BLS12-381 curve for aggregation.

* **Public key:** 48 bytes (G1 point, compressed).

* **Signature:** 96 bytes (G2 point, compressed).

* **Aggregation:** n individual signatures aggregate into a single 96-byte signature verifiable against the aggregated public key.

* **Library:** bls-net (native binding to blst C library, AOT-compatible).

**Key separation:** Validators MUST maintain separate key pairs for transaction signing (Ed25519) and consensus voting (BLS). Keys MUST NOT be derived from a shared seed.

## **2.3 Key Derivation and Address Format**

### **2.3.1 Address Derivation**

A Basalt address is derived from an Ed25519 public key:

* address \= "0x" \+ hex(keccak256(publicKey)\[12..32\])

* Address length: 20 bytes (40 hex characters) prefixed with 0x.

* This format is EVM-compatible, enabling address reuse across the EVM bridge.

### **2.3.2 Key Storage**

Private keys at rest MUST be encrypted using AES-256-GCM with a passphrase-derived key (Argon2id, minimum parameters: t=3, m=65536, p=4). The encrypted keystore format follows a JSON envelope:

{  
  "version": 1,  
  "address": "0x...",  
  "crypto": {  
    "kdf": "argon2id",  
    "kdfparams": { "t": 3, "m": 65536, "p": 4, "salt": "\<hex\>" },  
    "cipher": "aes-256-gcm",  
    "cipherparams": { "nonce": "\<hex\>" },  
    "ciphertext": "\<hex\>",  
    "tag": "\<hex\>"  
  }  
}

## **2.4 Serialization**

All protocol messages and persistent data structures use a canonical binary serialization based on a custom codec (BasaltCodec):

* **Wire format:** Length-prefixed fields with varint encoding for integers. Fixed-size fields for hashes (32 bytes), addresses (20 bytes), signatures (64/96 bytes).

* **Determinism:** Field order is fixed by specification. Map types are serialized in lexicographic key order. No optional fields in consensus-critical structures (use zero values instead).

* **Protobuf:** Used exclusively for the gRPC API layer. Not used for consensus or storage serialization to avoid non-determinism in proto encoding.

# **3\. Network Layer (P2P)**

The network layer handles peer discovery, connection management, and message propagation across the Basalt network.

## **3.1 Transport**

### **3.1.1 Primary Transport: QUIC**

All node-to-node communication MUST use QUIC (RFC 9000\) as the primary transport via System.Net.Quic (.NET 9).

* **TLS:** QUIC mandates TLS 1.3. Nodes authenticate using self-signed X.509 certificates containing their Ed25519 public key in the Subject Alternative Name extension.

* **Multiplexing:** Each logical protocol stream (gossip, consensus, sync, API) runs on a separate QUIC stream within a single connection, eliminating head-of-line blocking.

* **Connection resumption:** 0-RTT resumption is enabled for known peers, reducing reconnection latency to \<1ms.

### **3.1.2 Fallback Transport: TCP**

A TCP+Noise fallback is provided for environments where UDP is blocked. Noise\_IK handshake with Ed25519 static keys. TCP fallback activates automatically after 3 failed QUIC connection attempts.

| Parameter | Value | Rationale |
| :---- | :---- | :---- |
| Primary transport | QUIC (UDP) via System.Net.Quic | Native multiplexing, 0-RTT, built-in TLS 1.3 |
| Fallback transport | TCP \+ Noise\_IK | Compatibility with UDP-blocked networks |
| Max block size | 2 MB | Throughput/propagation balance at 2s block time |
| Block time (target) | 2s | Fast finality for enterprise use cases |
| Max direct peers | 50 | Sufficient connectivity without resource exhaustion |
| Max passive peers | 200 | Extended reach via gossip relay |
| Connection timeout | 5 seconds | Aggressive timeout for enterprise SLAs |
| Keep-alive interval | 30 seconds | NAT traversal and liveness detection |

## **3.2 Peer Discovery**

### **3.2.1 Kademlia DHT**

Peer discovery uses a modified Kademlia DHT with the following parameters:

* **Bucket size (k):** 20 peers per k-bucket.

* **Concurrency (α):** 3 parallel lookups per query.

* **ID space:** 256-bit, derived from BLAKE3(Ed25519PublicKey).

* **Reputation zones:** The DHT is partitioned into reputation zones. Nodes with reputation \< 0.3 (scale 0–1) are excluded from routing table insertions.

### **3.2.2 Bootstrap Nodes**

At genesis, a minimum of 5 bootstrap nodes are hardcoded. Bootstrap nodes serve as initial DHT entry points and do not have special protocol privileges. The bootstrap node list is updatable via on-chain governance.

### **3.2.3 Peer Reputation**

Each node maintains a local reputation score for every known peer:

struct PeerReputation {  
    float Availability;      // \[0,1\] \- uptime ratio over 24h window  
    float ResponseLatency;   // \[0,1\] \- normalized p50 latency (lower \= better)  
    float BlockValidity;     // \[0,1\] \- ratio of valid proposed blocks  
    float ProtocolCompliance;// \[0,1\] \- adherence to protocol rules  
}

float CompositeScore \=\>  
    0.25 \* Availability \+  
    0.15 \* ResponseLatency \+  
    0.35 \* BlockValidity \+  
    0.25 \* ProtocolCompliance;

Peers with a composite reputation below 0.2 are disconnected and blacklisted for 1 hour. Repeat offenders receive exponential backoff (2h, 4h, 8h, max 7 days).

## **3.3 Gossip Protocol (Episub)**

Transaction and block propagation uses Episub, a hybrid epidemic/subscription gossip protocol.

### **3.3.1 Two-Tier Propagation**

* **Priority tier:** Enterprise transactions (identified by a priority flag in the transaction header) are propagated via eager push to all direct peers with latency \< 50ms. Target propagation: full network coverage in \< 200ms.

* **Standard tier:** Regular transactions use lazy pull gossip. Nodes advertise transaction hashes (IHAVE messages) and peers request missing transactions (IWANT). Target propagation: full network coverage in \< 600ms.

### **3.3.2 Backpressure and Flood Protection**

* **Rate limiting:** Maximum 10,000 transaction advertisements per second per peer. Exceeding peers are throttled for 10 seconds.

* **Duplicate detection:** Bloom filter with 1% false positive rate, rotated every 60 seconds. Seen transactions are dropped immediately.

* **Mempool cap:** Maximum 50,000 pending transactions. When full, lowest-fee transactions are evicted. Enterprise-sponsored transactions have a reserved pool of 10,000 slots.

## **3.4 Message Types**

| Message | Direction | Size (typical) | Description |
| :---- | :---- | :---- | :---- |
| HELLO | Bidirectional | \~256 bytes | Handshake with protocol version, chain ID, genesis hash, head block |
| TX\_ANNOUNCE | Push | \~64 bytes | IHAVE advertisement: transaction hash \+ fee hint |
| TX\_REQUEST | Pull | \~36 bytes | IWANT request: transaction hash |
| TX\_PAYLOAD | Response | \~256–2048 bytes | Full serialized transaction |
| BLOCK\_ANNOUNCE | Push | \~128 bytes | New block header hash \+ height \+ proposer |
| BLOCK\_REQUEST | Pull | \~36 bytes | Request full block by hash |
| BLOCK\_PAYLOAD | Response | ≤2 MB | Full serialized block (header \+ transactions) |
| CONSENSUS\_VOTE | Push | \~200 bytes | BLS-signed consensus vote (phase \+ block hash) |
| CONSENSUS\_PROPOSAL | Push | \~2 MB | Block proposal from leader |
| PEER\_EXCHANGE | Bidirectional | \~1 KB | Periodic peer list exchange (10 peers) |
| STATUS | Bidirectional | \~64 bytes | Chain tip \+ sync status for state sync |

# **4\. Consensus Layer — BasaltBFT**

BasaltBFT is a pipelined BFT consensus protocol derived from HotStuff, optimized for enterprise workloads with fast finality and deterministic behavior.

## **4.1 Protocol Overview**

BasaltBFT operates in views. Each view has a designated leader who proposes a block. The protocol achieves consensus through three phases: PREPARE, PRE-COMMIT, and COMMIT. Phases are pipelined across consecutive blocks, achieving one block finalization per round-trip.

| Property | Value |
| :---- | :---- |
| Fault model | Byzantine (f \= ⌊(n−1)/3⌋ malicious validators) |
| Finality | Deterministic (absolute, no rollbacks) |
| Phases per block | 3 (PREPARE, PRE-COMMIT, COMMIT), pipelined |
| Finality latency | 2 round-trips \= 4s at 2s block time |
| Leader selection | Weighted round-robin (stake × reputation) |
| View change timeout | 2 seconds (exponential backoff: 2s, 4s, 8s, max 60s) |
| Signature aggregation | BLS12-381 (96 bytes per aggregated proof) |
| Quorum threshold | 2f \+ 1 votes (\>2/3 of validator set) |

## **4.2 Block Structure**

struct BlockHeader {  
    uint64       Version;        // Protocol version (current: 1\)  
    uint64       Height;         // Block number (0 \= genesis)  
    Hash256      ParentHash;     // BLAKE3 hash of parent block header  
    Hash256      StateRoot;      // MPT root after executing all transactions  
    Hash256      TransactionsRoot; // Merkle root of transaction hashes  
    Hash256      ReceiptsRoot;   // Merkle root of receipt hashes  
    Address      Proposer;       // Address of the block proposer  
    uint64       Timestamp;      // Unix timestamp in milliseconds  
    uint64       GasLimit;       // Maximum gas for this block  
    uint64       GasUsed;        // Total gas consumed by transactions  
    byte\[\]       ExtraData;      // Arbitrary data (max 256 bytes)  
    BLSSignature AggregateSign;  // Aggregated BLS signature of quorum  
    Bitfield     SignerBitfield; // Bitmask of signing validators  
    uint64       ViewNumber;     // Consensus view number  
}

struct Block {  
    BlockHeader        Header;  
    Transaction\[\]      Transactions;   // Ordered list (max: block gas limit)  
    ConsensusEvidence  Evidence;        // Slashing evidence (if any)  
}

## **4.3 Consensus Phases**

### **4.3.1 PREPARE Phase**

The leader for the current view:

* Selects transactions from the mempool, ordered by fee (descending), up to the block gas limit.

* Constructs a candidate block with a valid header.

* Broadcasts a PROPOSAL message containing the full block to all validators.

On receiving a valid proposal, each validator:

* Validates the block header (parent hash, height, timestamp monotonicity, gas limit).

* Executes all transactions against the current state to verify StateRoot.

* **If valid:** Signs a PREPARE vote (BLS signature over block hash \+ view number) and sends to the leader.

* **If invalid:** Drops the proposal and waits for view change timeout.

### **4.3.2 PRE-COMMIT Phase**

Upon collecting a quorum of PREPARE votes (2f \+ 1):

* The leader aggregates the BLS signatures into a single AggregateSign.

* Broadcasts a PRE-COMMIT message containing the aggregated signature and signer bitfield.

Validators verify the aggregated signature, then sign a PRE-COMMIT vote and send it to the leader.

### **4.3.3 COMMIT Phase**

Upon collecting a quorum of PRE-COMMIT votes:

* The leader aggregates and broadcasts the COMMIT proof.

* Validators verify, finalize the block, and advance to the next height.

* The finalized block is stored and propagated to non-validator nodes.

### **4.3.4 Pipelining**

Phases overlap across consecutive blocks. While block N is in COMMIT, block N+1 can be in PRE-COMMIT, and block N+2 in PREPARE. This keeps the pipeline full and maximizes throughput. A block achieves finality when its COMMIT proof is included in a subsequent block.

## **4.4 Leader Selection**

Leaders are selected via weighted round-robin. The weight for each validator is:

weight(v) \= stake(v) \* reputation(v)

// Leader for view V:  
leader(V) \= validators\[weightedIndex(V % totalWeight)\]

// Where weightedIndex maps a cumulative weight offset  
// to the corresponding validator index.

Reputation is computed from the validator’s block proposal success rate over the last 1000 views. A validator that fails to propose (timeout or invalid block) receives a reputation penalty of 0.05 per failure, recovering at 0.01 per successful view.

## **4.5 View Change**

If a validator does not receive a valid proposal within the view change timeout:

* It broadcasts a VIEW-CHANGE message containing its current view number and the highest committed block.

* Upon collecting 2f \+ 1 VIEW-CHANGE messages for view V+1, the new leader is determined by the rotation schedule.

* The new leader broadcasts a NEW-VIEW message with the quorum proof, and proposing proceeds from the highest committed block.

View change timeout doubles on each consecutive timeout (2s, 4s, 8s…) and resets to 2s after a successful block.

## **4.6 Validator Management**

### **4.6.1 Staking Requirements**

* **Minimum stake:** 100,000 BST for mainnet validators.

* **Unbonding period:** 21 days. During unbonding, the stake is not eligible for rewards and is still subject to slashing.

* **Delegation:** BST holders can delegate to validators. Delegators share rewards and slashing proportionally.

### **4.6.2 Slashing Conditions**

| Offense | Penalty | Evidence Required |
| :---- | :---- | :---- |
| Double signing (equivocation) | 100% of stake burned | Two conflicting signed blocks at same height/view |
| Extended inactivity (\>24h offline) | 5% of stake burned | Absence from 2/3+ of views in 24h window |
| Invalid block proposal | 1% of stake burned | Block failing validation by 2f+1 validators |
| Consensus key compromise | Forced exit, no slash | Validator-initiated key rotation report |

### **4.6.3 Key Rotation**

Validators MUST rotate their BLS consensus keys every 90 days. The rotation protocol:

* Validator generates a new BLS key pair and submits a KeyRotation transaction containing the new public key signed by both old and new keys.

* The protocol enforces a 24-hour activation delay for the new key.

* After activation, votes signed with the old key are rejected.

## **4.7 Enterprise Subnets**

BasaltBFT supports permissioned subnets with a restricted validator set:

* **Subnet creation:** Requires a CreateSubnet transaction on mainnet with a minimum stake of 500,000 BST and a list of approved validators (minimum 4, maximum 100).

* **Validator KYC:** All subnet validators must hold a verified identity attestation in the Identity Registry.

* **Cross-chain finality:** Subnet block headers are periodically anchored to mainnet (every 100 subnet blocks or every 60 seconds, whichever comes first). Mainnet validators verify the subnet header chain for bridge operations.

* **Performance:** With 21 validators: \~5,000 TPS, 2s finality. With 7 validators: \~10,000 TPS, 2s finality.

# **5\. Execution Layer — BasaltVM**

BasaltVM is the virtual machine responsible for executing smart contracts and processing state transitions. It supports two execution modes: Native C\# AOT and WASM.

## **5.1 Transaction Structure**

struct Transaction {  
    uint8        Type;           // 0=Transfer, 1=ContractDeploy, 2=ContractCall, 3=System  
    uint64       Nonce;          // Sender nonce (sequential, no gaps)  
    Address      Sender;         // Derived from signature recovery  
    Address      To;             // Recipient (Address.Zero for deploy)  
    UInt256      Value;          // BST transfer amount (in wei, 18 decimals)  
    uint64       GasLimit;       // Maximum gas for execution  
    UInt256      GasPrice;       // Price per gas unit in BST wei  
    byte\[\]       Data;           // Calldata (ABI-encoded for contract calls)  
    uint8        Priority;       // 0=Standard, 1=Enterprise (affects gossip tier)  
    uint64       ChainId;        // EIP-155 chain ID for replay protection  
    Ed25519Sig   Signature;      // Ed25519 signature over tx hash  
}

Hash256 TxHash \=\> BLAKE3(BasaltCodec.Serialize(this, excludeSignature: true));

## **5.2 Transaction Validation**

Before execution, every transaction MUST pass the following checks (in order):

* **1\. Signature:** Ed25519 signature verification. Recover sender address from signature.

* **2\. Nonce:** tx.Nonce \== account.Nonce. Reject if too low (replay) or too high (gap).

* **3\. Balance:** account.Balance \>= tx.Value \+ tx.GasLimit \* tx.GasPrice.

* **4\. Gas limit:** tx.GasLimit \<= block.GasLimit.

* **5\. Data size:** tx.Data.Length \<= 128 KB for contract calls, \<= 2 MB for contract deployments.

* **6\. Chain ID:** tx.ChainId must match the network chain ID.

* **7\. Compliance:** If the transaction involves a compliant token, run compliance pipeline (Section 8).

## **5.3 Execution Mode 1: Native C\# AOT**

### **5.3.1 Compilation Pipeline**

Smart contracts written in C\# follow this compilation pipeline:

* **Source → Roslyn Analysis:** The Basalt Roslyn Analyzer suite validates AOT compatibility, determinism, and security patterns.

* **Roslyn Analysis → IL:** Standard C\# compilation to IL assemblies.

* **IL → Native AOT:** ILC (IL Compiler) produces a native shared library (.so on Linux) with all reflection and dynamic code stripped.

* **Native library → Deployment:** The compiled artifact is uploaded as contract bytecode. The deployment transaction contains the native binary \+ metadata.

### **5.3.2 Sandbox Specification**

Each contract invocation runs in an isolated sandbox:

| Resource | Limit | Enforcement |
| :---- | :---- | :---- |
| Memory | 256 MB per invocation | mmap with MAP\_NORESERVE, SIGKILL on OOM |
| CPU time | Configurable per tx gas limit | POSIX timer (ITIMER\_REAL), SIGKILL on timeout |
| Stack depth | 1 MB | Guarded stack pages, SIGSEGV on overflow |
| System calls | Allowlist only (read, write, mmap, clock\_gettime) | seccomp-bpf filter (Linux) |
| File system | No access | seccomp \+ landlock |
| Network | No access | seccomp |
| Threads | Single-threaded only | seccomp (clone/fork blocked) |

### **5.3.3 Host Interface**

Contracts interact with the blockchain through a Host Interface exposed as native function pointers:

// State operations  
int storage\_read(byte\* key, int keyLen, byte\* valueBuf, int bufLen);  
int storage\_write(byte\* key, int keyLen, byte\* value, int valueLen);  
int storage\_delete(byte\* key, int keyLen);

// Context  
void get\_caller(byte\* addrBuf);          // 20 bytes  
void get\_block\_timestamp(uint64\* ts);     // Unix ms  
void get\_block\_height(uint64\* height);  
void get\_tx\_value(byte\* valueBuf);        // UInt256, 32 bytes  
void get\_gas\_remaining(uint64\* gas);

// Cross-contract calls  
int call\_contract(byte\* addr, byte\* data, int dataLen,  
                  uint64 gasLimit, byte\* resultBuf, int bufLen);

// Events  
void emit\_event(byte\* topics, int topicCount, byte\* data, int dataLen);

// Crypto  
int verify\_ed25519(byte\* msg, int msgLen, byte\* sig, byte\* pubkey);  
int verify\_bls(byte\* msg, int msgLen, byte\* sig, byte\* pubkey);  
void blake3\_hash(byte\* data, int dataLen, byte\* out);

## **5.4 Execution Mode 2: WASM**

For contracts compiled from other languages (Rust, AssemblyScript, C), BasaltVM includes a WASM runtime based on wasmtime ported to .NET.

* **WASM version:** WebAssembly MVP \+ bulk-memory-operations \+ sign-extension.

* **Memory:** Linear memory with a maximum of 16 pages (1 MB) by default, extendable to 256 MB via configuration.

* **Host imports:** The same Host Interface is exposed as WASM imports (basalt\_env module).

* **Metering:** Gas metering is injected at the WASM level by instrumenting every basic block with a gas\_charge call.

## **5.5 Gas Model**

Gas costs are calibrated against empirical benchmarks on reference hardware (AMD EPYC 7763, single core). Gas costs are reviewed and adjusted via governance every 6 months.

| Operation | Gas (AOT) | Gas (WASM) | WASM/AOT Ratio |
| :---- | :---- | :---- | :---- |
| Arithmetic (add/mul/div) | 1 | 3 | 3.0x |
| Storage read (hot cache) | 50 | 120 | 2.4x |
| Storage read (cold / disk) | 200 | 350 | 1.75x |
| Storage write (new slot) | 5,000 | 8,000 | 1.6x |
| Storage write (existing slot) | 500 | 800 | 1.6x |
| Storage delete (with refund) | \-2,500 refund | \-4,000 refund | 1.6x |
| Cross-contract call (base) | 150 | 400 | 2.7x |
| Ed25519 signature verification | 1,000 | 2,500 | 2.5x |
| BLS signature verification | 5,000 | 12,500 | 2.5x |
| ZK-SNARK verification (Groth16) | 10,000 | 25,000 | 2.5x |
| Event emission (base \+ 32 bytes) | 100 | 200 | 2.0x |
| Memory allocation (per 64KB page) | 200 | 500 | 2.5x |
| BLAKE3 hash (per 64 bytes) | 10 | 25 | 2.5x |
| Keccak-256 hash (per 64 bytes) | 30 | 75 | 2.5x |

## **5.6 Gas Sponsoring (Enterprise)**

Enterprises can pre-purchase gas quotas via the GasSponsor system contract:

* **Quota purchase:** Enterprise deposits BST (or approved stablecoin via gas abstraction) and receives a gas quota in their sponsor account.

* **Sponsored transactions:** Transactions include a sponsorAddress field. The sponsor’s quota is debited instead of the sender’s balance.

* **Fixed pricing:** Sponsor quotas are priced at a fixed BST/gas rate locked for 30-day periods, eliminating fee volatility.

* **Rate limiting:** Each sponsor can set per-address daily gas caps to prevent abuse.

# **6\. Storage Layer**

The storage layer provides persistent state management for accounts, contracts, blocks, and receipts.

## **6.1 State Database (MPT)**

Account and contract state is stored in a Modified Merkle Patricia Trie (MPT) backed by RocksDB.

### **6.1.1 Account State**

struct AccountState {  
    uint64   Nonce;           // Transaction count  
    UInt256  Balance;         // BST balance in wei  
    Hash256  StorageRoot;     // Root of contract storage trie (or EMPTY\_ROOT)  
    Hash256  CodeHash;        // BLAKE3 hash of contract code (or EMPTY\_HASH)  
    byte     AccountType;     // 0=EOA, 1=Contract, 2=System  
    Hash256  ComplianceHash;  // Hash of compliance attestation (or zero)  
}

### **6.1.2 Trie Node Types**

* **Branch node:** 16 children (nibble-indexed) \+ optional value. Serialized as: \[child0\_hash, child1\_hash, ..., child15\_hash, value?\].

* **Extension node:** Shared nibble prefix \+ child hash. Serialized as: \[prefix\_bytes, child\_hash\].

* **Leaf node:** Remaining key nibbles \+ value. Serialized as: \[key\_remainder, value\_bytes\].

All node hashes use BLAKE3. Empty trie root: BLAKE3(0x80) \= constant EMPTY\_ROOT.

### **6.1.3 RocksDB Configuration**

| Parameter | Value | Rationale |
| :---- | :---- | :---- |
| Column families | state, blocks, receipts, metadata | Isolation and independent compaction |
| Compression | Zstd (level 3\) | 60–70% size reduction with minimal CPU overhead |
| Block cache | 2 GB | Hot state fits in cache for 2s block times |
| Write buffer | 256 MB (4 x 64 MB) | Absorb burst writes during block execution |
| Bloom filter | 10 bits/key | Reduce read amplification for point lookups |
| Max open files | 1024 | Sufficient for active state \+ recent blocks |

## **6.2 Block Database**

Blocks are stored sequentially in a dedicated column family with dual indexing:

* **By hash:** key \= BLAKE3(blockHeader) → value \= serialized Block.

* **By height:** key \= uint64(height) → value \= block hash (indirection).

Block bodies and headers are stored together. Full blocks are retained for the most recent 128 blocks (approximately 256 seconds at 2s block time). Older blocks retain headers only; bodies are available from archive nodes.

## **6.3 Receipt Database**

struct TransactionReceipt {  
    Hash256      TxHash;  
    uint64       BlockHeight;  
    uint32       TxIndex;       // Position within block  
    bool         Success;       // true if execution succeeded  
    uint64       GasUsed;  
    byte\[\]       ReturnData;    // Return value or revert reason  
    EventLog\[\]   Logs;          // Emitted events  
    Hash256      PostStateRoot; // State root after this tx  
}

struct EventLog {  
    Address      ContractAddress;  
    Hash256\[\]    Topics;        // Indexed topics (max 4\)  
    byte\[\]       Data;          // Non-indexed event data  
}

Events are indexed by a Bloom filter (2048 bits per block, 3 hash functions) for fast log queries by topic across block ranges.

## **6.4 State Pruning and Expiry**

* **Pruning:** Trie nodes referenced only by states older than 256 blocks are eligible for pruning. Pruning runs as a background process with rate limiting to avoid I/O contention with block execution.

* **State expiry:** Account states untouched for 12 months are moved to the Archive DB. A witness proof (Merkle path) is retained in the active state, allowing restoration via a ReviveState transaction that includes the witness.

* **Archive nodes:** Maintain full state history with Zstd compression. Archive DB is append-only and can be stored on lower-cost storage tiers.

# **7\. API Layer**

The API layer provides three complementary interfaces for external interaction with the Basalt node.

## **7.1 gRPC API (High Performance)**

The primary developer interface for high-frequency applications. All gRPC services use Protobuf serialization and support bidirectional streaming.

### **7.1.1 Service Definitions**

service BasaltNode {  
  // Transactions  
  rpc SendTransaction(SignedTransaction) returns (TxResponse);  
  rpc GetTransaction(TxHashRequest) returns (Transaction);  
  rpc GetReceipt(TxHashRequest) returns (TransactionReceipt);  
  rpc EstimateGas(CallRequest) returns (GasEstimate);

  // Blocks  
  rpc GetBlock(BlockRequest) returns (Block);          // by hash or height  
  rpc GetLatestBlock(Empty) returns (Block);  
  rpc StreamBlocks(StreamRequest) returns (stream Block);

  // State  
  rpc GetAccount(AddressRequest) returns (AccountState);  
  rpc GetStorageAt(StorageRequest) returns (StorageValue);  
  rpc Call(CallRequest) returns (CallResult);           // read-only call

  // Events  
  rpc GetLogs(LogFilter) returns (LogResponse);  
  rpc StreamLogs(LogFilter) returns (stream EventLog);

  // Network  
  rpc GetPeers(Empty) returns (PeerList);  
  rpc GetSyncStatus(Empty) returns (SyncStatus);  
  rpc GetChainInfo(Empty) returns (ChainInfo);  
}

## **7.2 REST API (Compatibility)**

RESTful API via ASP.NET Minimal APIs for integration with existing enterprise systems. OpenAPI 3.0 specification auto-generated.

| Method | Endpoint | Description |
| :---- | :---- | :---- |
| POST | /v1/transactions | Submit a signed transaction |
| GET | /v1/transactions/{hash} | Get transaction by hash |
| GET | /v1/transactions/{hash}/receipt | Get transaction receipt |
| GET | /v1/blocks/latest | Get latest finalized block |
| GET | /v1/blocks/{hashOrHeight} | Get block by hash or height |
| GET | /v1/accounts/{address} | Get account state |
| GET | /v1/accounts/{address}/storage/{key} | Read contract storage slot |
| POST | /v1/call | Execute read-only contract call |
| POST | /v1/estimate-gas | Estimate gas for a transaction |
| GET | /v1/logs?topics=...\&fromBlock=...\&toBlock=... | Query event logs |
| GET | /v1/chain/info | Chain ID, genesis hash, head block |
| WS | /v1/ws | WebSocket for subscriptions (blocks, logs, pending tx) |

## **7.3 GraphQL API (Complex Queries)**

GraphQL endpoint for analytical queries, state exploration, and enterprise dashboards. Supports query complexity limits (max depth: 10, max fields: 100\) to prevent abuse.

## **7.4 Rate Limiting and Authentication**

* **Public nodes:** 100 requests/second per IP. Burst allowance of 200 requests.

* **Authenticated (API key):** 1,000 requests/second. API keys issued via the developer portal.

* **Enterprise (dedicated):** No rate limit. Dedicated gRPC endpoint with mTLS authentication.

* **WebSocket:** Max 10 concurrent subscriptions per connection. Heartbeat every 30 seconds.

# **8\. Smart Contract SDK**

The Basalt Contract SDK is the primary interface for developing, testing, and deploying smart contracts in C\#.

## **8.1 Contract Model**

### **8.1.1 Contract Anatomy**

A Basalt contract is a C\# class annotated with the \[BasaltContract\] attribute. The SDK provides typed abstractions for state, events, and inter-contract messaging.

\[BasaltContract\]  
public class TokenContract {  
    // Typed persistent storage  
    private readonly StorageMap\<Address, UInt256\> \_balances \= new();  
    private readonly StorageValue\<UInt256\> \_totalSupply \= new();  
    private readonly StorageValue\<string\> \_name \= new();

    // Indexed event  
    \[BasaltEvent\]  
    public record Transfer(  
        \[Indexed\] Address From,  
        \[Indexed\] Address To,  
        UInt256 Amount  
    );

    // Constructor (called once at deployment)  
    \[BasaltConstructor\]  
    public void Initialize(string name, UInt256 initialSupply) {  
        \_name.Set(name);  
        \_totalSupply.Set(initialSupply);  
        \_balances.Set(Context.Caller, initialSupply);  
        Emit(new Transfer(Address.Zero, Context.Caller, initialSupply));  
    }

    // Public entrypoint  
    \[BasaltEntrypoint\]  
    public bool Transfer(Address to, UInt256 amount) {  
        var sender \= Context.Caller;  
        Require(\_balances.Get(sender) \>= amount, "Insufficient balance");  
        \_balances.Set(sender, \_balances.Get(sender) \- amount);  
        \_balances.Set(to, \_balances.Get(to) \+ amount);  
        Emit(new Transfer(sender, to, amount));  
        return true;  
    }

    \[BasaltView\] // Read-only, no state modification  
    public UInt256 BalanceOf(Address account) \=\> \_balances.Get(account);  
}

### **8.1.2 Storage Primitives**

| Type | Description | Key Layout |
| :---- | :---- | :---- |
| StorageValue\<T\> | Single typed value | BLAKE3(contractAddr \+ slot\_index) |
| StorageMap\<K,V\> | Key-value mapping | BLAKE3(contractAddr \+ slot\_index \+ key) |
| StorageList\<T\> | Indexed list with length tracking | slot for length \+ BLAKE3(slot \+ index) |
| StorageSet\<T\> | Unordered set with membership check | BLAKE3(slot \+ item) for existence, separate count |

All storage operations go through the Host Interface. Serialization uses BasaltCodec for deterministic encoding. Type T must implement IBasaltSerializable (source-generated).

## **8.2 Roslyn Analyzers**

The SDK includes a suite of Roslyn analyzers that enforce compile-time safety:

| Analyzer | ID | Severity | Description |
| :---- | :---- | :---- | :---- |
| No Reflection | BST001 | Error | Detects System.Reflection usage, typeof() on non-constant types |
| No Dynamic | BST002 | Error | Blocks dynamic keyword and ExpandoObject |
| Determinism | BST003 | Error | Flags DateTime.Now, Random, Guid.NewGuid, floating-point |
| Reentrancy Guard | BST004 | Warning | Detects state reads after cross-contract calls without checks |
| Overflow Protection | BST005 | Warning | Flags unchecked arithmetic on UInt256/Int256 |
| Storage Access | BST006 | Warning | Detects raw storage access bypassing typed wrappers |
| Gas Estimation | BST007 | Info | Estimates gas cost of entrypoint methods |
| AOT Compatibility | BST008 | Error | Validates full ILC compatibility (no MakeGenericType, etc.) |

## **8.3 Token Standards**

Basalt defines standardized interfaces for tokens, extending Ethereum patterns with compliance hooks.

| Standard | Description | ERC Equivalent | Key Additions |
| :---- | :---- | :---- | :---- |
| BST-20 | Fungible token | ERC-20 | Compliance hooks, gas sponsoring, pausable |
| BST-721 | Non-fungible token | ERC-721 | On-chain metadata, royalty enforcement |
| BST-1155 | Multi-token | ERC-1155 | Batch compliance checks |
| BST-3525 | Semi-fungible (RWA, bonds) | ERC-3525 | Slot-based compliance, maturity dates |
| BST-4626 | Vault (yield, staking) | ERC-4626 | Yield blocking for e-money, audit hooks |
| BST-DID | Decentralized Identity | None (W3C DID) | eIDAS 2.0 compatible, revocable |
| BST-VC | Verifiable Credentials | None (W3C VC) | Selective disclosure, ZK proofs |

## **8.4 Testing Framework**

Contracts are tested using standard .NET testing frameworks (xUnit, NUnit) with the Basalt test harness:

* **BasaltTestHost:** In-process blockchain emulator. Simulates block production, time advancement, and multiple accounts.

* **Deterministic time:** Tests control block timestamps via testHost.AdvanceTime(TimeSpan).

* **Snapshot/restore:** testHost.Snapshot() and testHost.Restore(snapshotId) for test isolation.

* **Gas profiling:** testHost.GetGasUsed(txHash) returns detailed gas breakdown by operation type.

* **Coverage:** Integration with standard .NET code coverage tools (coverlet, dotCover).

# **9\. Compliance Module**

The compliance module is a pluggable protocol-level layer that enforces regulatory rules on token transfers and account operations.

## **9.1 Identity Registry**

### **9.1.1 Data Model**

struct IdentityAttestation {  
    Address      Subject;          // Account address  
    Address      Issuer;           // Approved KYC provider address  
    uint64       IssuedAt;         // Unix timestamp (ms)  
    uint64       ExpiresAt;        // Unix timestamp (ms), 0 \= no expiry  
    byte         Level;            // 0=None, 1=Basic, 2=Enhanced, 3=Institutional  
    uint16       CountryCode;      // ISO 3166-1 numeric code  
    Hash256      ClaimHash;        // ZK commitment to underlying identity data  
    bool         Revoked;  
}

The Identity Registry is a system contract deployed at a well-known address. It stores attestation metadata on-chain. No personal data is stored on-chain; only the ClaimHash (a Pedersen commitment) that can be verified against off-chain data via a ZK proof.

### **9.1.2 KYC Providers**

KYC providers are whitelisted via governance. Each provider MUST:

* Hold a valid attestation from the Basalt Foundation (at genesis) or DAO (post-maturity).

* Post a bond of 50,000 BST as collateral against fraudulent attestations.

* Implement the IKycProvider interface for on-chain attestation issuance and revocation.

## **9.2 Compliance Engine**

The compliance engine evaluates transfer compliance at the protocol level. When a transaction involves a compliant token (any BST-20+ with compliance enabled), the following pipeline executes before state changes:

| Step | Check | Failure Action |
| :---- | :---- | :---- |
| 1 | Sender has valid (non-expired, non-revoked) identity attestation | Reject transaction |
| 2 | Receiver has valid identity attestation at required level | Reject transaction |
| 3 | Neither party is on the sanctions list (on-chain blocklist) | Reject transaction |
| 4 | Transfer does not violate geographic restrictions set by issuer | Reject transaction |
| 5 | Transfer does not exceed holding limits (concentration rules) | Reject transaction |
| 6 | Transfer does not violate lock-up or vesting schedules | Reject transaction |
| 7 | Travel Rule data is attached (for transfers \> threshold) | Reject transaction |

Compliance rules are configured per-token by the issuer via the CompliancePolicy contract. Validators enforce compliance at block construction time: non-compliant transactions are excluded from blocks.

## **9.3 GDPR Module**

* **No personal data on-chain:** Only cryptographic commitments (Pedersen hashes) are stored. Underlying data lives in off-chain datastores controlled by the data subject.

* **Right to erasure:** Revoking an identity attestation breaks the on-chain link. The Pedersen commitment becomes unverifiable once the off-chain data is deleted.

* **Data portability:** Verifiable credentials (BST-VC) can be exported and presented to any relying party without re-issuing.

## **9.4 MiCA Compliance**

The protocol natively supports MiCA requirements through system contracts:

* **White paper registration:** TokenWhitePaper system contract stores on-chain references to regulatory white papers (IPFS hash \+ metadata).

* **Reserve proof:** Stablecoin issuers must configure a reserve oracle (Chainlink/Pyth) that periodically attests reserve adequacy. The proof is verified on-chain.

* **Redemption SLA:** Stablecoin contracts implement an automatic redemption mechanism with a configurable SLA (default: 5 business days).

* **Reporting:** The AuditModule system contract generates structured reports (SOC2/ISO27001 format) exportable via the API.

## **9.5 Audit Trail**

Every compliance-relevant action generates an immutable audit event:

* Attestation issuance, revocation, and expiry.

* Compliance check results (pass/fail with rule ID).

* Transfer blocking with reason codes.

* Policy changes by token issuers.

Audit events are indexed in the Receipt DB with dedicated topic prefixes for efficient querying by regulators.

# **10\. Confidentiality**

Basalt provides multiple confidentiality mechanisms to protect sensitive enterprise data while maintaining on-chain verifiability.

## **10.1 Confidential Transactions (ZK-SNARKs)**

Confidential transactions use Groth16 ZK-SNARKs (via bellman ported to .NET) to prove transaction validity without revealing amounts or parties.

### **10.1.1 Proof Circuit**

The confidential transfer circuit proves:

* The sender owns sufficient balance (Pedersen commitment opens to value \>= transfer amount).

* The transfer amount is non-negative (range proof via Bulletproofs sub-circuit).

* The new balance commitments are correctly computed.

* The sender holds a valid identity attestation (compliance sub-circuit).

### **10.1.2 Performance**

| Operation | Time (AOT) | Proof Size |
| :---- | :---- | :---- |
| Proof generation (sender-side) | \~2 seconds | 192 bytes (Groth16) |
| Proof verification (on-chain) | \~5 ms | N/A |
| Trusted setup | One-time ceremony | \~50 MB SRS (structured reference string) |

## **10.2 Private State Channels**

Bilateral private channels allow enterprises to transact off-chain with periodic on-chain settlement:

* **Channel opening:** Both parties deposit collateral into a ChannelManager system contract.

* **Off-chain execution:** Transactions are signed bilaterally and exchanged directly (via QUIC). State updates are tracked by incrementing a nonce.

* **Settlement:** Either party can submit the latest mutually signed state to the chain. A dispute window of 24 hours allows the counterparty to submit a more recent state.

* **Force close:** If one party is unresponsive, the other can force-close after the dispute period using the last signed state.

## **10.3 TEE Enclaves (Optional)**

For use cases requiring computation on encrypted data (sealed-bid auctions, matching engines), Basalt supports optional SGX/TrustZone enclaves:

* **Attestation:** Enclave attestation is verified on-chain via a dedicated oracle that validates Intel SGX quotes.

* **Key management:** Enclave-sealed keys are rotated every 30 days. Key shares are distributed via a threshold scheme (t-of-n) among approved validators.

* **Availability:** TEE enclaves are NOT required for core protocol operation. They are an optional module for enterprise subnets.

# **11\. Tokenomics Protocol**

This section specifies the on-chain mechanisms governing BST token economics.

## **11.1 Fee Mechanism**

Transaction fees are computed as:

fee \= gasUsed \* gasPrice

// Gas price has a protocol-enforced minimum:  
minGasPrice \= baseFee \+ priorityFee

// baseFee adjusts per block (EIP-1559 style):  
if (parentBlock.gasUsed \> targetGas) {  
    baseFee \= parentBaseFee \* (1 \+ delta);  // delta capped at 12.5%  
} else {  
    baseFee \= parentBaseFee \* (1 \- delta);  
}

* **Target gas per block:** 50% of block gas limit.

* **Base fee adjustment:** Maximum 12.5% change per block (dampened to prevent volatility).

* **Minimum gas price:** 1 gwei (10^-9 BST). Governance-adjustable.

## **11.2 Fee Distribution and Burn**

* **50% burned:** Base fee component is burned (sent to address 0x0). This creates deflationary pressure proportional to network usage.

* **50% to validators:** Priority fee component is distributed to the block proposer (70%) and the rest of the active validator set (30%, weighted by stake).

## **11.3 Staking Rewards**

Staking rewards follow a decreasing emission schedule over 10 years:

| Year | Annual Emission (BST) | Cumulative (BST) | Annual Rate |
| :---- | :---- | :---- | :---- |
| 1 | 24,000,000 | 24,000,000 | 2.4% |
| 2 | 21,600,000 | 45,600,000 | 2.16% |
| 3 | 19,440,000 | 65,040,000 | 1.94% |
| 4 | 17,496,000 | 82,536,000 | 1.75% |
| 5 | 15,746,400 | 98,282,400 | 1.57% |
| 6–10 | \~22,317,600 total | 120,000,000 | Decreasing to \~0.5% |

Emission decreases by 10% per year. Total staking rewards are capped at 120,000,000 BST (12% of total supply). Rewards are distributed per-block, proportional to each validator’s stake.

## **11.4 Gas Abstraction**

To improve enterprise UX, transaction fees can be paid in approved stablecoins (USDC, EUROC) via the GasAbstraction system contract:

* User submits a meta-transaction with fee payment in stablecoin.

* A relayer converts the stablecoin fee to BST via the on-chain DEX oracle price.

* The BST fee follows the standard burn/distribution path.

* Approved stablecoins are governed by the DAO. Adding or removing a stablecoin requires a governance vote.

# **12\. Non-Functional Requirements**

## **12.1 Performance Targets**

| Metric | Target (Mainnet) | Target (Enterprise Subnet) | Measurement Method |
| :---- | :---- | :---- | :---- |
| Transaction throughput | ≥2,000 TPS | ≥5,000 TPS (21 val.) | Sustained load test, 10 min, standard transfer tx |
| Block finality | ≤4s | ≤2s | Time from proposal to commit proof |
| Transaction latency (p50) | ≤2.5s | ≤1.5s | Submit to receipt confirmation |
| Transaction latency (p99) | ≤6s | ≤3s | Submit to receipt confirmation |
| State read latency (hot) | ≤50 µs | ≤50 µs | StorageMap.Get with cached trie nodes |
| State write latency | ≤200 µs | ≤200 µs | StorageMap.Set single slot |
| Node startup (cold) | ≤30 seconds | ≤30 seconds | Process start to block sync ready |
| Memory usage (idle) | ≤2 GB | ≤1 GB | RSS after sync, no active transactions |
| Memory usage (peak) | ≤8 GB | ≤4 GB | RSS during sustained 12K TPS load |
| Disk usage (1 year) | ≤500 GB (pruned) | ≤200 GB (pruned) | State \+ recent blocks, Zstd compressed |

## **12.2 Availability and Reliability**

* **Node uptime target:** 99.9% (8.7 hours downtime per year maximum).

* **Network partition tolerance:** The network MUST continue producing blocks as long as \>2/3 of validators are reachable.

* **Data durability:** Finalized blocks are immutable. Loss of finalized data constitutes a critical failure.

* **Graceful degradation:** Under extreme load (\>2x target TPS), the node must shed transactions by fee priority without crashing.

## **12.3 Security Requirements**

* **Cryptographic strength:** All primitives MUST provide ≥128-bit security level.

* **Key management:** Private keys MUST never be stored in plaintext. AES-256-GCM encryption with Argon2id KDF.

* **Transport security:** All node-to-node communication MUST use TLS 1.3 (via QUIC) or Noise\_IK.

* **Sandbox escape:** Smart contract sandbox escape MUST be treated as a critical severity vulnerability.

* **Audit cadence:** Full security audit by two independent firms before each major release.

## **12.4 Observability**

* **Metrics:** Prometheus-compatible /metrics endpoint exposing: block height, TPS, mempool size, peer count, consensus round time, gas price, validator status.

* **Logging:** Structured JSON logging (Serilog) with configurable log levels. MUST NOT log private keys or transaction content at any level.

* **Tracing:** OpenTelemetry integration for distributed tracing across P2P, consensus, execution, and storage layers.

* **Health check:** /health endpoint returning node sync status, peer connectivity, and resource utilization.

# **13\. Acceptance Criteria**

Each specification section maps to testable acceptance criteria for Phase 1 (Foundation) delivery.

## **13.1 Network Layer**

* A node MUST establish QUIC connections to at least 10 peers within 30 seconds of startup on the devnet.

* Transaction gossip MUST achieve \>95% network coverage within 600ms on a 50-node testnet.

* Priority transactions MUST achieve \>95% coverage within 200ms.

* A node MUST gracefully handle and recover from 10% of peers disconnecting simultaneously.

## **13.2 Consensus**

* A 4-validator devnet MUST produce blocks at 2s intervals with \<100ms jitter.

* Finality MUST be achieved within 4s measured from block proposal to commit proof.

* The protocol MUST correctly handle a single Byzantine validator (1 of 4\) without stalling.

* View change MUST complete within 2 seconds when the leader is offline.

## **13.3 Execution**

* A standard BST-20 transfer MUST execute in \<1ms on the reference hardware.

* The sandbox MUST prevent a malicious contract from accessing the filesystem, network, or other contracts’ memory.

* Gas metering MUST be accurate within 5% of the published gas table.

* A contract exceeding its gas limit MUST be terminated and its state changes reverted.

## **13.4 Storage**

* State root computation MUST be deterministic: given the same transactions in the same order, all nodes produce the same state root.

* Merkle proofs MUST be verifiable by a light client using only the state root and the proof path.

* RocksDB MUST sustain 50,000 random reads/second and 20,000 writes/second on reference hardware.

## **13.5 API**

* gRPC SendTransaction MUST return within 50ms (acknowledgment, not confirmation).

* REST API MUST pass OpenAPI validation with zero errors.

* WebSocket block subscriptions MUST deliver new blocks within 100ms of finalization.

## **13.6 Smart Contracts**

* A developer MUST be able to scaffold, compile, test, and deploy a BST-20 token in under 10 minutes using the CLI.

* All Roslyn analyzers MUST produce zero false positives on the SDK’s example contract suite.

* Contract unit tests MUST run against the in-process emulator without requiring a running node.

## **13.7 End-to-End**

* A 4-node devnet MUST sustain 10,000 TPS for 10 minutes without errors, with all nodes converging on the same state root.

* The devnet MUST recover from a complete restart (all 4 nodes stopped and restarted) and resume block production within 30 seconds.

# **14\. Appendices**

## **Appendix A: Chain Parameters**

| Parameter | Value | Governance-Adjustable |
| :---- | :---- | :---- |
| Chain ID | TBD (registered at chainlist.org) | No |
| Block time | 2s | Yes |
| Block gas limit | 100,000,000 | Yes |
| Max block size | 2 MB | Yes |
| Max transaction size | 128 KB (call) / 2 MB (deploy) | Yes |
| Min gas price | 1 gwei | Yes |
| Staking minimum | 100,000 BST | Yes |
| Unbonding period | 21 days | Yes |
| Validator key rotation | 90 days | Yes |
| State expiry window | 12 months | Yes |
| Max validators (mainnet) | 200 | Yes |
| Fee burn rate | 50% | Yes |
| BST decimals | 18 | No |
| Total supply | 1,000,000,000 BST | No |
| Address format | 0x \+ 40 hex chars (EVM-compatible) | No |

## **Appendix B: System Contract Addresses**

| Contract | Address | Description |
| :---- | :---- | :---- |
| Staking | 0x0...0001 | Validator registration, staking, delegation, slashing |
| Governance | 0x0...0002 | Proposal creation, voting, execution |
| IdentityRegistry | 0x0...0003 | KYC attestation storage and verification |
| ComplianceEngine | 0x0...0004 | Transfer compliance rule evaluation |
| GasSponsor | 0x0...0005 | Enterprise gas sponsoring and quota management |
| GasAbstraction | 0x0...0006 | Stablecoin-to-BST fee conversion |
| SubnetManager | 0x0...0007 | Subnet creation, validator approval, anchoring |
| ChannelManager | 0x0...0008 | Private state channel lifecycle |
| TokenWhitePaper | 0x0...0009 | MiCA white paper registration |
| AuditModule | 0x0...000A | Compliance audit trail and reporting |
| BridgeETH | 0x0...0010 | EVM bridge (Ethereum/Polygon) |

## **Appendix C: Error Codes**

| Code | Name | Description |
| :---- | :---- | :---- |
| 0x0001 | INVALID\_SIGNATURE | Transaction signature verification failed |
| 0x0002 | NONCE\_TOO\_LOW | Transaction nonce \< account nonce (replay) |
| 0x0003 | NONCE\_TOO\_HIGH | Transaction nonce \> account nonce (gap) |
| 0x0004 | INSUFFICIENT\_BALANCE | Balance \< value \+ gasLimit \* gasPrice |
| 0x0005 | GAS\_LIMIT\_EXCEEDED | tx.GasLimit \> block.GasLimit |
| 0x0006 | DATA\_SIZE\_EXCEEDED | Transaction data exceeds maximum size |
| 0x0007 | CHAIN\_ID\_MISMATCH | Wrong chain ID |
| 0x0010 | OUT\_OF\_GAS | Execution ran out of gas |
| 0x0011 | STACK\_OVERFLOW | Contract exceeded stack depth limit |
| 0x0012 | REVERT | Contract called Revert() with reason |
| 0x0013 | SANDBOX\_VIOLATION | Contract attempted forbidden system call |
| 0x0020 | COMPLIANCE\_KYC\_MISSING | Sender or receiver lacks KYC attestation |
| 0x0021 | COMPLIANCE\_SANCTIONED | Address is on the sanctions blocklist |
| 0x0022 | COMPLIANCE\_GEO\_RESTRICTED | Transfer violates geographic restrictions |
| 0x0023 | COMPLIANCE\_HOLDING\_LIMIT | Transfer would exceed holding concentration |
| 0x0024 | COMPLIANCE\_LOCKUP | Tokens are under lock-up/vesting |

*— End of Specification —*