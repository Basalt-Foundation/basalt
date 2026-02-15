using FluentAssertions;
using Xunit;

namespace Basalt.Node.Tests;

public class NodeConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new NodeConfiguration();
        config.ValidatorIndex.Should().Be(-1);
        config.ValidatorAddress.Should().Be("");
        config.ValidatorKeyHex.Should().Be("");
        config.NetworkName.Should().Be("basalt-devnet");
        config.ChainId.Should().Be(31337u);
        config.HttpPort.Should().Be(5000);
        config.P2PPort.Should().Be(30303);
        config.DataDir.Should().BeNull();
        config.Peers.Should().BeEmpty();
        config.UsePipelining.Should().BeFalse();
        config.UseSandbox.Should().BeFalse();
    }

    [Fact]
    public void IsConsensusMode_NoPeers_ReturnsFalse()
    {
        var config = new NodeConfiguration { ValidatorIndex = 0 };
        config.IsConsensusMode.Should().BeFalse();
    }

    [Fact]
    public void IsConsensusMode_NoValidatorIndex_ReturnsFalse()
    {
        var config = new NodeConfiguration { Peers = ["localhost:30300"] };
        config.IsConsensusMode.Should().BeFalse();
    }

    [Fact]
    public void IsConsensusMode_WithPeersAndIndex_ReturnsTrue()
    {
        var config = new NodeConfiguration
        {
            ValidatorIndex = 0,
            Peers = ["localhost:30300", "localhost:30301"],
        };
        config.IsConsensusMode.Should().BeTrue();
    }

    [Fact]
    public void InitProperties_AreSet()
    {
        var config = new NodeConfiguration
        {
            ValidatorIndex = 2,
            ValidatorAddress = "0xabc",
            ValidatorKeyHex = "deadbeef",
            NetworkName = "basalt-mainnet",
            ChainId = 1,
            HttpPort = 8080,
            P2PPort = 9090,
            DataDir = "/data",
            Peers = ["peer1:30303", "peer2:30303"],
            UsePipelining = true,
            UseSandbox = true,
        };
        config.ValidatorIndex.Should().Be(2);
        config.ValidatorAddress.Should().Be("0xabc");
        config.ValidatorKeyHex.Should().Be("deadbeef");
        config.NetworkName.Should().Be("basalt-mainnet");
        config.ChainId.Should().Be(1u);
        config.HttpPort.Should().Be(8080);
        config.P2PPort.Should().Be(9090);
        config.DataDir.Should().Be("/data");
        config.Peers.Should().HaveCount(2);
        config.UsePipelining.Should().BeTrue();
        config.UseSandbox.Should().BeTrue();
    }
}
