using System.Numerics;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Basalt.Consensus.Dkg;

/// <summary>
/// Phases of the DKG protocol.
/// </summary>
public enum DkgPhase
{
    Idle,
    Deal,
    Complaint,
    Justification,
    Finalize,
    Completed,
    Failed,
}

/// <summary>
/// Result of a completed DKG round.
/// </summary>
public sealed class DkgResult
{
    /// <summary>Epoch this DKG was for.</summary>
    public ulong EpochNumber { get; init; }

    /// <summary>Group threshold public key (sum of C_0 from all qualified dealers).</summary>
    public BlsPublicKey GroupPublicKey { get; init; }

    /// <summary>This validator's combined secret share (sum of all received shares).</summary>
    public BigInteger SecretShare { get; init; }

    /// <summary>Indices of qualified dealers (those not disqualified by complaints).</summary>
    public IReadOnlySet<int> QualifiedDealers { get; init; } = new HashSet<int>();

    /// <summary>Threshold: t+1 shares needed for reconstruction.</summary>
    public int Threshold { get; init; }
}

/// <summary>
/// Feldman VSS Distributed Key Generation protocol.
/// Orchestrates the Deal → Complaint → Justification → Finalize state machine.
///
/// Each validator runs one instance per epoch. The protocol produces a threshold
/// BLS key pair where t+1 validators can cooperate to sign/decrypt but no single
/// validator (or coalition smaller than t+1) can recover the secret.
/// </summary>
public sealed class DkgProtocol
{
    private readonly int _validatorIndex;
    private readonly int _validatorCount;
    private readonly int _threshold;
    private readonly ulong _epochNumber;
    private readonly BlsPublicKey[] _validatorBlsKeys;
    private readonly BlsPublicKey _myBlsKey;
    private readonly byte[]? _myPrivateKey; // C-03: private key for ECDH encryption
    private readonly ILogger _logger;
    private readonly object _lock = new();

    private DkgPhase _phase = DkgPhase.Idle;

    // Deal phase: track received deals
    private readonly Dictionary<int, DealData> _receivedDeals = new();

    // Our own deal (polynomial + commitments)
    private BigInteger[]? _myPolynomial;
    private BlsPublicKey[]? _myCommitments;

    // Complaint phase: track complaints filed
    private readonly Dictionary<(int Complainer, int Dealer), BigInteger> _complaints = new();

    // Justification phase: track justifications received
    private readonly Dictionary<(int Dealer, int Complainer), BigInteger> _justifications = new();

    // Disqualified dealers
    private readonly HashSet<int> _disqualifiedDealers = new();

    // Finalize: received group public key proposals
    private readonly Dictionary<int, BlsPublicKey> _finalizeProposals = new();

    /// <summary>
    /// Fired when a DKG message needs to be broadcast to all validators.
    /// </summary>
    public event Action<NetworkMessage>? OnBroadcast;

    /// <summary>
    /// Current protocol phase.
    /// </summary>
    public DkgPhase Phase
    {
        get { lock (_lock) return _phase; }
    }

    /// <summary>
    /// Result of the DKG round (only set when Phase == Completed).
    /// </summary>
    public DkgResult? Result { get; private set; }

    public DkgProtocol(
        int validatorIndex,
        int validatorCount,
        ulong epochNumber,
        BlsPublicKey[] validatorBlsKeys,
        ILogger? logger = null)
        : this(validatorIndex, validatorCount, epochNumber, validatorBlsKeys, null, logger) { }

