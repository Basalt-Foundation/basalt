using Basalt.Consensus.Staking;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Consensus;

/// <summary>
/// Detects epoch boundaries and rebuilds the ValidatorSet from StakingState.
/// An epoch transition occurs every <see cref="ChainParameters.EpochLength"/> blocks.
/// At each transition, the top N validators by stake are selected and assigned
/// deterministic indices (sorted by address ascending).
///
/// Also tracks per-block commit participation via voter bitmaps and applies
/// deterministic inactivity slashing at epoch boundaries.
/// </summary>
public sealed class EpochManager
{
    private readonly ChainParameters _chainParams;
    private readonly StakingState _stakingState;
    private readonly IBlsSigner _blsSigner;
    private readonly SlashingEngine? _slashingEngine;
    private readonly ILogger _logger;
    private ulong _currentEpoch;
    private ValidatorSet _currentSet;

    /// <summary>
    /// Per-block commit voter bitmaps within the current epoch.
    /// Key = block number, Value = bitmap where bit i = validator at index i committed.
    /// Guarded by <see cref="_blockSignersLock"/> for thread safety.
    /// </summary>
    private readonly Dictionary<ulong, ulong> _blockSigners = new();
    private readonly object _blockSignersLock = new();

    /// <summary>
    /// Fired when an epoch transition occurs. Provides the new epoch number and the rebuilt ValidatorSet.
    /// </summary>
    public event Action<ulong, ValidatorSet>? OnEpochTransition;

    public EpochManager(ChainParameters chainParams, StakingState stakingState,
        ValidatorSet initialSet, IBlsSigner? blsSigner = null,
        SlashingEngine? slashingEngine = null, ILogger? logger = null)
    {
        _chainParams = chainParams;
        _stakingState = stakingState;
        _currentSet = initialSet;
        _blsSigner = blsSigner ?? new BlsSigner();
        _slashingEngine = slashingEngine;
        _logger = logger ?? NullLogger.Instance;
        _currentEpoch = 0;
    }

