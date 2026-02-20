using Basalt.Core;

namespace Basalt.Consensus.Staking;

/// <summary>
/// In-memory staking state that tracks validator stakes, delegations, and unbonding.
/// In production, this would be backed by a system contract at 0x0...0001.
/// </summary>
public sealed class StakingState : IStakingState
{
    private readonly Dictionary<Address, StakeInfo> _stakes = new();
    private readonly List<UnbondingEntry> _unbondingQueue = new();
    private readonly object _lock = new();

    /// <summary>
    /// Minimum stake required to register as a validator.
    /// </summary>
    public UInt256 MinValidatorStake { get; init; } = UInt256.Parse("100000000000000000000000");

    /// <summary>
    /// Unbonding period in blocks (~21 days at 2s blocks).
    /// </summary>
    public uint UnbondingPeriod { get; init; } = 907_200;

    /// <summary>
    /// Register a new validator with an initial stake.
    /// </summary>
    public StakingResult RegisterValidator(Address validatorAddress, UInt256 initialStake,
        ulong blockNumber = 0, string? p2pEndpoint = null)
    {
        lock (_lock)
        {
            if (_stakes.ContainsKey(validatorAddress))
                return StakingResult.Error("Validator already registered");

            if (initialStake < MinValidatorStake)
                return StakingResult.Error($"Stake too low. Minimum: {MinValidatorStake}");

            _stakes[validatorAddress] = new StakeInfo
            {
                Address = validatorAddress,
                SelfStake = initialStake,
                TotalStake = initialStake,
                IsActive = true,
                RegisteredAtBlock = blockNumber,
                P2PEndpoint = p2pEndpoint ?? "",
            };

            return StakingResult.Ok();
        }
    }

    /// <summary>
    /// Add stake to an existing validator.
    /// </summary>
    public StakingResult AddStake(Address validatorAddress, UInt256 amount)
    {
        lock (_lock)
        {
            if (!_stakes.TryGetValue(validatorAddress, out var info))
                return StakingResult.Error("Validator not registered");

            info.SelfStake += amount;
            info.TotalStake += amount;
            return StakingResult.Ok();
        }
    }

    /// <summary>
    /// Initiate unstaking (starts unbonding period).
    /// </summary>
    public StakingResult InitiateUnstake(Address validatorAddress, UInt256 amount, ulong currentBlock)
    {
        lock (_lock)
        {
            if (!_stakes.TryGetValue(validatorAddress, out var info))
                return StakingResult.Error("Validator not registered");

            if (info.SelfStake < amount)
                return StakingResult.Error("Insufficient stake");

            var remainingStake = info.SelfStake - amount;
            if (remainingStake > UInt256.Zero && remainingStake < MinValidatorStake)
                return StakingResult.Error("Remaining stake below minimum. Unstake all or keep minimum.");

            info.SelfStake -= amount;
            info.TotalStake -= amount;

            if (info.SelfStake == UInt256.Zero)
                info.IsActive = false;

            _unbondingQueue.Add(new UnbondingEntry
            {
                Validator = validatorAddress,
                Amount = amount,
                UnbondingCompleteBlock = currentBlock + UnbondingPeriod,
            });

            return StakingResult.Ok();
        }
    }

    /// <summary>
    /// Delegate stake to a validator.
    /// </summary>
    public StakingResult Delegate(Address delegator, Address validatorAddress, UInt256 amount)
    {
        lock (_lock)
        {
            if (!_stakes.TryGetValue(validatorAddress, out var info))
                return StakingResult.Error("Validator not registered");

            if (!info.IsActive)
                return StakingResult.Error("Validator is not active");

            info.DelegatedStake += amount;
            info.TotalStake += amount;

            if (!info.Delegators.ContainsKey(delegator))
                info.Delegators[delegator] = UInt256.Zero;
            info.Delegators[delegator] += amount;

            return StakingResult.Ok();
        }
    }

    /// <summary>
    /// Process completed unbonding entries (return funds to validators).
    /// </summary>
    public List<UnbondingEntry> ProcessUnbonding(ulong currentBlock)
    {
        lock (_lock)
        {
            var completed = _unbondingQueue
                .Where(e => e.UnbondingCompleteBlock <= currentBlock)
                .ToList();

            foreach (var entry in completed)
                _unbondingQueue.Remove(entry);

            return completed;
        }
    }

    /// <summary>
    /// Get stake info for a validator.
    /// </summary>
    public StakeInfo? GetStakeInfo(Address validatorAddress)
    {
        lock (_lock)
            return _stakes.TryGetValue(validatorAddress, out var info) ? info : null;
    }

