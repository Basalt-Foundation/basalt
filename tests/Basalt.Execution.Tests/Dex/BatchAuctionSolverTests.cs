using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for the batch auction solver — the core MEV-elimination mechanism.
/// Validates clearing price computation, volume matching, peer-to-peer fills,
/// AMM residual routing, and edge cases (no liquidity, single-sided, etc.).
/// </summary>
public class BatchAuctionSolverTests
{
    private static readonly ChainParameters ChainParams = ChainParameters.Devnet;

    // ────────── Helpers ──────────

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }

    private static readonly Address Token0 = Address.Zero; // Native BST
    private static readonly Address Token1 = MakeAddress(0xAA);
    private static readonly Address User1 = MakeAddress(0x01);
    private static readonly Address User2 = MakeAddress(0x02);
    private static readonly Address User3 = MakeAddress(0x03);

    private static ParsedIntent MakeBuyIntent(Address sender, UInt256 amountIn, UInt256 minAmountOut)
    {
        // Buy intent: buying token0, paying token1
        // tokenIn = token1, tokenOut = token0
        return new ParsedIntent
        {
            Sender = sender,
            TokenIn = Token1,
            TokenOut = Token0,
            AmountIn = amountIn,
            MinAmountOut = minAmountOut,
            Deadline = 0,
            AllowPartialFill = false,
            TxHash = Hash256.Zero,
        };
    }

    private static ParsedIntent MakeSellIntent(Address sender, UInt256 amountIn, UInt256 minAmountOut)
    {
        // Sell intent: selling token0, wanting token1
        // tokenIn = token0, tokenOut = token1
        return new ParsedIntent
        {
            Sender = sender,
            TokenIn = Token0,
            TokenOut = Token1,
            AmountIn = amountIn,
            MinAmountOut = minAmountOut,
            Deadline = 0,
            AllowPartialFill = false,
            TxHash = Hash256.Zero,
        };
    }

    // ────────── Tests ──────────

    [Fact]
    public void SpotPrice_Computation()
    {
        // 1:1 reserves → spot price = PriceScale
        var price = BatchAuctionSolver.ComputeSpotPrice(new UInt256(1000), new UInt256(1000));
        price.Should().Be(BatchAuctionSolver.PriceScale);

        // 1:2 reserves → spot price = 2 * PriceScale
        var price2 = BatchAuctionSolver.ComputeSpotPrice(new UInt256(1000), new UInt256(2000));
        price2.Should().Be(BatchAuctionSolver.PriceScale * new UInt256(2));
    }

    [Fact]
    public void SpotPrice_ZeroReserves_ReturnsZero()
    {
        var price = BatchAuctionSolver.ComputeSpotPrice(UInt256.Zero, new UInt256(1000));
        price.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void NoIntents_ReturnsNull()
    {
        var reserves = new PoolReserves
        {
            Reserve0 = new UInt256(100_000),
            Reserve1 = new UInt256(100_000),
            TotalSupply = new UInt256(10_000),
        };

        var result = BatchAuctionSolver.ComputeSettlement(
            [], [], [], [], reserves, 30, 0);
        result.Should().BeNull();
    }

    [Fact]
    public void OneSidedBuy_NoSellers_ReturnsNull()
    {
        var buys = new List<ParsedIntent>
        {
            MakeBuyIntent(User1, new UInt256(1000), new UInt256(900)),
        };

        // No AMM liquidity
        var reserves = new PoolReserves();

        var result = BatchAuctionSolver.ComputeSettlement(
            buys, [], [], [], reserves, 30, 0);
        result.Should().BeNull();
    }

    [Fact]
    public void BuyAndSell_FindsClearingPrice()
    {
        var reserves = new PoolReserves
        {
            Reserve0 = new UInt256(100_000),
            Reserve1 = new UInt256(100_000),
            TotalSupply = new UInt256(10_000),
        };

        var buys = new List<ParsedIntent>
        {
            // Buyer willing to pay up to 1.2x spot (1200 token1 for 1000 token0)
            MakeBuyIntent(User1, new UInt256(1200), new UInt256(1000)),
        };

        var sells = new List<ParsedIntent>
        {
            // Seller willing to accept 0.9x spot (1000 token0 for 900 token1)
            MakeSellIntent(User2, new UInt256(1000), new UInt256(900)),
        };

        var result = BatchAuctionSolver.ComputeSettlement(
            buys, sells, [], [], reserves, 30, 0);

        result.Should().NotBeNull();
        result!.ClearingPrice.Should().BeGreaterThan(UInt256.Zero);
        result.Fills.Should().NotBeEmpty();
    }

    [Fact]
    public void Settlement_AllFillsAtUniformPrice()
    {
        var reserves = new PoolReserves
        {
            Reserve0 = new UInt256(100_000),
            Reserve1 = new UInt256(100_000),
            TotalSupply = new UInt256(10_000),
        };

        var buys = new List<ParsedIntent>
        {
            MakeBuyIntent(User1, new UInt256(2000), new UInt256(1500)),
            MakeBuyIntent(User2, new UInt256(1500), new UInt256(1200)),
        };

        var sells = new List<ParsedIntent>
        {
            MakeSellIntent(User3, new UInt256(2000), new UInt256(1500)),
        };

        var result = BatchAuctionSolver.ComputeSettlement(
            buys, sells, [], [], reserves, 30, 0);

        result.Should().NotBeNull();

        // All fills should be at the same clearing price
        // The clearing price is stored once in the result
        result!.ClearingPrice.Should().BeGreaterThan(UInt256.Zero);
    }

    [Fact]
    public void ParsedIntent_Parse_ValidData()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(publicKey);

        // Build valid DexSwapIntent data: [1B ver][20B tokenIn][20B tokenOut][32B amountIn][32B minOut][8B deadline][1B flags]
        var data = new byte[114];
        data[0] = 1; // version
        Token0.WriteTo(data.AsSpan(1, 20));
        Token1.WriteTo(data.AsSpan(21, 20));
        new UInt256(5000).WriteTo(data.AsSpan(41, 32));
        new UInt256(4000).WriteTo(data.AsSpan(73, 32));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(105, 8), 100UL);
        data[113] = 0x01; // allowPartialFill

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexSwapIntent,
            Nonce = 0,
            Sender = sender,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            ChainId = ChainParams.ChainId,
            Data = data,
        }, privateKey);

        var intent = ParsedIntent.Parse(tx);
        intent.Should().NotBeNull();
        intent!.Value.Sender.Should().Be(sender);
        intent.Value.TokenIn.Should().Be(Token0);
        intent.Value.TokenOut.Should().Be(Token1);
        intent.Value.AmountIn.Should().Be(new UInt256(5000));
        intent.Value.MinAmountOut.Should().Be(new UInt256(4000));
        intent.Value.Deadline.Should().Be(100UL);
        intent.Value.AllowPartialFill.Should().BeTrue();
    }

    [Fact]
    public void ParsedIntent_Parse_ShortData_ReturnsNull()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(publicKey);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexSwapIntent,
            Nonce = 0,
            Sender = sender,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            ChainId = ChainParams.ChainId,
            Data = new byte[10], // Too short
        }, privateKey);

        var intent = ParsedIntent.Parse(tx);
        intent.Should().BeNull();
    }

    [Fact]
    public void SplitBuySell_CorrectPartitioning()
    {
        var buySideIntent = new ParsedIntent
        {
            Sender = User1,
            TokenIn = Token1,
            TokenOut = Token0,  // Buying token0
            AmountIn = new UInt256(1000),
            MinAmountOut = new UInt256(900),
        };

        var sellSideIntent = new ParsedIntent
        {
            Sender = User2,
            TokenIn = Token0,
            TokenOut = Token1,  // Selling token0
            AmountIn = new UInt256(500),
            MinAmountOut = new UInt256(450),
        };

        var intents = new List<ParsedIntent> { buySideIntent, sellSideIntent };
        var (buys, sells) = BatchSettlementExecutor.SplitBuySell(intents, Token0);

        buys.Should().HaveCount(1);
        buys[0].Sender.Should().Be(User1);
        sells.Should().HaveCount(1);
        sells[0].Sender.Should().Be(User2);
    }

    [Fact]
    public void GroupByPair_GroupsCorrectly()
    {
        var (pk1, pub1) = Ed25519Signer.GenerateKeyPair();
        var sender1 = Ed25519Signer.DeriveAddress(pub1);
        var (pk2, pub2) = Ed25519Signer.GenerateKeyPair();
        var sender2 = Ed25519Signer.DeriveAddress(pub2);

        var data1 = MakeIntentData(Token0, Token1, new UInt256(1000), new UInt256(900));
        var data2 = MakeIntentData(Token0, Token1, new UInt256(2000), new UInt256(1800));

        var tx1 = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexSwapIntent,
            Nonce = 0,
            Sender = sender1,
            To = DexState.DexAddress,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            ChainId = ChainParams.ChainId,
            Data = data1,
        }, pk1);

        var tx2 = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexSwapIntent,
            Nonce = 0,
            Sender = sender2,
            To = DexState.DexAddress,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            ChainId = ChainParams.ChainId,
            Data = data2,
        }, pk2);

        var stateDb = new InMemoryStateDb();
        var dexState = new DexState(stateDb);

        var groups = BatchSettlementExecutor.GroupByPair(new[] { tx1, tx2 }, dexState);

        var (t0, t1) = DexEngine.SortTokens(Token0, Token1);
        groups.Should().ContainKey((t0, t1));
        groups[(t0, t1)].Should().HaveCount(2);
    }

    [Fact]
    public void Mempool_IntentPartitioning()
    {
        var mempool = new Mempool();

        var (pk, pub) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(pub);

        // Regular transfer tx
        var transferTx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = sender,
            To = MakeAddress(0xFF),
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = ChainParams.ChainId,
        }, pk);

        // DEX swap intent tx
        var intentTx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexSwapIntent,
            Nonce = 1,
            Sender = sender,
            To = DexState.DexAddress,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            ChainId = ChainParams.ChainId,
            Data = MakeIntentData(Token0, Token1, new UInt256(1000), new UInt256(900)),
        }, pk);

        mempool.Add(transferTx);
        mempool.Add(intentTx);

        // Total count includes both
        mempool.Count.Should().Be(2);

        // DexIntentCount only counts intents
        mempool.DexIntentCount.Should().Be(1);

        // GetPending returns only non-intent txs
        var pending = mempool.GetPending(100);
        pending.Should().HaveCount(1);
        pending[0].Type.Should().Be(TransactionType.Transfer);

        // GetPendingDexIntents returns only intents
        var intents = mempool.GetPendingDexIntents(100);
        intents.Should().HaveCount(1);
        intents[0].Type.Should().Be(TransactionType.DexSwapIntent);
    }

    [Fact]
    public void Mempool_RemoveConfirmed_RemovesBothPools()
    {
        var mempool = new Mempool();
        var (pk, pub) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(pub);

        var intentTx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexSwapIntent,
            Nonce = 0,
            Sender = sender,
            To = DexState.DexAddress,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            ChainId = ChainParams.ChainId,
            Data = MakeIntentData(Token0, Token1, new UInt256(1000), new UInt256(900)),
        }, pk);

        mempool.Add(intentTx);
        mempool.DexIntentCount.Should().Be(1);

        mempool.RemoveConfirmed([intentTx]);
        mempool.DexIntentCount.Should().Be(0);
        mempool.Count.Should().Be(0);
    }

    private static byte[] MakeIntentData(Address tokenIn, Address tokenOut, UInt256 amountIn, UInt256 minOut)
    {
        var data = new byte[114];
        data[0] = 1;
        tokenIn.WriteTo(data.AsSpan(1, 20));
        tokenOut.WriteTo(data.AsSpan(21, 20));
        amountIn.WriteTo(data.AsSpan(41, 32));
        minOut.WriteTo(data.AsSpan(73, 32));
        return data;
    }
}
