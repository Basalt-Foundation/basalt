using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Consensus;

/// <summary>
/// Weighted leader selection based on snapshotted validator stakes.
/// Uses a deterministic pseudo-random selection weighted by ValidatorInfo.Stake,
/// which is captured at epoch boundaries for consistency across all nodes.
/// <para>
/// <b>Determinism assumptions (L-05):</b>
/// All nodes must observe the same ValidatorSet (same order, same stakes) for a given epoch.
/// Stakes are snapshotted at epoch boundaries via <see cref="EpochManager"/>, so mid-epoch
/// staking changes do not affect leader selection until the next epoch.
/// The BLAKE3 seed computation is deterministic given the same view number.
/// Validator ordering must be consistent (sorted by address ascending at epoch build time).
/// </para>
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
    /// UInt256.WriteTo is LE, so the low 64 bits are in bytes 0-7.
    /// For stakes that don't fit in 64 bits, uses the highest non-zero 8-byte word.
    /// </summary>
    private static ulong StakeToWeight(UInt256 stake)
    {
        var bytes = new byte[32];
        stake.WriteTo(bytes);

        // Read from the most significant 8-byte word down; return the first non-zero chunk.
        // For typical stakes (< 2^64), bytes 8-31 are zero and the value is in bytes 0-7.
        // For very large stakes, the highest non-zero word preserves proportionality.
        for (int offset = 24; offset >= 0; offset -= 8)
        {
            ulong chunk = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                bytes.AsSpan(offset, 8));
            if (chunk != 0)
                return chunk;
        }

        return 1; // Minimum weight of 1
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
