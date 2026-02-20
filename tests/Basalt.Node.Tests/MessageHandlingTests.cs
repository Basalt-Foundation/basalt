using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using FluentAssertions;
using Xunit;

namespace Basalt.Node.Tests;

public class MessageHandlingTests
{
    private static PeerId MakePeerId()
    {
        var (_, pub) = Ed25519Signer.GenerateKeyPair();
        return PeerId.FromPublicKey(pub);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void TxAnnounceMessage_RoundTrips()
    {
        var msg = new TxAnnounceMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            TransactionHashes = [Blake3Hasher.Hash([1]), Blake3Hasher.Hash([2])],
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<TxAnnounceMessage>();
        var result = (TxAnnounceMessage)deserialized;
        result.TransactionHashes.Should().HaveCount(2);
    }

    [Fact]
    public void BlockAnnounceMessage_RoundTrips()
    {
        var msg = new BlockAnnounceMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            BlockNumber = 42,
            BlockHash = Blake3Hasher.Hash([1, 2, 3]),
            ParentHash = Blake3Hasher.Hash([0]),
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<BlockAnnounceMessage>();
        var result = (BlockAnnounceMessage)deserialized;
        result.BlockNumber.Should().Be(42);
        result.BlockHash.Should().Be(msg.BlockHash);
    }

    [Fact]
    public void BlockRequestMessage_RoundTrips()
    {
        var msg = new BlockRequestMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            StartNumber = 10,
            Count = 5,
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<BlockRequestMessage>();
        var result = (BlockRequestMessage)deserialized;
        result.StartNumber.Should().Be(10);
        result.Count.Should().Be(5);
    }

    [Fact]
    public void TxRequestMessage_RoundTrips()
    {
        var hash = Blake3Hasher.Hash([99]);
        var msg = new TxRequestMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            TransactionHashes = [hash],
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<TxRequestMessage>();
        ((TxRequestMessage)deserialized).TransactionHashes[0].Should().Be(hash);
    }

    [Fact]
    public void SyncRequestMessage_RoundTrips()
    {
        var msg = new SyncRequestMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            FromBlock = 100,
            MaxBlocks = 50,
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<SyncRequestMessage>();
        var result = (SyncRequestMessage)deserialized;
        result.FromBlock.Should().Be(100);
        result.MaxBlocks.Should().Be(50);
    }

    [Fact]
    public void PingMessage_RoundTrips()
    {
        var msg = new PingMessage { SenderId = MakePeerId(), Timestamp = Now() };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<PingMessage>();
    }

    [Fact]
    public void PongMessage_RoundTrips()
    {
        var msg = new PongMessage { SenderId = MakePeerId(), Timestamp = Now() };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<PongMessage>();
    }

    [Fact]
    public void ConsensusProposalMessage_RoundTrips()
    {
        var msg = new ConsensusProposalMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            ViewNumber = 1,
            BlockNumber = 1,
            BlockHash = Blake3Hasher.Hash([1]),
            BlockData = [0x01, 0x02, 0x03],
            ProposerSignature = new BlsSignature(new byte[96]),
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<ConsensusProposalMessage>();
        var result = (ConsensusProposalMessage)deserialized;
        result.ViewNumber.Should().Be(1);
        result.BlockNumber.Should().Be(1);
        result.BlockData.Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public void ConsensusVoteMessage_RoundTrips()
    {
        var msg = new ConsensusVoteMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            BlockNumber = 5,
            BlockHash = Blake3Hasher.Hash([5]),
            Phase = VotePhase.Prepare,
            VoterSignature = new BlsSignature(new byte[96]),
            VoterPublicKey = new BlsPublicKey(new byte[48]),
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<ConsensusVoteMessage>();
        var result = (ConsensusVoteMessage)deserialized;
        result.BlockNumber.Should().Be(5);
        result.Phase.Should().Be(VotePhase.Prepare);
    }

    [Fact]
    public void ViewChangeMessage_RoundTrips()
    {
        var msg = new ViewChangeMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            CurrentView = 1,
            ProposedView = 5,
            VoterSignature = new BlsSignature(new byte[96]),
            VoterPublicKey = new BlsPublicKey(new byte[48]),
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<ViewChangeMessage>();
        var result = (ViewChangeMessage)deserialized;
        result.CurrentView.Should().Be(1);
        result.ProposedView.Should().Be(5);
    }

    [Fact]
    public void IHaveMessage_RoundTrips()
    {
        var msg = new IHaveMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            MessageIds = [Blake3Hasher.Hash([1]), Blake3Hasher.Hash([2])],
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<IHaveMessage>();
        ((IHaveMessage)deserialized).MessageIds.Should().HaveCount(2);
    }

    [Fact]
    public void IWantMessage_RoundTrips()
    {
        var msg = new IWantMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            MessageIds = [Blake3Hasher.Hash([3])],
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<IWantMessage>();
        ((IWantMessage)deserialized).MessageIds.Should().HaveCount(1);
    }

    [Fact]
    public void GraftMessage_RoundTrips()
    {
        var msg = new GraftMessage { SenderId = MakePeerId(), Timestamp = Now() };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<GraftMessage>();
    }

    [Fact]
    public void PruneMessage_RoundTrips()
    {
        var msg = new PruneMessage { SenderId = MakePeerId(), Timestamp = Now() };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<PruneMessage>();
    }

    [Fact]
    public void BlockPayloadMessage_RoundTrips()
    {
        var msg = new BlockPayloadMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            Blocks = [new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 }],
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<BlockPayloadMessage>();
        ((BlockPayloadMessage)deserialized).Blocks.Should().HaveCount(2);
    }

    [Fact]
    public void TxPayloadMessage_RoundTrips()
    {
        var msg = new TxPayloadMessage
        {
            SenderId = MakePeerId(),
            Timestamp = Now(),
            Transactions = [new byte[] { 10, 20 }],
        };
        var bytes = MessageCodec.Serialize(msg);
        var deserialized = MessageCodec.Deserialize(bytes);
        deserialized.Should().BeOfType<TxPayloadMessage>();
        ((TxPayloadMessage)deserialized).Transactions.Should().HaveCount(1);
    }
}
