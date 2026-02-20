using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Consensus;

/// <summary>
/// Weighted leader selection based on snapshotted validator stakes.
/// Uses a deterministic pseudo-random selection weighted by ValidatorInfo.Stake,
/// which is captured at epoch boundaries for consistency across all nodes.
/// </summary>
public sealed class WeightedLeaderSelector
{
    private readonly ValidatorSet _validatorSet;

    public WeightedLeaderSelector(ValidatorSet validatorSet)
    {
        _validatorSet = validatorSet;
    }

    /// <summary>
    /// Select the leader for a given view using weighted random selection.
    /// Weights are read from ValidatorInfo.Stake (snapshotted at epoch boundary).
    /// </summary>
    public ValidatorInfo SelectLeader(ulong viewNumber)
    {
        var validators = _validatorSet.Validators;
        if (validators.Count == 0)
            throw new InvalidOperationException("No validators in set");

        if (validators.Count == 1)
            return validators[0];

        // Compute weights from snapshotted stakes (saturate at ulong.MaxValue to prevent wrap)
        var weights = new ulong[validators.Count];
        ulong totalWeight = 0;

        for (int i = 0; i < validators.Count; i++)
        {
            var stakeWeight = validators[i].Stake != UInt256.Zero
                ? StakeToWeight(validators[i].Stake) : 1UL;
            weights[i] = stakeWeight;
            // Saturating addition: clamp at ulong.MaxValue instead of wrapping
            totalWeight = ulong.MaxValue - totalWeight >= stakeWeight
                ? totalWeight + stakeWeight
                : ulong.MaxValue;
        }

        if (totalWeight == 0)
        {
            // Fallback to round-robin
            return _validatorSet.GetLeader(viewNumber);
        }

        // Deterministic pseudo-random based on view number
        var seed = ComputeSeed(viewNumber);
        var target = seed % totalWeight;

        ulong cumulative = 0;
        for (int i = 0; i < validators.Count; i++)
        {
            cumulative += weights[i];
            if (target < cumulative)
                return validators[i];
        }

        // Fallback (should not reach here)
        return validators[^1];
    }

    /// <summary>
    /// Convert a UInt256 stake to a proportional ulong weight.
    /// Uses the top 8 bytes to get a meaningful weight.
    /// </summary>
    private static ulong StakeToWeight(UInt256 stake)
    {
        // Normalize to a reasonable range by dividing by 10^18 (1 token worth)
        // For simplicity, convert to ulong representation
        var bytes = new byte[32];
        stake.WriteTo(bytes);

        // Take bytes 0-7 (most significant after normalization) for weight
        // If stake fits in ulong (< 2^64), use it directly
        ulong weight = 0;
        for (int i = 24; i < 32; i++)
            weight = (weight << 8) | bytes[i];

        return weight == 0 ? 1 : weight; // Minimum weight of 1
    }

    /// <summary>
    /// Compute a deterministic seed from the view number using BLAKE3.
    /// </summary>
    private static ulong ComputeSeed(ulong viewNumber)
    {
        Span<byte> input = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(input, viewNumber);
        var hash = Blake3Hasher.Hash(input);
        var hashBytes = hash.ToArray();
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hashBytes.AsSpan(0, 8));
    }
}
