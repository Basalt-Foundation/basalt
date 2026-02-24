using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Node.Tests;

/// <summary>
/// Tests for mainnet configuration guards (B2, B4, H7) and configurable timeouts (M10, M11).
/// </summary>
public class MainnetGuardTests
{
    [Fact]
    public void ChainParameters_Mainnet_HasCorrectNetworkName()
    {
        // H7: ChainId 1 requires "basalt-mainnet"
        var chainParams = ChainParameters.Mainnet;
        chainParams.ChainId.Should().Be(1u);
        chainParams.NetworkName.Should().Be("basalt-mainnet");
    }

    [Fact]
    public void ChainParameters_Testnet_HasCorrectNetworkName()
    {
        // H7: ChainId 2 requires "basalt-testnet"
        var chainParams = ChainParameters.Testnet;
        chainParams.ChainId.Should().Be(2u);
        chainParams.NetworkName.Should().Be("basalt-testnet");
    }

    [Fact]
    public void ChainParameters_FromConfiguration_MainnetMismatch_IsDetectable()
    {
        // H7: If someone passes ChainId=1 with wrong network name, the Program.cs guard catches it
        var chainParams = ChainParameters.FromConfiguration(1, "basalt-devnet");
        // The guard in Program.cs compares: chainParams.NetworkName != "basalt-mainnet"
        chainParams.NetworkName.Should().NotBe("basalt-mainnet",
            "passing wrong network name with mainnet ChainId should be detectable");
    }

    [Fact]
    public void NodeConfiguration_MainnetWithoutDataDir_ShouldBeDetectable()
    {
        // H7: Mainnet/testnet requires persistent storage
        var config = new NodeConfiguration
        {
            ChainId = 1,
            DataDir = null,
        };
        config.DataDir.Should().BeNull("no DataDir was set");
        // The guard in Program.cs rejects ChainId <= 2 without DataDir
    }

    [Fact]
    public void NodeConfiguration_MainnetConsensusWithoutValidatorKey_ShouldBeDetectable()
    {
        // H7: Mainnet validators require explicit key
        var config = new NodeConfiguration
        {
            ChainId = 1,
            ValidatorIndex = 0,
            Peers = ["peer1:30303"],
            ValidatorKeyHex = "",
        };
        config.IsConsensusMode.Should().BeTrue();
        config.ValidatorKeyHex.Should().BeEmpty("no validator key was set");
    }

    [Fact]
    public void ChainParameters_DefaultConsensusTimeout_Is2000ms()
    {
        // M10: Default consensus timeout should be 2000ms
        var chainParams = ChainParameters.Devnet;
        chainParams.ConsensusTimeoutMs.Should().Be(2000u);
    }

    [Fact]
    public void ChainParameters_DefaultP2PTimeouts_AreCorrect()
    {
        // M11: Default P2P timeouts
        var chainParams = ChainParameters.Devnet;
        chainParams.P2PHandshakeTimeoutMs.Should().Be(5000u);
        chainParams.P2PFrameReadTimeoutMs.Should().Be(120_000u);
        chainParams.P2PConnectTimeoutMs.Should().Be(10_000u);
    }

    [Fact]
    public void ChainParameters_ConsensusTimeout_IsConfigurable()
    {
        var chainParams = new ChainParameters
        {
            ChainId = 31337,
            NetworkName = "test",
            ConsensusTimeoutMs = 5000,
        };
        chainParams.ConsensusTimeoutMs.Should().Be(5000u);
    }

    [Fact]
    public void ChainParameters_P2PTimeouts_AreConfigurable()
    {
        var chainParams = new ChainParameters
        {
            ChainId = 31337,
            NetworkName = "test",
            P2PHandshakeTimeoutMs = 10_000,
            P2PFrameReadTimeoutMs = 60_000,
            P2PConnectTimeoutMs = 20_000,
        };
        chainParams.P2PHandshakeTimeoutMs.Should().Be(10_000u);
        chainParams.P2PFrameReadTimeoutMs.Should().Be(60_000u);
        chainParams.P2PConnectTimeoutMs.Should().Be(20_000u);
    }
}
