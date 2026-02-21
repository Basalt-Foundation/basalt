using System.Collections.Concurrent;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using Microsoft.Extensions.Logging;

namespace Basalt.Consensus;

/// <summary>
/// BasaltBFT consensus engine — a simplified HotStuff-based BFT protocol.
/// Phase 1: Sequential 3-phase (PREPARE/PRE-COMMIT/COMMIT), no pipelining.
///
/// Consensus flow:
/// 1. Leader proposes a block
/// 2. Validators send PREPARE votes
/// 3. On 2f+1 PREPARE votes, validators send PRE-COMMIT votes
/// 4. On 2f+1 PRE-COMMIT votes, validators send COMMIT votes
/// 5. On 2f+1 COMMIT votes, block is finalized
/// </summary>
public sealed class BasaltBft
{
    private ValidatorSet _validatorSet;
    private readonly PeerId _localPeerId;
    private readonly byte[] _privateKey;
    private readonly IBlsSigner _blsSigner;
    private readonly ILogger<BasaltBft> _logger;

    // State
    private ulong _currentView;
    private ulong _currentBlockNumber;
    private ConsensusState _state = ConsensusState.Idle;
    private Hash256 _currentProposalHash;
    private byte[]? _currentProposalData;

    // Vote tracking
    private readonly ConcurrentDictionary<(ulong View, VotePhase Phase), HashSet<PeerId>> _votes = new();
    private readonly ConcurrentDictionary<(ulong View, VotePhase Phase), List<(byte[] Signature, byte[] PublicKey)>> _voteSignatures = new();

    // Timing
    private DateTimeOffset _viewStartTime;
    private readonly TimeSpan _viewTimeout;
    private ulong? _viewChangeRequestedForView;

    // Callbacks
    // Events raised during consensus - consumed by the node integration layer
#pragma warning disable CS0067
    public event Action<byte[]>? OnBlockProposal;
    public event Action<ConsensusVoteMessage>? OnVote;
#pragma warning restore CS0067
    public event Action<Hash256, byte[], ulong>? OnBlockFinalized;
    public event Action<AggregateVoteMessage>? OnAggregateVote;
    public event Action<ulong>? OnViewChange;

    /// <summary>
    /// Fired when a proposal arrives for a block number ahead of ours,
    /// indicating this node is behind and needs to sync.
    /// </summary>
    public event Action<ulong>? OnBehindDetected;

    public BasaltBft(
        ValidatorSet validatorSet,
        PeerId localPeerId,
        byte[] privateKey,
        ILogger<BasaltBft> logger,
        IBlsSigner? blsSigner = null,
        TimeSpan? viewTimeout = null)
    {
        _validatorSet = validatorSet;
        _localPeerId = localPeerId;
        _privateKey = privateKey;
        _blsSigner = blsSigner ?? new BlsSigner();
        _logger = logger;
        _viewTimeout = viewTimeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Current consensus state.
    /// </summary>
    public ConsensusState State => _state;

    /// <summary>
    /// Current view number.
    /// </summary>
    public ulong CurrentView => _currentView;

    /// <summary>
    /// Current block number being decided.
    /// </summary>
    public ulong CurrentBlockNumber => _currentBlockNumber;

    /// <summary>
    /// Atomically replace the validator set (e.g., on epoch transition).
    /// Clears all in-flight vote state and resets consensus to Idle.
    /// </summary>
    public void UpdateValidatorSet(ValidatorSet newSet)
    {
        _validatorSet = newSet;
        _votes.Clear();
        _voteSignatures.Clear();
        _viewChangeRequestedForView = null;
        _state = ConsensusState.Idle;
    }

    /// <summary>
    /// Check if this node is the leader for the current view.
    /// </summary>
    public bool IsLeader => _validatorSet.GetLeader(_currentView).PeerId == _localPeerId;

    private bool IsLeaderForView(ulong view) =>
        _validatorSet.GetLeader(view).PeerId == _localPeerId;

    /// <summary>
    /// Start a new consensus round for the given block number.
    /// </summary>
    public void StartRound(ulong blockNumber)
    {
        _currentBlockNumber = blockNumber;
        _currentView = blockNumber; // View tracks block number in Phase 1
        _state = ConsensusState.Proposing;
        _viewStartTime = DateTimeOffset.UtcNow;
        _viewChangeRequestedForView = null;
        _votes.Clear();
        _voteSignatures.Clear();

        _logger.LogInformation(
            "Starting consensus round for block {BlockNumber}, view {View}, leader: {Leader}",
            blockNumber, _currentView, _validatorSet.GetLeader(_currentView).PeerId);
    }

    /// <summary>
    /// Leader proposes a block.
    /// </summary>
    public ConsensusProposalMessage? ProposeBlock(byte[] blockData, Hash256 blockHash)
    {
        if (!IsLeader || _state != ConsensusState.Proposing)
            return null;

        _currentProposalHash = blockHash;
        _currentProposalData = blockData;
        _state = ConsensusState.Preparing;

        // Sign the proposal (domain-separated: phase || view || blockNumber || blockHash)
        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, VotePhase.Prepare, _currentView, _currentBlockNumber, blockHash);
        var signature = new BlsSignature(_blsSigner.Sign(_privateKey, sigPayload));

        var proposal = new ConsensusProposalMessage
        {
            SenderId = _localPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ViewNumber = _currentView,
            BlockNumber = _currentBlockNumber,
            BlockHash = blockHash,
            BlockData = blockData,
            ProposerSignature = signature,
        };

        _logger.LogInformation("Proposed block {BlockHash} for height {Height}", blockHash, _currentBlockNumber);

        // Self-vote PREPARE — CreateVote tracks the signature in _voteSignatures
        // for future aggregation; HandlePrepareVote counts the PeerId in _votes.
        CreateVote(VotePhase.Prepare, blockHash);
        HandlePrepareVote(_localPeerId, _currentView, blockHash);

        return proposal;
    }

