using Basalt.Consensus.Staking;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;

namespace Basalt.Consensus;

/// <summary>
/// Detects epoch boundaries and rebuilds the ValidatorSet from StakingState.
/// An epoch transition occurs every <see cref="ChainParameters.EpochLength"/> blocks.
/// At each transition, the top N validators by stake are selected and assigned
/// deterministic indices (sorted by address ascending).
/// </summary>
public sealed class EpochManager
{
    private readonly ChainParameters _chainParams;
    private readonly StakingState _stakingState;
    private readonly IBlsSigner _blsSigner;
    private ulong _currentEpoch;
    private ValidatorSet _currentSet;

    /// <summary>
    /// Fired when an epoch transition occurs. Provides the new epoch number and the rebuilt ValidatorSet.
    /// </summary>
    public event Action<ulong, ValidatorSet>? OnEpochTransition;

    public EpochManager(ChainParameters chainParams, StakingState stakingState, ValidatorSet initialSet, IBlsSigner? blsSigner = null)
    {
        _chainParams = chainParams;
        _stakingState = stakingState;
        _currentSet = initialSet;
        _blsSigner = blsSigner ?? new BlsSigner();
        _currentEpoch = 0;
    }

    /// <summary>
    /// Current epoch number.
    /// </summary>
    public ulong CurrentEpoch => _currentEpoch;

    /// <summary>
    /// Current validator set.
    /// </summary>
    public ValidatorSet CurrentSet => _currentSet;

    /// <summary>
    /// Compute the epoch number for a given block number.
    /// </summary>
    public static ulong ComputeEpoch(ulong blockNumber, uint epochLength)
        => epochLength == 0 ? 0 : blockNumber / epochLength;

    /// <summary>
    /// Check if a block number is an epoch boundary.
    /// </summary>
    public bool IsEpochBoundary(ulong blockNumber)
        => blockNumber > 0 && _chainParams.EpochLength > 0 && blockNumber % _chainParams.EpochLength == 0;

    /// <summary>
    /// Called after each block is finalized. If the block triggers an epoch transition,
    /// rebuilds the ValidatorSet and fires OnEpochTransition.
    /// Returns the new ValidatorSet if a transition occurred, null otherwise.
    /// </summary>
    public ValidatorSet? OnBlockFinalized(ulong blockNumber)
    {
        if (!IsEpochBoundary(blockNumber))
            return null;

        var newEpoch = ComputeEpoch(blockNumber, _chainParams.EpochLength);
        if (newEpoch <= _currentEpoch)
            return null;

        var newSet = BuildValidatorSetFromStaking();
        newSet.TransferIdentities(_currentSet);

        _currentEpoch = newEpoch;
        _currentSet = newSet;

        OnEpochTransition?.Invoke(newEpoch, newSet);
        return newSet;
    }

    /// <summary>
    /// Build a new ValidatorSet from the current StakingState.
    /// Selects the top N active validators by stake, then sorts by address ascending
    /// for deterministic index assignment.
    /// </summary>
    public ValidatorSet BuildValidatorSetFromStaking()
    {
        var activeValidators = _stakingState.GetActiveValidators(); // sorted by TotalStake desc
        var maxSize = (int)_chainParams.ValidatorSetSize;
        var selected = activeValidators.Take(maxSize).ToList();

        // Sort by address ascending for deterministic index assignment across all nodes
        selected.Sort((a, b) => a.Address.CompareTo(b.Address));

        var validators = new List<ValidatorInfo>(selected.Count);
        for (int i = 0; i < selected.Count; i++)
        {
            var stake = selected[i];

            // Create placeholder identity — TransferIdentities will fill in real PeerIds
            var placeholderKey = new byte[32];
            stake.Address.WriteTo(placeholderKey.AsSpan(0, Address.Size));
            placeholderKey[31] |= 1; // Ensure non-zero for BLS
            var pk = Ed25519Signer.GetPublicKey(placeholderKey);

            validators.Add(new ValidatorInfo
            {
                PeerId = PeerId.FromPublicKey(pk),
                PublicKey = pk,
                BlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(placeholderKey)),
                Address = stake.Address,
                Index = i,
                Stake = stake.TotalStake,
            });
        }

        return new ValidatorSet(validators);
    }
}
