using Basalt.Core;

namespace Basalt.Network;

/// <summary>
/// Network message types for the Basalt P2P protocol.
/// </summary>
public enum MessageType : byte
{
    Hello = 0x01,
    HelloAck = 0x02,
    Ping = 0x03,
    Pong = 0x04,

    // Transaction gossip
    TxAnnounce = 0x10,
    TxRequest = 0x11,
    TxPayload = 0x12,

    // Block gossip
    BlockAnnounce = 0x20,
    BlockRequest = 0x21,
    BlockPayload = 0x22,

    // Consensus
    ConsensusProposal = 0x30,
    ConsensusVote = 0x31,
    ConsensusViewChange = 0x32,
    ConsensusAggregateVote = 0x33,

    // Sync
    SyncRequest = 0x40,
    SyncResponse = 0x41,

    // Gossip protocol (Episub)
    IHave = 0x50,
    IWant = 0x51,
    Graft = 0x52,
    Prune = 0x53,

    // DHT
    FindNode = 0x60,
    FindNodeResponse = 0x61,
}

/// <summary>
/// Base network message.
/// </summary>
public abstract class NetworkMessage
{
    public abstract MessageType Type { get; }
    public PeerId SenderId { get; init; }
    /// <summary>
    /// NET-I03: No default — force explicit initialization to prevent silent non-determinism.
    /// All message creation sites must set Timestamp explicitly.
    /// </summary>
    public long Timestamp { get; init; }
}

/// <summary>
/// Initial handshake message.
/// </summary>
public sealed class HelloMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Hello;
    public uint ProtocolVersion { get; init; } = 1;
    public uint ChainId { get; init; }
    public ulong BestBlockNumber { get; init; }
    public Hash256 BestBlockHash { get; init; }
    public Hash256 GenesisHash { get; init; }
    public PublicKey NodePublicKey { get; init; }
    public BlsPublicKey BlsPublicKey { get; init; }
    public string ListenAddress { get; init; } = "";
    public int ListenPort { get; init; }

    /// <summary>
    /// NET-C01: 32-byte random nonce for challenge-response authentication.
    /// </summary>
    public byte[] ChallengeNonce { get; init; } = [];

    /// <summary>
    /// NET-C01: Ed25519 signature of BLAKE3("basalt-hello-v1" || ChallengeNonce || ChainId),
    /// proving the initiator owns the stated NodePublicKey.
    /// </summary>
    public Signature AuthSignature { get; init; }

    /// <summary>
    /// NET-C02: Ephemeral X25519 public key for transport encryption key exchange.
    /// </summary>
    public byte[] X25519PublicKey { get; init; } = [];

    /// <summary>
    /// NET-C02: Ed25519 signature of the X25519 public key, binding it to the node identity.
    /// </summary>
    public Signature X25519KeySignature { get; init; }
}

/// <summary>
/// Handshake acknowledgement. Carries the responder's identity so the initiator
/// can derive the remote PeerId (which is not available from HelloAck alone otherwise).
/// </summary>
public sealed class HelloAckMessage : NetworkMessage
{
    public override MessageType Type => MessageType.HelloAck;
    public bool Accepted { get; init; }
    public string RejectReason { get; init; } = "";
    public PublicKey NodePublicKey { get; init; }
    public BlsPublicKey BlsPublicKey { get; init; }
    public int ListenPort { get; init; }
    public ulong BestBlockNumber { get; init; }
    public Hash256 BestBlockHash { get; init; }

    /// <summary>
    /// NET-C01: Ed25519 signature of BLAKE3("basalt-ack-v1" || initiator ChallengeNonce || ChainId),
    /// proving the responder owns the stated NodePublicKey.
    /// </summary>
    public Signature ChallengeResponse { get; init; }

    /// <summary>
    /// NET-H03: Genesis hash for cross-validation by initiator.
    /// </summary>
    public Hash256 GenesisHash { get; init; }

    /// <summary>
    /// NET-C02: Ephemeral X25519 public key for transport encryption key exchange.
    /// </summary>
    public byte[] X25519PublicKey { get; init; } = [];

    /// <summary>
    /// NET-C02: Ed25519 signature of the X25519 public key, binding it to the node identity.
    /// </summary>
    public Signature X25519KeySignature { get; init; }
}

/// <summary>
/// Announce a transaction hash to peers.
/// </summary>
public sealed class TxAnnounceMessage : NetworkMessage
{
    public override MessageType Type => MessageType.TxAnnounce;
    public Hash256[] TransactionHashes { get; init; } = [];
}

/// <summary>
/// Request full transaction data.
/// </summary>
public sealed class TxRequestMessage : NetworkMessage
{
    public override MessageType Type => MessageType.TxRequest;
    public Hash256[] TransactionHashes { get; init; } = [];
}

