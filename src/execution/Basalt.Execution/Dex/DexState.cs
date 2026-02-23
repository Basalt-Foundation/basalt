using Basalt.Core;
using Basalt.Crypto;
using Basalt.Storage;

namespace Basalt.Execution.Dex;

/// <summary>
/// Reads and writes DEX state from the trie-based state database.
/// All DEX data lives at a well-known system address (0x0000...1009) as contract storage,
/// giving us Merkle proof support, RocksDB persistence, and fork-merge atomicity for free.
///
/// Key schema (32 bytes each, prefix byte determines data type):
/// <list type="table">
/// <listheader><term>Prefix</term><description>Data</description></listheader>
/// <item><term>0x01 + poolId(8B)</term><description>Pool metadata (token0, token1, feeBps)</description></item>
/// <item><term>0x02 + poolId(8B)</term><description>Pool reserves (reserve0, reserve1, totalSupply, kLast)</description></item>
/// <item><term>0x03 + poolId(8B) + owner(20B)</term><description>LP balance (UInt256)</description></item>
/// <item><term>0x04 + orderId(8B)</term><description>Limit order data</description></item>
/// <item><term>0x05 + poolId(8B)</term><description>TWAP accumulator</description></item>
/// <item><term>0x06</term><description>Global pool count (ulong)</description></item>
/// <item><term>0x07</term><description>Global order count (ulong)</description></item>
/// <item><term>0x09 + token0(20B) + token1(10B) + feeBps(2B)</term><description>Pool lookup by pair</description></item>
/// <item><term>0x0A + poolId(8B) + tick(4B signed BE)</term><description>Tick info (concentrated liquidity)</description></item>
/// <item><term>0x0B + positionId(8B)</term><description>Concentrated liquidity position</description></item>
/// <item><term>0x0C + poolId(8B)</term><description>Concentrated pool state (sqrtPrice, currentTick, totalLiquidity)</description></item>
/// <item><term>0x0D</term><description>Global position count (ulong)</description></item>
/// </list>
/// </summary>
public sealed class DexState
{
    private readonly IStateDatabase _stateDb;

    /// <summary>
    /// Well-known system address for DEX state: 0x000...1009.
    /// </summary>
    public static readonly Address DexAddress = MakeDexAddress();

    /// <summary>
    /// Creates a new DexState reader/writer backed by the given state database.
    /// </summary>
    /// <param name="stateDb">The state database to read from and write to.</param>
    public DexState(IStateDatabase stateDb)
    {
        _stateDb = stateDb;
    }

    // ────────── Pool CRUD ──────────

