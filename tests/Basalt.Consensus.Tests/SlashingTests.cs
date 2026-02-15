using Basalt.Core;
using Basalt.Consensus.Staking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Consensus.Tests;

public class SlashingTests
{
    private static Address MakeAddr(byte seed) => Address.FromHexString($"0x{seed:X40}");

    [Fact]
    public void DoubleSign_Slashes_100_Percent()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        var hash1 = new Hash256(new byte[32]);
        var hash2Bytes = new byte[32];
        hash2Bytes[0] = 1;
        var hash2 = new Hash256(hash2Bytes);

        var result = engine.SlashDoubleSign(MakeAddr(1), blockNumber: 100, hash1, hash2);
        Assert.True(result.IsSuccess);
        Assert.Equal(new UInt256(10000), result.PenaltyApplied);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(UInt256.Zero, info!.TotalStake);
        Assert.False(info.IsActive);
    }

    [Fact]
    public void Inactivity_Slashes_5_Percent()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        var result = engine.SlashInactivity(MakeAddr(1), fromBlock: 0, toBlock: 1000);
        Assert.True(result.IsSuccess);
        Assert.Equal(new UInt256(500), result.PenaltyApplied); // 5% of 10000

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(new UInt256(9500), info!.TotalStake);
        Assert.True(info.IsActive); // Still above minimum
    }

    [Fact]
    public void InvalidBlock_Slashes_1_Percent()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        var result = engine.SlashInvalidBlock(MakeAddr(1), blockNumber: 50, "bad state root");
        Assert.True(result.IsSuccess);
        Assert.Equal(new UInt256(100), result.PenaltyApplied); // 1% of 10000

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(new UInt256(9900), info!.TotalStake);
    }

    [Fact]
    public void Slashing_Deactivates_Below_Minimum()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(1000) };
        state.RegisterValidator(MakeAddr(1), new UInt256(1500));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        // 5% of 1500 = 75, remaining = 1425, still active
        engine.SlashInactivity(MakeAddr(1), 0, 100);
        Assert.True(state.GetStakeInfo(MakeAddr(1))!.IsActive);

        // Slash invalid block repeatedly
        for (int i = 0; i < 50; i++)
            engine.SlashInvalidBlock(MakeAddr(1), (ulong)i, "bad");

        var info = state.GetStakeInfo(MakeAddr(1));
        // After many 1% slashes, should eventually go below minimum
        if (info!.TotalStake < state.MinValidatorStake)
            Assert.False(info.IsActive);
    }

    [Fact]
    public void SlashingHistory_Records_Events()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        engine.SlashInactivity(MakeAddr(1), 0, 100);
        engine.SlashInvalidBlock(MakeAddr(1), 50, "bad");

        Assert.Equal(2, engine.SlashingHistory.Count);
        Assert.Equal(SlashingReason.Inactivity, engine.SlashingHistory[0].Reason);
        Assert.Equal(SlashingReason.InvalidBlock, engine.SlashingHistory[1].Reason);
    }

    // --- Slash unknown validator ---

    [Fact]
    public void SlashDoubleSign_Unknown_Validator_Fails()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        var hash1 = new Hash256(new byte[32]);
        var hash2 = new Hash256(new byte[32]);
        var result = engine.SlashDoubleSign(MakeAddr(99), blockNumber: 1, hash1, hash2);

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.ErrorMessage!);
        Assert.Equal(UInt256.Zero, result.PenaltyApplied);
    }

    [Fact]
    public void SlashInactivity_Unknown_Validator_Fails()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        var result = engine.SlashInactivity(MakeAddr(99), fromBlock: 0, toBlock: 100);
        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.ErrorMessage!);
    }

    [Fact]
    public void SlashInvalidBlock_Unknown_Validator_Fails()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        var result = engine.SlashInvalidBlock(MakeAddr(99), blockNumber: 1, "bad");
        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.ErrorMessage!);
    }

    // --- Double-sign overflows into delegated stake ---

    [Fact]
    public void DoubleSign_Overflows_SelfStake_Into_DelegatedStake()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(3000));
        state.Delegate(MakeAddr(10), MakeAddr(1), new UInt256(7000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        // Total stake = 10000, double-sign slashes 100% = 10000
        var hash1 = new Hash256(new byte[32]);
        var hash2Bytes = new byte[32];
        hash2Bytes[0] = 1;
        var hash2 = new Hash256(hash2Bytes);

        var result = engine.SlashDoubleSign(MakeAddr(1), blockNumber: 1, hash1, hash2);
        Assert.True(result.IsSuccess);
        Assert.Equal(new UInt256(10000), result.PenaltyApplied);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(UInt256.Zero, info!.SelfStake);
        Assert.Equal(UInt256.Zero, info.DelegatedStake);
        Assert.Equal(UInt256.Zero, info.TotalStake);
        Assert.False(info.IsActive);
    }

    [Fact]
    public void Inactivity_Slash_With_Delegation_Takes_From_SelfStake_First()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(5000));
        state.Delegate(MakeAddr(10), MakeAddr(1), new UInt256(5000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        // Total = 10000, 5% = 500, should come from selfStake
        var result = engine.SlashInactivity(MakeAddr(1), fromBlock: 0, toBlock: 100);
        Assert.True(result.IsSuccess);
        Assert.Equal(new UInt256(500), result.PenaltyApplied);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(new UInt256(4500), info!.SelfStake);
        Assert.Equal(new UInt256(5000), info.DelegatedStake); // Delegated untouched
        Assert.Equal(new UInt256(9500), info.TotalStake);
    }

    [Fact]
    public void Inactivity_Slash_Overflows_Into_DelegatedStake()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(200));
        state.Delegate(MakeAddr(10), MakeAddr(1), new UInt256(9800));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        // Total = 10000, 5% = 500
        // SelfStake = 200, so 200 comes from self, 300 from delegated
        var result = engine.SlashInactivity(MakeAddr(1), fromBlock: 0, toBlock: 100);
        Assert.True(result.IsSuccess);
        Assert.Equal(new UInt256(500), result.PenaltyApplied);

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(UInt256.Zero, info!.SelfStake);
        Assert.Equal(new UInt256(9500), info.DelegatedStake);
        Assert.Equal(new UInt256(9500), info.TotalStake);
    }

    // --- Consecutive slashes ---

    [Fact]
    public void Consecutive_Inactivity_Slashes_Reduce_Stake_Correctly()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        // First slash: 5% of 10000 = 500, remaining = 9500
        engine.SlashInactivity(MakeAddr(1), 0, 100);
        Assert.Equal(new UInt256(9500), state.GetStakeInfo(MakeAddr(1))!.TotalStake);

        // Second slash: 5% of 9500 = 475, remaining = 9025
        engine.SlashInactivity(MakeAddr(1), 100, 200);
        Assert.Equal(new UInt256(9025), state.GetStakeInfo(MakeAddr(1))!.TotalStake);

        // Third slash: 5% of 9025 = 451 (integer division), remaining = 8574
        var result = engine.SlashInactivity(MakeAddr(1), 200, 300);
        Assert.True(result.IsSuccess);
        Assert.Equal(new UInt256(451), result.PenaltyApplied);
    }

    [Fact]
    public void Consecutive_InvalidBlock_Slashes_Reduce_Stake_Correctly()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        // 1% of 10000 = 100
        engine.SlashInvalidBlock(MakeAddr(1), 1, "bad");
        Assert.Equal(new UInt256(9900), state.GetStakeInfo(MakeAddr(1))!.TotalStake);

        // 1% of 9900 = 99
        engine.SlashInvalidBlock(MakeAddr(1), 2, "bad");
        Assert.Equal(new UInt256(9801), state.GetStakeInfo(MakeAddr(1))!.TotalStake);
    }

    // --- SlashingHistory details ---

    [Fact]
    public void SlashingHistory_Contains_Correct_Details()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        var hash1 = new Hash256(new byte[32]);
        var hash2Bytes = new byte[32];
        hash2Bytes[0] = 0xAB;
        var hash2 = new Hash256(hash2Bytes);

        engine.SlashDoubleSign(MakeAddr(1), blockNumber: 42, hash1, hash2);

        Assert.Single(engine.SlashingHistory);
        var evt = engine.SlashingHistory[0];
        Assert.Equal(MakeAddr(1), evt.Validator);
        Assert.Equal(SlashingReason.DoubleSign, evt.Reason);
        Assert.Equal(new UInt256(10000), evt.Penalty);
        Assert.Equal((ulong)42, evt.BlockNumber);
        Assert.Contains("Double-sign", evt.Description);
        Assert.Contains("42", evt.Description);
    }

    [Fact]
    public void SlashingHistory_InvalidBlock_Contains_Reason_String()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);
        engine.SlashInvalidBlock(MakeAddr(1), blockNumber: 7, "invalid state root");

        var evt = engine.SlashingHistory[0];
        Assert.Contains("invalid state root", evt.Description);
        Assert.Equal((ulong)7, evt.BlockNumber);
    }

    [Fact]
    public void SlashingHistory_Preserves_Chronological_Order()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(100000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        engine.SlashInvalidBlock(MakeAddr(1), blockNumber: 10, "first");
        engine.SlashInactivity(MakeAddr(1), fromBlock: 0, toBlock: 20);
        engine.SlashInvalidBlock(MakeAddr(1), blockNumber: 30, "third");

        Assert.Equal(3, engine.SlashingHistory.Count);
        Assert.Equal(SlashingReason.InvalidBlock, engine.SlashingHistory[0].Reason);
        Assert.Equal(SlashingReason.Inactivity, engine.SlashingHistory[1].Reason);
        Assert.Equal(SlashingReason.InvalidBlock, engine.SlashingHistory[2].Reason);
    }

    // --- Penalty constants ---

    [Fact]
    public void PenaltyConstants_AreCorrect()
    {
        Assert.Equal(100, SlashingEngine.DoubleSignPenaltyPercent);
        Assert.Equal(5, SlashingEngine.InactivityPenaltyPercent);
        Assert.Equal(1, SlashingEngine.InvalidBlockPenaltyPercent);
    }

    // --- Slash after double-sign (already zeroed) ---

    [Fact]
    public void Slash_After_DoubleSign_NoStakeRemaining()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        // Double-sign zeroes everything
        engine.SlashDoubleSign(MakeAddr(1), 1, new Hash256(new byte[32]), new Hash256(new byte[32]));

        // Further slash on zero stake
        var result = engine.SlashInactivity(MakeAddr(1), 0, 100);
        Assert.True(result.IsSuccess);
        Assert.Equal(UInt256.Zero, result.PenaltyApplied); // 5% of 0 = 0

        var info = state.GetStakeInfo(MakeAddr(1));
        Assert.Equal(UInt256.Zero, info!.TotalStake);
    }

    // --- Multiple validators slashed independently ---

    [Fact]
    public void Slashing_Multiple_Validators_Independently()
    {
        var state = new StakingState { MinValidatorStake = new UInt256(100) };
        state.RegisterValidator(MakeAddr(1), new UInt256(10000));
        state.RegisterValidator(MakeAddr(2), new UInt256(20000));

        var engine = new SlashingEngine(state, NullLogger<SlashingEngine>.Instance);

        engine.SlashInactivity(MakeAddr(1), 0, 100);
        engine.SlashInvalidBlock(MakeAddr(2), 50, "bad");

        // Validator 1: 5% of 10000 = 500 slashed
        Assert.Equal(new UInt256(9500), state.GetStakeInfo(MakeAddr(1))!.TotalStake);
        // Validator 2: 1% of 20000 = 200 slashed
        Assert.Equal(new UInt256(19800), state.GetStakeInfo(MakeAddr(2))!.TotalStake);

        Assert.Equal(2, engine.SlashingHistory.Count);
    }
}
