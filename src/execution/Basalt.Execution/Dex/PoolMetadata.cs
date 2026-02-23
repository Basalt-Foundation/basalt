using Basalt.Core;

namespace Basalt.Execution.Dex;

/// <summary>
/// Immutable metadata for a liquidity pool.
/// Stored at key prefix <c>0x01</c> in the DEX state address.
/// Token0 and Token1 are canonically ordered (Token0 &lt; Token1).
/// </summary>
public readonly struct PoolMetadata
{
    /// <summary>Address of the first token (lower address).</summary>
    public Address Token0 { get; init; }

    /// <summary>Address of the second token (higher address).</summary>
    public Address Token1 { get; init; }

    /// <summary>Swap fee in basis points (e.g. 30 = 0.3%).</summary>
    public uint FeeBps { get; init; }

    /// <summary>
    /// Serialized size in bytes: 20 (token0) + 20 (token1) + 4 (feeBps) = 44 bytes.
    /// </summary>
    public const int SerializedSize = Address.Size + Address.Size + 4;

    /// <summary>
    /// Serialize this pool metadata to a byte array.
    /// Format: <c>[20B token0][20B token1][4B feeBps BE]</c>
    /// </summary>
    public byte[] Serialize()
    {
        var buffer = new byte[SerializedSize];
        Token0.WriteTo(buffer.AsSpan(0, Address.Size));
        Token1.WriteTo(buffer.AsSpan(Address.Size, Address.Size));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(40, 4), FeeBps);
        return buffer;
    }

    /// <summary>
    /// Deserialize pool metadata from a byte span.
    /// </summary>
    public static PoolMetadata Deserialize(ReadOnlySpan<byte> data)
    {
        return new PoolMetadata
        {
            Token0 = new Address(data[..Address.Size]),
            Token1 = new Address(data[Address.Size..(Address.Size * 2)]),
            FeeBps = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[40..44]),
        };
    }
}

/// <summary>
/// Mutable reserve state for a liquidity pool.
/// Stored at key prefix <c>0x02</c> in the DEX state address.
/// Updated on every swap, liquidity add/remove, and batch settlement.
/// </summary>
public struct PoolReserves
{
    /// <summary>Reserve amount of token0.</summary>
    public UInt256 Reserve0 { get; set; }

    /// <summary>Reserve amount of token1.</summary>
    public UInt256 Reserve1 { get; set; }

    /// <summary>Total supply of LP shares for this pool.</summary>
    public UInt256 TotalSupply { get; set; }

    /// <summary>
    /// Product of reserves at the last fee collection point.
    /// Used for protocol fee calculation (Uniswap v2 style).
    /// </summary>
    public UInt256 KLast { get; set; }

    /// <summary>
    /// Serialized size: 4 * 32 = 128 bytes.
    /// </summary>
    public const int SerializedSize = 32 * 4;

    /// <summary>
    /// Serialize this reserve state to a byte array.
    /// Format: <c>[32B reserve0 LE][32B reserve1 LE][32B totalSupply LE][32B kLast LE]</c>
    /// </summary>
    public readonly byte[] Serialize()
    {
        var buffer = new byte[SerializedSize];
        Reserve0.WriteTo(buffer.AsSpan(0, 32));
        Reserve1.WriteTo(buffer.AsSpan(32, 32));
        TotalSupply.WriteTo(buffer.AsSpan(64, 32));
        KLast.WriteTo(buffer.AsSpan(96, 32));
        return buffer;
    }

    /// <summary>
    /// Deserialize reserve state from a byte span.
    /// </summary>
    public static PoolReserves Deserialize(ReadOnlySpan<byte> data)
    {
        return new PoolReserves
        {
            Reserve0 = new UInt256(data[..32]),
            Reserve1 = new UInt256(data[32..64]),
            TotalSupply = new UInt256(data[64..96]),
            KLast = new UInt256(data[96..128]),
        };
    }
}

/// <summary>
/// A persistent limit order in the DEX order book.
/// Stored at key prefix <c>0x04</c> in the DEX state address.
/// </summary>
public struct LimitOrder
{
    /// <summary>Address of the order placer.</summary>
    public Address Owner { get; set; }

    /// <summary>Pool this order is placed against.</summary>
    public ulong PoolId { get; set; }

