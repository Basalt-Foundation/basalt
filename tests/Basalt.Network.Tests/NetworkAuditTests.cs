using Basalt.Codec;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Network;
using Basalt.Network.DHT;
using Basalt.Network.Gossip;
using Basalt.Network.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Network.Tests;

/// <summary>
/// Tests for network layer audit findings (Issue #13).
/// </summary>
public class NetworkAuditTests
{
    private static PeerId MakePeerId(int seed)
    {
        var bytes = new byte[32];
        bytes[31] = (byte)seed;
        return new PeerId(new Hash256(bytes));
    }

    private static Hash256 MakeHash(int seed)
    {
        var bytes = new byte[32];
        bytes[0] = (byte)seed;
        return new Hash256(bytes);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static PublicKey MakePublicKey()
    {
        var (_, pub) = Ed25519Signer.GenerateKeyPair();
        return pub;
    }

    // ──────────────────────────────────────────────────
    // H-2: BlockCodec tx count validation
    // ──────────────────────────────────────────────────

    [Fact]
    public void BlockCodec_DeserializeBlock_RejectsExcessiveTxCount()
    {
        // Craft a minimal block header followed by a huge tx count varint
        var buffer = new byte[512];
        var writer = new BasaltWriter(buffer);

        // Write minimal block header
        writer.WriteUInt64(1);                    // Number
        writer.WriteHash256(Hash256.Zero);        // ParentHash
        writer.WriteHash256(Hash256.Zero);        // StateRoot
        writer.WriteHash256(Hash256.Zero);        // TransactionsRoot
        writer.WriteHash256(Hash256.Zero);        // ReceiptsRoot
        writer.WriteInt64(0);                     // Timestamp
        writer.WriteAddress(Address.Zero);        // Proposer
        writer.WriteUInt32(1);                    // ChainId
        writer.WriteUInt64(0);                    // GasUsed
        writer.WriteUInt64(0);                    // GasLimit
        writer.WriteUInt256(UInt256.Zero);        // BaseFee
        writer.WriteUInt32(1);                    // ProtocolVersion
        writer.WriteBytes([]);                    // ExtraData

        // Write excessively large tx count
        writer.WriteVarInt(999_999);

        var data = writer.WrittenSpan.ToArray();
        Assert.Throws<InvalidOperationException>(() => BlockCodec.DeserializeBlock(data));
    }

    [Fact]
    public void BlockCodec_DeserializeBlock_AcceptsValidTxCount()
    {
        // Serialize a valid block with 0 transactions
        var block = new Block
        {
            Header = new BlockHeader
            {
                Number = 1,
                ParentHash = Hash256.Zero,
                StateRoot = Hash256.Zero,
                TransactionsRoot = Hash256.Zero,
                ReceiptsRoot = Hash256.Zero,
                Timestamp = 0,
                Proposer = Address.Zero,
                ChainId = 1,
                GasUsed = 0,
                GasLimit = 0,
                BaseFee = UInt256.Zero,
                ProtocolVersion = 1,
                ExtraData = [],
            },
            Transactions = [],
        };

        var bytes = BlockCodec.SerializeBlock(block);
        var result = BlockCodec.DeserializeBlock(bytes);
        Assert.NotNull(result);
        Assert.Empty(result.Transactions);
    }

    // ──────────────────────────────────────────────────
    // H-3: BlockCodec ComplianceProof count validation
    // ──────────────────────────────────────────────────

    [Fact]
    public void BlockCodec_ReadTransaction_RejectsExcessiveProofCount()
    {
        // Craft a transaction with valid fields but excessive proof count
        var buffer = new byte[1024];
        var writer = new BasaltWriter(buffer);

        writer.WriteByte(0);                      // Type
        writer.WriteUInt64(0);                    // Nonce
        writer.WriteAddress(Address.Zero);        // Sender
        writer.WriteAddress(Address.Zero);        // To
        writer.WriteUInt256(UInt256.Zero);        // Value
        writer.WriteUInt64(21000);                // GasLimit
        writer.WriteUInt256(UInt256.Zero);        // GasPrice
        writer.WriteUInt256(UInt256.Zero);        // MaxFeePerGas
        writer.WriteUInt256(UInt256.Zero);        // MaxPriorityFeePerGas
        writer.WriteBytes([]);                    // Data
        writer.WriteByte(0);                      // Priority
        writer.WriteUInt32(1);                    // ChainId
        writer.WriteSignature(default);           // Signature
        writer.WritePublicKey(MakePublicKey());   // SenderPublicKey

        // Excessive proof count
        writer.WriteVarInt(50_000);

        var data = writer.WrittenSpan.ToArray();
        Assert.Throws<InvalidOperationException>(() => BlockCodec.DeserializeTransaction(data));
    }

    // ──────────────────────────────────────────────────
    // H-4: BlockRequest.Count validation
    // ──────────────────────────────────────────────────

    [Fact]
    public void MessageCodec_BlockRequest_RejectsZeroCount()
    {
        var msg = new BlockRequestMessage
        {
            SenderId = MakePeerId(1),
            Timestamp = Now(),
            StartNumber = 0,
            Count = 0,
        };

        var bytes = MessageCodec.Serialize(msg);
        Assert.Throws<InvalidOperationException>(() => MessageCodec.Deserialize(bytes));
    }

    [Fact]
    public void MessageCodec_BlockRequest_RejectsNegativeCount()
    {
        var msg = new BlockRequestMessage
        {
            SenderId = MakePeerId(1),
            Timestamp = Now(),
            StartNumber = 0,
            Count = -1,
        };

        var bytes = MessageCodec.Serialize(msg);
        Assert.Throws<InvalidOperationException>(() => MessageCodec.Deserialize(bytes));
    }

    [Fact]
    public void MessageCodec_BlockRequest_RejectsExcessiveCount()
    {
        var msg = new BlockRequestMessage
        {
            SenderId = MakePeerId(1),
            Timestamp = Now(),
            StartNumber = 0,
            Count = 999,
        };

        var bytes = MessageCodec.Serialize(msg);
        Assert.Throws<InvalidOperationException>(() => MessageCodec.Deserialize(bytes));
    }

    [Fact]
    public void MessageCodec_BlockRequest_AcceptsMaxValidCount()
    {
        var msg = new BlockRequestMessage
        {
            SenderId = MakePeerId(1),
            Timestamp = Now(),
            StartNumber = 0,
            Count = 200, // MaxSyncBlocks
        };

        var bytes = MessageCodec.Serialize(msg);
        var result = MessageCodec.Deserialize(bytes);
        Assert.IsType<BlockRequestMessage>(result);
        Assert.Equal(200, ((BlockRequestMessage)result).Count);
    }

    // ──────────────────────────────────────────────────
    // L-5: FindNodeResponse host/port validation
    // ──────────────────────────────────────────────────

    [Fact]
    public void MessageCodec_FindNodeResponse_RejectsEmptyHost()
    {
        var msg = new FindNodeResponseMessage
        {
            SenderId = MakePeerId(1),
            Timestamp = Now(),
            ClosestPeers = new[]
            {
                new PeerNodeInfo
                {
                    Id = MakePeerId(2),
                    Host = "",
                    Port = 30303,
                    PublicKey = MakePublicKey(),
                },
            },
        };

        var bytes = MessageCodec.Serialize(msg);
        Assert.Throws<InvalidOperationException>(() => MessageCodec.Deserialize(bytes));
    }

    [Fact]
    public void MessageCodec_FindNodeResponse_RejectsInvalidPort()
    {
        var msg = new FindNodeResponseMessage
        {
            SenderId = MakePeerId(1),
            Timestamp = Now(),
            ClosestPeers = new[]
            {
                new PeerNodeInfo
                {
                    Id = MakePeerId(2),
                    Host = "10.0.0.1",
                    Port = 0,
                    PublicKey = MakePublicKey(),
                },
            },
        };

        var bytes = MessageCodec.Serialize(msg);
        Assert.Throws<InvalidOperationException>(() => MessageCodec.Deserialize(bytes));
    }

    [Fact]
    public void MessageCodec_FindNodeResponse_RejectsNegativePort()
    {
        var msg = new FindNodeResponseMessage
        {
            SenderId = MakePeerId(1),
            Timestamp = Now(),
            ClosestPeers = new[]
            {
                new PeerNodeInfo
                {
                    Id = MakePeerId(2),
                    Host = "10.0.0.1",
                    Port = -1,
                    PublicKey = MakePublicKey(),
                },
            },
        };

        var bytes = MessageCodec.Serialize(msg);
        Assert.Throws<InvalidOperationException>(() => MessageCodec.Deserialize(bytes));
    }

    // ──────────────────────────────────────────────────
    // L-6: PeerInfo thread-safe LastSeen/ConnectedAt
    // ──────────────────────────────────────────────────

    [Fact]
    public void PeerInfo_LastSeen_ThreadSafe()
    {
        var peer = new PeerInfo
        {
            Id = MakePeerId(1),
            PublicKey = MakePublicKey(),
            Host = "10.0.0.1",
            Port = 30303,
        };

        var now = DateTimeOffset.UtcNow;
        peer.LastSeen = now;
        Assert.Equal(now.UtcTicks, peer.LastSeen.UtcTicks);
    }

    [Fact]
    public void PeerInfo_ConnectedAt_ThreadSafe()
    {
        var peer = new PeerInfo
        {
            Id = MakePeerId(1),
            PublicKey = MakePublicKey(),
            Host = "10.0.0.1",
            Port = 30303,
        };

        var now = DateTimeOffset.UtcNow;
        peer.ConnectedAt = now;
        Assert.Equal(now.UtcTicks, peer.ConnectedAt.UtcTicks);
    }

    // ──────────────────────────────────────────────────
    // L-7: IPv6 /48 subnet extraction
    // ──────────────────────────────────────────────────

    [Fact]
    public void KademliaTable_GetSubnet24_IPv4()
    {
        var subnet = KademliaTable.GetSubnet24("192.168.1.100");
        Assert.Equal("192.168.1", subnet);
    }

    [Fact]
    public void KademliaTable_GetSubnet24_IPv6_ExtractsSlash48()
    {
        var subnet = KademliaTable.GetSubnet24("2001:0db8:85a3:0000:0000:8a2e:0370:7334");
        Assert.Equal("2001:0db8:85a3", subnet);
    }

    [Fact]
    public void KademliaTable_GetSubnet24_IPv6_Abbreviated()
    {
        // "fe80::1" splits on ':' to ["fe80", "", "1"] → first 3 colon groups = "fe80::1"
        var subnet = KademliaTable.GetSubnet24("fe80::1");
        Assert.Equal("fe80::1", subnet);
    }

    [Fact]
    public void KademliaTable_GetSubnet24_IPv6_Full_NotAbbreviated()
    {
        // Full IPv6: "2001:db8:1:2:3:4:5:6" → first 3 groups = "2001:db8:1"
        var subnet = KademliaTable.GetSubnet24("2001:db8:1:2:3:4:5:6");
        Assert.Equal("2001:db8:1", subnet);
    }

    [Fact]
    public void KademliaTable_GetSubnet24_Hostname()
    {
        var subnet = KademliaTable.GetSubnet24("myhost.example.com");
        Assert.Equal("myhost.example.com", subnet);
    }

    [Fact]
    public void KademliaTable_GetSubnet24_Empty()
    {
        Assert.Equal("", KademliaTable.GetSubnet24(""));
    }

    // ──────────────────────────────────────────────────
    // M-6: EpisubService GraftPeer MaxEagerPeers cap
    // ──────────────────────────────────────────────────

    [Fact]
    public void EpisubService_GraftPeer_RespectsMaxEagerPeers()
    {
        var peerManager = new PeerManager(NullLogger<PeerManager>.Instance);
        var episub = new EpisubService(peerManager, NullLogger<EpisubService>.Instance);

        // Fill eager tier up to max (TargetEagerPeers * 2 = 12)
        for (int i = 1; i <= 12; i++)
        {
            episub.OnPeerConnected(MakePeerId(i));
        }

        // First 6 go to eager (TargetEagerPeers), rest go to lazy
        Assert.Equal(6, episub.EagerPeerCount);
        Assert.Equal(6, episub.LazyPeerCount);

        // Graft all lazy peers to eager (should succeed for the first 6)
        for (int i = 7; i <= 12; i++)
        {
            episub.GraftPeer(MakePeerId(i));
        }
        Assert.Equal(12, episub.EagerPeerCount);
        Assert.Equal(0, episub.LazyPeerCount);

        // Add one more peer (goes to lazy since eager is at TargetEagerPeers)
        episub.OnPeerConnected(MakePeerId(13));
        Assert.Equal(1, episub.LazyPeerCount);

        // Graft should be rejected — eager tier is full at MaxEagerPeers (12)
        episub.GraftPeer(MakePeerId(13));
        // Peer should remain in lazy tier
        Assert.Equal(12, episub.EagerPeerCount);
        Assert.Equal(1, episub.LazyPeerCount);
    }

    // ──────────────────────────────────────────────────
    // M-2: HandshakeResult.ZeroSharedSecret
    // ──────────────────────────────────────────────────

    [Fact]
    public void HandshakeResult_ZeroSharedSecret_WipesBytes()
    {
        var secret = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var result = HandshakeResult.Success(
            MakePeerId(1), MakePublicKey(), default, "", 30303, 0, Hash256.Zero,
            sharedSecret: secret);

        result.ZeroSharedSecret();

        // All bytes should be zero
        Assert.All(secret, b => Assert.Equal(0, b));
    }

    [Fact]
    public void HandshakeResult_ZeroSharedSecret_NullIsNoop()
    {
        var result = HandshakeResult.Failed("test");
        // Should not throw
        result.ZeroSharedSecret();
    }

    // ──────────────────────────────────────────────────
    // BlockCodec roundtrip with ComplianceProofs
    // ──────────────────────────────────────────────────

    [Fact]
    public void BlockCodec_Transaction_Roundtrip_WithComplianceProofs()
    {
        var (priv, pub) = Ed25519Signer.GenerateKeyPair();
        var tx = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 42,
            Sender = Address.Zero,
            To = Address.Zero,
            Value = new UInt256(1000),
            GasLimit = 21000,
            GasPrice = new UInt256(1),
            MaxFeePerGas = UInt256.Zero,
            MaxPriorityFeePerGas = UInt256.Zero,
            Data = new byte[] { 0xAB, 0xCD },
            Priority = 0,
            ChainId = 1,
            SenderPublicKey = pub,
            ComplianceProofs = new[]
            {
                new ComplianceProof
                {
                    SchemaId = MakeHash(10),
                    Proof = new byte[] { 1, 2, 3 },
                    PublicInputs = new byte[] { 4, 5, 6 },
                    Nullifier = MakeHash(20),
                },
            },
        };

        var bytes = BlockCodec.SerializeTransaction(tx);
        var result = BlockCodec.DeserializeTransaction(bytes);

        Assert.Single(result.ComplianceProofs);
        Assert.Equal(tx.ComplianceProofs[0].SchemaId, result.ComplianceProofs[0].SchemaId);
        Assert.Equal(tx.ComplianceProofs[0].Proof, result.ComplianceProofs[0].Proof);
        Assert.Equal(tx.ComplianceProofs[0].PublicInputs, result.ComplianceProofs[0].PublicInputs);
        Assert.Equal(tx.ComplianceProofs[0].Nullifier, result.ComplianceProofs[0].Nullifier);
    }
}
