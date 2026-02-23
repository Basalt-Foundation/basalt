using Basalt.Core;

namespace Basalt.Execution.Dex;

/// <summary>
/// Result of a batch auction settlement for a single trading pair.
/// Contains the uniform clearing price and the fill details for each participant.
/// </summary>
public sealed class BatchResult
{
    /// <summary>The pool ID this settlement applies to.</summary>
    public ulong PoolId { get; init; }

    /// <summary>
    /// The uniform clearing price at which all fills execute.
    /// Expressed as token1-per-token0 scaled by 2^64.
    /// Zero if no settlement was possible.
    /// </summary>
    public UInt256 ClearingPrice { get; init; }

    /// <summary>Total volume of token0 traded.</summary>
    public UInt256 TotalVolume0 { get; init; }

    /// <summary>Total volume of token1 traded.</summary>
    public UInt256 TotalVolume1 { get; init; }

    /// <summary>Volume routed through the AMM (residual after peer-to-peer matching).</summary>
    public UInt256 AmmVolume { get; init; }

    /// <summary>Individual fill records for each intent and order participant.</summary>
    public List<FillRecord> Fills { get; init; } = [];

    /// <summary>Updated AMM reserves after settlement.</summary>
    public PoolReserves UpdatedReserves { get; init; }
}

/// <summary>
/// A fill record for a single participant in a batch settlement.
/// Records how much of an intent or order was filled at the clearing price.
/// </summary>
public readonly struct FillRecord
{
    /// <summary>Address of the participant.</summary>
    public Address Participant { get; init; }

    /// <summary>Amount of input tokens consumed.</summary>
    public UInt256 AmountIn { get; init; }

    /// <summary>Amount of output tokens received.</summary>
    public UInt256 AmountOut { get; init; }

    /// <summary>Whether this fill is from a limit order (vs. swap intent).</summary>
    public bool IsLimitOrder { get; init; }

    /// <summary>Order ID if this is a limit order fill.</summary>
    public ulong OrderId { get; init; }

    /// <summary>Transaction hash if this is a swap intent fill.</summary>
    public Hash256 TxHash { get; init; }
}