    /// <summary>
    /// Initialize epoch state from the current chain height and replay persisted commit bitmaps
    /// for blocks in the current epoch. This ensures deterministic slashing after node restarts.
    /// </summary>
    /// <param name="chainHeight">Current chain tip block number.</param>
    /// <param name="bitmapLoader">Function to load a persisted commit bitmap by block number (returns null if missing).</param>
    public void SeedFromChainHeight(ulong chainHeight, Func<ulong, ulong?> bitmapLoader)
    {
        _currentEpoch = ComputeEpoch(chainHeight, _chainParams.EpochLength);

        if (_chainParams.EpochLength == 0)
            return;

        // Compute the first block of the current (incomplete) epoch
        var epochStart = _currentEpoch * _chainParams.EpochLength + 1;

        // Replay bitmaps for blocks in the current epoch window
        lock (_blockSignersLock)
        {
            for (ulong b = epochStart; b <= chainHeight; b++)
            {
                var bitmap = bitmapLoader(b);
                if (bitmap.HasValue)
                    _blockSigners[b] = bitmap.Value;
            }
        }

        _logger.LogInformation(
            "EpochManager seeded: epoch={Epoch}, replayed {Count} bitmaps (blocks {Start}..{End})",
            _currentEpoch, _blockSigners.Count, epochStart, chainHeight);
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
    /// Record which validators signed the commit phase for a given block.
    /// Called after each block finalization with the commit voter bitmap.
    /// All nodes record the same bitmap (from the same finalized QC), ensuring determinism.
    /// M-05: Only records bitmaps for blocks in the current epoch to prevent stale data.
    /// </summary>
    public void RecordBlockSigners(ulong blockNumber, ulong commitBitmap)
    {
        // Guard: only track bitmaps for the current epoch
        if (_chainParams.EpochLength > 0)
        {
            var blockEpoch = ComputeEpoch(blockNumber, _chainParams.EpochLength);
            if (blockEpoch != _currentEpoch && blockEpoch != _currentEpoch + 1)
                return; // Stale or too-far-future block
        }

        lock (_blockSignersLock)
            _blockSigners[blockNumber] = commitBitmap;
    }

    /// <summary>
    /// Called after each block is finalized. If the block triggers an epoch transition,
    /// applies deterministic inactivity slashing, rebuilds the ValidatorSet, and fires OnEpochTransition.
    /// Returns the new ValidatorSet if a transition occurred, null otherwise.
    /// </summary>
    public ValidatorSet? OnBlockFinalized(ulong blockNumber)
    {
        if (!IsEpochBoundary(blockNumber))
            return null;

        var newEpoch = ComputeEpoch(blockNumber, _chainParams.EpochLength);
        if (newEpoch <= _currentEpoch)
            return null;

        // Snapshot and clear bitmaps under lock, then slash outside lock
        Dictionary<ulong, ulong> bitmapSnapshot;
        lock (_blockSignersLock)
        {
            bitmapSnapshot = new Dictionary<ulong, ulong>(_blockSigners);
            _blockSigners.Clear();
        }

        // Apply deterministic inactivity slashing BEFORE rebuilding the validator set.
        // This ensures the new set reflects any stake reductions from the completed epoch.
        if (_slashingEngine != null && bitmapSnapshot.Count > 0)
            SlashInactiveValidators(blockNumber, bitmapSnapshot);

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
        // Cap at MaxValidatorSetSize — commit voter bitmap is a ulong, so indices >= 64 cannot be represented
        var configuredSize = (int)_chainParams.ValidatorSetSize;
        var maxSize = Math.Min(configuredSize, (int)ChainParameters.MaxValidatorSetSize);
        if (configuredSize > (int)ChainParameters.MaxValidatorSetSize)
            _logger.LogWarning("ValidatorSetSize {Configured} exceeds bitmap limit of {Max}; effective set size capped",
                configuredSize, ChainParameters.MaxValidatorSetSize);
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

    /// <summary>
    /// Deterministic inactivity slashing at epoch boundary.
    /// Counts how many blocks each validator signed (committed) during the epoch.
    /// Validators below the participation threshold are slashed once for the entire epoch.
    /// Since all nodes process the same finalized blocks with the same bitmaps,
    /// this produces identical results on every node.
    /// </summary>
    private void SlashInactiveValidators(ulong epochEndBlock, Dictionary<ulong, ulong> bitmapSnapshot)
    {
        if (_slashingEngine == null || _currentSet.Count == 0)
            return;

        var totalBlocks = (ulong)bitmapSnapshot.Count;
        if (totalBlocks == 0)
            return;

        // LOW-01: When InactivityThresholdPercent=0, threshold would be 0 blocks,
        // causing ALL validators to be slashed. Skip inactivity slashing entirely.
        if (_chainParams.InactivityThresholdPercent == 0)
            return;

        // Clamp to [0, 100] to prevent overflow or slashing-all with invalid values
        var percent = Math.Min(_chainParams.InactivityThresholdPercent, 100u);
        // Ceiling division: validators must sign >= InactivityThresholdPercent of blocks
        var threshold = (totalBlocks * percent + 99) / 100;

        // Count signed blocks per validator index
        var signedCounts = new ulong[_currentSet.Count];
        foreach (var (_, bitmap) in bitmapSnapshot)
        {
            for (int i = 0; i < _currentSet.Count && i < 64; i++)
            {
                if ((bitmap & (1UL << i)) != 0)
                    signedCounts[i]++;
            }
        }

        // Slash validators below threshold
        var epochStartBlock = epochEndBlock >= _chainParams.EpochLength
            ? epochEndBlock - _chainParams.EpochLength + 1
            : 1;

        foreach (var validator in _currentSet.Validators)
        {
            // Validators beyond bitmap range cannot be tracked — skip to avoid false slashing
            if (validator.Index >= 64)
                continue;

            var signed = validator.Index < signedCounts.Length ? signedCounts[validator.Index] : 0UL;
            if (signed < threshold)
            {
                _slashingEngine.SlashInactivity(validator.Address, epochStartBlock, epochEndBlock);
                _logger.LogWarning(
                    "Epoch {Epoch}: slashed validator {Address} for inactivity ({Signed}/{Total} blocks, threshold: {Threshold})",
                    _currentEpoch + 1, validator.Address, signed, totalBlocks, threshold);
            }
        }
    }
}