    /// <summary>
    /// Handle a received proposal from the leader.
    /// Accepts proposals for the current view or any future view (fast-forward).
    /// Fast-forward prevents the liveness deadlock where a leader proposes for a
    /// new view but some validators haven't completed the view change yet — without
    /// fast-forward they reject the proposal, time out, and trigger another view change
    /// in an infinite loop.
    /// </summary>
    public ConsensusVoteMessage? HandleProposal(ConsensusProposalMessage proposal)
    {
        if (proposal.ViewNumber != _currentView)
        {
            // Only fast-forward when ALL of these hold:
            // 1) The proposal is for a future view (not a past one)
            // 2) We're in Proposing state — no active consensus in progress.
            //    If we're in Preparing/PreCommitting/Committing, an active round
            //    may be about to finalize; abandoning it would cause us to miss
            //    the block and desync from the chain.
            // 3) The proposal is for the SAME block number we're deciding.
            //    If the leader already finalized our block and moved on to block N+1,
            //    fast-forwarding would make us finalize N+1 without having N, breaking
            //    the chain.
            if (proposal.ViewNumber > _currentView
                && _state == ConsensusState.Proposing
                && proposal.BlockNumber == _currentBlockNumber)
            {
                // Verify the sender is the legitimate leader for the proposed view
                var futureLeader = _validatorSet.GetLeader(proposal.ViewNumber);
                if (proposal.SenderId != futureLeader.PeerId)
                {
                    _logger.LogWarning("Proposal for future view {View} from non-leader {Sender}",
                        proposal.ViewNumber, proposal.SenderId);
                    return null;
                }

                // Verify signature before fast-forwarding (domain-separated)
                Span<byte> ffPayload = stackalloc byte[ConsensusPayloadSize];
                WriteConsensusSigningPayload(ffPayload, VotePhase.Prepare, proposal.ViewNumber, proposal.BlockNumber, proposal.BlockHash);
                if (!_blsSigner.Verify(futureLeader.BlsPublicKey.ToArray(), ffPayload, proposal.ProposerSignature.ToArray()))
                {
                    _logger.LogWarning("Invalid proposal signature for future view {View}", proposal.ViewNumber);
                    return null;
                }

                // Fast-forward: the leader already processed the view change before us.
                // Preserve any pre-counted votes for the target view (from votes that
                // arrived before this proposal) — only clear stale votes from old views.
                _logger.LogInformation("Fast-forwarding from view {Old} to {New} (valid leader proposal)",
                    _currentView, proposal.ViewNumber);
                var targetView = proposal.ViewNumber;
                foreach (var key in _votes.Keys)
                {
                    if (key.View != targetView)
                        _votes.TryRemove(key, out _);
                }
                foreach (var key in _voteSignatures.Keys)
                {
                    if (key.View != targetView)
                        _voteSignatures.TryRemove(key, out _);
                }
                _currentView = targetView;
                _state = ConsensusState.Proposing;
                _viewStartTime = DateTimeOffset.UtcNow;
                _viewChangeRequestedForView = null;
                OnViewChange?.Invoke(_currentView);
            }
            else
            {
                _logger.LogDebug("Ignoring proposal for view {View} (current view {Current}, state {State}, block {Block}/{Expected})",
                    proposal.ViewNumber, _currentView, _state, proposal.BlockNumber, _currentBlockNumber);
                return null;
            }
        }

        var leader = _validatorSet.GetLeader(_currentView);
        if (proposal.SenderId != leader.PeerId)
        {
            _logger.LogWarning("Received proposal from non-leader {Sender}", proposal.SenderId);
            return null;
        }

        // Block number validation: after a view change, the leader may have already
        // finalized our block and moved on. Accepting a proposal for a different block
        // would cause chain desync when finalized.
        if (proposal.BlockNumber != _currentBlockNumber)
        {
            if (proposal.BlockNumber > _currentBlockNumber)
            {
                _logger.LogWarning("Proposal for block {Block} but we are deciding block {Current} — we are behind",
                    proposal.BlockNumber, _currentBlockNumber);
                OnBehindDetected?.Invoke(proposal.BlockNumber);
            }
            return null;
        }

        // Verify leader's signature (domain-separated; may already be verified for fast-forwarded
        // proposals, but the cost is negligible and keeps the code straightforward)
        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, VotePhase.Prepare, _currentView, _currentBlockNumber, proposal.BlockHash);
        if (!_blsSigner.Verify(leader.BlsPublicKey.ToArray(), sigPayload, proposal.ProposerSignature.ToArray()))
        {
            _logger.LogWarning("Invalid proposal signature from {Sender}", proposal.SenderId);
            return null;
        }