    /// <summary>
    /// Get the metadata for a pool by its ID.
    /// Returns null if the pool does not exist.
    /// </summary>
    public PoolMetadata? GetPoolMetadata(ulong poolId)
    {
        var key = MakePoolMetadataKey(poolId);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < PoolMetadata.SerializedSize)
            return null;
        return PoolMetadata.Deserialize(data);
    }

    /// <summary>
    /// Get the reserve state for a pool by its ID.
    /// Returns null if the pool does not exist.
    /// </summary>
    public PoolReserves? GetPoolReserves(ulong poolId)
    {
        var key = MakePoolReservesKey(poolId);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < PoolReserves.SerializedSize)
            return null;
        return PoolReserves.Deserialize(data);
    }

    /// <summary>
    /// Write the reserve state for a pool.
    /// </summary>
    public void SetPoolReserves(ulong poolId, PoolReserves reserves)
    {
        var key = MakePoolReservesKey(poolId);
        _stateDb.SetStorage(DexAddress, key, reserves.Serialize());
    }

    /// <summary>
    /// Create a new liquidity pool with the given token pair and fee tier.
    /// Tokens are canonically ordered (token0 &lt; token1).
    /// Returns the new pool ID.
    /// </summary>
    /// <param name="token0">Address of token0 (must be less than token1).</param>
    /// <param name="token1">Address of token1 (must be greater than token0).</param>
    /// <param name="feeBps">Swap fee in basis points.</param>
    /// <returns>The assigned pool ID.</returns>
    public ulong CreatePool(Address token0, Address token1, uint feeBps)
    {
        var poolId = GetPoolCount();
        SetPoolCount(poolId + 1);

        var metadata = new PoolMetadata
        {
            Token0 = token0,
            Token1 = token1,
            FeeBps = feeBps,
        };
        _stateDb.SetStorage(DexAddress, MakePoolMetadataKey(poolId), metadata.Serialize());

        var reserves = new PoolReserves
        {
            Reserve0 = UInt256.Zero,
            Reserve1 = UInt256.Zero,
            TotalSupply = UInt256.Zero,
            KLast = UInt256.Zero,
        };
        _stateDb.SetStorage(DexAddress, MakePoolReservesKey(poolId), reserves.Serialize());

        // Register pool lookup (pair + fee → poolId)
        SetPoolLookup(token0, token1, feeBps, poolId);

        return poolId;
    }

    /// <summary>
    /// Look up a pool ID by its token pair and fee tier.
    /// Returns null if no pool exists for this combination.
    /// </summary>
    public ulong? LookupPool(Address token0, Address token1, uint feeBps)
    {
        var key = MakePoolLookupKey(token0, token1, feeBps);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < 8)
            return null;
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data);
    }

    // ────────── LP Positions ──────────

    /// <summary>
    /// Get the LP token balance for a specific owner in a pool.
    /// </summary>
    public UInt256 GetLpBalance(ulong poolId, Address owner)
    {
        var key = MakeLpBalanceKey(poolId, owner);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < 32)
            return UInt256.Zero;
        return new UInt256(data);
    }

    /// <summary>
    /// Set the LP token balance for a specific owner in a pool.
    /// </summary>
    public void SetLpBalance(ulong poolId, Address owner, UInt256 balance)
    {
        var key = MakeLpBalanceKey(poolId, owner);
        _stateDb.SetStorage(DexAddress, key, balance.ToArray());
    }

    // ────────── LP Allowances ──────────

    /// <summary>
    /// Get the LP token allowance granted by an owner to a spender for a specific pool.
    /// </summary>
    public UInt256 GetLpAllowance(ulong poolId, Address owner, Address spender)
    {
        var key = MakeLpAllowanceKey(poolId, owner, spender);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < 32)
            return UInt256.Zero;
        return new UInt256(data);
    }

    /// <summary>
    /// Set the LP token allowance granted by an owner to a spender for a specific pool.
    /// </summary>
    public void SetLpAllowance(ulong poolId, Address owner, Address spender, UInt256 allowance)
    {
        var key = MakeLpAllowanceKey(poolId, owner, spender);
        _stateDb.SetStorage(DexAddress, key, allowance.ToArray());
    }

    // ────────── Order Book ──────────

    /// <summary>
    /// Get a limit order by its ID.
    /// Returns null if the order does not exist.
    /// </summary>
    public LimitOrder? GetOrder(ulong orderId)
    {
        var key = MakeOrderKey(orderId);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < LimitOrder.SerializedSize)
            return null;
        return LimitOrder.Deserialize(data);
    }

    /// <summary>
    /// Place a new limit order.
    /// Returns the assigned order ID.
    /// </summary>
    public ulong PlaceOrder(Address owner, ulong poolId, UInt256 price, UInt256 amount, bool isBuy, ulong expiry)
    {
        var orderId = GetOrderCount();
        SetOrderCount(orderId + 1);

        var order = new LimitOrder
        {
            Owner = owner,
            PoolId = poolId,
            Price = price,
            Amount = amount,
            IsBuy = isBuy,
            ExpiryBlock = expiry,
        };
        _stateDb.SetStorage(DexAddress, MakeOrderKey(orderId), order.Serialize());

        return orderId;
    }

    /// <summary>
    /// Update the remaining amount on an existing order.
    /// Used during partial fills.
    /// </summary>
    public void UpdateOrderAmount(ulong orderId, UInt256 newAmount)
    {
        var existing = GetOrder(orderId);
        if (existing == null) return;

        var order = existing.Value;
        order.Amount = newAmount;
        _stateDb.SetStorage(DexAddress, MakeOrderKey(orderId), order.Serialize());
    }

    /// <summary>
    /// Delete a limit order (fully filled or canceled).
    /// </summary>
    public void DeleteOrder(ulong orderId)
    {
        _stateDb.DeleteStorage(DexAddress, MakeOrderKey(orderId));
    }

    // ────────── TWAP ──────────

    /// <summary>
    /// Get the TWAP accumulator for a pool.
    /// Returns a zero-initialized accumulator if no data exists.
    /// </summary>
    public TwapAccumulator GetTwapAccumulator(ulong poolId)
    {
        var key = MakeTwapKey(poolId);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < TwapAccumulator.SerializedSize)
            return default;
        return TwapAccumulator.Deserialize(data);
    }

    /// <summary>
    /// Update the TWAP accumulator for a pool.
    /// Adds the current price weighted by the number of blocks since the last update.
    /// </summary>
    /// <param name="poolId">The pool to update.</param>
    /// <param name="price">The current spot price (scaled by 2^128).</param>
    /// <param name="blockNumber">The current block number.</param>
    public void UpdateTwapAccumulator(ulong poolId, UInt256 price, ulong blockNumber)
    {
        var acc = GetTwapAccumulator(poolId);
        if (acc.LastBlock > 0 && blockNumber > acc.LastBlock)
        {
            var blockDelta = new UInt256(blockNumber - acc.LastBlock);
            // cumulative += price * blockDelta
            acc.CumulativePrice = UInt256.CheckedAdd(
                acc.CumulativePrice,
                UInt256.CheckedMul(price, blockDelta));
        }
        acc.LastBlock = blockNumber;
        _stateDb.SetStorage(DexAddress, MakeTwapKey(poolId), acc.Serialize());
    }

    // ────────── Concentrated Liquidity (Phase E2) ──────────

    /// <summary>Get the tick info for a specific tick in a pool. Returns default if not initialized.</summary>
    public TickInfo GetTickInfo(ulong poolId, int tick)
    {
        var key = MakeTickKey(poolId, tick);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < TickInfo.SerializedSize) return default;
        return TickInfo.Deserialize(data);
    }

    /// <summary>Set the tick info for a specific tick in a pool.</summary>
    public void SetTickInfo(ulong poolId, int tick, TickInfo info)
    {
        _stateDb.SetStorage(DexAddress, MakeTickKey(poolId, tick), info.Serialize());
    }

    /// <summary>Delete tick info when a tick is fully de-initialized.</summary>
    public void DeleteTickInfo(ulong poolId, int tick)
    {
        _stateDb.DeleteStorage(DexAddress, MakeTickKey(poolId, tick));
    }

    /// <summary>Get a concentrated liquidity position by its ID. Returns null if not found.</summary>
    public Position? GetPosition(ulong positionId)
    {
        var key = MakePositionKey(positionId);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < Position.SerializedSize) return null;
        return Position.Deserialize(data);
    }

    /// <summary>Set a concentrated liquidity position.</summary>
    public void SetPosition(ulong positionId, Position position)
    {
        _stateDb.SetStorage(DexAddress, MakePositionKey(positionId), position.Serialize());
    }

    /// <summary>Delete a position (fully burned).</summary>
    public void DeletePosition(ulong positionId)
    {
        _stateDb.DeleteStorage(DexAddress, MakePositionKey(positionId));
    }

    /// <summary>Get the concentrated pool state. Returns null if not a concentrated pool.</summary>
    public ConcentratedPoolState? GetConcentratedPoolState(ulong poolId)
    {
        var key = MakeConcentratedPoolKey(poolId);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < ConcentratedPoolState.SerializedSize) return null;
        return ConcentratedPoolState.Deserialize(data);
    }

    /// <summary>Set the concentrated pool state.</summary>
    public void SetConcentratedPoolState(ulong poolId, ConcentratedPoolState state)
    {
        _stateDb.SetStorage(DexAddress, MakeConcentratedPoolKey(poolId), state.Serialize());
    }

    /// <summary>Get the global position counter.</summary>
    public ulong GetPositionCount()
    {
        var key = MakeGlobalKey(0x0D);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < 8) return 0;
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data);
    }

    /// <summary>Set the global position counter.</summary>
    public void SetPositionCount(ulong count)
    {
        var key = MakeGlobalKey(0x0D);
        var data = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data, count);
        _stateDb.SetStorage(DexAddress, key, data);
    }

    // ────────── Globals ──────────

    /// <summary>Get the total number of pools created.</summary>
    public ulong GetPoolCount()
    {
        var key = MakeGlobalKey(0x06);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < 8)
            return 0;
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data);
    }

    /// <summary>Get the total number of orders created.</summary>
    public ulong GetOrderCount()
    {
        var key = MakeGlobalKey(0x07);
        var data = _stateDb.GetStorage(DexAddress, key);
        if (data == null || data.Length < 8)
            return 0;
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data);
    }

    // ────────── Key Construction ──────────

    /// <summary>
    /// Construct the storage key for pool metadata: <c>0x01 + poolId(8B) + 0x00(23B)</c>.
    /// </summary>
    public static Hash256 MakePoolMetadataKey(ulong poolId)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x01;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key[1..9], poolId);
        return new Hash256(key);
    }

    /// <summary>
    /// Construct the storage key for pool reserves: <c>0x02 + poolId(8B) + 0x00(23B)</c>.
    /// </summary>
    public static Hash256 MakePoolReservesKey(ulong poolId)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x02;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key[1..9], poolId);
        return new Hash256(key);
    }

    /// <summary>
    /// Construct the storage key for an LP balance: <c>0x03 + poolId(8B) + owner(20B) + 0x00(3B)</c>.
    /// </summary>
    public static Hash256 MakeLpBalanceKey(ulong poolId, Address owner)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x03;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key[1..9], poolId);
        owner.WriteTo(key[9..29]);
        return new Hash256(key);
    }

    /// <summary>
    /// Construct the storage key for a limit order: <c>0x04 + orderId(8B) + 0x00(23B)</c>.
    /// </summary>
    public static Hash256 MakeOrderKey(ulong orderId)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x04;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key[1..9], orderId);
        return new Hash256(key);
    }

    /// <summary>
    /// Construct the storage key for a TWAP accumulator: <c>0x05 + poolId(8B) + 0x00(23B)</c>.
    /// </summary>
    public static Hash256 MakeTwapKey(ulong poolId)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x05;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key[1..9], poolId);
        return new Hash256(key);
    }

    /// <summary>
    /// Construct a global counter key: <c>prefix + 0x00(31B)</c>.
    /// </summary>
    public static Hash256 MakeGlobalKey(byte prefix)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = prefix;
        return new Hash256(key);
    }

    /// <summary>
    /// Construct the storage key for an LP allowance: <c>0x08 + poolId(8B) + BLAKE3(owner ++ spender)[0..23]</c>.
    /// The 23-byte hash suffix avoids truncating either address while fitting in 32 bytes.
    /// </summary>
    public static Hash256 MakeLpAllowanceKey(ulong poolId, Address owner, Address spender)
    {
        // Hash owner + spender to get a unique 23-byte suffix
        Span<byte> input = stackalloc byte[Address.Size * 2];
        owner.WriteTo(input[..Address.Size]);
        spender.WriteTo(input[Address.Size..]);
        var hash = Blake3Hasher.Hash(input);
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(hashBytes);

        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x08;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key[1..9], poolId);
        hashBytes[..23].CopyTo(key[9..32]);
        return new Hash256(key);
    }

    /// <summary>
    /// Construct the storage key for tick info: <c>0x0A + poolId(8B) + tick(4B signed BE) + 0x00(19B)</c>.
    /// </summary>
    public static Hash256 MakeTickKey(ulong poolId, int tick)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x0A;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key[1..9], poolId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key[9..13], tick);
        return new Hash256(key);
    }

    /// <summary>
    /// Construct the storage key for a position: <c>0x0B + positionId(8B) + 0x00(23B)</c>.
    /// </summary>
    public static Hash256 MakePositionKey(ulong positionId)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x0B;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key[1..9], positionId);
        return new Hash256(key);
    }

    /// <summary>
    /// Construct the storage key for concentrated pool state: <c>0x0C + poolId(8B) + 0x00(23B)</c>.
    /// </summary>
    public static Hash256 MakeConcentratedPoolKey(ulong poolId)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x0C;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(key[1..9], poolId);
        return new Hash256(key);
    }

    /// <summary>
    /// Construct the pool lookup key: <c>0x09 + token0(20B) + token1(10B) + feeBps(2B)</c>.
    /// Note: token1 is truncated to first 10 bytes due to 32-byte key limit.
    /// This provides sufficient uniqueness for pool lookup while fitting in a single hash key.
    /// </summary>
    public static Hash256 MakePoolLookupKey(Address token0, Address token1, uint feeBps)
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0x09;
        token0.WriteTo(key[1..21]);
        // Truncate token1 to 9 bytes to fit feeBps (4B) in 32 bytes: 1 + 20 + 9 + 2 = 32
        Span<byte> t1 = stackalloc byte[Address.Size];
        token1.WriteTo(t1);
        t1[..9].CopyTo(key[21..30]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(key[30..32], (ushort)feeBps);
        return new Hash256(key);
    }

    // ────────── Private Helpers ──────────

    private void SetPoolCount(ulong count)
    {
        var key = MakeGlobalKey(0x06);
        var data = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data, count);
        _stateDb.SetStorage(DexAddress, key, data);
    }

    private void SetOrderCount(ulong count)
    {
        var key = MakeGlobalKey(0x07);
        var data = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data, count);
        _stateDb.SetStorage(DexAddress, key, data);
    }

    private void SetPoolLookup(Address token0, Address token1, uint feeBps, ulong poolId)
    {
        var key = MakePoolLookupKey(token0, token1, feeBps);
        var data = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data, poolId);
        _stateDb.SetStorage(DexAddress, key, data);
    }

    private static Address MakeDexAddress()
    {
        var bytes = new byte[20];
        bytes[18] = 0x10;
        bytes[19] = 0x09;
        return new Address(bytes);
    }
}
