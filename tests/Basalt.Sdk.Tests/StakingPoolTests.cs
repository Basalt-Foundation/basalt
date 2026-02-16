using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class StakingPoolTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly StakingPool _pool;
    private readonly byte[] _operator;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    public StakingPoolTests()
    {
        _pool = new StakingPool();
        _operator = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);

        // Wire up native transfer handler (no-op for testing)
        Context.NativeTransferHandler = (to, amount) => { };
    }

    [Fact]
    public void CreatePool_Returns_Id()
    {
        _host.SetCaller(_operator);
        var id = _host.Call(() => _pool.CreatePool());

        id.Should().Be(0);
    }

    [Fact]
    public void CreatePool_Increments_Id()
    {
        _host.SetCaller(_operator);
        var id0 = _host.Call(() => _pool.CreatePool());
        var id1 = _host.Call(() => _pool.CreatePool());

        id0.Should().Be(0);
        id1.Should().Be(1);
    }

    [Fact]
    public void CreatePool_Initial_Stake_Is_Zero()
    {
        _host.SetCaller(_operator);
        var id = _host.Call(() => _pool.CreatePool());

        _host.Call(() => _pool.GetPoolStake(id)).Should().Be(0);
    }

    [Fact]
    public void CreatePool_Emits_PoolCreatedEvent()
    {
        _host.SetCaller(_operator);
        _host.ClearEvents();
        _host.Call(() => _pool.CreatePool());

        var events = _host.GetEvents<PoolCreatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].PoolId.Should().Be(0);
        events[0].Operator.Should().BeEquivalentTo(_operator);
    }

    [Fact]
    public void Delegate_Increases_PoolStake_And_Delegation()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _pool.Delegate(poolId));

        _host.Call(() => _pool.GetPoolStake(poolId)).Should().Be(5000);
        _host.Call(() => _pool.GetDelegation(poolId, _alice)).Should().Be(5000);
    }

    [Fact]
    public void Delegate_Multiple_Times_Accumulates()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 3000;
        _host.Call(() => _pool.Delegate(poolId));

        Context.TxValue = 2000;
        _host.Call(() => _pool.Delegate(poolId));

        _host.Call(() => _pool.GetPoolStake(poolId)).Should().Be(5000);
        _host.Call(() => _pool.GetDelegation(poolId, _alice)).Should().Be(5000);
    }

    [Fact]
    public void Delegate_Multiple_Delegators()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 3000;
        _host.Call(() => _pool.Delegate(poolId));

        _host.SetCaller(_bob);
        Context.TxValue = 7000;
        _host.Call(() => _pool.Delegate(poolId));

        _host.Call(() => _pool.GetPoolStake(poolId)).Should().Be(10000);
        _host.Call(() => _pool.GetDelegation(poolId, _alice)).Should().Be(3000);
        _host.Call(() => _pool.GetDelegation(poolId, _bob)).Should().Be(7000);
    }

    [Fact]
    public void Delegate_With_Zero_Value_Fails()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _pool.Delegate(poolId));
        msg.Should().Contain("must send value");
    }

    [Fact]
    public void Delegate_To_Nonexistent_Pool_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 5000;

        var msg = _host.ExpectRevert(() => _pool.Delegate(999));
        msg.Should().Contain("not found");
    }

    [Fact]
    public void Delegate_Emits_DelegatedEvent()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        _host.ClearEvents();
        Context.TxValue = 5000;
        _host.Call(() => _pool.Delegate(poolId));

        var events = _host.GetEvents<DelegatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].PoolId.Should().Be(poolId);
        events[0].Delegator.Should().BeEquivalentTo(_alice);
        events[0].Amount.Should().Be(5000);
    }

    [Fact]
    public void Undelegate_Partial_Decreases_Amounts()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _pool.Delegate(poolId));

        Context.TxValue = 0;
        _host.Call(() => _pool.Undelegate(poolId, 2000));

        _host.Call(() => _pool.GetPoolStake(poolId)).Should().Be(3000);
        _host.Call(() => _pool.GetDelegation(poolId, _alice)).Should().Be(3000);
    }

    [Fact]
    public void Undelegate_Full_Amount()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _pool.Delegate(poolId));

        Context.TxValue = 0;
        _host.Call(() => _pool.Undelegate(poolId, 5000));

        _host.Call(() => _pool.GetPoolStake(poolId)).Should().Be(0);
        _host.Call(() => _pool.GetDelegation(poolId, _alice)).Should().Be(0);
    }

    [Fact]
    public void Undelegate_Insufficient_Fails()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 1000;
        _host.Call(() => _pool.Delegate(poolId));

        Context.TxValue = 0;
        var msg = _host.ExpectRevert(() => _pool.Undelegate(poolId, 2000));
        msg.Should().Contain("insufficient delegation");
    }

    [Fact]
    public void Undelegate_Zero_Amount_Fails()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _pool.Delegate(poolId));

        Context.TxValue = 0;
        var msg = _host.ExpectRevert(() => _pool.Undelegate(poolId, 0));
        msg.Should().Contain("amount must be > 0");
    }

    [Fact]
    public void Undelegate_Emits_UndelegatedEvent()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _pool.Delegate(poolId));

        _host.ClearEvents();
        Context.TxValue = 0;
        _host.Call(() => _pool.Undelegate(poolId, 2000));

        var events = _host.GetEvents<UndelegatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].PoolId.Should().Be(poolId);
        events[0].Delegator.Should().BeEquivalentTo(_alice);
        events[0].Amount.Should().Be(2000);
    }

    [Fact]
    public void AddRewards_Increases_PoolRewards()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        Context.TxValue = 10000;
        _host.Call(() => _pool.AddRewards(poolId));

        _host.Call(() => _pool.GetPoolRewards(poolId)).Should().Be(10000);
    }

    [Fact]
    public void AddRewards_Accumulates()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        Context.TxValue = 5000;
        _host.Call(() => _pool.AddRewards(poolId));

        Context.TxValue = 3000;
        _host.Call(() => _pool.AddRewards(poolId));

        _host.Call(() => _pool.GetPoolRewards(poolId)).Should().Be(8000);
    }

    [Fact]
    public void AddRewards_With_Zero_Value_Fails()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        Context.TxValue = 0;
        var msg = _host.ExpectRevert(() => _pool.AddRewards(poolId));
        msg.Should().Contain("must send value");
    }

    [Fact]
    public void AddRewards_To_Nonexistent_Pool_Fails()
    {
        _host.SetCaller(_operator);
        Context.TxValue = 5000;

        var msg = _host.ExpectRevert(() => _pool.AddRewards(999));
        msg.Should().Contain("not found");
    }

    [Fact]
    public void ClaimRewards_Single_Delegator_Gets_All()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        // Alice delegates 5000
        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _pool.Delegate(poolId));

        // Add 10000 in rewards
        _host.SetCaller(_operator);
        Context.TxValue = 10000;
        _host.Call(() => _pool.AddRewards(poolId));

        // Alice claims — she has 100% of the pool
        _host.SetCaller(_alice);
        Context.TxValue = 0;
        _host.Call(() => _pool.ClaimRewards(poolId));

        var events = _host.GetEvents<RewardsClaimedEvent>().ToList();
        events.Should().Contain(e => e.PoolId == poolId && e.Amount == 10000);
    }

    [Fact]
    public void ClaimRewards_Proportional_Distribution()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        // Alice delegates 3000, Bob delegates 7000
        _host.SetCaller(_alice);
        Context.TxValue = 3000;
        _host.Call(() => _pool.Delegate(poolId));

        _host.SetCaller(_bob);
        Context.TxValue = 7000;
        _host.Call(() => _pool.Delegate(poolId));

        // Add 10000 rewards
        _host.SetCaller(_operator);
        Context.TxValue = 10000;
        _host.Call(() => _pool.AddRewards(poolId));

        // Alice claims: 3000/10000 * 10000 = 3000
        _host.SetCaller(_alice);
        _host.ClearEvents();
        Context.TxValue = 0;
        _host.Call(() => _pool.ClaimRewards(poolId));

        var aliceEvents = _host.GetEvents<RewardsClaimedEvent>().ToList();
        aliceEvents.Should().HaveCount(1);
        aliceEvents[0].Amount.Should().Be(3000);

        // Bob claims: 7000/10000 * 10000 = 7000
        _host.SetCaller(_bob);
        _host.ClearEvents();
        Context.TxValue = 0;
        _host.Call(() => _pool.ClaimRewards(poolId));

        var bobEvents = _host.GetEvents<RewardsClaimedEvent>().ToList();
        bobEvents.Should().HaveCount(1);
        bobEvents[0].Amount.Should().Be(7000);
    }

    [Fact]
    public void ClaimRewards_No_Delegation_Fails()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _pool.ClaimRewards(poolId));
        msg.Should().Contain("no delegation");
    }

    [Fact]
    public void ClaimRewards_No_Rewards_Available_Fails()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _pool.Delegate(poolId));

        // No rewards added, try to claim
        Context.TxValue = 0;
        var msg = _host.ExpectRevert(() => _pool.ClaimRewards(poolId));
        msg.Should().Contain("no rewards to claim");
    }

    [Fact]
    public void ClaimRewards_Cannot_Double_Claim()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _pool.Delegate(poolId));

        _host.SetCaller(_operator);
        Context.TxValue = 10000;
        _host.Call(() => _pool.AddRewards(poolId));

        // First claim succeeds
        _host.SetCaller(_alice);
        Context.TxValue = 0;
        _host.Call(() => _pool.ClaimRewards(poolId));

        // Second claim fails — already claimed all
        var msg = _host.ExpectRevert(() => _pool.ClaimRewards(poolId));
        msg.Should().Contain("no rewards to claim");
    }

    [Fact]
    public void GetPoolRewards_Initially_Zero()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.Call(() => _pool.GetPoolRewards(poolId)).Should().Be(0);
    }

    [Fact]
    public void GetDelegation_For_NonDelegator_Is_Zero()
    {
        _host.SetCaller(_operator);
        var poolId = _host.Call(() => _pool.CreatePool());

        _host.Call(() => _pool.GetDelegation(poolId, _alice)).Should().Be(0);
    }

    public void Dispose() => _host.Dispose();
}
