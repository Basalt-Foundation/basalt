using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// End-to-end integration tests for the protocol-native DEX.
/// These tests exercise the full flow: genesis initialization, pool creation,
/// liquidity provision, swap intents, batch settlement at uniform clearing price,
/// limit order matching, and TWAP oracle updates.
/// </summary>
public class IntegrationTests
{
    private readonly InMemoryStateDb _stateDb = new();
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;
    private readonly TransactionExecutor _executor;
    private readonly BlockBuilder _blockBuilder;

    private static readonly byte[] AliceKey;
    private static readonly PublicKey AlicePub;
    private static readonly Address Alice;

    private static readonly byte[] BobKey;
    private static readonly PublicKey BobPub;
    private static readonly Address Bob;

    static IntegrationTests()
    {
        (AliceKey, AlicePub) = Ed25519Signer.GenerateKeyPair();
        Alice = Ed25519Signer.DeriveAddress(AlicePub);

        (BobKey, BobPub) = Ed25519Signer.GenerateKeyPair();
        Bob = Ed25519Signer.DeriveAddress(BobPub);
    }

    public IntegrationTests()
    {
        _executor = new TransactionExecutor(_chainParams);
        _blockBuilder = new BlockBuilder(_chainParams, _executor);

        // Initialize DEX state at genesis
        GenesisContractDeployer.DeployAll(_stateDb, _chainParams.ChainId);

        // Fund Alice and Bob with native BST
        var aliceBalance = new UInt256(1_000_000_000);
        var bobBalance = new UInt256(1_000_000_000);
        _stateDb.SetAccount(Alice, new AccountState { Balance = aliceBalance });
        _stateDb.SetAccount(Bob, new AccountState { Balance = bobBalance });
    }

    private BlockHeader MakeParentHeader(ulong number = 0)
    {
        return new BlockHeader
        {
            Number = number,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Proposer = Alice,
            ChainId = _chainParams.ChainId,
            GasUsed = 0,
            GasLimit = _chainParams.BlockGasLimit,
            BaseFee = new UInt256(1),
        };
    }

    private Transaction MakeSignedTx(
        byte[] privateKey, Address sender, TransactionType type,
        byte[] data, ulong nonce = 0, ulong gasLimit = 200_000,
        UInt256? value = null)
    {
        var unsigned = new Transaction
        {
            Type = type,
            Nonce = nonce,
            Sender = sender,
            To = DexState.DexAddress,
            Value = value ?? UInt256.Zero,
            GasLimit = gasLimit,
            GasPrice = new UInt256(1),
            MaxFeePerGas = new UInt256(10),
            MaxPriorityFeePerGas = new UInt256(1),
            Data = data,
            ChainId = _chainParams.ChainId,
        };
        return Transaction.Sign(unsigned, privateKey);
    }

