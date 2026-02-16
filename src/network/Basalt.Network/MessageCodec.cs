using Basalt.Codec;
using Basalt.Core;

namespace Basalt.Network;

// --- Simple message types not defined elsewhere ---

public sealed class PingMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Ping;
}

public sealed class PongMessage : NetworkMessage
{
    public override MessageType Type => MessageType.Pong;
}

public sealed class SyncRequestMessage : NetworkMessage
{
    public override MessageType Type => MessageType.SyncRequest;
    public ulong FromBlock { get; init; }
    public int MaxBlocks { get; init; }
}

public sealed class SyncResponseMessage : NetworkMessage
{
    public override MessageType Type => MessageType.SyncResponse;
    public byte[][] Blocks { get; init; } = [];
}

/// <summary>
/// Serializes and deserializes all Basalt network messages using BasaltWriter/BasaltReader.
/// Wire format: [1 byte MessageType] [32 bytes SenderId] [8 bytes Timestamp] [payload...]
/// </summary>
public static class MessageCodec
{
    /// <summary>
    /// Stack allocation threshold. Messages estimated larger than this use heap allocation.
    /// </summary>
    private const int StackAllocThreshold = 8192;

    /// <summary>
    /// Maximum buffer size for any single message.
    /// </summary>
    private const int MaxBufferSize = 65536;

    /// <summary>
    /// Serialize a network message to a byte array.
    /// </summary>
    public static byte[] Serialize(NetworkMessage message)
    {
        int estimatedSize = EstimateSize(message);
        if (estimatedSize > MaxBufferSize)
            estimatedSize = MaxBufferSize;

        if (estimatedSize > StackAllocThreshold)
        {
            byte[] heapBuffer = new byte[estimatedSize];
            return SerializeInto(heapBuffer, message);
        }

        Span<byte> stackBuffer = stackalloc byte[estimatedSize];
        return SerializeInto(stackBuffer, message);
    }

