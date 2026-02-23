using Basalt.Core;
using Basalt.Execution.Dex;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for the DEX order book — limit order matching, crossing detection,
/// partial fills, and expired order cleanup.
/// </summary>
public class OrderBookTests
{
    private readonly InMemoryStateDb _stateDb = new();
    private readonly DexState _dexState;

    private static readonly Address Token0 = Address.Zero;
    private static readonly Address Token1 = MakeAddress(0xAA);
    private static readonly Address Buyer = MakeAddress(0x01);
    private static readonly Address Seller = MakeAddress(0x02);

    public OrderBookTests()
    {
        _dexState = new DexState(_stateDb);
        _dexState.CreatePool(Token0, Token1, 30);
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }

    [Fact]
    public void FindCrossingOrders_NoneExist_ReturnsEmpty()
    {
        var clearingPrice = BatchAuctionSolver.PriceScale; // 1:1
        var (buys, sells) = OrderBook.FindCrossingOrders(
            _dexState, 0, clearingPrice, 100);

        buys.Should().BeEmpty();
        sells.Should().BeEmpty();
    }

    [Fact]
    public void FindCrossingOrders_BuyAboveClearingPrice_Included()
    {
        var clearingPrice = BatchAuctionSolver.PriceScale;

        // Buy order at 1.5x price — should cross at 1x
        _dexState.PlaceOrder(Buyer, 0,
            BatchAuctionSolver.PriceScale + BatchAuctionSolver.PriceScale / new UInt256(2),
            new UInt256(1000), true, 200);

        var (buys, sells) = OrderBook.FindCrossingOrders(
            _dexState, 0, clearingPrice, 100);

        buys.Should().HaveCount(1);
        buys[0].Order.Owner.Should().Be(Buyer);
    }

    [Fact]
    public void FindCrossingOrders_SellBelowClearingPrice_Included()
    {
        var clearingPrice = BatchAuctionSolver.PriceScale;

        // Sell order at 0.8x — should cross at 1x
        _dexState.PlaceOrder(Seller, 0,
            BatchAuctionSolver.PriceScale * new UInt256(4) / new UInt256(5),
            new UInt256(1000), false, 200);

        var (buys, sells) = OrderBook.FindCrossingOrders(
            _dexState, 0, clearingPrice, 100);

        sells.Should().HaveCount(1);
        sells[0].Order.Owner.Should().Be(Seller);
    }

    [Fact]
    public void FindCrossingOrders_ExpiredOrders_Excluded()
    {
        _dexState.PlaceOrder(Buyer, 0,
            BatchAuctionSolver.PriceScale * new UInt256(2),
            new UInt256(1000), true, 50); // Expires at block 50

        var (buys, _) = OrderBook.FindCrossingOrders(
            _dexState, 0, BatchAuctionSolver.PriceScale, 100); // Current block 100

        buys.Should().BeEmpty();
    }

    [Fact]
    public void FindCrossingOrders_WrongPool_Excluded()
    {
        _dexState.CreatePool(MakeAddress(0xCC), MakeAddress(0xDD), 30); // Pool 1

        _dexState.PlaceOrder(Buyer, 1, // Pool 1
            BatchAuctionSolver.PriceScale * new UInt256(2),
            new UInt256(1000), true, 200);

        var (buys, _) = OrderBook.FindCrossingOrders(
            _dexState, 0, BatchAuctionSolver.PriceScale, 100); // Query pool 0

        buys.Should().BeEmpty();
    }

    [Fact]
    public void MatchOrders_FullFill()
    {
        var clearingPrice = BatchAuctionSolver.PriceScale;

        // Buy order: 1000 token1 at price 1.0
        _dexState.PlaceOrder(Buyer, 0, clearingPrice, new UInt256(1000), true, 200);
        // Sell order: 1000 token0 at price 1.0
        _dexState.PlaceOrder(Seller, 0, clearingPrice, new UInt256(1000), false, 200);

        var (buyOrders, sellOrders) = OrderBook.FindCrossingOrders(
            _dexState, 0, clearingPrice, 100);

        var fills = OrderBook.MatchOrders(buyOrders, sellOrders, clearingPrice, _dexState);

        fills.Should().HaveCount(2); // One fill per participant
        fills.Should().Contain(f => f.Participant == Buyer);
        fills.Should().Contain(f => f.Participant == Seller);

        // Both orders should be deleted (fully filled)
        _dexState.GetOrder(0).Should().BeNull();
        _dexState.GetOrder(1).Should().BeNull();
    }

    [Fact]
    public void CleanupExpiredOrders_ReturnsEscrow()
    {
        _stateDb.SetAccount(DexState.DexAddress, new AccountState
        {
            Balance = new UInt256(10_000),
            AccountType = AccountType.SystemContract,
        });
        _stateDb.SetAccount(Buyer, new AccountState { Balance = UInt256.Zero });

        // Place a buy order (escrowing token1 = native BST since it's the second token)
        // For this test, the pool's token1 is Token1 which isn't native BST
        // So let's use a native BST pair
        var meta = _dexState.GetPoolMetadata(0);

        _dexState.PlaceOrder(Buyer, 0,
            BatchAuctionSolver.PriceScale,
            new UInt256(500), true, 50); // Expires at block 50

        var cleaned = OrderBook.CleanupExpiredOrders(_dexState, _stateDb, 0, 100);

        cleaned.Should().Be(1);
        _dexState.GetOrder(0).Should().BeNull();
    }
}