    /// <summary>
    /// Limit price as a UInt256 (scaled by 2^128 for precision).
    /// For buy orders: maximum price willing to pay.
    /// For sell orders: minimum price willing to accept.
    /// </summary>
    public UInt256 Price { get; set; }

    /// <summary>Remaining amount to fill (in input token units).</summary>
    public UInt256 Amount { get; set; }

    /// <summary>True if this is a buy order (buying token0 with token1), false for sell.</summary>
    public bool IsBuy { get; set; }

    /// <summary>Block number after which this order expires and can be cleaned up.</summary>
    public ulong ExpiryBlock { get; set; }

    /// <summary>
    /// Serialized size: 20 + 8 + 32 + 32 + 1 + 8 = 101 bytes.
    /// </summary>
    public const int SerializedSize = Address.Size + 8 + 32 + 32 + 1 + 8;

    /// <summary>
    /// Serialize this order to a byte array.
    /// Format: <c>[20B owner][8B poolId BE][32B price LE][32B amount LE][1B isBuy][8B expiry BE]</c>
    /// </summary>
    public readonly byte[] Serialize()
    {
        var buffer = new byte[SerializedSize];
        Owner.WriteTo(buffer.AsSpan(0, Address.Size));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(20, 8), PoolId);
        Price.WriteTo(buffer.AsSpan(28, 32));
        Amount.WriteTo(buffer.AsSpan(60, 32));
        buffer[92] = IsBuy ? (byte)1 : (byte)0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(93, 8), ExpiryBlock);
        return buffer;
    }

    /// <summary>
    /// Deserialize a limit order from a byte span.
    /// </summary>
    public static LimitOrder Deserialize(ReadOnlySpan<byte> data)
    {
        return new LimitOrder
        {
            Owner = new Address(data[..Address.Size]),
            PoolId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data[20..28]),
            Price = new UInt256(data[28..60]),
            Amount = new UInt256(data[60..92]),
            IsBuy = data[92] == 1,
            ExpiryBlock = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data[93..101]),
        };
    }
}

/// <summary>
/// TWAP (Time-Weighted Average Price) accumulator for a pool.
/// Stored at key prefix <c>0x05</c> in the DEX state address.
/// Updated each block a pool is active, enabling on-chain price oracle queries.
/// </summary>
public struct TwapAccumulator
{
    /// <summary>
    /// Cumulative price: sum of (price * blockDelta) over all updates.
    /// To compute TWAP over a window: (accumulator_now - accumulator_start) / (block_now - block_start).
    /// </summary>
    public UInt256 CumulativePrice { get; set; }

    /// <summary>Last block number when this accumulator was updated.</summary>
    public ulong LastBlock { get; set; }

    /// <summary>
    /// Serialized size: 32 + 8 = 40 bytes.
    /// </summary>
    public const int SerializedSize = 32 + 8;

    /// <summary>
    /// Serialize this accumulator to a byte array.
    /// </summary>
    public readonly byte[] Serialize()
    {
        var buffer = new byte[SerializedSize];
        CumulativePrice.WriteTo(buffer.AsSpan(0, 32));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(32, 8), LastBlock);
        return buffer;
    }

    /// <summary>
    /// Deserialize a TWAP accumulator from a byte span.
    /// </summary>
    public static TwapAccumulator Deserialize(ReadOnlySpan<byte> data)
    {
        return new TwapAccumulator
        {
            CumulativePrice = new UInt256(data[..32]),
            LastBlock = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data[32..40]),
        };
    }
}

// ════════════════════════════════════════════════════════════════════
// Concentrated Liquidity Structures (Phase E2)
// ════════════════════════════════════════════════════════════════════

/// <summary>
/// Tick-level liquidity info for concentrated liquidity pools.
/// Each initialized tick stores the net liquidity change that occurs when the tick is crossed.
/// Stored at key prefix <c>0x0A</c> in the DEX state address.
/// </summary>
public struct TickInfo
{
    /// <summary>
    /// Net liquidity change when crossing this tick left-to-right.
    /// Positive = liquidity added (lower bound of a position).
    /// Negative = liquidity removed (upper bound of a position).
    /// Stored as a signed 64-bit value.
    /// </summary>
    public long LiquidityNet { get; set; }

