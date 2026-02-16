using Xunit;
using FluentAssertions;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Sdk.Wallet.Transactions;

namespace Basalt.Sdk.Wallet.Tests;

public sealed class TransactionBuilderTests
{
    [Fact]
    public void Transfer_SetsTypeAndDefaults()
    {
        var tx = TransactionBuilder.Transfer().Build();

        tx.Type.Should().Be(TransactionType.Transfer);
        tx.GasLimit.Should().Be(21_000UL);
        tx.GasPrice.Should().Be(UInt256.One);
    }

    [Fact]
    public void Transfer_WithAllFields()
    {
        var sender = new Address(new byte[20] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });
        var to = new Address(new byte[20] { 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 });
        var value = new UInt256(500);

        var tx = TransactionBuilder.Transfer()
            .WithNonce(42)
            .WithSender(sender)
            .WithTo(to)
            .WithValue(value)
            .WithGasLimit(50_000)
            .WithGasPrice(new UInt256(10))
            .WithPriority(3)
            .WithChainId(99)
            .Build();

        tx.Type.Should().Be(TransactionType.Transfer);
        tx.Nonce.Should().Be(42UL);
        tx.Sender.Should().Be(sender);
        tx.To.Should().Be(to);
        tx.Value.Should().Be(value);
        tx.GasLimit.Should().Be(50_000UL);
        tx.GasPrice.Should().Be(new UInt256(10));
        tx.Priority.Should().Be(3);
        tx.ChainId.Should().Be(99U);
    }

    [Fact]
    public void ContractDeploy_SetsTypeAndData()
    {
        byte[] bytecode = [0xCA, 0xFE, 0xBA, 0xBE];

        var tx = TransactionBuilder.ContractDeploy()
            .WithData(bytecode)
            .Build();

        tx.Type.Should().Be(TransactionType.ContractDeploy);
        tx.Data.Should().BeEquivalentTo(bytecode);
    }

    [Fact]
    public void ContractCall_SetsType()
    {
        var tx = TransactionBuilder.ContractCall().Build();

        tx.Type.Should().Be(TransactionType.ContractCall);
    }

    [Fact]
    public void StakeDeposit_SetsType()
    {
        var tx = TransactionBuilder.StakeDeposit().Build();

        tx.Type.Should().Be(TransactionType.StakeDeposit);
    }

    [Fact]
    public void StakeWithdraw_SetsType()
    {
        var tx = TransactionBuilder.StakeWithdraw().Build();

        tx.Type.Should().Be(TransactionType.StakeWithdraw);
    }

    [Fact]
    public void ValidatorRegister_SetsType()
    {
        var tx = TransactionBuilder.ValidatorRegister().Build();

        tx.Type.Should().Be(TransactionType.ValidatorRegister);
    }

    [Fact]
    public void TransferBuilder_SetsToAndValue()
    {
        var to = new Address(new byte[20] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });
        var value = new UInt256(100);

        var tx = new TransferBuilder(to, value).Build();

        tx.Type.Should().Be(TransactionType.Transfer);
        tx.To.Should().Be(to);
        tx.Value.Should().Be(value);
    }

    [Fact]
    public void ContractDeployBuilder_SetsToZeroAndBytecode()
    {
        byte[] bytecode = [0x01, 0x02, 0x03];

        var tx = new ContractDeployBuilder(bytecode).Build();

        tx.Type.Should().Be(TransactionType.ContractDeploy);
        tx.To.Should().Be(Address.Zero);
        tx.Data.Should().BeEquivalentTo(bytecode);
    }

    [Fact]
    public void ContractCallBuilder_ComputesSelector()
    {
        var contractAddr = new Address(new byte[20] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200 });

        var tx = new ContractCallBuilder(contractAddr, "transfer").Build();

        tx.Type.Should().Be(TransactionType.ContractCall);
        tx.To.Should().Be(contractAddr);
        tx.Data.Should().HaveCountGreaterOrEqualTo(4);

        byte[] expectedSelector = Basalt.Sdk.Wallet.Contracts.AbiEncoder.ComputeSelector("transfer");
        tx.Data[..4].Should().BeEquivalentTo(expectedSelector);
    }

    [Fact]
    public void StakingBuilder_Deposit_SetsValueAndType()
    {
        var amount = new UInt256(100_000);

        var tx = StakingBuilder.Deposit(amount).Build();

        tx.Type.Should().Be(TransactionType.StakeDeposit);
        tx.Value.Should().Be(amount);
    }
}
