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

    [Theory]
    [InlineData("/etc/shadow")]
    [InlineData("/usr/local/data")]
    [InlineData("/bin/basalt")]
    [InlineData("/sbin/data")]
    [InlineData("/var/run/basalt")]
    [InlineData("/proc/self")]
    [InlineData("/sys/kernel")]
    [InlineData("/boot/grub")]
    [InlineData("/dev/null")]
    [InlineData("/lib/basalt")]
    public void ValidateDataDir_RejectsSystemPaths(string path)
    {
        var act = () => NodeConfiguration.ValidateDataDir(path);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*system directory*");
    }

    [Theory]
    [InlineData("/tmp/basalt")]
    [InlineData("/data/basalt")]
    [InlineData("/home/user/basalt")]
    public void ValidateDataDir_AllowsSafePaths(string path)
    {
        var result = NodeConfiguration.ValidateDataDir(path);
        result.Should().NotBeEmpty();
    }

    [Fact]
    public void ValidateDataDir_ResolvesRelativePaths()
    {
        // Relative paths should be resolved to absolute paths
        var result = NodeConfiguration.ValidateDataDir("./data/basalt");
        result.Should().StartWith("/");
        result.Should().EndWith("data/basalt");
    }

    // ── ResolvedMode tests ──

    [Fact]
    public void ResolvedMode_Default_ReturnsStandalone()
    {
        var config = new NodeConfiguration();
        config.ResolvedMode.Should().Be(NodeMode.Standalone);
    }

    [Fact]
    public void ResolvedMode_ExplicitStandalone_ReturnsStandalone()
    {
        var config = new NodeConfiguration { Mode = "standalone" };
        config.ResolvedMode.Should().Be(NodeMode.Standalone);
    }

    [Fact]
    public void ResolvedMode_ExplicitValidator_ReturnsValidator()
    {
        var config = new NodeConfiguration { Mode = "validator" };
        config.ResolvedMode.Should().Be(NodeMode.Validator);
    }

    [Fact]
    public void ResolvedMode_AutoWithPeersAndIndex_ReturnsValidator()
    {
        var config = new NodeConfiguration
        {
            Mode = "auto",
            ValidatorIndex = 0,
            Peers = ["peer1:30303"],
        };
        config.ResolvedMode.Should().Be(NodeMode.Validator);
    }

    [Fact]
    public void ResolvedMode_Rpc_WithSyncSource_ReturnsRpc()
    {
        var config = new NodeConfiguration
        {
            Mode = "rpc",
            SyncSource = "http://validator-0:5000",
        };
        config.ResolvedMode.Should().Be(NodeMode.Rpc);
    }

    [Fact]
    public void ResolvedMode_Rpc_WithoutSyncSource_Throws()
    {
        var config = new NodeConfiguration { Mode = "rpc" };
        var act = () => config.ResolvedMode;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BASALT_SYNC_SOURCE*");
    }

    [Fact]
    public void ResolvedMode_CaseInsensitive()
    {
        var config = new NodeConfiguration
        {
            Mode = "RPC",
            SyncSource = "http://validator-0:5000",
        };
        config.ResolvedMode.Should().Be(NodeMode.Rpc);
    }

    [Fact]
    public void IsConsensusMode_RpcMode_ReturnsFalse()
    {
        var config = new NodeConfiguration
        {
            Mode = "rpc",
            SyncSource = "http://validator-0:5000",
        };
        config.IsConsensusMode.Should().BeFalse();
    }
}