    /// <summary>
    /// Total liquidity referencing this tick (sum of all positions using it as a bound).
    /// When this reaches zero, the tick can be de-initialized.
    /// </summary>
    public UInt256 LiquidityGross { get; set; }

    /// <summary>Serialized size: 8 (liquidityNet) + 32 (liquidityGross) = 40 bytes.</summary>
    public const int SerializedSize = 8 + 32;

    /// <summary>Serialize to byte array.</summary>
    public readonly byte[] Serialize()
    {
        var buffer = new byte[SerializedSize];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(0, 8), LiquidityNet);
        LiquidityGross.WriteTo(buffer.AsSpan(8, 32));
        return buffer;
    }

    /// <summary>Deserialize from byte span.</summary>
    public static TickInfo Deserialize(ReadOnlySpan<byte> data)
    {
        return new TickInfo
        {
            LiquidityNet = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(data[..8]),
            LiquidityGross = new UInt256(data[8..40]),
        };
    }
}

/// <summary>
/// A concentrated liquidity position — liquidity deployed within a specific tick range.
/// Stored at key prefix <c>0x0B</c> in the DEX state address.
/// </summary>
public struct Position
{
    /// <summary>Address of the position owner.</summary>
    public Address Owner { get; set; }

    /// <summary>Pool this position belongs to.</summary>
    public ulong PoolId { get; set; }

    /// <summary>Lower tick boundary (inclusive).</summary>
    public int TickLower { get; set; }

    /// <summary>Upper tick boundary (exclusive).</summary>
    public int TickUpper { get; set; }

    /// <summary>Amount of liquidity in this position.</summary>
    public UInt256 Liquidity { get; set; }

    /// <summary>Serialized size: 20 + 8 + 4 + 4 + 32 = 68 bytes.</summary>
    public const int SerializedSize = Address.Size + 8 + 4 + 4 + 32;

    /// <summary>Serialize to byte array.</summary>
    public readonly byte[] Serialize()
    {
        var buffer = new byte[SerializedSize];
        Owner.WriteTo(buffer.AsSpan(0, Address.Size));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(20, 8), PoolId);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(28, 4), TickLower);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(32, 4), TickUpper);
        Liquidity.WriteTo(buffer.AsSpan(36, 32));
        return buffer;
    }

    /// <summary>Deserialize from byte span.</summary>
    public static Position Deserialize(ReadOnlySpan<byte> data)
    {
        return new Position
        {
            Owner = new Address(data[..Address.Size]),
            PoolId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data[20..28]),
            TickLower = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[28..32]),
            TickUpper = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[32..36]),
            Liquidity = new UInt256(data[36..68]),
        };
    }
}

/// <summary>
/// Global state for a concentrated liquidity pool.
/// Stored at key prefix <c>0x0C</c> in the DEX state address.
/// Tracks the current sqrt price, tick, and total active liquidity.
/// </summary>
public struct ConcentratedPoolState
{
    /// <summary>Current sqrt price as Q64.96 fixed-point.</summary>
    public UInt256 SqrtPriceX96 { get; set; }

    /// <summary>Current tick (derived from SqrtPriceX96).</summary>
    public int CurrentTick { get; set; }

    /// <summary>Total liquidity available at the current tick.</summary>
    public UInt256 TotalLiquidity { get; set; }

    /// <summary>Serialized size: 32 + 4 + 32 = 68 bytes.</summary>
    public const int SerializedSize = 32 + 4 + 32;

    /// <summary>Serialize to byte array.</summary>
    public readonly byte[] Serialize()
    {
        var buffer = new byte[SerializedSize];
        SqrtPriceX96.WriteTo(buffer.AsSpan(0, 32));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(32, 4), CurrentTick);
        TotalLiquidity.WriteTo(buffer.AsSpan(36, 32));
        return buffer;
    }

    /// <summary>Deserialize from byte span.</summary>
    public static ConcentratedPoolState Deserialize(ReadOnlySpan<byte> data)
    {
        return new ConcentratedPoolState
        {
            SqrtPriceX96 = new UInt256(data[..32]),
            CurrentTick = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[32..36]),
            TotalLiquidity = new UInt256(data[36..68]),
        };
    }
}