    private static byte[] SerializeInto(Span<byte> buffer, NetworkMessage message)
    {
        var writer = new BasaltWriter(buffer);

        // Header: type + sender + timestamp
        writer.WriteByte((byte)message.Type);
        writer.WriteHash256(message.SenderId.AsHash256());
        writer.WriteInt64(message.Timestamp);

        // Message-specific payload
        switch (message)
        {
            case HelloMessage hello:
                WriteHello(ref writer, hello);
                break;

            case HelloAckMessage helloAck:
                WriteHelloAck(ref writer, helloAck);
                break;

            case PingMessage:
            case PongMessage:
                // No payload beyond the header
                break;

            case TxAnnounceMessage txAnnounce:
                WriteHashArray(ref writer, txAnnounce.TransactionHashes);
                break;

            case TxRequestMessage txRequest:
                WriteHashArray(ref writer, txRequest.TransactionHashes);
                break;

            case TxPayloadMessage txPayload:
                WriteByteArrays(ref writer, txPayload.Transactions);
                break;

            case BlockAnnounceMessage blockAnnounce:
                WriteBlockAnnounce(ref writer, blockAnnounce);
                break;

            case BlockRequestMessage blockRequest:
                WriteBlockRequest(ref writer, blockRequest);
                break;

            case BlockPayloadMessage blockPayload:
                WriteByteArrays(ref writer, blockPayload.Blocks);
                break;

            case ConsensusProposalMessage proposal:
                WriteConsensusProposal(ref writer, proposal);
                break;

            case ConsensusVoteMessage vote:
                WriteConsensusVote(ref writer, vote);
                break;

            case ViewChangeMessage viewChange:
                WriteViewChange(ref writer, viewChange);
                break;

            case SyncRequestMessage syncReq:
                WriteSyncRequest(ref writer, syncReq);
                break;

            case SyncResponseMessage syncResp:
                WriteByteArrays(ref writer, syncResp.Blocks);
                break;

            case IHaveMessage iHave:
                WriteHashArray(ref writer, iHave.MessageIds);
                break;

            case IWantMessage iWant:
                WriteHashArray(ref writer, iWant.MessageIds);
                break;

            case GraftMessage:
            case PruneMessage:
                // No payload beyond the header
                break;

            case FindNodeMessage findNode:
                writer.WriteHash256(findNode.Target.AsHash256());
                break;

            case FindNodeResponseMessage findNodeResp:
                WriteFindNodeResponse(ref writer, findNodeResp);
                break;

            default:
                throw new ArgumentException($"Unknown message type: {message.GetType().Name}");
        }

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Deserialize a byte array into a network message.
    /// </summary>
    public static NetworkMessage Deserialize(ReadOnlySpan<byte> data)
    {
        var reader = new BasaltReader(data);

        // Header
        var type = (MessageType)reader.ReadByte();
        var senderId = new PeerId(reader.ReadHash256());
        var timestamp = reader.ReadInt64();

        // Each branch sets SenderId and Timestamp in the object initializer since
        // NetworkMessage is a class with init-only properties (not a record).
        return type switch
        {
            MessageType.Hello => ReadHello(ref reader, senderId, timestamp),
            MessageType.HelloAck => ReadHelloAck(ref reader, senderId, timestamp),
            MessageType.Ping => new PingMessage { SenderId = senderId, Timestamp = timestamp },
            MessageType.Pong => new PongMessage { SenderId = senderId, Timestamp = timestamp },
            MessageType.TxAnnounce => new TxAnnounceMessage
            {
                SenderId = senderId, Timestamp = timestamp,
                TransactionHashes = ReadHashArray(ref reader),
            },
            MessageType.TxRequest => new TxRequestMessage
            {
                SenderId = senderId, Timestamp = timestamp,
                TransactionHashes = ReadHashArray(ref reader),
            },
            MessageType.TxPayload => new TxPayloadMessage
            {
                SenderId = senderId, Timestamp = timestamp,
                Transactions = ReadByteArrays(ref reader),
            },
            MessageType.BlockAnnounce => ReadBlockAnnounce(ref reader, senderId, timestamp),
            MessageType.BlockRequest => ReadBlockRequest(ref reader, senderId, timestamp),
            MessageType.BlockPayload => new BlockPayloadMessage
            {
                SenderId = senderId, Timestamp = timestamp,
                Blocks = ReadByteArrays(ref reader),
            },
            MessageType.ConsensusProposal => ReadConsensusProposal(ref reader, senderId, timestamp),
            MessageType.ConsensusVote => ReadConsensusVote(ref reader, senderId, timestamp),
            MessageType.ConsensusViewChange => ReadViewChange(ref reader, senderId, timestamp),
            MessageType.SyncRequest => ReadSyncRequest(ref reader, senderId, timestamp),
            MessageType.SyncResponse => new SyncResponseMessage
            {
                SenderId = senderId, Timestamp = timestamp,
                Blocks = ReadByteArrays(ref reader),
            },
            MessageType.IHave => new IHaveMessage
            {
                SenderId = senderId, Timestamp = timestamp,
                MessageIds = ReadHashArray(ref reader),
            },
            MessageType.IWant => new IWantMessage
            {
                SenderId = senderId, Timestamp = timestamp,
                MessageIds = ReadHashArray(ref reader),
            },
            MessageType.Graft => new GraftMessage { SenderId = senderId, Timestamp = timestamp },
            MessageType.Prune => new PruneMessage { SenderId = senderId, Timestamp = timestamp },
            MessageType.FindNode => new FindNodeMessage
            {
                SenderId = senderId, Timestamp = timestamp,
                Target = new PeerId(reader.ReadHash256()),
            },
            MessageType.FindNodeResponse => ReadFindNodeResponse(ref reader, senderId, timestamp),
            _ => throw new InvalidOperationException($"Unknown message type: 0x{(byte)type:X2}"),
        };
    }

    // --- Write helpers ---

    private static void WriteHello(ref BasaltWriter writer, HelloMessage msg)
    {
        writer.WriteUInt32(msg.ProtocolVersion);
        writer.WriteUInt32(msg.ChainId);
        writer.WriteUInt64(msg.BestBlockNumber);
        writer.WriteHash256(msg.BestBlockHash);
        writer.WriteHash256(msg.GenesisHash);
        writer.WritePublicKey(msg.NodePublicKey);
        writer.WriteBlsPublicKey(msg.BlsPublicKey);
        writer.WriteString(msg.ListenAddress);
        writer.WriteInt32(msg.ListenPort);
    }

    private static void WriteHelloAck(ref BasaltWriter writer, HelloAckMessage msg)
    {
        writer.WriteBool(msg.Accepted);
        writer.WriteString(msg.RejectReason);
        writer.WritePublicKey(msg.NodePublicKey);
        writer.WriteBlsPublicKey(msg.BlsPublicKey);
        writer.WriteInt32(msg.ListenPort);
        writer.WriteUInt64(msg.BestBlockNumber);
        writer.WriteHash256(msg.BestBlockHash);
    }

    private static void WriteBlockAnnounce(ref BasaltWriter writer, BlockAnnounceMessage msg)
    {
        writer.WriteUInt64(msg.BlockNumber);
        writer.WriteHash256(msg.BlockHash);
        writer.WriteHash256(msg.ParentHash);
    }

    private static void WriteBlockRequest(ref BasaltWriter writer, BlockRequestMessage msg)
    {
        writer.WriteUInt64(msg.StartNumber);
        writer.WriteInt32(msg.Count);
    }

    private static void WriteConsensusProposal(ref BasaltWriter writer, ConsensusProposalMessage msg)
    {
        writer.WriteUInt64(msg.ViewNumber);
        writer.WriteUInt64(msg.BlockNumber);
        writer.WriteHash256(msg.BlockHash);
        writer.WriteBytes(msg.BlockData);
        writer.WriteBlsSignature(msg.ProposerSignature);
    }

    private static void WriteConsensusVote(ref BasaltWriter writer, ConsensusVoteMessage msg)
    {
        writer.WriteUInt64(msg.ViewNumber);
        writer.WriteUInt64(msg.BlockNumber);
        writer.WriteHash256(msg.BlockHash);
        writer.WriteByte((byte)msg.Phase);
        writer.WriteBlsSignature(msg.VoterSignature);
        writer.WriteBlsPublicKey(msg.VoterPublicKey);
    }

    private static void WriteViewChange(ref BasaltWriter writer, ViewChangeMessage msg)
    {
        writer.WriteUInt64(msg.CurrentView);
        writer.WriteUInt64(msg.ProposedView);
        writer.WriteBlsSignature(msg.VoterSignature);
        writer.WriteBlsPublicKey(msg.VoterPublicKey);
    }

    private static void WriteSyncRequest(ref BasaltWriter writer, SyncRequestMessage msg)
    {
        writer.WriteUInt64(msg.FromBlock);
        writer.WriteInt32(msg.MaxBlocks);
    }

    private static void WriteFindNodeResponse(ref BasaltWriter writer, FindNodeResponseMessage msg)
    {
        writer.WriteVarInt((ulong)msg.ClosestPeers.Length);
        foreach (var peer in msg.ClosestPeers)
        {
            writer.WriteHash256(peer.Id.AsHash256());
            writer.WriteString(peer.Host);
            writer.WriteInt32(peer.Port);
            writer.WritePublicKey(peer.PublicKey);
        }
    }

    private static void WriteHashArray(ref BasaltWriter writer, Hash256[] hashes)
    {
        writer.WriteVarInt((ulong)hashes.Length);
        foreach (var hash in hashes)
        {
            writer.WriteHash256(hash);
        }
    }

    private static void WriteByteArrays(ref BasaltWriter writer, byte[][] arrays)
    {
        writer.WriteVarInt((ulong)arrays.Length);
        foreach (var item in arrays)
        {
            writer.WriteBytes(item);
        }
    }

    // --- Read helpers ---

    private static HelloMessage ReadHello(ref BasaltReader reader, PeerId senderId, long timestamp)
    {
        return new HelloMessage
        {
            SenderId = senderId,
            Timestamp = timestamp,
            ProtocolVersion = reader.ReadUInt32(),
            ChainId = reader.ReadUInt32(),
            BestBlockNumber = reader.ReadUInt64(),
            BestBlockHash = reader.ReadHash256(),
            GenesisHash = reader.ReadHash256(),
            NodePublicKey = reader.ReadPublicKey(),
            BlsPublicKey = reader.ReadBlsPublicKey(),
            ListenAddress = reader.ReadString(),
            ListenPort = reader.ReadInt32(),
        };
    }

    private static HelloAckMessage ReadHelloAck(ref BasaltReader reader, PeerId senderId, long timestamp)
    {
        return new HelloAckMessage
        {
            SenderId = senderId,
            Timestamp = timestamp,
            Accepted = reader.ReadBool(),
            RejectReason = reader.ReadString(),
            NodePublicKey = reader.ReadPublicKey(),
            BlsPublicKey = reader.ReadBlsPublicKey(),
            ListenPort = reader.ReadInt32(),
            BestBlockNumber = reader.ReadUInt64(),
            BestBlockHash = reader.ReadHash256(),
        };
    }

    private static BlockAnnounceMessage ReadBlockAnnounce(ref BasaltReader reader, PeerId senderId, long timestamp)
    {
        return new BlockAnnounceMessage
        {
            SenderId = senderId,
            Timestamp = timestamp,
            BlockNumber = reader.ReadUInt64(),
            BlockHash = reader.ReadHash256(),
            ParentHash = reader.ReadHash256(),
        };
    }

    private static BlockRequestMessage ReadBlockRequest(ref BasaltReader reader, PeerId senderId, long timestamp)
    {
        return new BlockRequestMessage
        {
            SenderId = senderId,
            Timestamp = timestamp,
            StartNumber = reader.ReadUInt64(),
            Count = reader.ReadInt32(),
        };
    }

    private static ConsensusProposalMessage ReadConsensusProposal(ref BasaltReader reader, PeerId senderId, long timestamp)
    {
        return new ConsensusProposalMessage
        {
            SenderId = senderId,
            Timestamp = timestamp,
            ViewNumber = reader.ReadUInt64(),
            BlockNumber = reader.ReadUInt64(),
            BlockHash = reader.ReadHash256(),
            BlockData = reader.ReadBytes().ToArray(),
            ProposerSignature = reader.ReadBlsSignature(),
        };
    }

    private static ConsensusVoteMessage ReadConsensusVote(ref BasaltReader reader, PeerId senderId, long timestamp)
    {
        return new ConsensusVoteMessage
        {
            SenderId = senderId,
            Timestamp = timestamp,
            ViewNumber = reader.ReadUInt64(),
            BlockNumber = reader.ReadUInt64(),
            BlockHash = reader.ReadHash256(),
            Phase = (VotePhase)reader.ReadByte(),
            VoterSignature = reader.ReadBlsSignature(),
            VoterPublicKey = reader.ReadBlsPublicKey(),
        };
    }

    private static ViewChangeMessage ReadViewChange(ref BasaltReader reader, PeerId senderId, long timestamp)
    {
        return new ViewChangeMessage
        {
            SenderId = senderId,
            Timestamp = timestamp,
            CurrentView = reader.ReadUInt64(),
            ProposedView = reader.ReadUInt64(),
            VoterSignature = reader.ReadBlsSignature(),
            VoterPublicKey = reader.ReadBlsPublicKey(),
        };
    }

    private static SyncRequestMessage ReadSyncRequest(ref BasaltReader reader, PeerId senderId, long timestamp)
    {
        return new SyncRequestMessage
        {
            SenderId = senderId,
            Timestamp = timestamp,
            FromBlock = reader.ReadUInt64(),
            MaxBlocks = reader.ReadInt32(),
        };
    }

    private static FindNodeResponseMessage ReadFindNodeResponse(ref BasaltReader reader, PeerId senderId, long timestamp)
    {
        var count = (int)reader.ReadVarInt();
        var peers = new PeerNodeInfo[count];
        for (int i = 0; i < count; i++)
        {
            peers[i] = new PeerNodeInfo
            {
                Id = new PeerId(reader.ReadHash256()),
                Host = reader.ReadString(),
                Port = reader.ReadInt32(),
                PublicKey = reader.ReadPublicKey(),
            };
        }

        return new FindNodeResponseMessage
        {
            SenderId = senderId,
            Timestamp = timestamp,
            ClosestPeers = peers,
        };
    }

    private static Hash256[] ReadHashArray(ref BasaltReader reader)
    {
        var count = (int)reader.ReadVarInt();
        var hashes = new Hash256[count];
        for (int i = 0; i < count; i++)
        {
            hashes[i] = reader.ReadHash256();
        }

        return hashes;
    }

    private static byte[][] ReadByteArrays(ref BasaltReader reader)
    {
        var count = (int)reader.ReadVarInt();
        var arrays = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            arrays[i] = reader.ReadBytes().ToArray();
        }

        return arrays;
    }

