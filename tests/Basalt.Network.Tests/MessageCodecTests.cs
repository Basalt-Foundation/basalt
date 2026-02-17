using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using Xunit;

namespace Basalt.Network.Tests;

public class MessageCodecTests
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

    private static (byte[] privateKey, PublicKey publicKey, Signature signature) MakeSignature()
    {
        var (priv, pub) = Ed25519Signer.GenerateKeyPair();
        var sig = Ed25519Signer.Sign(priv, new byte[] { 1, 2, 3 });
        return (priv, pub, sig);
    }

    private static PublicKey MakePublicKey()
    {
        var (_, pub) = Ed25519Signer.GenerateKeyPair();
        return pub;
    }

    private static void AssertHeaderEquals(NetworkMessage original, NetworkMessage result)
    {
        Assert.Equal(original.SenderId.AsHash256(), result.SenderId.AsHash256());
        Assert.Equal(original.Timestamp, result.Timestamp);
    }

    [Fact]
    public void Roundtrip_HelloMessage()
    {
        var pubKey = MakePublicKey();
        var original = new HelloMessage
        {
            SenderId = MakePeerId(1),
            Timestamp = 1234567890L,
            ProtocolVersion = 42,
            ChainId = 7,
            BestBlockNumber = 999UL,
            BestBlockHash = MakeHash(10),
            GenesisHash = MakeHash(20),
            NodePublicKey = pubKey,
            ListenAddress = "192.168.1.100",
            ListenPort = 30303,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<HelloMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.ProtocolVersion, result.ProtocolVersion);
        Assert.Equal(original.ChainId, result.ChainId);
        Assert.Equal(original.BestBlockNumber, result.BestBlockNumber);
        Assert.Equal(original.BestBlockHash, result.BestBlockHash);
        Assert.Equal(original.GenesisHash, result.GenesisHash);
        Assert.Equal(original.NodePublicKey.ToArray(), result.NodePublicKey.ToArray());
        Assert.Equal(original.ListenAddress, result.ListenAddress);
        Assert.Equal(original.ListenPort, result.ListenPort);
    }

    [Fact]
    public void Roundtrip_HelloAckMessage()
    {
        var pubKey = MakePublicKey();
        var original = new HelloAckMessage
        {
            SenderId = MakePeerId(2),
            Timestamp = 9876543210L,
            Accepted = true,
            RejectReason = "",
            NodePublicKey = pubKey,
            ListenPort = 30303,
            BestBlockNumber = 42UL,
            BestBlockHash = MakeHash(55),
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<HelloAckMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.Accepted, result.Accepted);
        Assert.Equal(original.RejectReason, result.RejectReason);
        Assert.Equal(original.NodePublicKey.ToArray(), result.NodePublicKey.ToArray());
        Assert.Equal(original.ListenPort, result.ListenPort);
        Assert.Equal(original.BestBlockNumber, result.BestBlockNumber);
        Assert.Equal(original.BestBlockHash, result.BestBlockHash);
    }

    [Fact]
    public void Roundtrip_HelloAckMessage_Rejected()
    {
        var pubKey = MakePublicKey();
        var original = new HelloAckMessage
        {
            SenderId = MakePeerId(3),
            Timestamp = 1111111111L,
            Accepted = false,
            RejectReason = "incompatible protocol version",
            NodePublicKey = pubKey,
            ListenPort = 30303,
            BestBlockNumber = 0UL,
            BestBlockHash = MakeHash(0),
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<HelloAckMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.Accepted, result.Accepted);
        Assert.Equal(original.RejectReason, result.RejectReason);
        Assert.Equal(original.NodePublicKey.ToArray(), result.NodePublicKey.ToArray());
        Assert.Equal(original.ListenPort, result.ListenPort);
        Assert.Equal(original.BestBlockNumber, result.BestBlockNumber);
        Assert.Equal(original.BestBlockHash, result.BestBlockHash);
    }

    [Fact]
    public void Roundtrip_PingMessage()
    {
        var original = new PingMessage
        {
            SenderId = MakePeerId(4),
            Timestamp = 5555555555L,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<PingMessage>(deserialized);
        AssertHeaderEquals(original, result);
    }

    [Fact]
    public void Roundtrip_PongMessage()
    {
        var original = new PongMessage
        {
            SenderId = MakePeerId(5),
            Timestamp = 6666666666L,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<PongMessage>(deserialized);
        AssertHeaderEquals(original, result);
    }

    [Fact]
    public void Roundtrip_TxAnnounceMessage()
    {
        var original = new TxAnnounceMessage
        {
            SenderId = MakePeerId(6),
            Timestamp = 7777777777L,
            TransactionHashes = new[] { MakeHash(1), MakeHash(2), MakeHash(3) },
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<TxAnnounceMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.TransactionHashes.Length, result.TransactionHashes.Length);
        for (int i = 0; i < original.TransactionHashes.Length; i++)
        {
            Assert.Equal(original.TransactionHashes[i], result.TransactionHashes[i]);
        }
    }

    [Fact]
    public void Roundtrip_TxRequestMessage()
    {
        var original = new TxRequestMessage
        {
            SenderId = MakePeerId(7),
            Timestamp = 8888888888L,
            TransactionHashes = new[] { MakeHash(11), MakeHash(22) },
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<TxRequestMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.TransactionHashes.Length, result.TransactionHashes.Length);
        for (int i = 0; i < original.TransactionHashes.Length; i++)
        {
            Assert.Equal(original.TransactionHashes[i], result.TransactionHashes[i]);
        }
    }

    [Fact]
    public void Roundtrip_TxPayloadMessage()
    {
        var original = new TxPayloadMessage
        {
            SenderId = MakePeerId(8),
            Timestamp = 9999999999L,
            Transactions = new[]
            {
                new byte[] { 0xAA, 0xBB, 0xCC },
                new byte[] { 0xDD, 0xEE },
                new byte[] { 0xFF },
            },
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<TxPayloadMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.Transactions.Length, result.Transactions.Length);
        for (int i = 0; i < original.Transactions.Length; i++)
        {
            Assert.Equal(original.Transactions[i], result.Transactions[i]);
        }
    }

    [Fact]
    public void Roundtrip_BlockAnnounceMessage()
    {
        var original = new BlockAnnounceMessage
        {
            SenderId = MakePeerId(9),
            Timestamp = 1000000000L,
            BlockNumber = 42UL,
            BlockHash = MakeHash(100),
            ParentHash = MakeHash(99),
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<BlockAnnounceMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.BlockNumber, result.BlockNumber);
        Assert.Equal(original.BlockHash, result.BlockHash);
        Assert.Equal(original.ParentHash, result.ParentHash);
    }

    [Fact]
    public void Roundtrip_BlockRequestMessage()
    {
        var original = new BlockRequestMessage
        {
            SenderId = MakePeerId(10),
            Timestamp = 2000000000L,
            StartNumber = 100UL,
            Count = 50,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<BlockRequestMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.StartNumber, result.StartNumber);
        Assert.Equal(original.Count, result.Count);
    }

    [Fact]
    public void Roundtrip_BlockPayloadMessage()
    {
        var original = new BlockPayloadMessage
        {
            SenderId = MakePeerId(11),
            Timestamp = 3000000000L,
            Blocks = new[]
            {
                new byte[] { 1, 2, 3, 4, 5 },
                new byte[] { 6, 7, 8 },
            },
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<BlockPayloadMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.Blocks.Length, result.Blocks.Length);
        for (int i = 0; i < original.Blocks.Length; i++)
        {
            Assert.Equal(original.Blocks[i], result.Blocks[i]);
        }
    }

    [Fact]
    public void Roundtrip_ConsensusProposalMessage()
    {
        var sig = new BlsSignature(new byte[96]);
        var original = new ConsensusProposalMessage
        {
            SenderId = MakePeerId(12),
            Timestamp = 4000000000L,
            ViewNumber = 5UL,
            BlockNumber = 101UL,
            BlockHash = MakeHash(50),
            BlockData = new byte[] { 10, 20, 30, 40, 50, 60 },
            ProposerSignature = sig,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<ConsensusProposalMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.ViewNumber, result.ViewNumber);
        Assert.Equal(original.BlockNumber, result.BlockNumber);
        Assert.Equal(original.BlockHash, result.BlockHash);
        Assert.Equal(original.BlockData, result.BlockData);
        Assert.Equal(original.ProposerSignature.ToArray(), result.ProposerSignature.ToArray());
    }

    [Fact]
    public void Roundtrip_ConsensusVoteMessage()
    {
        var sig = new BlsSignature(new byte[96]);
        var pub = new BlsPublicKey(new byte[48]);
        var original = new ConsensusVoteMessage
        {
            SenderId = MakePeerId(13),
            Timestamp = 5000000000L,
            ViewNumber = 10UL,
            BlockNumber = 200UL,
            BlockHash = MakeHash(60),
            Phase = VotePhase.PreCommit,
            VoterSignature = sig,
            VoterPublicKey = pub,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<ConsensusVoteMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.ViewNumber, result.ViewNumber);
        Assert.Equal(original.BlockNumber, result.BlockNumber);
        Assert.Equal(original.BlockHash, result.BlockHash);
        Assert.Equal(original.Phase, result.Phase);
        Assert.Equal(original.VoterSignature.ToArray(), result.VoterSignature.ToArray());
        Assert.Equal(original.VoterPublicKey.ToArray(), result.VoterPublicKey.ToArray());
    }

    [Fact]
    public void Roundtrip_ConsensusVoteMessage_PreparePhase()
    {
        var sig = new BlsSignature(new byte[96]);
        var pub = new BlsPublicKey(new byte[48]);
        var original = new ConsensusVoteMessage
        {
            SenderId = MakePeerId(14),
            Timestamp = 5100000000L,
            ViewNumber = 1UL,
            BlockNumber = 1UL,
            BlockHash = MakeHash(70),
            Phase = VotePhase.Prepare,
            VoterSignature = sig,
            VoterPublicKey = pub,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<ConsensusVoteMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(VotePhase.Prepare, result.Phase);
    }

    [Fact]
    public void Roundtrip_ConsensusVoteMessage_CommitPhase()
    {
        var sig = new BlsSignature(new byte[96]);
        var pub = new BlsPublicKey(new byte[48]);
        var original = new ConsensusVoteMessage
        {
            SenderId = MakePeerId(15),
            Timestamp = 5200000000L,
            ViewNumber = 3UL,
            BlockNumber = 3UL,
            BlockHash = MakeHash(80),
            Phase = VotePhase.Commit,
            VoterSignature = sig,
            VoterPublicKey = pub,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<ConsensusVoteMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(VotePhase.Commit, result.Phase);
    }

    [Fact]
    public void Roundtrip_ViewChangeMessage()
    {
        var sig = new BlsSignature(new byte[96]);
        var pub = new BlsPublicKey(new byte[48]);
        var original = new ViewChangeMessage
        {
            SenderId = MakePeerId(16),
            Timestamp = 6000000000L,
            CurrentView = 7UL,
            ProposedView = 8UL,
            VoterSignature = sig,
            VoterPublicKey = pub,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<ViewChangeMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.CurrentView, result.CurrentView);
        Assert.Equal(original.ProposedView, result.ProposedView);
        Assert.Equal(original.VoterSignature.ToArray(), result.VoterSignature.ToArray());
        Assert.Equal(original.VoterPublicKey.ToArray(), result.VoterPublicKey.ToArray());
    }

    [Fact]
    public void Roundtrip_AggregateVoteMessage()
    {
        var sig = new BlsSignature(new byte[96]);
        var original = new AggregateVoteMessage
        {
            SenderId = MakePeerId(28),
            Timestamp = 5500000000L,
            ViewNumber = 7UL,
            BlockNumber = 42UL,
            BlockHash = MakeHash(90),
            Phase = VotePhase.PreCommit,
            AggregateSignature = sig,
            VoterBitmap = 0b1011UL, // validators 0, 1, 3
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<AggregateVoteMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.ViewNumber, result.ViewNumber);
        Assert.Equal(original.BlockNumber, result.BlockNumber);
        Assert.Equal(original.BlockHash, result.BlockHash);
        Assert.Equal(original.Phase, result.Phase);
        Assert.Equal(original.AggregateSignature.ToArray(), result.AggregateSignature.ToArray());
        Assert.Equal(original.VoterBitmap, result.VoterBitmap);
    }

    [Fact]
    public void Roundtrip_SyncRequestMessage()
    {
        var original = new SyncRequestMessage
        {
            SenderId = MakePeerId(17),
            Timestamp = 7000000000L,
            FromBlock = 500UL,
            MaxBlocks = 128,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<SyncRequestMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.FromBlock, result.FromBlock);
        Assert.Equal(original.MaxBlocks, result.MaxBlocks);
    }

    [Fact]
    public void Roundtrip_SyncResponseMessage()
    {
        var original = new SyncResponseMessage
        {
            SenderId = MakePeerId(18),
            Timestamp = 8000000000L,
            Blocks = new[]
            {
                new byte[] { 0x01, 0x02, 0x03 },
                new byte[] { 0x04, 0x05, 0x06, 0x07 },
                new byte[] { 0x08 },
            },
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<SyncResponseMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.Blocks.Length, result.Blocks.Length);
        for (int i = 0; i < original.Blocks.Length; i++)
        {
            Assert.Equal(original.Blocks[i], result.Blocks[i]);
        }
    }

    [Fact]
    public void Roundtrip_IHaveMessage()
    {
        var original = new IHaveMessage
        {
            SenderId = MakePeerId(19),
            Timestamp = 9000000000L,
            MessageIds = new[] { MakeHash(31), MakeHash(32), MakeHash(33) },
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<IHaveMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.MessageIds.Length, result.MessageIds.Length);
        for (int i = 0; i < original.MessageIds.Length; i++)
        {
            Assert.Equal(original.MessageIds[i], result.MessageIds[i]);
        }
    }

    [Fact]
    public void Roundtrip_IWantMessage()
    {
        var original = new IWantMessage
        {
            SenderId = MakePeerId(20),
            Timestamp = 9100000000L,
            MessageIds = new[] { MakeHash(41), MakeHash(42) },
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<IWantMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.MessageIds.Length, result.MessageIds.Length);
        for (int i = 0; i < original.MessageIds.Length; i++)
        {
            Assert.Equal(original.MessageIds[i], result.MessageIds[i]);
        }
    }

    [Fact]
    public void Roundtrip_GraftMessage()
    {
        var original = new GraftMessage
        {
            SenderId = MakePeerId(21),
            Timestamp = 9200000000L,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<GraftMessage>(deserialized);
        AssertHeaderEquals(original, result);
    }

    [Fact]
    public void Roundtrip_PruneMessage()
    {
        var original = new PruneMessage
        {
            SenderId = MakePeerId(22),
            Timestamp = 9300000000L,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<PruneMessage>(deserialized);
        AssertHeaderEquals(original, result);
    }

    [Fact]
    public void Roundtrip_FindNodeMessage()
    {
        var targetId = MakePeerId(99);
        var original = new FindNodeMessage
        {
            SenderId = MakePeerId(23),
            Timestamp = 9400000000L,
            Target = targetId,
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<FindNodeMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.Target.AsHash256(), result.Target.AsHash256());
    }

    [Fact]
    public void Roundtrip_FindNodeResponseMessage()
    {
        var pub1 = MakePublicKey();
        var pub2 = MakePublicKey();
        var original = new FindNodeResponseMessage
        {
            SenderId = MakePeerId(24),
            Timestamp = 9500000000L,
            ClosestPeers = new[]
            {
                new PeerNodeInfo
                {
                    Id = MakePeerId(50),
                    Host = "10.0.0.50",
                    Port = 30303,
                    PublicKey = pub1,
                },
                new PeerNodeInfo
                {
                    Id = MakePeerId(51),
                    Host = "10.0.0.51",
                    Port = 30304,
                    PublicKey = pub2,
                },
            },
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<FindNodeResponseMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Equal(original.ClosestPeers.Length, result.ClosestPeers.Length);
        for (int i = 0; i < original.ClosestPeers.Length; i++)
        {
            Assert.Equal(original.ClosestPeers[i].Id.AsHash256(), result.ClosestPeers[i].Id.AsHash256());
            Assert.Equal(original.ClosestPeers[i].Host, result.ClosestPeers[i].Host);
            Assert.Equal(original.ClosestPeers[i].Port, result.ClosestPeers[i].Port);
            Assert.Equal(original.ClosestPeers[i].PublicKey.ToArray(), result.ClosestPeers[i].PublicKey.ToArray());
        }
    }

    [Fact]
    public void Roundtrip_TxAnnounceMessage_EmptyArray()
    {
        var original = new TxAnnounceMessage
        {
            SenderId = MakePeerId(25),
            Timestamp = 9600000000L,
            TransactionHashes = [],
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<TxAnnounceMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Empty(result.TransactionHashes);
    }

    [Fact]
    public void Roundtrip_BlockPayloadMessage_EmptyArray()
    {
        var original = new BlockPayloadMessage
        {
            SenderId = MakePeerId(26),
            Timestamp = 9700000000L,
            Blocks = [],
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<BlockPayloadMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Empty(result.Blocks);
    }

    [Fact]
    public void Roundtrip_FindNodeResponseMessage_EmptyPeers()
    {
        var original = new FindNodeResponseMessage
        {
            SenderId = MakePeerId(27),
            Timestamp = 9800000000L,
            ClosestPeers = [],
        };

        var bytes = MessageCodec.Serialize(original);
        var deserialized = MessageCodec.Deserialize(bytes);

        var result = Assert.IsType<FindNodeResponseMessage>(deserialized);
        AssertHeaderEquals(original, result);
        Assert.Empty(result.ClosestPeers);
    }
}
