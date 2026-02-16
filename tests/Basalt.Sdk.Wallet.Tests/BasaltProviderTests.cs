namespace Basalt.Sdk.Wallet.Tests;

using Xunit;
using FluentAssertions;
using Moq;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.Rpc;
using Basalt.Sdk.Wallet.Rpc.Models;

public class BasaltProviderTests
{
    private readonly Mock<IBasaltClient> _mockClient;
    private readonly BasaltProvider _provider;

    public BasaltProviderTests()
    {
        _mockClient = new Mock<IBasaltClient>();
        _provider = new BasaltProvider(_mockClient.Object, chainId: 1);
    }

    [Fact]
    public async Task GetBalanceAsync_ReturnsBalance()
    {
        var account = Account.Create();
        var hex = "0x" + Convert.ToHexString(account.Address.ToArray()).ToLowerInvariant();

        _mockClient.Setup(c => c.GetAccountAsync(hex, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Address = hex, Balance = "500", Nonce = 0, AccountType = "EOA" });

        var balance = await _provider.GetBalanceAsync(account.Address);

        balance.Should().Be("500");
        account.Dispose();
    }

    [Fact]
    public async Task GetBalanceAsync_ReturnsZero_WhenAccountNotFound()
    {
        var account = Account.Create();
        var hex = "0x" + Convert.ToHexString(account.Address.ToArray()).ToLowerInvariant();

        _mockClient.Setup(c => c.GetAccountAsync(hex, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountInfo?)null);

        var balance = await _provider.GetBalanceAsync(account.Address);

        balance.Should().Be("0");
        account.Dispose();
    }

    [Fact]
    public async Task GetNonceAsync_ReturnsNonce()
    {
        var account = Account.Create();
        var hex = "0x" + Convert.ToHexString(account.Address.ToArray()).ToLowerInvariant();

        _mockClient.Setup(c => c.GetAccountAsync(hex, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Address = hex, Balance = "0", Nonce = 10, AccountType = "EOA" });

        var nonce = await _provider.GetNonceAsync(account.Address);

        nonce.Should().Be(10UL);
        account.Dispose();
    }

    [Fact]
    public async Task GetNonceAsync_ReturnsZero_WhenAccountNotFound()
    {
        var account = Account.Create();
        var hex = "0x" + Convert.ToHexString(account.Address.ToArray()).ToLowerInvariant();

        _mockClient.Setup(c => c.GetAccountAsync(hex, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AccountInfo?)null);

        var nonce = await _provider.GetNonceAsync(account.Address);

        nonce.Should().Be(0UL);
        account.Dispose();
    }

    [Fact]
    public async Task GetStatusAsync_DelegatesToClient()
    {
        var expected = new NodeStatus
        {
            BlockHeight = 42,
            LatestBlockHash = "0xabc",
            MempoolSize = 5,
            ProtocolVersion = 1
        };

        _mockClient.Setup(c => c.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _provider.GetStatusAsync();

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GetLatestBlockAsync_DelegatesToClient()
    {
        var expected = new BlockInfo
        {
            Number = 100,
            Hash = "0xblockhash",
            ParentHash = "0xparent",
            StateRoot = "0xstateroot",
            Timestamp = 1700000000,
            Proposer = "0xproposer",
            GasUsed = 21000,
            GasLimit = 1000000,
            TransactionCount = 3
        };

        _mockClient.Setup(c => c.GetLatestBlockAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _provider.GetLatestBlockAsync();

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task TransferAsync_SignsAndSubmits()
    {
        using var account = Account.Create();
        var recipientBytes = new byte[Address.Size];
        recipientBytes[0] = 0xAB;
        var recipient = new Address(recipientBytes);
        UInt256 value = 1000UL;
        Transaction? capturedTx = null;

        _mockClient.Setup(c => c.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Address = "0x00", Balance = "5000", Nonce = 0, AccountType = "EOA" });

        _mockClient.Setup(c => c.SendTransactionAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .Callback<Transaction, CancellationToken>((tx, _) => capturedTx = tx)
            .ReturnsAsync(new TransactionSubmitResult { Hash = "0xabc123", Status = "pending" });

        var result = await _provider.TransferAsync(account, recipient, value);

        result.Hash.Should().Be("0xabc123");
        result.Status.Should().Be("pending");
        capturedTx.Should().NotBeNull();
        capturedTx!.Type.Should().Be(TransactionType.Transfer);
        capturedTx.To.Should().Be(recipient);
        capturedTx.Value.Should().Be(value);
        capturedTx.Signature.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task SendTransactionAsync_AutoFillsNonce()
    {
        using var account = Account.Create();
        var hex = "0x" + Convert.ToHexString(account.Address.ToArray()).ToLowerInvariant();
        Transaction? capturedTx = null;

        _mockClient.Setup(c => c.GetAccountAsync(hex, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Address = hex, Balance = "1000", Nonce = 3, AccountType = "EOA" });

        _mockClient.Setup(c => c.SendTransactionAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .Callback<Transaction, CancellationToken>((tx, _) => capturedTx = tx)
            .ReturnsAsync(new TransactionSubmitResult { Hash = "0xdef456", Status = "pending" });

        var unsignedTx = new Transaction
        {
            Type = TransactionType.Transfer,
            To = Address.Zero,
            Value = 100UL,
            GasLimit = 21_000,
        };

        await _provider.SendTransactionAsync(account, unsignedTx);

        capturedTx.Should().NotBeNull();
        capturedTx!.Nonce.Should().Be(3UL);
    }

    [Fact]
    public async Task DeployContractAsync_SubmitsDeployTx()
    {
        using var account = Account.Create();
        var bytecode = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        Transaction? capturedTx = null;

        _mockClient.Setup(c => c.GetAccountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AccountInfo { Address = "0x00", Balance = "5000", Nonce = 0, AccountType = "EOA" });

        _mockClient.Setup(c => c.SendTransactionAsync(It.IsAny<Transaction>(), It.IsAny<CancellationToken>()))
            .Callback<Transaction, CancellationToken>((tx, _) => capturedTx = tx)
            .ReturnsAsync(new TransactionSubmitResult { Hash = "0xdeploy", Status = "pending" });

        await _provider.DeployContractAsync(account, bytecode, gasLimit: 500_000);

        capturedTx.Should().NotBeNull();
        capturedTx!.Type.Should().Be(TransactionType.ContractDeploy);
        capturedTx.Data.Should().Contain(bytecode);
    }

    [Fact]
    public void GetContract_ReturnsContractClient()
    {
        var contractBytes = new byte[Address.Size];
        contractBytes[19] = 0x42;
        var contractAddress = new Address(contractBytes);

        var client = _provider.GetContract(contractAddress);

        client.Should().NotBeNull();
        client.Should().BeOfType<ContractClient>();
        client.ContractAddress.Should().Be(contractAddress);
    }

    [Fact]
    public void Dispose_DoesNotDisposeInjectedClient()
    {
        var mockClient = new Mock<IBasaltClient>();
        var provider = new BasaltProvider(mockClient.Object, chainId: 1);

        provider.Dispose();

        mockClient.Verify(c => c.Dispose(), Times.Never);
    }
}