    /// <summary>
    /// Get all active validators sorted by total stake (descending).
    /// </summary>
    public List<StakeInfo> GetActiveValidators()
    {
        lock (_lock)
            return _stakes.Values
                .Where(s => s.IsActive)
                .OrderByDescending(s => s.TotalStake)
                .ToList();
    }

    /// <summary>
    /// Atomically apply a slash to a validator's stake under the lock.
    /// This prevents race conditions where concurrent slashing operations
    /// read stale StakeInfo and double-slash (F-CON-03).
    /// </summary>
    /// <returns>The actual penalty applied, or null if validator not found.</returns>
    public UInt256? ApplySlash(Address validatorAddress, UInt256 penalty)
    {
        lock (_lock)
        {
            if (!_stakes.TryGetValue(validatorAddress, out var info))
                return null;

            // Cap penalty at total stake
            if (penalty > info.TotalStake)
                penalty = info.TotalStake;

            // Apply penalty to self-stake first, then delegated
            if (penalty <= info.SelfStake)
            {
                info.SelfStake -= penalty;
            }
            else
            {
                var remaining = penalty - info.SelfStake;
                info.SelfStake = UInt256.Zero;
                info.DelegatedStake = info.DelegatedStake > remaining
                    ? info.DelegatedStake - remaining
                    : UInt256.Zero;
            }

            info.TotalStake = info.SelfStake + info.DelegatedStake;

            // Deactivate if stake is too low
            if (info.TotalStake < MinValidatorStake)
                info.IsActive = false;

            return penalty;
        }
    }

    /// <summary>
    /// Get the self-stake of a validator.
    /// </summary>
    public UInt256? GetSelfStake(Address validatorAddress)
    {
        lock (_lock)
            return _stakes.TryGetValue(validatorAddress, out var info) ? info.SelfStake : null;
    }

    // Explicit IStakingState implementations that bridge StakingResult → StakingOperationResult
    StakingOperationResult IStakingState.RegisterValidator(Address validatorAddress, UInt256 initialStake,
        ulong blockNumber, string? p2pEndpoint)
    {
        var result = RegisterValidator(validatorAddress, initialStake, blockNumber, p2pEndpoint);
        return result.IsSuccess ? StakingOperationResult.Ok() : StakingOperationResult.Error(result.ErrorMessage!);
    }

    StakingOperationResult IStakingState.AddStake(Address validatorAddress, UInt256 amount)
    {
        var result = AddStake(validatorAddress, amount);
        return result.IsSuccess ? StakingOperationResult.Ok() : StakingOperationResult.Error(result.ErrorMessage!);
    }

    StakingOperationResult IStakingState.InitiateUnstake(Address validatorAddress, UInt256 amount, ulong currentBlock)
    {
        var result = InitiateUnstake(validatorAddress, amount, currentBlock);
        return result.IsSuccess ? StakingOperationResult.Ok() : StakingOperationResult.Error(result.ErrorMessage!);
    }

    /// <summary>
    /// Total staked across all validators.
    /// </summary>
    public UInt256 TotalStaked
    {
        get
        {
            lock (_lock)
            {
                var total = UInt256.Zero;
                foreach (var info in _stakes.Values)
                    total += info.TotalStake;
                return total;
            }
        }
    }
}

/// <summary>
/// Stake information for a validator.
/// </summary>
public sealed class StakeInfo
{
    public required Address Address { get; init; }
    public UInt256 SelfStake { get; set; }
    public UInt256 DelegatedStake { get; set; }
    public UInt256 TotalStake { get; set; }
    public bool IsActive { get; set; }
    public ulong RegisteredAtBlock { get; set; }
    public string P2PEndpoint { get; set; } = "";
    public Dictionary<Address, UInt256> Delegators { get; } = new();
}

/// <summary>
/// An entry in the unbonding queue.
/// </summary>
public sealed class UnbondingEntry
{
    public required Address Validator { get; init; }
    public required UInt256 Amount { get; init; }
    public required ulong UnbondingCompleteBlock { get; init; }
}

/// <summary>
/// Result of a staking operation.
/// </summary>
public readonly struct StakingResult
{
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }

    private StakingResult(bool success, string? error)
    {
        IsSuccess = success;
        ErrorMessage = error;
    }

    public static StakingResult Ok() => new(true, null);
    public static StakingResult Error(string message) => new(false, message);
}
