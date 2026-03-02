using Basalt.Core;
using Basalt.Execution;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

public class MainnetHardeningTests
{
    [Fact]
    public void Mainnet_DexAdminAddress_IsSet()
    {
        ChainParameters.Mainnet.DexAdminAddress.Should().NotBeNull();
    }

    [Fact]
    public void Mainnet_Validate_DoesNotThrow()
    {
        var act = () => ChainParameters.Mainnet.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Testnet_DexAdminAddress_IsSet()
    {
        ChainParameters.Testnet.DexAdminAddress.Should().NotBeNull();
    }

    [Fact]
    public void Testnet_Validate_DoesNotThrow()
    {
        var act = () => ChainParameters.Testnet.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void ChainId1_Without_DexAdmin_Validate_Throws()
    {
        var badParams = new ChainParameters
        {
            ChainId = 1,
            NetworkName = "bad-mainnet",
            DexAdminAddress = null,
        };

        var act = () => badParams.Validate();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*DexAdminAddress*");
    }

    [Fact]
    public void Mainnet_NullifierWindowBlocks_IsSet()
    {
        ChainParameters.Mainnet.NullifierWindowBlocks.Should().Be(256u);
    }

    [Fact]
    public void Mempool_RejectsTransactionBelowBaseFee()
    {
        var mempool = new Mempool(100);
        mempool.UpdateBaseFee(new UInt256(1000));

        var tx = CreateTransaction(gasPrice: new UInt256(500)); // Below base fee
        mempool.Add(tx).Should().BeFalse();
    }

    [Fact]
    public void Mempool_AcceptsTransactionAboveBaseFee()
    {
        var mempool = new Mempool(100);
        mempool.UpdateBaseFee(new UInt256(1000));

        var tx = CreateTransaction(gasPrice: new UInt256(2000)); // Above base fee
        mempool.Add(tx).Should().BeTrue();
    }

    [Fact]
    public void Mempool_RejectsOversizedTransactionData()
    {
        var mempool = new Mempool(100, null!, null!, 1024); // Max 1KB data

        var tx = CreateTransaction(dataSize: 2048); // 2KB > 1KB limit
        mempool.Add(tx).Should().BeFalse();
    }

    [Fact]
    public void Mempool_AcceptsTransactionWithinDataLimit()
    {
        var mempool = new Mempool(100, null!, null!, 1024); // Max 1KB data

        var tx = CreateTransaction(dataSize: 512); // 512B < 1KB limit
        mempool.Add(tx).Should().BeTrue();
    }

    [Fact]
    public void FromConfiguration_Devnet_HasNullifierWindow()
    {
        var devnet = ChainParameters.FromConfiguration(31337, "test-devnet");
        devnet.NullifierWindowBlocks.Should().Be(16u);
    }

    [Fact]
    public void FromConfiguration_Mainnet_HasDexAdmin()
    {
        var mainnet = ChainParameters.FromConfiguration(1, "basalt-mainnet");
        mainnet.DexAdminAddress.Should().NotBeNull();
    }

    [Fact]
    public void FromConfiguration_Testnet_HasDexAdmin()
    {
        var testnet = ChainParameters.FromConfiguration(2, "basalt-testnet");
        testnet.DexAdminAddress.Should().NotBeNull();
    }

    private static Transaction CreateTransaction(
        UInt256? gasPrice = null,
        int dataSize = 0)
    {
        var senderKey = new byte[32];
        senderKey[0] = 0xAA;
        senderKey[31] = 1;
        var senderPk = Basalt.Crypto.Ed25519Signer.GetPublicKey(senderKey);
        var senderAddr = Basalt.Crypto.Ed25519Signer.DeriveAddress(senderPk);

        var data = dataSize > 0 ? new byte[dataSize] : [];
        var effectiveGasPrice = gasPrice ?? new UInt256(1);

        var unsigned = new Transaction
        {
            Type = TransactionType.Transfer,
            Sender = senderAddr,
            To = senderAddr,
            Value = UInt256.Zero,
            Nonce = 0,
            GasLimit = 21000,
            GasPrice = effectiveGasPrice,
            Data = data,
            ChainId = 31337,
        };

        return Transaction.Sign(unsigned, senderKey);
    }
}