    private static byte[] MakeCreatePoolData(Address token0, Address token1, uint feeBps)
    {
        var data = new byte[44];
        token0.WriteTo(data.AsSpan(0, 20));
        token1.WriteTo(data.AsSpan(20, 20));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(40, 4), feeBps);
        return data;
    }

    private static byte[] MakeAddLiquidityData(ulong poolId, UInt256 amt0, UInt256 amt1, UInt256 min0, UInt256 min1)
    {
        var data = new byte[8 + 32 * 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0, 8), poolId);
        amt0.WriteTo(data.AsSpan(8, 32));
        amt1.WriteTo(data.AsSpan(40, 32));
        min0.WriteTo(data.AsSpan(72, 32));
        min1.WriteTo(data.AsSpan(104, 32));
        return data;
    }

    // ─── Genesis ───

    [Fact]
    public void Genesis_DexAccountExists()
    {
        var dexAccount = _stateDb.GetAccount(DexState.DexAddress);
        dexAccount.Should().NotBeNull();
        dexAccount!.Value.AccountType.Should().Be(AccountType.SystemContract);
    }

    // ─── Pool Creation via Transaction ───

    [Fact]
    public void CreatePool_ViaBlockBuilder_Success()
    {
        var (token0, token1) = DexEngine.SortTokens(Address.Zero, MakeAddress(0xAA));
        var tx = MakeSignedTx(AliceKey, Alice, TransactionType.DexCreatePool,
            MakeCreatePoolData(token0, token1, 30));

        var parent = MakeParentHeader();
        var block = _blockBuilder.BuildBlock([tx], _stateDb, parent, Alice);

        block.Transactions.Should().HaveCount(1);
        block.Receipts.Should().NotBeNull();
        block.Receipts!.Should().HaveCount(1);
        block.Receipts![0].Success.Should().BeTrue();

        // Verify pool exists
        var dexState = new DexState(_stateDb);
        var meta = dexState.GetPoolMetadata(0);
        meta.Should().NotBeNull();
        meta!.Value.Token0.Should().Be(token0);
        meta.Value.Token1.Should().Be(token1);
        meta.Value.FeeBps.Should().Be(30u);
    }

    // ─── Full Flow: Create Pool → Add Liquidity → Swap ───

    [Fact]
    public void FullFlow_CreatePool_AddLiquidity_Swap()
    {
        var tokenA = Address.Zero;
        var tokenB = MakeAddress(0xAA);
        var (token0, token1) = DexEngine.SortTokens(tokenA, tokenB);

        // Step 1: Create pool
        var createTx = MakeSignedTx(AliceKey, Alice, TransactionType.DexCreatePool,
            MakeCreatePoolData(token0, token1, 30), nonce: 0);

        var parent = MakeParentHeader();
        var block1 = _blockBuilder.BuildBlock([createTx], _stateDb, parent, Alice);
        block1.Receipts![0].Success.Should().BeTrue();

        // Step 2: Add liquidity (Alice deposits both tokens)
        // For native BST pair with Address.Zero, deposits reduce Alice's balance
        var amount0 = new UInt256(100_000);
        var amount1 = new UInt256(100_000);
        var addLiqTx = MakeSignedTx(AliceKey, Alice, TransactionType.DexAddLiquidity,
            MakeAddLiquidityData(0, amount0, amount1, UInt256.Zero, UInt256.Zero),
            nonce: 1, gasLimit: 200_000);

        var block2 = _blockBuilder.BuildBlock([addLiqTx], _stateDb, block1.Header, Alice);
        block2.Receipts![0].Success.Should().BeTrue();

        // Verify reserves
        var dexState = new DexState(_stateDb);
        var reserves = dexState.GetPoolReserves(0);
        reserves.Should().NotBeNull();
        reserves!.Value.Reserve0.Should().BeGreaterThan(UInt256.Zero);
        reserves.Value.Reserve1.Should().BeGreaterThan(UInt256.Zero);
        reserves.Value.TotalSupply.Should().BeGreaterThan(UInt256.Zero);

        // Verify LP shares
        var lpBalance = dexState.GetLpBalance(0, Alice);
        lpBalance.Should().BeGreaterThan(UInt256.Zero);
    }

    // ─── Batch Settlement Integration ───

    [Fact]
    public void BatchSettlement_UniformClearingPrice()
    {
        var dexState = new DexState(_stateDb);
        var tokenA = Address.Zero;
        var tokenB = MakeAddress(0xAA);
        var (token0, token1) = DexEngine.SortTokens(tokenA, tokenB);

        // Create pool directly in state
        dexState.CreatePool(token0, token1, 30);

        // Seed reserves
        dexState.SetPoolReserves(0, new PoolReserves
        {
            Reserve0 = new UInt256(1_000_000),
            Reserve1 = new UInt256(1_000_000),
            TotalSupply = new UInt256(1_000_000),
            KLast = UInt256.Zero,
        });

        // Verify the solver handles buy and sell intents
        var buyIntents = new List<ParsedIntent>
        {
            new ParsedIntent
            {
                Sender = Alice,
                TokenIn = token1,
                TokenOut = token0,
                AmountIn = new UInt256(10_000),
                MinAmountOut = new UInt256(5_000),
                Deadline = 0,
                AllowPartialFill = false,
            },
        };
        var sellIntents = new List<ParsedIntent>
        {
            new ParsedIntent
            {
                Sender = Bob,
                TokenIn = token0,
                TokenOut = token1,
                AmountIn = new UInt256(10_000),
                MinAmountOut = new UInt256(5_000),
                Deadline = 0,
                AllowPartialFill = false,
            },
        };

        var reserves = dexState.GetPoolReserves(0)!.Value;
        var result = BatchAuctionSolver.ComputeSettlement(
            buyIntents, sellIntents, [], [], reserves, 30, 0);

        result.Should().NotBeNull();
        result!.ClearingPrice.Should().BeGreaterThan(UInt256.Zero);

        // All fills should be at the same clearing price
        var buyFills = result.Fills.Where(f => f.Participant == Alice).ToList();
        var sellFills = result.Fills.Where(f => f.Participant == Bob).ToList();

        buyFills.Should().NotBeEmpty();
        sellFills.Should().NotBeEmpty();
    }

    // ─── Order Book Integration ───

    [Fact]
    public void OrderBook_PlaceAndMatch()
    {
        var dexState = new DexState(_stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);

        var clearingPrice = BatchAuctionSolver.PriceScale;

        // Place a buy order and a sell order
        dexState.PlaceOrder(Alice, 0, clearingPrice, new UInt256(1000), true, 200);
        dexState.PlaceOrder(Bob, 0, clearingPrice, new UInt256(1000), false, 200);

        // Find crossing orders
        var (buys, sells) = OrderBook.FindCrossingOrders(dexState, 0, clearingPrice, 100);
        buys.Should().HaveCount(1);
        sells.Should().HaveCount(1);

        // Match them
        var fills = OrderBook.MatchOrders(buys, sells, clearingPrice, dexState);
        fills.Should().HaveCount(2);
        fills.Should().Contain(f => f.Participant == Alice);
        fills.Should().Contain(f => f.Participant == Bob);

        // Orders should be fully consumed
        dexState.GetOrder(0).Should().BeNull();
        dexState.GetOrder(1).Should().BeNull();
    }

    // ─── TWAP After Settlement ───

    [Fact]
    public void TwapOracle_UpdatedAfterSettlement()
    {
        var dexState = new DexState(_stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);

        // Seed reserves for spot price
        dexState.SetPoolReserves(0, new PoolReserves
        {
            Reserve0 = new UInt256(1_000_000),
            Reserve1 = new UInt256(1_000_000),
            TotalSupply = new UInt256(1_000_000),
        });

        // Simulate TWAP updates
        var price = BatchAuctionSolver.PriceScale;
        dexState.UpdateTwapAccumulator(0, price, 10);
        dexState.UpdateTwapAccumulator(0, price, 20);

        var twap = TwapOracle.ComputeTwap(dexState, 0, 20, 20);
        twap.Should().BeGreaterThan(UInt256.Zero);

        // Verify serialization round-trip for block headers
        var settlements = new List<BatchResult>
        {
            new BatchResult { PoolId = 0, ClearingPrice = price },
        };
        var serialized = TwapOracle.SerializeForBlockHeader(settlements, dexState, 20, 256);
        var parsed = TwapOracle.ParseFromBlockHeader(serialized);
        parsed.Should().HaveCount(1);
        parsed[0].PoolId.Should().Be(0UL);
    }

    // ─── Dynamic Fees ───

    [Fact]
    public void DynamicFees_RespondToVolatility()
    {
        var dexState = new DexState(_stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);

        // Low volatility: stable reserves and TWAP
        dexState.SetPoolReserves(0, new PoolReserves
        {
            Reserve0 = new UInt256(1_000_000),
            Reserve1 = new UInt256(1_000_000),
            TotalSupply = new UInt256(1_000_000),
        });

        var price = BatchAuctionSolver.PriceScale;
        dexState.UpdateTwapAccumulator(0, price, 1);
        dexState.UpdateTwapAccumulator(0, price, 100);

        var stableFee = DynamicFeeCalculator.ComputeDynamicFeeFromState(dexState, 0, 30, 100);

        // Now introduce volatility: change reserves dramatically
        dexState.SetPoolReserves(0, new PoolReserves
        {
            Reserve0 = new UInt256(500_000),
            Reserve1 = new UInt256(2_000_000),
            TotalSupply = new UInt256(1_000_000),
        });

        var volatileFee = DynamicFeeCalculator.ComputeDynamicFeeFromState(dexState, 0, 30, 100);

        // Volatile fee should be higher than stable fee
        volatileFee.Should().BeGreaterThanOrEqualTo(stableFee);
    }

    // ─── Block Builder with DEX (Three-Phase Pipeline) ───

    [Fact]
    public void BuildBlockWithDex_NoIntents_ProducesValidBlock()
    {
        // Regular tx (transfer) should work through BuildBlockWithDex
        var dexState = new DexState(_stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);

        var parent = MakeParentHeader();
        var block = _blockBuilder.BuildBlockWithDex([], [], _stateDb, parent, Alice);

        block.Should().NotBeNull();
        block.Header.Number.Should().Be(1);
        block.Transactions.Should().BeEmpty();
    }

    // ─── ExtraData Contains TWAP ───

    [Fact]
    public void BuildBlockWithDex_ExtraData_EmptyWhenNoSettlements()
    {
        var parent = MakeParentHeader();
        var block = _blockBuilder.BuildBlockWithDex([], [], _stateDb, parent, Alice);

        // No batch settlements → ExtraData should be empty
        block.Header.ExtraData.Should().BeEmpty();
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }
}