        _currentProposalHash = proposal.BlockHash;
        _currentProposalData = proposal.BlockData;
        _state = ConsensusState.Preparing;

        // Non-leaders just create and return their PREPARE vote (sent to leader only).
        // They don't self-count because they transition only via aggregate QCs.
        var vote = CreateVote(VotePhase.Prepare, proposal.BlockHash);
        return vote;
    }

    /// <summary>
    /// Handle a vote message.
    /// Accepts votes within a small window around the current view to tolerate
    /// minor view divergence between validators after view changes.
    /// Pre-counted votes for nearby views are preserved so that when a proposal
    /// or view change fast-forwards us, quorum can be reached immediately.
    /// </summary>
    public ConsensusVoteMessage? HandleVote(ConsensusVoteMessage vote)
    {
        // Only the leader collects individual votes; non-leaders transition via aggregate QCs.
        if (!IsLeaderForView(vote.ViewNumber))
            return null;

        // Accept votes within ±1 of our current view to tolerate minor divergence.
        var diff = vote.ViewNumber > _currentView
            ? vote.ViewNumber - _currentView
            : _currentView - vote.ViewNumber;
        if (diff > 1)
            return null;

        if (!_validatorSet.IsValidator(vote.SenderId))
        {
            _logger.LogWarning("Vote from non-validator {Sender}", vote.SenderId);
            return null;
        }

        // F-CON-02: Verify vote is for the correct block hash
        if (vote.BlockHash != _currentProposalHash)
        {
            _logger.LogWarning("Vote from {Sender} has mismatched block hash", vote.SenderId);
            return null;
        }

        // Verify vote signature (domain-separated)
        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, vote.Phase, vote.ViewNumber, vote.BlockNumber, vote.BlockHash);
        if (!_blsSigner.Verify(vote.VoterPublicKey.ToArray(), sigPayload, vote.VoterSignature.ToArray()))
        {
            _logger.LogWarning("Invalid vote signature from {Sender}", vote.SenderId);
            return null;
        }

        // L-02: Track vote signature for aggregation, deduplicating by public key
        var sigKey = (vote.ViewNumber, vote.Phase);
        var sigList = _voteSignatures.GetOrAdd(sigKey, _ => new List<(byte[], byte[])>());
        var voterSigBytes = vote.VoterSignature.ToArray();
        var voterPkBytes = vote.VoterPublicKey.ToArray();
        lock (sigList)
        {
            if (!sigList.Exists(s => s.Item2.AsSpan().SequenceEqual(voterPkBytes)))
                sigList.Add((voterSigBytes, voterPkBytes));
        }

        return vote.Phase switch
        {
            VotePhase.Prepare => HandlePrepareVote(vote.SenderId, vote.ViewNumber, vote.BlockHash),
            VotePhase.PreCommit => HandlePreCommitVote(vote.SenderId, vote.ViewNumber, vote.BlockHash),
            VotePhase.Commit => HandleCommitVote(vote.SenderId, vote.ViewNumber, vote.BlockHash),
            _ => null,
        };
    }

    /// <summary>
    /// Check if a view change is needed (timeout).
    /// </summary>
    public ViewChangeMessage? CheckViewTimeout()
    {
        if (_state == ConsensusState.Idle || _state == ConsensusState.Finalized)
            return null;

        // Don't send duplicate view change for the same view
        if (_viewChangeRequestedForView == _currentView)
            return null;

        if (DateTimeOffset.UtcNow - _viewStartTime > _viewTimeout)
        {
            _logger.LogWarning("View {View} timed out, requesting view change", _currentView);
            _viewChangeRequestedForView = _currentView;
            _viewStartTime = DateTimeOffset.UtcNow; // Reset timer for next potential timeout
            return RequestViewChange();
        }

        return null;
    }

    /// <summary>
    /// Handle a view change message.
    /// Returns a ViewChangeMessage if this node auto-joined the view change
    /// (so the caller can broadcast it to make the vote visible to other nodes).
    /// Without broadcasting auto-joins, a 2-2 parity split causes views to skip
    /// by 2 indefinitely because each side's auto-join vote is invisible to the other.
    /// </summary>
    public ViewChangeMessage? HandleViewChange(ViewChangeMessage viewChange)
    {
        // M-06: Only accept view changes from known validators
        if (!_validatorSet.IsValidator(viewChange.SenderId))
        {
            _logger.LogWarning("View change from non-validator {Sender}", viewChange.SenderId);
            return null;
        }

        // H-01: Verify view change signature (domain-separated)
        Span<byte> sigPayload = stackalloc byte[ViewChangePayloadSize];
        WriteViewChangeSigningPayload(sigPayload, viewChange.ProposedView);
        if (!_blsSigner.Verify(viewChange.VoterPublicKey.ToArray(), sigPayload, viewChange.VoterSignature.ToArray()))
        {
            _logger.LogWarning("Invalid view change signature from {Sender}", viewChange.SenderId);
            return null;
        }

        // Use a distinct key (Commit+1 via cast) to avoid collision with consensus phase votes
        var voteKey = (viewChange.ProposedView, (VotePhase)0xFF);
        var votes = _votes.GetOrAdd(voteKey, _ => new HashSet<PeerId>());
        bool newAutoJoin = false;

        lock (votes)
        {
            votes.Add(viewChange.SenderId);

            // Auto-join: if the proposed view is higher than ours AND we have
            // independently timed out, add our own vote. The timeout guard prevents
            // a cascade where a single validator's timeout instantly propagates to
            // all others (racing against and defeating proposals). Without the guard,
            // by the time the leader proposes, everyone has already jumped to the
            // next view. With it, only nodes that have genuinely timed out participate,
            // which still resolves the parity-split deadlock (both sides have timed out).
            if (viewChange.ProposedView > _currentView
                && _viewChangeRequestedForView == _currentView)
                newAutoJoin = votes.Add(_localPeerId);

            if (votes.Count >= _validatorSet.QuorumThreshold && _currentView < viewChange.ProposedView)
            {
                var newView = viewChange.ProposedView;
                _currentView = newView;
                _state = ConsensusState.Proposing;
                _viewStartTime = DateTimeOffset.UtcNow;
                _viewChangeRequestedForView = null;

                // Preserve any pre-arrived votes for the new view — only clear stale ones.
                // Matches the selective clearing in HandleProposal fast-forward.
                foreach (var key in _votes.Keys)
                {
                    if (key.View != newView)
                        _votes.TryRemove(key, out _);
                }
                foreach (var key in _voteSignatures.Keys)
                {
                    if (key.View != newView)
                        _voteSignatures.TryRemove(key, out _);
                }

                _logger.LogInformation("View changed to {View}", _currentView);
                OnViewChange?.Invoke(_currentView);
            }
        }

        // Return a signed ViewChangeMessage so the caller can broadcast it.
        // This makes the auto-join vote visible to other nodes, resolving the
        // parity split where two groups of validators alternate even/odd views.
        if (newAutoJoin)
        {
            Span<byte> viewPayload = stackalloc byte[ViewChangePayloadSize];
            WriteViewChangeSigningPayload(viewPayload, viewChange.ProposedView);
            var signature = new BlsSignature(_blsSigner.Sign(_privateKey, viewPayload));
            var publicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));

            return new ViewChangeMessage
            {
                SenderId = _localPeerId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CurrentView = _currentView,
                ProposedView = viewChange.ProposedView,
                VoterSignature = signature,
                VoterPublicKey = publicKey,
            };
        }

        return null;
    }

    private ConsensusVoteMessage? HandlePrepareVote(PeerId sender, ulong view, Hash256 blockHash)
    {
        var voteKey = (view, VotePhase.Prepare);
        var votes = _votes.GetOrAdd(voteKey, _ => new HashSet<PeerId>());

        lock (votes)
        {
            votes.Add(sender);

            if (votes.Count >= _validatorSet.QuorumThreshold && _state == ConsensusState.Preparing)
            {
                _state = ConsensusState.PreCommitting;
                _logger.LogInformation("PREPARE quorum reached ({Count}/{Threshold}), building aggregate QC",
                    votes.Count, _validatorSet.QuorumThreshold);

                // Build and broadcast aggregate PREPARE QC
                var aggregate = BuildAggregateVote(view, blockHash, VotePhase.Prepare);
                if (aggregate != null)
                    OnAggregateVote?.Invoke(aggregate);

                // Leader self-votes next phase and self-counts
                var vote = CreateVote(VotePhase.PreCommit, blockHash);
                HandlePreCommitVote(_localPeerId, view, blockHash);
                return null;
            }
        }

        return null;
    }

    private ConsensusVoteMessage? HandlePreCommitVote(PeerId sender, ulong view, Hash256 blockHash)
    {
        var voteKey = (view, VotePhase.PreCommit);
        var votes = _votes.GetOrAdd(voteKey, _ => new HashSet<PeerId>());

        lock (votes)
        {
            votes.Add(sender);

            if (votes.Count >= _validatorSet.QuorumThreshold && _state == ConsensusState.PreCommitting)
            {
                _state = ConsensusState.Committing;
                _logger.LogInformation("PRE-COMMIT quorum reached ({Count}/{Threshold}), building aggregate QC",
                    votes.Count, _validatorSet.QuorumThreshold);

                // Build and broadcast aggregate PRE-COMMIT QC
                var aggregate = BuildAggregateVote(view, blockHash, VotePhase.PreCommit);
                if (aggregate != null)
                    OnAggregateVote?.Invoke(aggregate);

                // Leader self-votes next phase and self-counts
                var vote = CreateVote(VotePhase.Commit, blockHash);
                HandleCommitVote(_localPeerId, view, blockHash);
                return null;
            }
        }

        return null;
    }

    private ConsensusVoteMessage? HandleCommitVote(PeerId sender, ulong view, Hash256 blockHash)
    {
        var voteKey = (view, VotePhase.Commit);
        var votes = _votes.GetOrAdd(voteKey, _ => new HashSet<PeerId>());

        lock (votes)
        {
            votes.Add(sender);

            if (votes.Count >= _validatorSet.QuorumThreshold && _state == ConsensusState.Committing)
            {
                _state = ConsensusState.Finalized;
                _logger.LogInformation("COMMIT quorum reached — block {Hash} finalized at height {Height}",
                    blockHash, _currentBlockNumber);

                // Build and broadcast aggregate COMMIT QC
                var aggregate = BuildAggregateVote(view, blockHash, VotePhase.Commit);
                if (aggregate != null)
                    OnAggregateVote?.Invoke(aggregate);

                OnBlockFinalized?.Invoke(blockHash, _currentProposalData ?? [], aggregate?.VoterBitmap ?? 0UL);
                return null;
            }
        }

        return null;
    }

    private ConsensusVoteMessage CreateVote(VotePhase phase, Hash256 blockHash)
    {
        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, phase, _currentView, _currentBlockNumber, blockHash);
        var signatureBytes = _blsSigner.Sign(_privateKey, sigPayload);
        var signature = new BlsSignature(signatureBytes);
        var publicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));

        // Track signature for aggregation (dedup by public key)
        var sigKey = (_currentView, phase);
        var sigList = _voteSignatures.GetOrAdd(sigKey, _ => new List<(byte[], byte[])>());
        var selfPkBytes = publicKey.ToArray();
        lock (sigList)
        {
            if (!sigList.Exists(s => s.Item2.AsSpan().SequenceEqual(selfPkBytes)))
                sigList.Add((signatureBytes, selfPkBytes));
        }

        return new ConsensusVoteMessage
        {
            SenderId = _localPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ViewNumber = _currentView,
            BlockNumber = _currentBlockNumber,
            BlockHash = blockHash,
            Phase = phase,
            VoterSignature = signature,
            VoterPublicKey = publicKey,
        };
    }

    private AggregateVoteMessage? BuildAggregateVote(ulong view, Hash256 blockHash, VotePhase phase)
    {
        var sigKey = (view, phase);
        if (!_voteSignatures.TryGetValue(sigKey, out var sigList))
            return null;

        byte[][] sigs;
        ulong bitmap = 0;
        lock (sigList)
        {
            sigs = sigList.Select(s => s.Signature).ToArray();
            foreach (var (sig, pubKey) in sigList)
            {
                // Find validator index by matching BLS public key
                foreach (var v in _validatorSet.Validators)
                {
                    if (v.BlsPublicKey.ToArray().AsSpan().SequenceEqual(pubKey))
                    {
                        bitmap |= 1UL << v.Index;
                        break;
                    }
                }
            }
        }

        if (sigs.Length == 0)
            return null;

        var aggregated = _blsSigner.AggregateSignatures(sigs);

        return new AggregateVoteMessage
        {
            SenderId = _localPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ViewNumber = view,
            BlockNumber = _currentBlockNumber,
            BlockHash = blockHash,
            Phase = phase,
            AggregateSignature = new BlsSignature(aggregated),
            VoterBitmap = bitmap,
        };
    }

    /// <summary>
    /// Handle an aggregate vote (quorum certificate) from the leader.
    /// Non-leaders use this to advance through consensus phases.
    /// </summary>
    public ConsensusVoteMessage? HandleAggregateVote(AggregateVoteMessage aggregate)
    {
        // Verify sender is the leader for this view
        var leader = _validatorSet.GetLeader(aggregate.ViewNumber);
        if (aggregate.SenderId != leader.PeerId)
        {
            _logger.LogWarning("Aggregate vote from non-leader {Sender} for view {View}",
                aggregate.SenderId, aggregate.ViewNumber);
            return null;
        }

        // Accept within ±1 view window
        var diff = aggregate.ViewNumber > _currentView
            ? aggregate.ViewNumber - _currentView
            : _currentView - aggregate.ViewNumber;
        if (diff > 1)
            return null;

        // Verify quorum: bitmap must have >= QuorumThreshold bits set
        var voterCount = System.Numerics.BitOperations.PopCount(aggregate.VoterBitmap);
        if (voterCount < _validatorSet.QuorumThreshold)
        {
            _logger.LogWarning("Aggregate vote has insufficient voters ({Count}/{Threshold})",
                voterCount, _validatorSet.QuorumThreshold);
            return null;
        }

        // Collect public keys from bitmap and verify aggregate BLS signature
        var voters = _validatorSet.GetValidatorsFromBitmap(aggregate.VoterBitmap).ToArray();
        var publicKeys = voters.Select(v => v.BlsPublicKey.ToArray()).ToArray();

        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, aggregate.Phase, aggregate.ViewNumber, aggregate.BlockNumber, aggregate.BlockHash);

        if (!_blsSigner.VerifyAggregate(publicKeys, sigPayload, aggregate.AggregateSignature.ToArray()))
        {
            _logger.LogWarning("Invalid aggregate signature for view {View} phase {Phase}",
                aggregate.ViewNumber, aggregate.Phase);
            return null;
        }

        // Store proposal data if we have it
        if (_currentProposalHash != aggregate.BlockHash)
        {
            _logger.LogDebug("Aggregate vote for unknown block hash {Hash}", aggregate.BlockHash);
        }

        return aggregate.Phase switch
        {
            VotePhase.Prepare => HandlePrepareQC(aggregate),
            VotePhase.PreCommit => HandlePreCommitQC(aggregate),
            VotePhase.Commit => HandleCommitQC(aggregate),
            _ => null,
        };
    }

    private ConsensusVoteMessage? HandlePrepareQC(AggregateVoteMessage aggregate)
    {
        if (_state != ConsensusState.Preparing)
            return null;

        _state = ConsensusState.PreCommitting;
        _logger.LogInformation("PREPARE QC received, moving to PRE-COMMIT");
        return CreateVote(VotePhase.PreCommit, aggregate.BlockHash);
    }

    private ConsensusVoteMessage? HandlePreCommitQC(AggregateVoteMessage aggregate)
    {
        if (_state != ConsensusState.PreCommitting)
            return null;

        _state = ConsensusState.Committing;
        _logger.LogInformation("PRE-COMMIT QC received, moving to COMMIT");
        return CreateVote(VotePhase.Commit, aggregate.BlockHash);
    }

    private ConsensusVoteMessage? HandleCommitQC(AggregateVoteMessage aggregate)
    {
        if (_state != ConsensusState.Committing)
            return null;

        _state = ConsensusState.Finalized;
        _logger.LogInformation("COMMIT QC received — block {Hash} finalized at height {Height}",
            aggregate.BlockHash, _currentBlockNumber);
        OnBlockFinalized?.Invoke(aggregate.BlockHash, _currentProposalData ?? [], aggregate.VoterBitmap);
        return null;
    }

    /// <summary>
    /// Build the domain-separated signing payload for BLS consensus signatures.
    /// Format: [1-byte phase tag || 8-byte view LE || 8-byte blockNumber LE || 32-byte blockHash]
    /// This prevents cross-phase and cross-view signature replay attacks (F-CON-01).
    /// </summary>
    private const int ConsensusPayloadSize = 1 + 8 + 8 + Hash256.Size; // 49 bytes

    private static void WriteConsensusSigningPayload(Span<byte> buffer, VotePhase phase, ulong view, ulong blockNumber, Hash256 blockHash)
    {
        buffer[0] = (byte)phase;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], view);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[9..], blockNumber);
        blockHash.WriteTo(buffer[17..]);
    }

    /// <summary>
    /// Build the domain-separated signing payload for view change messages.
    /// Format: [0xFF tag || 8-byte proposedView LE]
    /// </summary>
    private const int ViewChangePayloadSize = 1 + 8; // 9 bytes

    private static void WriteViewChangeSigningPayload(Span<byte> buffer, ulong proposedView)
    {
        buffer[0] = 0xFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], proposedView);
    }

    private ViewChangeMessage RequestViewChange()
    {
        var proposedView = _currentView + 1;
        Span<byte> viewPayload = stackalloc byte[ViewChangePayloadSize];
        WriteViewChangeSigningPayload(viewPayload, proposedView);
        var signature = new BlsSignature(_blsSigner.Sign(_privateKey, viewPayload));
        var publicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));

        return new ViewChangeMessage
        {
            SenderId = _localPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CurrentView = _currentView,
            ProposedView = proposedView,
            VoterSignature = signature,
            VoterPublicKey = publicKey,
        };
    }
}

/// <summary>
/// States of the BasaltBFT consensus engine.
/// </summary>
public enum ConsensusState
{
    Idle,
    Proposing,
    Preparing,
    PreCommitting,
    Committing,
    Finalized,
}
