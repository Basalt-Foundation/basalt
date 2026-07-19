using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Core.Tests;

/// <summary>
/// The public-network classification that arms the stricter startup guards. The incentivized testnet (4242)
/// must count as public even though its chain id is above 2, or it would silently run with devnet-relaxed
/// guards (no required faucet key, debug CORS allowed).
/// </summary>
public class ChainParametersPublicNetworkTests
{
    private static ChainParameters WithChainId(uint chainId) => new()
    {
        ChainId = chainId,
        NetworkName = "test",
        DexAdminAddress = new Address(new byte[20]),
    };

    [Theory]
    [InlineData(1u)]     // mainnet
    [InlineData(2u)]     // built-in testnet
    [InlineData(4242u)]  // public incentivized testnet
    public void Public_networks_are_classified_public(uint chainId)
        => WithChainId(chainId).IsPublicNetwork.Should().BeTrue();

    [Theory]
    [InlineData(3u)]
    [InlineData(31337u)] // devnet
    [InlineData(1337u)]
    public void Private_networks_are_not_public(uint chainId)
        => WithChainId(chainId).IsPublicNetwork.Should().BeFalse();

    [Fact]
    public void Incentivized_testnet_id_is_4242()
        => ChainParameters.IncentivizedTestnetChainId.Should().Be(4242u);
}
