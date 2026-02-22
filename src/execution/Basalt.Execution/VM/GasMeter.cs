using Basalt.Core;

namespace Basalt.Execution.VM;

/// <summary>
/// Gas metering for contract execution.
/// Tracks gas consumption and enforces limits.
/// </summary>
public sealed class GasMeter
{
    public ulong GasLimit { get; }
    public ulong GasUsed { get; private set; }
    public ulong GasRemaining => GasLimit - GasUsed;
    public ulong GasRefund { get; private set; }

    public GasMeter(ulong gasLimit)
    {
        GasLimit = gasLimit;
    }

    /// <summary>
    /// Consume gas. Throws OutOfGasException if insufficient.
    /// H-1: Uses overflow-safe check (amount > remaining) instead of (used + amount > limit).
    /// </summary>
    public void Consume(ulong amount)
    {
        if (amount > GasRemaining)
            throw new OutOfGasException(GasUsed, amount, GasLimit);
        GasUsed += amount;
    }

    /// <summary>
    /// Try to consume gas. Returns false if insufficient.
    /// H-1: Uses overflow-safe check.
    /// </summary>
    public bool TryConsume(ulong amount)
    {
        if (amount > GasRemaining)
            return false;
        GasUsed += amount;
        return true;
    }

    /// <summary>
    /// Add a gas refund (e.g. from storage deletion).
    /// MED-04: Uses checked arithmetic to prevent silent overflow.
    /// </summary>
    public void AddRefund(ulong amount)
    {
        checked { GasRefund += amount; }
    }

    /// <summary>
    /// Compute effective gas used after applying refunds.
    /// Refund capped at 50% of total gas used.
    /// </summary>
    public ulong EffectiveGasUsed()
    {
        var maxRefund = GasUsed / 2;
        var appliedRefund = Math.Min(GasRefund, maxRefund);
        return GasUsed - appliedRefund;
    }
}

/// <summary>
/// Thrown when a contract runs out of gas.
/// </summary>
public sealed class OutOfGasException : BasaltException
{
    public ulong GasUsed { get; }
    public ulong GasRequested { get; }
    public ulong GasLimit { get; }

    public OutOfGasException(ulong gasUsed, ulong gasRequested, ulong gasLimit)
        : base(BasaltErrorCode.OutOfGas, $"Out of gas: used={gasUsed}, requested={gasRequested}, limit={gasLimit}")
    {
        GasUsed = gasUsed;
        GasRequested = gasRequested;
        GasLimit = gasLimit;
    }
}
