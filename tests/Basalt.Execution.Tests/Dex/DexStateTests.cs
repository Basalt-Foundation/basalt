using Basalt.Core;
using Basalt.Execution.Dex;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for DexState — the storage layer that reads/writes DEX data
/// from the trie-based state database. Validates key construction, serialization
/// round-trips, pool CRUD, LP positions, order book, TWAP accumulators, and global counters.
/// </summary>
public class DexStateTests
{
    private static DexState CreateState() => new(new InMemoryStateDb());

    private static readonly Address TokenA = MakeAddress(1);
    private static readonly Address TokenB = MakeAddress(2);

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }

    // ────────── Pool CRUD ──────────

    [Fact]
    public void CreatePool_AssignsSequentialIds()
    {
        var state = CreateState();

        var id0 = state.CreatePool(TokenA, TokenB, 30);
        var id1 = state.CreatePool(TokenA, TokenB, 100);

        id0.Should().Be(0UL);
        id1.Should().Be(1UL);
        state.GetPoolCount().Should().Be(2UL);
    }

    [Fact]
    public void GetPoolMetadata_RoundTrip()
    {
        var state = CreateState();
        var poolId = state.CreatePool(TokenA, TokenB, 30);

        var meta = state.GetPoolMetadata(poolId);
        meta.Should().NotBeNull();
        meta!.Value.Token0.Should().Be(TokenA);
        meta.Value.Token1.Should().Be(TokenB);
        meta.Value.FeeBps.Should().Be(30u);
    }

    [Fact]
    public void GetPoolMetadata_NonexistentPool_ReturnsNull()
    {
        var state = CreateState();
        state.GetPoolMetadata(999).Should().BeNull();
    }

    [Fact]
    public void GetSetPoolReserves_RoundTrip()
    {
        var state = CreateState();
        var poolId = state.CreatePool(TokenA, TokenB, 30);

        var reserves = new PoolReserves
        {
            Reserve0 = new UInt256(50_000),
            Reserve1 = new UInt256(100_000),
            TotalSupply = new UInt256(7000),
            KLast = new UInt256(5_000_000_000),
        };
        state.SetPoolReserves(poolId, reserves);

        var loaded = state.GetPoolReserves(poolId);
        loaded.Should().NotBeNull();
        loaded!.Value.Reserve0.Should().Be(new UInt256(50_000));
        loaded.Value.Reserve1.Should().Be(new UInt256(100_000));
        loaded.Value.TotalSupply.Should().Be(new UInt256(7000));
        loaded.Value.KLast.Should().Be(new UInt256(5_000_000_000));
    }

    [Fact]
    public void LookupPool_ByPairAndFee()
    {
        var state = CreateState();
        var poolId = state.CreatePool(TokenA, TokenB, 30);

        var found = state.LookupPool(TokenA, TokenB, 30);
        found.Should().Be(poolId);
    }

    [Fact]
    public void LookupPool_DifferentFee_ReturnsNull()
    {
        var state = CreateState();
        state.CreatePool(TokenA, TokenB, 30);

        state.LookupPool(TokenA, TokenB, 100).Should().BeNull();
    }

    // ────────── LP Positions ──────────

    [Fact]
    public void LpBalance_DefaultsToZero()
    {
        var state = CreateState();
        state.CreatePool(TokenA, TokenB, 30);

        state.GetLpBalance(0, TokenA).Should().Be(UInt256.Zero);
    }

    [Fact]
    public void LpBalance_SetAndGet()
    {
        var state = CreateState();
        state.CreatePool(TokenA, TokenB, 30);

        state.SetLpBalance(0, TokenA, new UInt256(5000));
        state.GetLpBalance(0, TokenA).Should().Be(new UInt256(5000));
    }

    [Fact]
    public void LpBalance_IndependentPerOwner()
    {
        var state = CreateState();
        state.CreatePool(TokenA, TokenB, 30);

        state.SetLpBalance(0, TokenA, new UInt256(100));
        state.SetLpBalance(0, TokenB, new UInt256(200));

        state.GetLpBalance(0, TokenA).Should().Be(new UInt256(100));
        state.GetLpBalance(0, TokenB).Should().Be(new UInt256(200));
    }

    // ────────── Order Book ──────────

    [Fact]
    public void PlaceOrder_AssignsSequentialIds()
    {
        var state = CreateState();
        var id0 = state.PlaceOrder(TokenA, 0, new UInt256(100), new UInt256(50), true, 1000);
        var id1 = state.PlaceOrder(TokenB, 0, new UInt256(200), new UInt256(100), false, 2000);

        id0.Should().Be(0UL);
        id1.Should().Be(1UL);
        state.GetOrderCount().Should().Be(2UL);
    }

    [Fact]
    public void GetOrder_RoundTrip()
    {
        var state = CreateState();
        var orderId = state.PlaceOrder(TokenA, 5, new UInt256(1000), new UInt256(500), true, 100);

        var order = state.GetOrder(orderId);
        order.Should().NotBeNull();
        order!.Value.Owner.Should().Be(TokenA);
        order.Value.PoolId.Should().Be(5UL);
        order.Value.Price.Should().Be(new UInt256(1000));
        order.Value.Amount.Should().Be(new UInt256(500));
        order.Value.IsBuy.Should().BeTrue();
        order.Value.ExpiryBlock.Should().Be(100UL);
    }

    [Fact]
    public void UpdateOrderAmount_ModifiesAmount()
    {
        var state = CreateState();
        var orderId = state.PlaceOrder(TokenA, 0, new UInt256(100), new UInt256(500), true, 1000);

        state.UpdateOrderAmount(orderId, new UInt256(250));

        var order = state.GetOrder(orderId);
        order!.Value.Amount.Should().Be(new UInt256(250));
    }

    [Fact]
    public void DeleteOrder_RemovesOrder()
    {
        var state = CreateState();
        var orderId = state.PlaceOrder(TokenA, 0, new UInt256(100), new UInt256(500), true, 1000);

        state.DeleteOrder(orderId);
        state.GetOrder(orderId).Should().BeNull();
    }

    // ────────── TWAP ──────────

    [Fact]
    public void TwapAccumulator_DefaultsToZero()
    {
        var state = CreateState();
        var acc = state.GetTwapAccumulator(0);
        acc.CumulativePrice.Should().Be(UInt256.Zero);
        acc.LastBlock.Should().Be(0UL);
    }

    [Fact]
    public void UpdateTwapAccumulator_AccumulatesPrice()
    {
        var state = CreateState();
        state.CreatePool(TokenA, TokenB, 30);

        // First update — sets lastBlock
        state.UpdateTwapAccumulator(0, new UInt256(1000), 10);
        var acc1 = state.GetTwapAccumulator(0);
        acc1.LastBlock.Should().Be(10UL);
        acc1.CumulativePrice.Should().Be(UInt256.Zero); // No previous block

        // Second update — accumulates
        state.UpdateTwapAccumulator(0, new UInt256(1500), 15);
        var acc2 = state.GetTwapAccumulator(0);
        acc2.LastBlock.Should().Be(15UL);
        // cumulative = 0 + 1500 * (15 - 10) = 7500
        acc2.CumulativePrice.Should().Be(new UInt256(7500));
    }

    // ────────── Key Construction ──────────

    [Fact]
    public void Keys_AreDeterministic()
    {
        var k1 = DexState.MakePoolMetadataKey(42);
        var k2 = DexState.MakePoolMetadataKey(42);
        k1.Should().Be(k2);
    }

    [Fact]
    public void Keys_DifferByPrefix()
    {
        var metaKey = DexState.MakePoolMetadataKey(0);
        var reserveKey = DexState.MakePoolReservesKey(0);
        metaKey.Should().NotBe(reserveKey);
    }

    [Fact]
    public void Keys_DifferByPoolId()
    {
        var k0 = DexState.MakePoolMetadataKey(0);
        var k1 = DexState.MakePoolMetadataKey(1);
        k0.Should().NotBe(k1);
    }

    // ────────── Serialization ──────────

    [Fact]
    public void PoolMetadata_SerializeRoundTrip()
    {
        var meta = new PoolMetadata
        {
            Token0 = TokenA,
            Token1 = TokenB,
            FeeBps = 30,
        };

        var bytes = meta.Serialize();
        bytes.Length.Should().Be(PoolMetadata.SerializedSize);

        var deserialized = PoolMetadata.Deserialize(bytes);
        deserialized.Token0.Should().Be(TokenA);
        deserialized.Token1.Should().Be(TokenB);
        deserialized.FeeBps.Should().Be(30u);
    }

    [Fact]
    public void PoolReserves_SerializeRoundTrip()
    {
        var reserves = new PoolReserves
        {
            Reserve0 = new UInt256(123456),
            Reserve1 = new UInt256(789012),
            TotalSupply = new UInt256(5000),
            KLast = new UInt256(97_406_107_872),
        };

        var bytes = reserves.Serialize();
        bytes.Length.Should().Be(PoolReserves.SerializedSize);

        var deserialized = PoolReserves.Deserialize(bytes);
        deserialized.Reserve0.Should().Be(new UInt256(123456));
        deserialized.Reserve1.Should().Be(new UInt256(789012));
        deserialized.TotalSupply.Should().Be(new UInt256(5000));
        deserialized.KLast.Should().Be(new UInt256(97_406_107_872));
    }

    [Fact]
    public void LimitOrder_SerializeRoundTrip()
    {
        var order = new LimitOrder
        {
            Owner = TokenA,
            PoolId = 42,
            Price = new UInt256(1_000_000),
            Amount = new UInt256(500),
            IsBuy = true,
            ExpiryBlock = 99999,
        };

        var bytes = order.Serialize();
        bytes.Length.Should().Be(LimitOrder.SerializedSize);

        var deserialized = LimitOrder.Deserialize(bytes);
        deserialized.Owner.Should().Be(TokenA);
        deserialized.PoolId.Should().Be(42UL);
        deserialized.Price.Should().Be(new UInt256(1_000_000));
        deserialized.Amount.Should().Be(new UInt256(500));
        deserialized.IsBuy.Should().BeTrue();
        deserialized.ExpiryBlock.Should().Be(99999UL);
    }

    [Fact]
    public void TwapAccumulator_SerializeRoundTrip()
    {
        var acc = new TwapAccumulator
        {
            CumulativePrice = new UInt256(999_999_999),
            LastBlock = 12345,
        };

        var bytes = acc.Serialize();
        bytes.Length.Should().Be(TwapAccumulator.SerializedSize);

        var deserialized = TwapAccumulator.Deserialize(bytes);
        deserialized.CumulativePrice.Should().Be(new UInt256(999_999_999));
        deserialized.LastBlock.Should().Be(12345UL);
    }
}
