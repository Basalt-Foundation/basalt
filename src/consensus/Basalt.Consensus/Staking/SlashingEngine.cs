using Basalt.Core;
using Microsoft.Extensions.Logging;

namespace Basalt.Consensus.Staking;

/// <summary>
/// Slashing engine for penalizing validator misbehavior.
///
/// Slashing rules:
/// - Double-sign (equivocation): 100% of stake
/// - Extended inactivity (>1 epoch): 5% of stake
/// - Invalid block proposal: 1% of stake
/// </summary>
public sealed class SlashingEngine
{
    private readonly StakingState _stakingState;
    private readonly ILogger<SlashingEngine> _logger;
    private readonly List<SlashingEvent> _slashingHistory = new();

    /// <summary>
    /// Double-sign penalty: 100% of stake.
    /// </summary>
    public const int DoubleSignPenaltyPercent = 100;

    /// <summary>
    /// Inactivity penalty: 5% of stake.
    /// </summary>
    public const int InactivityPenaltyPercent = 5;

    /// <summary>
    /// Invalid block penalty: 1% of stake.
    /// </summary>
    public const int InvalidBlockPenaltyPercent = 1;

    public SlashingEngine(StakingState stakingState, ILogger<SlashingEngine> logger)
    {
        _stakingState = stakingState;
        _logger = logger;
    }

    /// <summary>
    /// All recorded slashing events.
    /// </summary>
    public IReadOnlyList<SlashingEvent> SlashingHistory => _slashingHistory;

    /// <summary>
    /// Slash for double-signing (equivocation) — 100% of stake.
    /// This is the most severe offense.
    /// </summary>
    public SlashingResult SlashDoubleSign(Address validator, ulong blockNumber, Hash256 hash1, Hash256 hash2)
    {
        var info = _stakingState.GetStakeInfo(validator);
        if (info == null)
            return SlashingResult.Error("Validator not found");

        var penalty = info.TotalStake; // 100%
        return ApplySlash(validator, penalty, SlashingReason.DoubleSign, blockNumber,
            $"Double-sign at block {blockNumber}: {hash1.ToHexString()[..16]}.. vs {hash2.ToHexString()[..16]}..");
    }

    /// <summary>
    /// Slash for extended inactivity — 5% of stake.
    /// </summary>
    public SlashingResult SlashInactivity(Address validator, ulong fromBlock, ulong toBlock)
    {
        var info = _stakingState.GetStakeInfo(validator);
        if (info == null)
            return SlashingResult.Error("Validator not found");

        var penalty = info.TotalStake * new UInt256(InactivityPenaltyPercent) / new UInt256(100);
        return ApplySlash(validator, penalty, SlashingReason.Inactivity, toBlock,
            $"Inactive from block {fromBlock} to {toBlock}");
    }

    /// <summary>
    /// Slash for proposing an invalid block — 1% of stake.
    /// </summary>
    public SlashingResult SlashInvalidBlock(Address validator, ulong blockNumber, string reason)
    {
        var info = _stakingState.GetStakeInfo(validator);
        if (info == null)
            return SlashingResult.Error("Validator not found");

        var penalty = info.TotalStake * new UInt256(InvalidBlockPenaltyPercent) / new UInt256(100);
        return ApplySlash(validator, penalty, SlashingReason.InvalidBlock, blockNumber,
            $"Invalid block at {blockNumber}: {reason}");
    }

    private SlashingResult ApplySlash(Address validator, UInt256 penalty, SlashingReason reason,
        ulong blockNumber, string description)
    {
        // F-CON-03: Delegate the entire read-modify-write to StakingState.ApplySlash
        // which runs atomically under the lock. This prevents race conditions where
        // concurrent slashing operations read stale StakeInfo and double-slash.
        var appliedPenalty = _stakingState.ApplySlash(validator, penalty);
        if (appliedPenalty == null)
            return SlashingResult.Error("Validator not found");

        var evt = new SlashingEvent
        {
            Validator = validator,
            Reason = reason,
            Penalty = appliedPenalty.Value,
            BlockNumber = blockNumber,
            Description = description,
            Timestamp = DateTimeOffset.UtcNow,
        };

        _slashingHistory.Add(evt);

        var remainingStake = _stakingState.GetStakeInfo(validator)?.TotalStake ?? UInt256.Zero;
        _logger.LogWarning(
            "Slashed validator {Validator} for {Reason}: {Penalty} tokens. Remaining: {Remaining}",
            validator, reason, appliedPenalty.Value, remainingStake);

        return SlashingResult.Success(appliedPenalty.Value);
    }
}

/// <summary>
/// Reasons for slashing.
/// </summary>
public enum SlashingReason
{
    DoubleSign,
    Inactivity,
    InvalidBlock,
}

/// <summary>
/// Record of a slashing event.
/// </summary>
public sealed class SlashingEvent
{
    public required Address Validator { get; init; }
    public required SlashingReason Reason { get; init; }
    public required UInt256 Penalty { get; init; }
    public required ulong BlockNumber { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// Result of a slashing operation.
/// </summary>
public readonly struct SlashingResult
{
    public bool IsSuccess { get; }
    public UInt256 PenaltyApplied { get; }
    public string? ErrorMessage { get; }

    private SlashingResult(bool success, UInt256 penalty, string? error)
    {
        IsSuccess = success;
        PenaltyApplied = penalty;
        ErrorMessage = error;
    }

    public static SlashingResult Success(UInt256 penalty) => new(true, penalty, null);
    public static SlashingResult Error(string message) => new(false, UInt256.Zero, message);
}
