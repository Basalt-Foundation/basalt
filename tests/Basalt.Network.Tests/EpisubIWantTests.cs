using Basalt.Core;
using Basalt.Network;
using Basalt.Network.Gossip;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Network.Tests;

public class EpisubIWantTests
{
    private static EpisubService CreateService()
    {
        var peerManager = new PeerManager(NullLogger<PeerManager>.Instance);
        return new EpisubService(peerManager, NullLogger<EpisubService>.Instance);
    }

    private static Hash256 MakeMessageId(byte b)
    {
        var bytes = new byte[32];
        bytes[0] = b;
        return new Hash256(bytes);
    }

    [Fact]
    public void CacheMessage_StoresMessage()
    {
        var service = CreateService();
        var msgId = MakeMessageId(1);
        var data = new byte[] { 0x01, 0x02, 0x03 };

        service.CacheMessage(msgId, data);

        service.CachedMessageCount.Should().Be(1);
        service.TryGetCachedMessage(msgId, out var retrieved).Should().BeTrue();
        retrieved.Should().Equal(data);
    }

    [Fact]
    public void HandleIWant_CachedMessage_ReturnsData()
    {
        var service = CreateService();
        var msgId = MakeMessageId(2);

        // NET-M16: Set up IHAVE correlation by broadcasting to the peer first.
        // The sender must be a lazy peer so BroadcastPriority sends IHAVE and records it.
        var sender = new PeerId(new Hash256(new byte[32]));
        // Fill eager slots so sender gets placed in the lazy tier
        for (var i = 0; i < 6; i++)
        {
            var filler = new byte[32];
            filler[0] = (byte)(i + 10);
            service.OnPeerConnected(new PeerId(new Hash256(filler)));
        }
        service.OnPeerConnected(sender); // goes to lazy tier
        service.OnSendMessage += (_, _) => { }; // sink for serialized messages
        service.BroadcastPriority(msgId, new PingMessage());

        // The message is now cached and the IHAVE correlation is recorded for sender
        var results = service.HandleIWant(sender, [msgId]).ToList();

        results.Should().HaveCount(1);
        results[0].Id.Should().Be(msgId);
    }

    [Fact]
    public void HandleIWant_UnknownMessage_ReturnsEmpty()
    {
        var service = CreateService();
        var unknownId = MakeMessageId(99);

        var sender = new PeerId(new Hash256(new byte[32]));
        var results = service.HandleIWant(sender, [unknownId]).ToList();

        results.Should().BeEmpty();
    }

    [Fact]
    public void HandleIWant_MixedKnownUnknown_ReturnsOnlyKnown()
    {
        var service = CreateService();
        var knownId = MakeMessageId(1);
        var unknownId = MakeMessageId(2);

        // NET-M16: Set up IHAVE correlation by broadcasting the known message to the peer.
        var sender = new PeerId(new Hash256(new byte[32]));
        // Fill eager slots so sender gets placed in the lazy tier
        for (var i = 0; i < 6; i++)
        {
            var filler = new byte[32];
            filler[0] = (byte)(i + 10);
            service.OnPeerConnected(new PeerId(new Hash256(filler)));
        }
        service.OnPeerConnected(sender); // goes to lazy tier
        service.OnSendMessage += (_, _) => { }; // sink for serialized messages
        service.BroadcastPriority(knownId, new PingMessage());
        // unknownId is never broadcast, so no IHAVE correlation exists for it

        var results = service.HandleIWant(sender, [knownId, unknownId]).ToList();

        results.Should().HaveCount(1);
        results[0].Id.Should().Be(knownId);
    }

    [Fact]
    public void TryGetCachedMessage_NotCached_ReturnsFalse()
    {
        var service = CreateService();
        var msgId = MakeMessageId(42);

        service.TryGetCachedMessage(msgId, out var data).Should().BeFalse();
        data.Should().BeNull();
    }

    [Fact]
    public void CleanupSeenMessages_RemovesCachedMessages()
    {
        var service = CreateService();
        var msgId = MakeMessageId(1);
        var data = new byte[] { 0x01 };

        // Cache a message without marking it as seen
        service.CacheMessage(msgId, data);
        service.CachedMessageCount.Should().Be(1);

        // Cleanup should remove it since it's not in the seen messages
        service.CleanupSeenMessages();
        service.CachedMessageCount.Should().Be(0);
    }
}