/// <summary>
/// Full transaction payload.
/// </summary>
public sealed class TxPayloadMessage : NetworkMessage
{
    public override MessageType Type => MessageType.TxPayload;
    public byte[][] Transactions { get; init; } = [];
}

/// <summary>
/// Announce a new block.
/// </summary>
public sealed class BlockAnnounceMessage : NetworkMessage
{
    public override MessageType Type => MessageType.BlockAnnounce;
    public ulong BlockNumber { get; init; }
    public Hash256 BlockHash { get; init; }
    public Hash256 ParentHash { get; init; }
}

/// <summary>
/// Request block data.
/// </summary>
public sealed class BlockRequestMessage : NetworkMessage
{
    public override MessageType Type => MessageType.BlockRequest;
    public ulong StartNumber { get; init; }
    public int Count { get; init; }
}

/// <summary>
/// Block payload with full block data.
/// </summary>
public sealed class BlockPayloadMessage : NetworkMessage
{
    public override MessageType Type => MessageType.BlockPayload;
    public byte[][] Blocks { get; init; } = [];
}

/// <summary>
/// Consensus proposal for a new block.
/// </summary>
public sealed class ConsensusProposalMessage : NetworkMessage
{
    public override MessageType Type => MessageType.ConsensusProposal;
    public ulong ViewNumber { get; init; }
    public ulong BlockNumber { get; init; }
    public Hash256 BlockHash { get; init; }
    public byte[] BlockData { get; init; } = [];
    public BlsSignature ProposerSignature { get; init; }
}

/// <summary>
/// Consensus vote (PREPARE/PRE-COMMIT/COMMIT).
/// </summary>
public sealed class ConsensusVoteMessage : NetworkMessage
{
    public override MessageType Type => MessageType.ConsensusVote;
    public ulong ViewNumber { get; init; }
    public ulong BlockNumber { get; init; }
    public Hash256 BlockHash { get; init; }
    public VotePhase Phase { get; init; }
    public BlsSignature VoterSignature { get; init; }
    public BlsPublicKey VoterPublicKey { get; init; }
}

/// <summary>
/// View change request when leader is unresponsive.
/// </summary>
public sealed class ViewChangeMessage : NetworkMessage
{
    public override MessageType Type => MessageType.ConsensusViewChange;
    public ulong CurrentView { get; init; }
    public ulong ProposedView { get; init; }
    public BlsSignature VoterSignature { get; init; }
    public BlsPublicKey VoterPublicKey { get; init; }
}

/// <summary>
/// Aggregated quorum certificate broadcast by the leader after collecting enough votes.
/// Contains a single BLS aggregate signature and a bitmap of contributing validators.
/// </summary>
public sealed class AggregateVoteMessage : NetworkMessage
{
    public override MessageType Type => MessageType.ConsensusAggregateVote;
    public ulong ViewNumber { get; init; }
    public ulong BlockNumber { get; init; }
    public Hash256 BlockHash { get; init; }
    public VotePhase Phase { get; init; }
    public BlsSignature AggregateSignature { get; init; }
    public ulong VoterBitmap { get; init; }
}

/// <summary>
/// Vote phases in BasaltBFT consensus.
/// </summary>
public enum VotePhase : byte
{
    Prepare = 0,
    PreCommit = 1,
    Commit = 2,
}

// --- Episub Gossip Messages ---

/// <summary>
/// IHAVE message: announce available message IDs to lazy peers.
/// </summary>
public sealed class IHaveMessage : NetworkMessage
{
    public override MessageType Type => MessageType.IHave;
    public Hash256[] MessageIds { get; init; } = [];
}

/// <summary>
/// IWANT message: request full messages from a peer.
/// </summary>
public sealed class IWantMessage : NetworkMessage
{
    public override MessageType Type => MessageType.IWant;
    public Hash256[] MessageIds { get; init; } = [];
}

/// <summary>
/// GRAFT message: request promotion to eager peer tier.
/// </summary>
public sealed class GraftMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Graft;
}

/// <summary>
/// PRUNE message: request demotion to lazy peer tier.
/// </summary>
public sealed class PruneMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Prune;
}

// --- DHT Messages ---

/// <summary>
/// FindNode request: ask a peer for nodes closest to a target.
/// </summary>
public sealed class FindNodeMessage : NetworkMessage
{
    public override MessageType Type => MessageType.FindNode;
    public PeerId Target { get; init; }
}

/// <summary>
/// FindNode response: return closest known peers.
/// </summary>
public sealed class FindNodeResponseMessage : NetworkMessage
{
    public override MessageType Type => MessageType.FindNodeResponse;
    public PeerNodeInfo[] ClosestPeers { get; init; } = [];
}

/// <summary>
/// Compact peer info for DHT responses.
/// </summary>
public sealed class PeerNodeInfo
{
    public required PeerId Id { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required PublicKey PublicKey { get; init; }
}
