namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Staking pool contract — pool delegated stakes and distribute rewards.
/// Type ID: 0x0104
/// </summary>
[BasaltContract]
public partial class StakingPool
{
    private readonly StorageValue<ulong> _nextPoolId;
    private readonly StorageMap<string, string> _poolOperators;      // poolId -> operator hex
    private readonly StorageMap<string, ulong> _poolTotalStake;      // poolId -> total
    private readonly StorageMap<string, ulong> _poolTotalRewards;    // poolId -> accumulated rewards
    private readonly StorageMap<string, ulong> _delegations;         // "poolId:delegator" -> amount
    private readonly StorageMap<string, ulong> _claimedRewards;      // "poolId:delegator" -> claimed

    public StakingPool()
    {
        _nextPoolId = new StorageValue<ulong>("sp_next");
        _poolOperators = new StorageMap<string, string>("sp_ops");
        _poolTotalStake = new StorageMap<string, ulong>("sp_total");
        _poolTotalRewards = new StorageMap<string, ulong>("sp_rewards");
        _delegations = new StorageMap<string, ulong>("sp_del");
        _claimedRewards = new StorageMap<string, ulong>("sp_claimed");
    }

    /// <summary>
    /// Create a new staking pool. Returns the pool ID.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreatePool()
    {
        var id = _nextPoolId.Get();
        _nextPoolId.Set(id + 1);

        _poolOperators.Set(id.ToString(), Convert.ToHexString(Context.Caller));

        Context.Emit(new PoolCreatedEvent
        {
            PoolId = id,
            Operator = Context.Caller,
        });

        return id;
    }

    /// <summary>
    /// Delegate stake to a pool. Send native tokens with the transaction.
    /// </summary>
    [BasaltEntrypoint]
    public void Delegate(ulong poolId)
    {
        Context.Require(Context.TxValue > 0, "POOL: must send value");
        var key = poolId.ToString();
        Context.Require(!string.IsNullOrEmpty(_poolOperators.Get(key)), "POOL: not found");

        var delegatorKey = key + ":" + Convert.ToHexString(Context.Caller);
        var current = _delegations.Get(delegatorKey);
        _delegations.Set(delegatorKey, current + Context.TxValue);

        var total = _poolTotalStake.Get(key);
        _poolTotalStake.Set(key, total + Context.TxValue);

        Context.Emit(new DelegatedEvent
        {
            PoolId = poolId,
            Delegator = Context.Caller,
            Amount = Context.TxValue,
        });
    }

    /// <summary>
    /// Undelegate stake from a pool.
    /// </summary>
    [BasaltEntrypoint]
    public void Undelegate(ulong poolId, ulong amount)
    {
        Context.Require(amount > 0, "POOL: amount must be > 0");
        var key = poolId.ToString();
        var delegatorKey = key + ":" + Convert.ToHexString(Context.Caller);

        var current = _delegations.Get(delegatorKey);
        Context.Require(current >= amount, "POOL: insufficient delegation");

        _delegations.Set(delegatorKey, current - amount);
        var total = _poolTotalStake.Get(key);
        _poolTotalStake.Set(key, total - amount);

        Context.TransferNative(Context.Caller, amount);

        Context.Emit(new UndelegatedEvent
        {
            PoolId = poolId,
            Delegator = Context.Caller,
            Amount = amount,
        });
    }

    /// <summary>
    /// Add rewards to a pool (called by the operator or the system).
    /// </summary>
    [BasaltEntrypoint]
    public void AddRewards(ulong poolId)
    {
        Context.Require(Context.TxValue > 0, "POOL: must send value");
        var key = poolId.ToString();
        Context.Require(!string.IsNullOrEmpty(_poolOperators.Get(key)), "POOL: not found");

        var rewards = _poolTotalRewards.Get(key);
        _poolTotalRewards.Set(key, rewards + Context.TxValue);
    }

    /// <summary>
    /// Claim proportional rewards from a pool.
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimRewards(ulong poolId)
    {
        var key = poolId.ToString();
        var delegatorKey = key + ":" + Convert.ToHexString(Context.Caller);

        var delegation = _delegations.Get(delegatorKey);
        Context.Require(delegation > 0, "POOL: no delegation");

        var totalStake = _poolTotalStake.Get(key);
        var totalRewards = _poolTotalRewards.Get(key);
        var claimed = _claimedRewards.Get(delegatorKey);

        // Pro-rata: (delegation / totalStake) * totalRewards - alreadyClaimed
        var entitled = totalStake > 0 ? delegation * totalRewards / totalStake : 0UL;
        var claimable = entitled > claimed ? entitled - claimed : 0UL;

        Context.Require(claimable > 0, "POOL: no rewards to claim");

        _claimedRewards.Set(delegatorKey, claimed + claimable);
        Context.TransferNative(Context.Caller, claimable);

        Context.Emit(new RewardsClaimedEvent
        {
            PoolId = poolId,
            Delegator = Context.Caller,
            Amount = claimable,
        });
    }

    [BasaltView]
    public ulong GetPoolStake(ulong poolId)
    {
        return _poolTotalStake.Get(poolId.ToString());
    }

    [BasaltView]
    public ulong GetDelegation(ulong poolId, byte[] delegator)
    {
        return _delegations.Get(poolId.ToString() + ":" + Convert.ToHexString(delegator));
    }

    [BasaltView]
    public ulong GetPoolRewards(ulong poolId)
    {
        return _poolTotalRewards.Get(poolId.ToString());
    }
}

[BasaltEvent]
public class PoolCreatedEvent
{
    [Indexed] public ulong PoolId { get; set; }
    [Indexed] public byte[] Operator { get; set; } = null!;
}

[BasaltEvent]
public class DelegatedEvent
{
    [Indexed] public ulong PoolId { get; set; }
    [Indexed] public byte[] Delegator { get; set; } = null!;
    public ulong Amount { get; set; }
}

[BasaltEvent]
public class UndelegatedEvent
{
    [Indexed] public ulong PoolId { get; set; }
    [Indexed] public byte[] Delegator { get; set; } = null!;
    public ulong Amount { get; set; }
}

[BasaltEvent]
public class RewardsClaimedEvent
{
    [Indexed] public ulong PoolId { get; set; }
    [Indexed] public byte[] Delegator { get; set; } = null!;
    public ulong Amount { get; set; }
}
