using Xunit;
using FluentAssertions;
using Moq;
using Basalt.Sdk.Wallet.Rpc;
using Basalt.Sdk.Wallet.Rpc.Models;

namespace Basalt.Sdk.Wallet.Tests;

public sealed class NonceManagerTests
{
    private readonly NonceManager _nonceManager = new();
    private readonly Mock<IBasaltClient> _clientMock = new();

    [Fact]
    public async Task GetNextNonce_FetchesFromChain_OnFirstCall()
    {
        _clientMock
            .Setup(c => c.GetAccountAsync("0xABC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Nonce = 5 });

        var nonce = await _nonceManager.GetNextNonceAsync("0xABC", _clientMock.Object);

        nonce.Should().Be(5UL);
        _clientMock.Verify(c => c.GetAccountAsync("0xABC", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetNextNonce_ReturnsZero_WhenAccountNotFound()
    {
        _clientMock
            .Setup(c => c.GetAccountAsync("0xDEAD", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountInfo?)null);

        var nonce = await _nonceManager.GetNextNonceAsync("0xDEAD", _clientMock.Object);

        nonce.Should().Be(0UL);
    }

    [Fact]
    public async Task IncrementNonce_IncrementsLocalNonce()
    {
        _clientMock
            .Setup(c => c.GetAccountAsync("0xABC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Nonce = 5 });

        await _nonceManager.GetNextNonceAsync("0xABC", _clientMock.Object);
        _nonceManager.IncrementNonce("0xABC");
        var nonce = await _nonceManager.GetNextNonceAsync("0xABC", _clientMock.Object);

        nonce.Should().Be(7UL);
    }

    [Fact]
    public async Task Reset_ForcesRefetch()
    {
        _clientMock
            .Setup(c => c.GetAccountAsync("0xABC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Nonce = 5 });

        await _nonceManager.GetNextNonceAsync("0xABC", _clientMock.Object);

        _clientMock
            .Setup(c => c.GetAccountAsync("0xABC", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Nonce = 10 });

        _nonceManager.Reset("0xABC");
        var nonce = await _nonceManager.GetNextNonceAsync("0xABC", _clientMock.Object);

        nonce.Should().Be(10UL);
        _clientMock.Verify(c => c.GetAccountAsync("0xABC", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ResetAll_ClearsAllAddresses()
    {
        _clientMock
            .Setup(c => c.GetAccountAsync("0xAAA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Nonce = 1 });
        _clientMock
            .Setup(c => c.GetAccountAsync("0xBBB", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Nonce = 2 });

        await _nonceManager.GetNextNonceAsync("0xAAA", _clientMock.Object);
        await _nonceManager.GetNextNonceAsync("0xBBB", _clientMock.Object);

        _clientMock
            .Setup(c => c.GetAccountAsync("0xAAA", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Nonce = 100 });
        _clientMock
            .Setup(c => c.GetAccountAsync("0xBBB", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Nonce = 200 });

        _nonceManager.ResetAll();

        var nonceA = await _nonceManager.GetNextNonceAsync("0xAAA", _clientMock.Object);
        var nonceB = await _nonceManager.GetNextNonceAsync("0xBBB", _clientMock.Object);

        nonceA.Should().Be(100UL);
        nonceB.Should().Be(200UL);
    }
}
