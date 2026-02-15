using Basalt.Core;
using Basalt.Consensus.Staking;
using Xunit;

namespace Basalt.Consensus.Tests;

public class StakingTests
{
    private static Address MakeAddr(byte seed) => Address.FromHexString($"0x{seed:X40}");

    [Fact]
    public void RegisterValidator_Success()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        var result = state.RegisterValidator(MakeAddr(1), new UInt256(5000));
        Assert.True(result.IsSuccess);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.NotNull(info);
        Assert.Equal(new UInt256(5000), info.TotalStake);
        Assert.True(info.IsActive);
    }

    [Fact]
    public void RegisterValidator_Fails_Below_Minimum()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        var result = state.RegisterValidator(MakeAddr(1), new UInt256(500));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void RegisterValidator_Fails_Duplicate()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));
        var result = state.RegisterValidator(MakeAddr(1), new UInt256(5000));
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void AddStake_Increases_TotalStake()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));
        state.AddStake(MakeAddr(1), new UInt256(3000));

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(new UInt256(8000), info!.TotalStake);
    }

    [Fact]
    public void InitiateUnstake_Starts_Unbonding()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000), UnbondingPeriod = 100 };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));

        var result = state.InitiateUnstake(MakeAddr(1), new UInt256(5000), currentBlock: 50);
        Assert.True(result.IsSuccess);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.False(info!.IsActive); // Fully unstaked
        Assert.Equal(UInt256.Zero, info.SelfStake);
    }

    [Fact]
    public void ProcessUnbonding_Returns_Completed_Entries()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000), UnbondingPeriod = 100 };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));
        state.InitiateUnstake(MakeAddr(1), new UInt256(5000), currentBlock: 50);

        // Before unbonding period
        var completed = state.ProcessUnbonding(100);
        Assert.Empty(completed);

        // After unbonding period
        completed = state.ProcessUnbonding(151);
        Assert.Single(completed);
        Assert.Equal(new UInt256(5000), completed[0].Amount);
    }

    [Fact]
    public void Delegate_Increases_TotalStake()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));

        var delegator = MakeAddr(2);
        var result = state.Delegate(delegator, MakeAddr(1), new UInt256(2000));
        Assert.True(result.IsSuccess);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(new UInt256(7000), info!.TotalStake);
        Assert.Equal(new UInt256(2000), info.DelegatedStake);
    }

    [Fact]
    public void GetActiveValidators_Returns_Sorted_By_Stake()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(500));
        state.RegisterValidator(MakeAddr(2), new UInt256(1000));
        state.RegisterValidator(MakeAddr(3), new UInt256(200));

        var active = state.GetActiveValidators();
        Assert.Equal(3, active.Count);
        Assert.Equal(new UInt256(1000), active[0].TotalStake);
        Assert.Equal(new UInt256(500), active[1].TotalStake);
        Assert.Equal(new UInt256(200), active[2].TotalStake);
    }

    // --- AddStake edge cases ---

    [Fact]
    public void AddStake_Unregistered_Validator_Fails()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        var result = state.AddStake(MakeAddr(99), new UInt256(500));
        Assert.False(result.IsSuccess);
        Assert.Contains("not registered", result.ErrorMessage!);
    }

    [Fact]
    public void AddStake_Multiple_Times_Accumulates()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(1000));

        state.AddStake(MakeAddr(1), new UInt256(500));
        state.AddStake(MakeAddr(1), new UInt256(300));
        state.AddStake(MakeAddr(1), new UInt256(200));

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(new UInt256(2000), info!.SelfStake);
        Assert.Equal(new UInt256(2000), info.TotalStake);
    }

    // --- InitiateUnstake edge cases ---

    [Fact]
    public void InitiateUnstake_Unregistered_Validator_Fails()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        var result = state.InitiateUnstake(MakeAddr(99), new UInt256(500), currentBlock: 1);
        Assert.False(result.IsSuccess);
        Assert.Contains("not registered", result.ErrorMessage!);
    }

    [Fact]
    public void InitiateUnstake_Insufficient_Stake_Fails()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));

        var result = state.InitiateUnstake(MakeAddr(1), new UInt256(6000), currentBlock: 1);
        Assert.False(result.IsSuccess);
        Assert.Contains("Insufficient", result.ErrorMessage!);
    }

    [Fact]
    public void InitiateUnstake_Partial_RemainingBelowMinimum_Fails()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));

        // Try to unstake so that remaining is 500, which is below minimum 1000
        var result = state.InitiateUnstake(MakeAddr(1), new UInt256(4500), currentBlock: 1);
        Assert.False(result.IsSuccess);
        Assert.Contains("minimum", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InitiateUnstake_Partial_RemainingAboveMinimum_Succeeds()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));

        var result = state.InitiateUnstake(MakeAddr(1), new UInt256(3000), currentBlock: 1);
        Assert.True(result.IsSuccess);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(new UInt256(2000), info!.SelfStake);
        Assert.True(info.IsActive); // Still above minimum
    }

    [Fact]
    public void InitiateUnstake_Full_DeactivatesValidator()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));

        state.InitiateUnstake(MakeAddr(1), new UInt256(5000), currentBlock: 1);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.False(info!.IsActive);
        Assert.Equal(UInt256.Zero, info.SelfStake);
    }

    // --- Delegation edge cases ---

    [Fact]
    public void Delegate_To_Unregistered_Validator_Fails()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        var result = state.Delegate(MakeAddr(2), MakeAddr(99), new UInt256(500));
        Assert.False(result.IsSuccess);
        Assert.Contains("not registered", result.ErrorMessage!);
    }

    [Fact]
    public void Delegate_To_Inactive_Validator_Fails()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));

        // Deactivate by full unstake
        state.InitiateUnstake(MakeAddr(1), new UInt256(5000), currentBlock: 1);

        var result = state.Delegate(MakeAddr(2), MakeAddr(1), new UInt256(500));
        Assert.False(result.IsSuccess);
        Assert.Contains("not active", result.ErrorMessage!);
    }

    [Fact]
    public void Delegate_Multiple_Delegators_Tracked_Separately()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(1000));

        state.Delegate(MakeAddr(10), MakeAddr(1), new UInt256(200));
        state.Delegate(MakeAddr(11), MakeAddr(1), new UInt256(300));
        state.Delegate(MakeAddr(12), MakeAddr(1), new UInt256(500));

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(new UInt256(1000), info!.DelegatedStake);
        Assert.Equal(new UInt256(2000), info.TotalStake);
        Assert.Equal(3, info.Delegators.Count);
        Assert.Equal(new UInt256(200), info.Delegators[MakeAddr(10)]);
        Assert.Equal(new UInt256(300), info.Delegators[MakeAddr(11)]);
        Assert.Equal(new UInt256(500), info.Delegators[MakeAddr(12)]);
    }

    [Fact]
    public void Delegate_Same_Delegator_Multiple_Times_Accumulates()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(1000));

        state.Delegate(MakeAddr(10), MakeAddr(1), new UInt256(200));
        state.Delegate(MakeAddr(10), MakeAddr(1), new UInt256(300));

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(new UInt256(500), info!.DelegatedStake);
        Assert.Equal(new UInt256(500), info.Delegators[MakeAddr(10)]);
    }

    // --- TotalStaked property ---

    [Fact]
    public void TotalStaked_Empty_IsZero()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        Assert.Equal(UInt256.Zero, state.TotalStaked);
    }

    [Fact]
    public void TotalStaked_SumsAllValidators()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(1000));
        state.RegisterValidator(MakeAddr(2), new UInt256(2000));
        state.RegisterValidator(MakeAddr(3), new UInt256(3000));

        Assert.Equal(new UInt256(6000), state.TotalStaked);
    }

    [Fact]
    public void TotalStaked_IncludesDelegations()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(1000));
        state.Delegate(MakeAddr(10), MakeAddr(1), new UInt256(500));

        Assert.Equal(new UInt256(1500), state.TotalStaked);
    }

    [Fact]
    public void TotalStaked_Decreases_After_Unstake()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(1000));
        state.RegisterValidator(MakeAddr(2), new UInt256(2000));

        Assert.Equal(new UInt256(3000), state.TotalStaked);

        state.InitiateUnstake(MakeAddr(1), new UInt256(1000), currentBlock: 1);

        Assert.Equal(new UInt256(2000), state.TotalStaked);
    }

    // --- GetStakeInfo edge cases ---

    [Fact]
    public void GetStakeInfo_Unknown_Address_ReturnsNull()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        Assert.Null(state.GetStakeInfo(MakeAddr(99)));
    }

    // --- GetActiveValidators edge cases ---

    [Fact]
    public void GetActiveValidators_Excludes_Inactive()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(1000));
        state.RegisterValidator(MakeAddr(2), new UInt256(2000));

        // Deactivate validator 1
        state.InitiateUnstake(MakeAddr(1), new UInt256(1000), currentBlock: 1);

        var active = state.GetActiveValidators();
        Assert.Single(active);
        Assert.Equal(MakeAddr(2), active[0].Address);
    }

    [Fact]
    public void GetActiveValidators_Empty_WhenNone()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        Assert.Empty(state.GetActiveValidators());
    }

    // --- ProcessUnbonding edge cases ---

    [Fact]
    public void ProcessUnbonding_ExactBoundary_Completes()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100), UnbondingPeriod = 100 };
        state.RegisterValidator(MakeAddr(1), new UInt256(1000));
        state.InitiateUnstake(MakeAddr(1), new UInt256(1000), currentBlock: 50);

        // Exactly at completion block (50 + 100 = 150)
        var completed = state.ProcessUnbonding(150);
        Assert.Single(completed);
    }

    [Fact]
    public void ProcessUnbonding_Multiple_Entries_CompletedSeparately()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100), UnbondingPeriod = 100 };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));

        // Two unstake operations at different blocks
        state.InitiateUnstake(MakeAddr(1), new UInt256(2000), currentBlock: 10);
        state.InitiateUnstake(MakeAddr(1), new UInt256(2000), currentBlock: 50);

        // Only first should complete at block 110
        var completed = state.ProcessUnbonding(110);
        Assert.Single(completed);
        Assert.Equal(new UInt256(2000), completed[0].Amount);

        // Second completes at block 150
        completed = state.ProcessUnbonding(150);
        Assert.Single(completed);
        Assert.Equal(new UInt256(2000), completed[0].Amount);
    }

    [Fact]
    public void ProcessUnbonding_Empty_Queue_ReturnsEmpty()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        var completed = state.ProcessUnbonding(1000);
        Assert.Empty(completed);
    }

    // --- RegisterValidator at exact minimum ---

    [Fact]
    public void RegisterValidator_AtExactMinimum_Succeeds()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        var result = state.RegisterValidator(MakeAddr(1), new UInt256(1000));
        Assert.True(result.IsSuccess);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.True(info!.IsActive);
    }

    // --- RegisterValidator sets correct initial state ---

    [Fact]
    public void RegisterValidator_SetsCorrectInitialState()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.NotNull(info);
        Assert.Equal(new UInt256(5000), info.SelfStake);
        Assert.Equal(UInt256.Zero, info.DelegatedStake);
        Assert.Equal(new UInt256(5000), info.TotalStake);
        Assert.True(info.IsActive);
        Assert.Empty(info.Delegators);
    }
}