    // --- Size estimation ---

    /// <summary>
    /// Estimate the serialized size of a message to determine buffer allocation strategy.
    /// </summary>
    private static int EstimateSize(NetworkMessage message)
    {
        // Header: 1 (type) + 32 (senderId) + 8 (timestamp) = 41 bytes
        const int headerSize = 41;

        int payloadEstimate = message switch
        {
            HelloMessage => 4 + 4 + 8 + 32 + 32 + 32 + BlsPublicKey.Size + 256 + 4, // generous for string
            HelloAckMessage => 1 + 256 + PublicKey.Size + BlsPublicKey.Size + 4 + 8 + Hash256.Size, // bool + string + identity
            PingMessage => 0,
            PongMessage => 0,
            TxAnnounceMessage m => 10 + (m.TransactionHashes.Length * Hash256.Size),
            TxRequestMessage m => 10 + (m.TransactionHashes.Length * Hash256.Size),
            TxPayloadMessage m => EstimateByteArraysSize(m.Transactions),
            BlockAnnounceMessage => 8 + 32 + 32,
            BlockRequestMessage => 8 + 4,
            BlockPayloadMessage m => EstimateByteArraysSize(m.Blocks),
            ConsensusProposalMessage m => 8 + 8 + 32 + 10 + m.BlockData.Length + BlsSignature.Size,
            ConsensusVoteMessage => 8 + 8 + 32 + 1 + BlsSignature.Size + BlsPublicKey.Size,
            ViewChangeMessage => 8 + 8 + BlsSignature.Size + BlsPublicKey.Size,
            SyncRequestMessage => 8 + 4,
            SyncResponseMessage m => EstimateByteArraysSize(m.Blocks),
            IHaveMessage m => 10 + (m.MessageIds.Length * Hash256.Size),
            IWantMessage m => 10 + (m.MessageIds.Length * Hash256.Size),
            GraftMessage => 0,
            PruneMessage => 0,
            FindNodeMessage => Hash256.Size,
            FindNodeResponseMessage m => 10 + (m.ClosestPeers.Length * (32 + 256 + 4 + 32)),
            _ => 1024,
        };

        return Math.Min(headerSize + payloadEstimate, MaxBufferSize);
    }

    private static int EstimateByteArraysSize(byte[][] arrays)
    {
        int total = 10; // varint for count
        foreach (var arr in arrays)
        {
            total += 10 + arr.Length; // varint prefix + data
        }

        return total;
    }
}