    /// <summary>
    /// C-03: Constructor with private key for ECDH-based share encryption.
    /// </summary>
    public DkgProtocol(
        int validatorIndex,
        int validatorCount,
        ulong epochNumber,
        BlsPublicKey[] validatorBlsKeys,
        byte[]? privateKey,
        ILogger? logger = null)
    {
        if (validatorIndex < 0 || validatorIndex >= validatorCount)
            throw new ArgumentOutOfRangeException(nameof(validatorIndex));
        if (validatorBlsKeys.Length != validatorCount)
            throw new ArgumentException("BLS key array must match validator count", nameof(validatorBlsKeys));

        _validatorIndex = validatorIndex;
        _validatorCount = validatorCount;
        _threshold = Math.Max(1, (validatorCount - 1) / 3); // BFT threshold: f = floor((n-1)/3)
        _epochNumber = epochNumber;
        _validatorBlsKeys = validatorBlsKeys;
        _myBlsKey = validatorBlsKeys[validatorIndex];
        _myPrivateKey = privateKey;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// BFT threshold (f). Requires f+1 shares for reconstruction.
    /// </summary>
    public int Threshold => _threshold;

    /// <summary>
    /// Start the deal phase: generate polynomial, compute commitments, encrypt shares, and broadcast.
    /// </summary>
    public void StartDealPhase(PeerId myPeerId)
    {
        lock (_lock)
        {
            if (_phase != DkgPhase.Idle)
                return;

            _phase = DkgPhase.Deal;

            // Generate random polynomial of degree t
            _myPolynomial = ThresholdCrypto.GeneratePolynomial(_threshold);
            _myCommitments = ThresholdCrypto.ComputeCommitments(_myPolynomial);

            // Compute and encrypt shares for each validator
            // C-03: Use ECDH encryption when private key is available
            var encryptedShares = new byte[_validatorCount][];
            for (int i = 0; i < _validatorCount; i++)
            {
                var share = ThresholdCrypto.EvaluatePolynomial(_myPolynomial, i + 1); // 1-based index
                encryptedShares[i] = _myPrivateKey != null
                    ? ThresholdCrypto.EncryptShare(share, _myPrivateKey, _validatorBlsKeys[i])
#pragma warning disable CS0618 // L-03: Legacy XOR path — fallback when private key unavailable
                    : ThresholdCrypto.EncryptShare(share, _myBlsKey, _validatorBlsKeys[i]);
#pragma warning restore CS0618
            }

            // Store our own deal
            var myDeal = new DealData
            {
                Commitments = _myCommitments,
                EncryptedShares = encryptedShares,
            };
            _receivedDeals[_validatorIndex] = myDeal;

            _logger.LogInformation("DKG epoch {Epoch}: started deal phase (validator {Index}, threshold {T})",
                _epochNumber, _validatorIndex, _threshold);

            // Broadcast deal message
            var msg = new DkgDealMessage
            {
                SenderId = myPeerId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                EpochNumber = _epochNumber,
                DealerIndex = _validatorIndex,
                Commitments = _myCommitments,
                EncryptedShares = encryptedShares,
            };

            OnBroadcast?.Invoke(msg);
        }
    }

    /// <summary>
    /// Process a received deal message from another validator.
    /// </summary>
    public void ProcessDeal(DkgDealMessage msg)
    {
        lock (_lock)
        {
            if (_phase != DkgPhase.Deal && _phase != DkgPhase.Complaint)
                return;

            if (msg.EpochNumber != _epochNumber)
                return;

            if (msg.DealerIndex < 0 || msg.DealerIndex >= _validatorCount)
                return;

            if (_receivedDeals.ContainsKey(msg.DealerIndex))
                return; // Already received from this dealer

            if (msg.Commitments.Length != _threshold + 1)
                return; // Wrong polynomial degree

            if (msg.EncryptedShares.Length != _validatorCount)
                return; // Wrong share count

            _receivedDeals[msg.DealerIndex] = new DealData
            {
                Commitments = msg.Commitments,
                EncryptedShares = msg.EncryptedShares,
            };

            _logger.LogDebug("DKG epoch {Epoch}: received deal from validator {Dealer} ({Count}/{Total})",
                _epochNumber, msg.DealerIndex, _receivedDeals.Count, _validatorCount);
        }
    }

    /// <summary>
    /// Transition to complaint phase: verify all received shares and file complaints for invalid ones.
    /// </summary>
    public void StartComplaintPhase(PeerId myPeerId)
    {
        lock (_lock)
        {
            if (_phase != DkgPhase.Deal)
                return;

            _phase = DkgPhase.Complaint;

            foreach (var (dealerIndex, deal) in _receivedDeals)
            {
                if (dealerIndex == _validatorIndex)
                    continue; // Don't verify our own deal

                // Decrypt our share from this dealer
                // C-03: Use ECDH decryption when private key is available
                var encrypted = deal.EncryptedShares[_validatorIndex];
                var share = _myPrivateKey != null
                    ? ThresholdCrypto.DecryptShare(encrypted, _myPrivateKey, _validatorBlsKeys[dealerIndex])
                    : ThresholdCrypto.DecryptShare(encrypted, _validatorBlsKeys[dealerIndex], _myBlsKey);

                // Verify the share against the commitment vector
                if (!ThresholdCrypto.VerifyShare(share, _validatorIndex + 1, deal.Commitments))
                {
                    _logger.LogWarning("DKG epoch {Epoch}: filing complaint against dealer {Dealer} (invalid share)",
                        _epochNumber, dealerIndex);

                    _complaints[(_validatorIndex, dealerIndex)] = share;

                    // L-13: Broadcast share * G1 instead of plaintext share to prevent leaking secrets
                    var sharePointBE = ThresholdCrypto.ScalarToBytesBE(share);
                    var shareG1 = BlsCrypto.ScalarMultG1(BlsCrypto.G1Generator(), sharePointBE);

                    var complaint = new DkgComplaintMessage
                    {
                        SenderId = myPeerId,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        EpochNumber = _epochNumber,
                        AccusedDealerIndex = dealerIndex,
                        ComplainerIndex = _validatorIndex,
                        RevealedShare = shareG1, // L-13: G1 point instead of scalar
                    };

                    OnBroadcast?.Invoke(complaint);
                }
            }

            _logger.LogInformation("DKG epoch {Epoch}: complaint phase ({Complaints} complaints filed)",
                _epochNumber, _complaints.Count);
        }
    }

    /// <summary>
    /// Process a complaint from another validator.
    /// </summary>
    public void ProcessComplaint(DkgComplaintMessage msg)
    {
        lock (_lock)
        {
            if (_phase != DkgPhase.Complaint && _phase != DkgPhase.Justification)
                return;

            if (msg.EpochNumber != _epochNumber)
                return;

            if (msg.ComplainerIndex < 0 || msg.ComplainerIndex >= _validatorCount)
                return;
            if (msg.AccusedDealerIndex < 0 || msg.AccusedDealerIndex >= _validatorCount)
                return;

            var key = (msg.ComplainerIndex, msg.AccusedDealerIndex);
            if (_complaints.ContainsKey(key))
                return; // Already received this complaint

            var revealedShare = new BigInteger(msg.RevealedShare, isUnsigned: true, isBigEndian: false);
            _complaints[key] = revealedShare;

            _logger.LogDebug("DKG epoch {Epoch}: received complaint from {Complainer} against dealer {Dealer}",
                _epochNumber, msg.ComplainerIndex, msg.AccusedDealerIndex);
        }
    }

    /// <summary>
    /// Transition to justification phase: respond to complaints by revealing correct shares.
    /// </summary>
    public void StartJustificationPhase(PeerId myPeerId)
    {
        lock (_lock)
        {
            if (_phase != DkgPhase.Complaint)
                return;

            _phase = DkgPhase.Justification;

            // Find complaints against us
            foreach (var ((complainerIndex, dealerIndex), _) in _complaints)
            {
                if (dealerIndex != _validatorIndex)
                    continue;

                if (_myPolynomial == null)
                    continue;

                // Recompute the correct share for the complainer
                var correctShare = ThresholdCrypto.EvaluatePolynomial(_myPolynomial, complainerIndex + 1);

                var justification = new DkgJustificationMessage
                {
                    SenderId = myPeerId,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    EpochNumber = _epochNumber,
                    DealerIndex = _validatorIndex,
                    ComplainerIndex = complainerIndex,
                    Share = ThresholdCrypto.ScalarToBytes(correctShare),
                };

                _justifications[(_validatorIndex, complainerIndex)] = correctShare;
                OnBroadcast?.Invoke(justification);

                _logger.LogInformation("DKG epoch {Epoch}: justifying against complaint from {Complainer}",
                    _epochNumber, complainerIndex);
            }
        }
    }

    /// <summary>
    /// Process a justification from a dealer.
    /// </summary>
    public void ProcessJustification(DkgJustificationMessage msg)
    {
        lock (_lock)
        {
            if (_phase != DkgPhase.Justification && _phase != DkgPhase.Finalize)
                return;

            if (msg.EpochNumber != _epochNumber)
                return;

            if (msg.DealerIndex < 0 || msg.DealerIndex >= _validatorCount)
                return;

            var key = (msg.DealerIndex, msg.ComplainerIndex);
            if (_justifications.ContainsKey(key))
                return;

            var share = new BigInteger(msg.Share, isUnsigned: true, isBigEndian: false);
            _justifications[key] = share;

            _logger.LogDebug("DKG epoch {Epoch}: received justification from dealer {Dealer} for complainer {Complainer}",
                _epochNumber, msg.DealerIndex, msg.ComplainerIndex);
        }
    }

    /// <summary>
    /// Finalize the DKG round: determine qualified dealers, compute group public key and secret share.
    /// </summary>
    public void Finalize(PeerId myPeerId)
    {
        lock (_lock)
        {
            // M-13: Only allow finalization from the Justification phase
            if (_phase != DkgPhase.Justification)
                return;

            _phase = DkgPhase.Finalize;

            // Determine disqualified dealers: dealers with unresolved complaints
            DetermineDisqualifiedDealers();

            // Compute set of qualified dealers
            var qualifiedDealers = new HashSet<int>();
            for (int i = 0; i < _validatorCount; i++)
            {
                if (_receivedDeals.ContainsKey(i) && !_disqualifiedDealers.Contains(i))
                    qualifiedDealers.Add(i);
            }

            if (qualifiedDealers.Count < _threshold + 1)
            {
                _logger.LogError("DKG epoch {Epoch}: FAILED — only {Count} qualified dealers, need {Need}",
                    _epochNumber, qualifiedDealers.Count, _threshold + 1);
                _phase = DkgPhase.Failed;
                return;
            }

            // Compute group public key: sum of C_0 from all qualified dealers
            // Since we can't add BLS points directly, we sum the underlying scalars
            // and derive the public key. This works because C_0 = a_0 * G1,
            // so sum(C_0) = sum(a_0) * G1 = groupSecret * G1.
            // However, we don't know a_0 of other dealers.
            //
            // For a practical implementation: the group public key is broadcast
            // and verified by consensus. Each validator computes it from the
            // commitment vectors they received.
            //
            // Since we can't do point addition on BlsPublicKey directly,
            // we use the additive homomorphism of the secret shares:
            // Our combined share = sum(s_i_j) for qualified dealer j
            // The group secret = sum(a_0_j) for qualified dealer j (at x=0)
            // Reconstruction via Lagrange at x=0 from threshold+1 combined shares
            // recovers the group secret.

            var combinedShare = BigInteger.Zero;
            foreach (var dealerIdx in qualifiedDealers)
            {
                var deal = _receivedDeals[dealerIdx];
                BigInteger share;
                if (dealerIdx == _validatorIndex)
                {
                    // Our own share: evaluate our polynomial at our own index
                    share = ThresholdCrypto.EvaluatePolynomial(_myPolynomial!, _validatorIndex + 1);
                }
                else
                {
                    // Decrypt the share from this dealer
                    // C-03: Use ECDH decryption when private key is available
                    share = _myPrivateKey != null
                        ? ThresholdCrypto.DecryptShare(deal.EncryptedShares[_validatorIndex], _myPrivateKey, _validatorBlsKeys[dealerIdx])
                        : ThresholdCrypto.DecryptShare(deal.EncryptedShares[_validatorIndex], _validatorBlsKeys[dealerIdx], _myBlsKey);
                }
                combinedShare = (combinedShare + share) % ThresholdCrypto.ScalarFieldOrder;
            }

            if (combinedShare < 0) combinedShare += ThresholdCrypto.ScalarFieldOrder;

            // C-02: Compute group public key as sum(C_0_j) for all qualified dealers
            // Each dealer's C_0 = a_0_j * G1, so sum(C_0) = groupSecret * G1
            byte[]? gpkBytes = null;
            foreach (var dealerIdx in qualifiedDealers)
            {
                var c0 = _receivedDeals[dealerIdx].Commitments[0];
                gpkBytes = gpkBytes == null ? c0.ToArray() : BlsCrypto.AddG1(gpkBytes, c0.ToArray());
            }
            var groupPk = new BlsPublicKey(gpkBytes!);

            Result = new DkgResult
            {
                EpochNumber = _epochNumber,
                GroupPublicKey = groupPk,
                SecretShare = combinedShare,
                QualifiedDealers = qualifiedDealers,
                Threshold = _threshold,
            };

            _phase = DkgPhase.Completed;

            _logger.LogInformation(
                "DKG epoch {Epoch}: COMPLETED — {QualifiedCount} qualified dealers, threshold {T}",
                _epochNumber, qualifiedDealers.Count, _threshold);

            // Broadcast finalize
            var finalizeMsg = new DkgFinalizeMessage
            {
                SenderId = myPeerId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                EpochNumber = _epochNumber,
                GroupPublicKey = groupPk,
            };

            OnBroadcast?.Invoke(finalizeMsg);
        }
    }

    /// <summary>
    /// Process a finalize message from another validator.
    /// </summary>
    public void ProcessFinalize(DkgFinalizeMessage msg)
    {
        lock (_lock)
        {
            if (msg.EpochNumber != _epochNumber)
                return;

            // Just record it — validators track what group key others computed
            // to detect disagreements
            var senderIdx = -1;
            for (int i = 0; i < _validatorCount; i++)
            {
                // We don't have PeerId → index mapping here, so store by order received
                if (!_finalizeProposals.ContainsKey(i) || _finalizeProposals.Count < _validatorCount)
                {
                    senderIdx = _finalizeProposals.Count;
                    break;
                }
            }

            if (senderIdx >= 0)
                _finalizeProposals[senderIdx] = msg.GroupPublicKey;
        }
    }

    /// <summary>
    /// Get the number of deals received so far.
    /// </summary>
    public int ReceivedDealCount
    {
        get { lock (_lock) return _receivedDeals.Count; }
    }

    /// <summary>
    /// Get the number of complaints filed.
    /// </summary>
    public int ComplaintCount
    {
        get { lock (_lock) return _complaints.Count; }
    }

    /// <summary>
    /// Get the set of disqualified dealers.
    /// </summary>
    public IReadOnlySet<int> DisqualifiedDealers
    {
        get { lock (_lock) return new HashSet<int>(_disqualifiedDealers); }
    }

    /// <summary>
    /// Determine which dealers should be disqualified based on unresolved complaints.
    /// A dealer is disqualified if:
    /// 1. A complaint was filed against them AND
    /// 2. They did not provide a valid justification
    /// </summary>
    private void DetermineDisqualifiedDealers()
    {
        // Group complaints by dealer
        var complaintsByDealer = new Dictionary<int, List<(int Complainer, BigInteger RevealedShare)>>();
        foreach (var ((complainer, dealer), share) in _complaints)
        {
            if (!complaintsByDealer.TryGetValue(dealer, out var list))
            {
                list = new List<(int, BigInteger)>();
                complaintsByDealer[dealer] = list;
            }
            list.Add((complainer, share));
        }

        foreach (var (dealerIdx, complaints) in complaintsByDealer)
        {
            if (!_receivedDeals.TryGetValue(dealerIdx, out var deal))
            {
                // No deal received — disqualify
                _disqualifiedDealers.Add(dealerIdx);
                continue;
            }

            foreach (var (complainerIdx, revealedShare) in complaints)
            {
                var justKey = (dealerIdx, complainerIdx);
                if (_justifications.TryGetValue(justKey, out var justifiedShare))
                {
                    // Dealer provided a justification — verify it
                    if (ThresholdCrypto.VerifyShare(justifiedShare, complainerIdx + 1, deal.Commitments))
                    {
                        // Justification is valid — dealer is not disqualified for this complaint
                        // But check if the revealed share matches the justified share.
                        // If the complainer's decrypted share doesn't match the justified one,
                        // the complaint is resolved in the dealer's favor (the complainer had
                        // a bad decryption key or was malicious).
                        continue;
                    }
                }

                // No valid justification — disqualify the dealer
                _disqualifiedDealers.Add(dealerIdx);
                _logger.LogWarning("DKG epoch {Epoch}: dealer {Dealer} disqualified (unresolved complaint from {Complainer})",
                    _epochNumber, dealerIdx, complainerIdx);
                break; // One unresolved complaint is enough
            }
        }
    }

    /// <summary>
    /// Internal deal data.
    /// </summary>
    private sealed class DealData
    {
        public required BlsPublicKey[] Commitments { get; init; }
        public required byte[][] EncryptedShares { get; init; }
    }
}
