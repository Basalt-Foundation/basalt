namespace Basalt.Core;

/// <summary>
/// Abstraction for staking state, allowing the execution layer to interact
/// with staking without a direct dependency on the consensus layer.
/// </summary>
public interface IStakingState
{
    /// <summary>Minimum stake required to register as a validator.</summary>
    UInt256 MinValidatorStake { get; }

    /// <summary>Register a new validator with an initial stake.</summary>
    StakingOperationResult RegisterValidator(Address validatorAddress, UInt256 initialStake,
        ulong blockNumber = 0, string? p2pEndpoint = null);

    /// <summary>Add stake to an existing validator.</summary>
    StakingOperationResult AddStake(Address validatorAddress, UInt256 amount);

    /// <summary>Initiate unstaking (starts unbonding period).</summary>
    StakingOperationResult InitiateUnstake(Address validatorAddress, UInt256 amount, ulong currentBlock);

    /// <summary>Get the self-stake of a validator. Returns null if not registered.</summary>
    UInt256? GetSelfStake(Address validatorAddress);
}

/// <summary>
/// Result of a staking operation exposed via the core interface.
/// </summary>
public readonly struct StakingOperationResult
{
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }

    private StakingOperationResult(bool success, string? error)
    {
        IsSuccess = success;
        ErrorMessage = error;
    }

    public static StakingOperationResult Ok() => new(true, null);
    public static StakingOperationResult Error(string message) => new(false, message);
}
