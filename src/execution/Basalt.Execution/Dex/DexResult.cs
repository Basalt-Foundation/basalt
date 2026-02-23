using Basalt.Core;

namespace Basalt.Execution.Dex;

/// <summary>
/// Result of a DEX operation (pool creation, swap, liquidity, order).
/// Contains both success/failure status and operation-specific output data.
/// </summary>
public readonly struct DexResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; }

    /// <summary>Error code if the operation failed.</summary>
    public BasaltErrorCode ErrorCode { get; }

    /// <summary>Human-readable error message on failure, null on success.</summary>
    public string? ErrorMessage { get; }

    /// <summary>Pool ID involved in the operation (for create/swap/liquidity).</summary>
    public ulong PoolId { get; }

    /// <summary>Amount of token0 involved (output for swaps, actual deposit for liquidity).</summary>
    public UInt256 Amount0 { get; }

    /// <summary>Amount of token1 involved (output for swaps, actual deposit for liquidity).</summary>
    public UInt256 Amount1 { get; }

    /// <summary>LP shares minted or burned (for liquidity operations).</summary>
    public UInt256 Shares { get; }

    /// <summary>Order ID (for limit order operations).</summary>
    public ulong OrderId { get; }

    /// <summary>Event logs emitted during the operation.</summary>
    public List<EventLog> Logs { get; }

    private DexResult(
        bool success, BasaltErrorCode errorCode, string? errorMessage,
        ulong poolId, UInt256 amount0, UInt256 amount1, UInt256 shares,
        ulong orderId, List<EventLog>? logs)
    {
        Success = success;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        PoolId = poolId;
        Amount0 = amount0;
        Amount1 = amount1;
        Shares = shares;
        OrderId = orderId;
        Logs = logs ?? [];
    }

    /// <summary>Create a successful result for pool creation.</summary>
    public static DexResult PoolCreated(ulong poolId, List<EventLog>? logs = null) =>
        new(true, BasaltErrorCode.Success, null, poolId, UInt256.Zero, UInt256.Zero, UInt256.Zero, 0, logs);

    /// <summary>Create a successful result for a swap.</summary>
    public static DexResult SwapExecuted(ulong poolId, UInt256 amountOut, List<EventLog>? logs = null) =>
        new(true, BasaltErrorCode.Success, null, poolId, amountOut, UInt256.Zero, UInt256.Zero, 0, logs);

    /// <summary>Create a successful result for adding liquidity.</summary>
    public static DexResult LiquidityAdded(ulong poolId, UInt256 amount0, UInt256 amount1, UInt256 shares, List<EventLog>? logs = null) =>
        new(true, BasaltErrorCode.Success, null, poolId, amount0, amount1, shares, 0, logs);

    /// <summary>Create a successful result for removing liquidity.</summary>
    public static DexResult LiquidityRemoved(ulong poolId, UInt256 amount0, UInt256 amount1, UInt256 shares, List<EventLog>? logs = null) =>
        new(true, BasaltErrorCode.Success, null, poolId, amount0, amount1, shares, 0, logs);

    /// <summary>Create a successful result for placing an order.</summary>
    public static DexResult OrderPlaced(ulong orderId, List<EventLog>? logs = null) =>
        new(true, BasaltErrorCode.Success, null, 0, UInt256.Zero, UInt256.Zero, UInt256.Zero, orderId, logs);

    /// <summary>Create a successful result for canceling an order.</summary>
    public static DexResult OrderCanceled(ulong orderId, List<EventLog>? logs = null) =>
        new(true, BasaltErrorCode.Success, null, 0, UInt256.Zero, UInt256.Zero, UInt256.Zero, orderId, logs);

    /// <summary>Create a successful result for a concentrated liquidity operation with both amounts.</summary>
    public static DexResult ConcentratedResult(ulong poolId, UInt256 amount0, UInt256 amount1, List<EventLog>? logs = null) =>
        new(true, BasaltErrorCode.Success, null, poolId, amount0, amount1, UInt256.Zero, 0, logs);

    /// <summary>Create a failed result with the specified error.</summary>
    public static DexResult Error(BasaltErrorCode code, string message) =>
        new(false, code, message, 0, UInt256.Zero, UInt256.Zero, UInt256.Zero, 0, null);
}
