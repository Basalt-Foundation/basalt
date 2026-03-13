using System.Buffers.Binary;
using Xunit;
using FluentAssertions;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Execution.Dex;
using Basalt.Sdk.Wallet.Transactions;

namespace Basalt.Sdk.Wallet.Tests;

public sealed class DexSwapIntentBuilderTests
{
    private static readonly Address TokenA = new(new byte[20] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });
    private static readonly Address TokenB = new(new byte[20] { 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 });

    [Fact]
    public void Plaintext_Build_SetsTypeAndDefaults()
    {
        var tx = DexSwapIntentBuilder.Create()
            .WithTokenIn(TokenA)
            .WithTokenOut(TokenB)
            .WithAmountIn(new UInt256(1000))
            .WithMinAmountOut(new UInt256(900))
            .Build();

        tx.Type.Should().Be(TransactionType.DexSwapIntent);
        tx.To.Should().Be(DexState.DexAddress);
        tx.GasLimit.Should().Be(80_000UL);
        tx.Data.Should().HaveCount(114);
    }

    [Fact]
    public void Plaintext_Roundtrip_MatchesParsedIntent()
    {
        var amountIn = new UInt256(5_000_000);
        var minAmountOut = new UInt256(4_500_000);
        ulong deadline = 12345;

        var tx = DexSwapIntentBuilder.Create()
            .WithTokenIn(TokenA)
            .WithTokenOut(TokenB)
            .WithAmountIn(amountIn)
            .WithMinAmountOut(minAmountOut)
            .WithDeadline(deadline)
            .WithAllowPartialFill(true)
            .WithSender(TokenA) // use TokenA as sender for roundtrip
            .Build();

        var parsed = ParsedIntent.Parse(tx);

        parsed.Should().NotBeNull();
        parsed!.Value.TokenIn.Should().Be(TokenA);
        parsed.Value.TokenOut.Should().Be(TokenB);
        parsed.Value.AmountIn.Should().Be(amountIn);
        parsed.Value.MinAmountOut.Should().Be(minAmountOut);
        parsed.Value.Deadline.Should().Be(deadline);
        parsed.Value.AllowPartialFill.Should().BeTrue();
    }

    [Fact]
    public void Encrypted_Build_ProducesLargerPayload()
    {
        // Use the real BLS12-381 G1 generator — a known-valid compressed point
        var gpk = new BlsPublicKey(BlsCrypto.G1Generator());

        var tx = DexSwapIntentBuilder.CreateEncrypted(gpk, epoch: 42)
            .WithTokenIn(TokenA)
            .WithTokenOut(TokenB)
            .WithAmountIn(new UInt256(1000))
            .WithMinAmountOut(new UInt256(900))
            .Build();

        tx.Type.Should().Be(TransactionType.DexEncryptedSwapIntent);
        tx.Data.Length.Should().BeGreaterThan(114);
        // Encrypted format: 8 (epoch) + 48 (C1) + 12 (nonce) + 114 (ciphertext) + 16 (tag) = 198
        tx.Data.Length.Should().BeGreaterOrEqualTo(EncryptedIntent.MinDataLength);
    }

    [Fact]
    public void AllowPartialFill_SetsFlag()
    {
        var txWithPartial = DexSwapIntentBuilder.Create()
            .WithTokenIn(TokenA)
            .WithTokenOut(TokenB)
            .WithAmountIn(new UInt256(1000))
            .WithMinAmountOut(new UInt256(900))
            .WithAllowPartialFill(true)
            .Build();

        var txWithoutPartial = DexSwapIntentBuilder.Create()
            .WithTokenIn(TokenA)
            .WithTokenOut(TokenB)
            .WithAmountIn(new UInt256(1000))
            .WithMinAmountOut(new UInt256(900))
            .WithAllowPartialFill(false)
            .Build();

        (txWithPartial.Data[113] & 0x01).Should().Be(1);
        (txWithoutPartial.Data[113] & 0x01).Should().Be(0);
    }

    [Fact]
    public void Deadline_EncodedAsBigEndian()
    {
        ulong deadline = 0x0102030405060708;

        var tx = DexSwapIntentBuilder.Create()
            .WithTokenIn(TokenA)
            .WithTokenOut(TokenB)
            .WithAmountIn(new UInt256(1000))
            .WithMinAmountOut(new UInt256(900))
            .WithDeadline(deadline)
            .Build();

        var encoded = BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(105, 8));
        encoded.Should().Be(deadline);
    }

    [Fact]
    public void Version_Byte_IsOne()
    {
        var tx = DexSwapIntentBuilder.Create()
            .WithTokenIn(TokenA)
            .WithTokenOut(TokenB)
            .WithAmountIn(new UInt256(1000))
            .WithMinAmountOut(new UInt256(900))
            .Build();

        tx.Data[0].Should().Be(1);
    }

    [Fact]
    public void FluentMethods_ChainCorrectly()
    {
        var tx = DexSwapIntentBuilder.Create()
            .WithTokenIn(TokenA)
            .WithTokenOut(TokenB)
            .WithAmountIn(new UInt256(1000))
            .WithMinAmountOut(new UInt256(900))
            .WithGasLimit(100_000)
            .WithGasPrice(new UInt256(5))
            .WithMaxFeePerGas(new UInt256(20))
            .WithMaxPriorityFeePerGas(new UInt256(2))
            .WithChainId(42)
            .WithNonce(7)
            .Build();

        tx.GasLimit.Should().Be(100_000UL);
        tx.GasPrice.Should().Be(new UInt256(5));
        tx.MaxFeePerGas.Should().Be(new UInt256(20));
        tx.MaxPriorityFeePerGas.Should().Be(new UInt256(2));
        tx.ChainId.Should().Be(42U);
        tx.Nonce.Should().Be(7UL);
    }
}
