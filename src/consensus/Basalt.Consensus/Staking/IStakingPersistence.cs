using Basalt.Core;

namespace Basalt.Consensus.Staking;

/// <summary>
/// B1: Persistence interface for staking state.
/// Allows StakingState to be serialized/deserialized to durable storage
/// so that validator registrations survive node restarts.
/// </summary>
public interface IStakingPersistence
{
    void SaveStakes(IReadOnlyDictionary<Address, StakeInfo> stakes);
    Dictionary<Address, StakeInfo> LoadStakes();
    void SaveUnbondingQueue(IReadOnlyList<UnbondingEntry> queue);
    List<UnbondingEntry> LoadUnbondingQueue();
}
