using Basalt.Core;

namespace Basalt.Execution;

/// <summary>
/// Computes the EIP-1559 base fee for a new block based on the parent block's gas usage.
/// Target gas = parentGasLimit / ElasticityMultiplier (default 50%).
/// Maximum adjustment per block: 1/BaseFeeChangeDenominator (default 12.5%).
/// </summary>
public static class BaseFeeCalculator
{
    /// <summary>
    /// Calculate the base fee for the next block.
    /// </summary>
    public static UInt256 Calculate(
        UInt256 parentBaseFee,
        ulong parentGasUsed,
        ulong parentGasLimit,
        ChainParameters chainParams)
    {
        // Genesis or first block: use initial base fee
        if (parentBaseFee.IsZero)
            return chainParams.InitialBaseFee;

        var targetGas = parentGasLimit / chainParams.ElasticityMultiplier;
        if (targetGas == 0)
            return parentBaseFee;

        if (parentGasUsed == targetGas)
            return parentBaseFee;

        if (parentGasUsed > targetGas)
        {
            // Block used more gas than target — increase base fee
            var gasUsedDelta = new UInt256(parentGasUsed - targetGas);
            var adjustment = parentBaseFee * gasUsedDelta / new UInt256(targetGas) / new UInt256(chainParams.BaseFeeChangeDenominator);

            // Minimum increase of 1 to ensure convergence
            if (adjustment.IsZero)
                adjustment = UInt256.One;

            return parentBaseFee + adjustment;
        }
        else
        {
            // Block used less gas than target — decrease base fee
            var gasUsedDelta = new UInt256(targetGas - parentGasUsed);
            var adjustment = parentBaseFee * gasUsedDelta / new UInt256(targetGas) / new UInt256(chainParams.BaseFeeChangeDenominator);

            // Floor at zero
            if (adjustment >= parentBaseFee)
                return UInt256.Zero;

            return parentBaseFee - adjustment;
        }
    }
}
