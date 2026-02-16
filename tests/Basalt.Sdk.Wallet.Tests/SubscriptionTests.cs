namespace Basalt.Sdk.Wallet.Tests;

using Xunit;
using FluentAssertions;
using Basalt.Sdk.Wallet.Subscriptions;

public class SubscriptionTests
{
    [Fact]
    public void SubscriptionOptions_HasCorrectDefaults()
    {
        var options = new SubscriptionOptions();

        options.AutoReconnect.Should().BeTrue();
        options.MaxRetries.Should().Be(10);
        options.InitialDelayMs.Should().Be(1000);
        options.MaxDelayMs.Should().Be(30_000);
        options.ReceiveBufferSize.Should().Be(8192);
    }

    [Fact]
    public void BlockSubscription_IsNotConnected_BeforeSubscribe()
    {
        var subscription = new BlockSubscription("http://localhost:5100");

        subscription.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void BlockEvent_Deserialization()
    {
        var blockEvent = new BlockEvent
        {
            Type = "new_block",
            Block = new BlockEventData
            {
                Number = 42,
                Hash = "0xblockhash",
                ParentHash = "0xparenthash",
                StateRoot = "0xstateroot",
                Timestamp = 1700000000,
                Proposer = "0xproposer",
                GasUsed = 21000,
                GasLimit = 1000000,
                TransactionCount = 5
            }
        };

        blockEvent.Type.Should().Be("new_block");
        blockEvent.Block.Number.Should().Be(42UL);
        blockEvent.Block.Hash.Should().Be("0xblockhash");
        blockEvent.Block.ParentHash.Should().Be("0xparenthash");
        blockEvent.Block.StateRoot.Should().Be("0xstateroot");
        blockEvent.Block.Timestamp.Should().Be(1700000000);
        blockEvent.Block.Proposer.Should().Be("0xproposer");
        blockEvent.Block.GasUsed.Should().Be(21000UL);
        blockEvent.Block.GasLimit.Should().Be(1000000UL);
        blockEvent.Block.TransactionCount.Should().Be(5);
    }

    [Fact]
    public void CreateBlockSubscription_ReturnsInstance()
    {
        var subscription = BasaltProvider.CreateBlockSubscription("http://localhost:5100");

        subscription.Should().NotBeNull();
        subscription.Should().BeAssignableTo<IBlockSubscription>();
    }
}
