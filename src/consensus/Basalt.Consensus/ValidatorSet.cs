using Basalt.Core;
using Basalt.Network;

namespace Basalt.Consensus;

/// <summary>
/// Represents a validator in the network.
/// </summary>
public sealed class ValidatorInfo
{
    public required PeerId PeerId { get; set; }
    public required PublicKey PublicKey { get; set; }
    public required BlsPublicKey BlsPublicKey { get; set; }
    public required Address Address { get; init; }
    public UInt256 Stake { get; set; } = UInt256.Zero;
    public int Index { get; init; }
}

/// <summary>
/// The set of validators for a given epoch.
/// Provides leader selection and quorum computation.
/// </summary>
public sealed class ValidatorSet
{
    private readonly List<ValidatorInfo> _validators;
    private readonly Dictionary<PeerId, ValidatorInfo> _byPeerId;
    private readonly Dictionary<Address, ValidatorInfo> _byAddress;
    private Func<ulong, ValidatorInfo>? _leaderSelector;

    public ValidatorSet(IEnumerable<ValidatorInfo> validators)
    {
        _validators = validators.ToList();
        _byPeerId = _validators.ToDictionary(v => v.PeerId);
        _byAddress = _validators.ToDictionary(v => v.Address);
    }

    /// <summary>
    /// Set a custom leader selection strategy (e.g., stake-weighted).
    /// </summary>
    public void SetLeaderSelector(Func<ulong, ValidatorInfo> selector)
        => _leaderSelector = selector;

    /// <summary>
    /// Total number of validators.
    /// </summary>
    public int Count => _validators.Count;

    /// <summary>
    /// All validators.
    /// </summary>
    public IReadOnlyList<ValidatorInfo> Validators => _validators;

    /// <summary>
    /// Get validator by peer ID.
    /// </summary>
    public ValidatorInfo? GetByPeerId(PeerId id) =>
        _byPeerId.TryGetValue(id, out var v) ? v : null;

    /// <summary>
    /// Get validator by address.
    /// </summary>
    public ValidatorInfo? GetByAddress(Address address) =>
        _byAddress.TryGetValue(address, out var v) ? v : null;

    /// <summary>
    /// Check if a peer ID belongs to a validator.
    /// </summary>
    public bool IsValidator(PeerId id) => _byPeerId.ContainsKey(id);

    /// <summary>
    /// Update a validator's identity after handshake reveals their real PeerId.
    /// Replaces placeholder PeerId/PublicKey with the actual values.
    /// </summary>
    public bool UpdateValidatorIdentity(int validatorIndex, PeerId newPeerId, PublicKey newPublicKey, BlsPublicKey? newBlsPublicKey = null)
    {
        var validator = _validators.FirstOrDefault(v => v.Index == validatorIndex);
        if (validator == null)
            return false;

        // Remove old PeerId mapping
        _byPeerId.Remove(validator.PeerId);

        // Update identity
        validator.PeerId = newPeerId;
        validator.PublicKey = newPublicKey;
        if (newBlsPublicKey.HasValue)
            validator.BlsPublicKey = newBlsPublicKey.Value;

        // Add new PeerId mapping
        _byPeerId[newPeerId] = validator;
        return true;
    }

    /// <summary>
    /// Get the index of a validator by their PeerId. Returns -1 if not found.
    /// </summary>
    public int GetValidatorIndex(PeerId id) =>
        _byPeerId.TryGetValue(id, out var v) ? v.Index : -1;

    /// <summary>
    /// Returns validators whose bit is set in the bitmap (bit i = validator at index i).
    /// </summary>
    public IEnumerable<ValidatorInfo> GetValidatorsFromBitmap(ulong bitmap)
    {
        for (int i = 0; i < _validators.Count && i < 64; i++)
        {
            if ((bitmap & (1UL << i)) != 0)
                yield return _validators[i];
        }
    }

    /// <summary>
    /// Compute the quorum threshold (2f+1 for 3f+1 validators).
    /// BFT requires >2/3 agreement.
    /// </summary>
    public int QuorumThreshold => (_validators.Count * 2 / 3) + 1;

    /// <summary>
    /// Maximum number of Byzantine faults tolerated.
    /// </summary>
    public int MaxFaults => (_validators.Count - 1) / 3;

    /// <summary>
    /// Select the leader for a given view number.
    /// Phase 1: Equal-weight round-robin.
    /// Phase 2: Weighted by stake * reputation.
    /// </summary>
    public ValidatorInfo GetLeader(ulong viewNumber)
    {
        if (_leaderSelector != null)
            return _leaderSelector(viewNumber);
        int index = (int)(viewNumber % (ulong)_validators.Count);
        return _validators[index];
    }
}
