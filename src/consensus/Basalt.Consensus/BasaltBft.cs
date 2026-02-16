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
    private readonly ValidatorSet _validatorSet;
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
    private readonly TimeSpan _viewTimeout = TimeSpan.FromSeconds(2);
    private ulong? _viewChangeRequestedForView;

    // Callbacks
    // Events raised during consensus - consumed by the node integration layer
#pragma warning disable CS0067
    public event Action<byte[]>? OnBlockProposal;
    public event Action<ConsensusVoteMessage>? OnVote;
#pragma warning restore CS0067
    public event Action<Hash256, byte[]>? OnBlockFinalized;
    public event Action<ulong>? OnViewChange;

    public BasaltBft(
        ValidatorSet validatorSet,
        PeerId localPeerId,
        byte[] privateKey,
        ILogger<BasaltBft> logger,
        IBlsSigner? blsSigner = null)
    {
        _validatorSet = validatorSet;
        _localPeerId = localPeerId;
        _privateKey = privateKey;
        _blsSigner = blsSigner ?? new BlsSigner();
        _logger = logger;
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
    /// Check if this node is the leader for the current view.
    /// </summary>
    public bool IsLeader => _validatorSet.GetLeader(_currentView).PeerId == _localPeerId;

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

        // Sign the proposal
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        blockHash.WriteTo(hashBytes);
        var signature = new BlsSignature(_blsSigner.Sign(_privateKey, hashBytes));

        var proposal = new ConsensusProposalMessage
        {
            SenderId = _localPeerId,
            ViewNumber = _currentView,
            BlockNumber = _currentBlockNumber,
            BlockHash = blockHash,
            BlockData = blockData,
            ProposerSignature = signature,
        };

        _logger.LogInformation("Proposed block {BlockHash} for height {Height}", blockHash, _currentBlockNumber);

        // Self-vote PREPARE
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

                // Verify signature before fast-forwarding
                Span<byte> ffHash = stackalloc byte[Hash256.Size];
                proposal.BlockHash.WriteTo(ffHash);
                if (!_blsSigner.Verify(futureLeader.BlsPublicKey.ToArray(), ffHash, proposal.ProposerSignature.ToArray()))
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

        // Verify leader's signature (may already be verified for fast-forwarded proposals,
        // but the cost is negligible and keeps the code straightforward)
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        proposal.BlockHash.WriteTo(hashBytes);
        if (!_blsSigner.Verify(leader.BlsPublicKey.ToArray(), hashBytes, proposal.ProposerSignature.ToArray()))
        {
            _logger.LogWarning("Invalid proposal signature from {Sender}", proposal.SenderId);
            return null;
        }

        _currentProposalHash = proposal.BlockHash;
        _currentProposalData = proposal.BlockData;
        _state = ConsensusState.Preparing;

        // Count leader's implicit PREPARE vote — the leader self-voted locally in
        // ProposeBlock but doesn't broadcast a separate PREPARE message. Without this,
        // non-leaders can only count (self + other non-leaders) = 3 of 4 votes max,
        // and losing a single vote to network timing prevents quorum.
        HandlePrepareVote(proposal.SenderId, _currentView, proposal.BlockHash);

        // Self-count PREPARE vote before returning it for broadcast
        var vote = CreateVote(VotePhase.Prepare, proposal.BlockHash);
        HandlePrepareVote(_localPeerId, _currentView, proposal.BlockHash);
        return vote;
    }

    /// <summary>
    /// Handle a vote message.
    /// Accepts votes for the current view AND the next view (pre-counting).
    /// Votes for _currentView + 1 are stored but don't trigger phase transitions
    /// (because _state doesn't match). When HandleProposal fast-forwards to that view,
    /// the pre-counted votes are already available, preventing quorum failure when
    /// votes arrive before the proposal due to network timing.
    /// </summary>
    public ConsensusVoteMessage? HandleVote(ConsensusVoteMessage vote)
    {
        if (vote.ViewNumber != _currentView && vote.ViewNumber != _currentView + 1)
            return null;

        if (!_validatorSet.IsValidator(vote.SenderId))
        {
            _logger.LogWarning("Vote from non-validator {Sender}", vote.SenderId);
            return null;
        }

        // Verify vote signature
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        vote.BlockHash.WriteTo(hashBytes);
        if (!_blsSigner.Verify(vote.VoterPublicKey.ToArray(), hashBytes, vote.VoterSignature.ToArray()))
        {
            _logger.LogWarning("Invalid vote signature from {Sender}", vote.SenderId);
            return null;
        }

        // Track vote signature for aggregation
        var sigKey = (vote.ViewNumber, vote.Phase);
        var sigList = _voteSignatures.GetOrAdd(sigKey, _ => new List<(byte[], byte[])>());
        lock (sigList)
        {
            sigList.Add((vote.VoterSignature.ToArray(), vote.VoterPublicKey.ToArray()));
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
        // Use a distinct key (Commit+1 via cast) to avoid collision with consensus phase votes
        var voteKey = (viewChange.ProposedView, (VotePhase)0xFF);
        var votes = _votes.GetOrAdd(voteKey, _ => new HashSet<PeerId>());
        bool newAutoJoin = false;

        lock (votes)
        {
            votes.Add(viewChange.SenderId);

            // Auto-join: if the proposed view is higher than ours, add our own vote.
            // This prevents deadlocks where validators at different views each propose
            // currentView+1 but never converge (e.g. V2 at view 750 proposes 751,
            // while V1/V3 at view 751 propose 752 — neither reaches quorum).
            // HashSet.Add returns true only if the element was newly added.
            if (viewChange.ProposedView > _currentView)
                newAutoJoin = votes.Add(_localPeerId);

            if (votes.Count >= _validatorSet.QuorumThreshold && _currentView < viewChange.ProposedView)
            {
                _currentView = viewChange.ProposedView;
                _state = ConsensusState.Proposing;
                _viewStartTime = DateTimeOffset.UtcNow;
                _viewChangeRequestedForView = null;

                // Selective clearing: keep pre-counted votes for the target view,
                // clear everything else (stale consensus + view change votes).
                var targetView = viewChange.ProposedView;
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

                _logger.LogInformation("View changed to {View}", _currentView);
                OnViewChange?.Invoke(_currentView);
            }
        }

        // Return a signed ViewChangeMessage so the caller can broadcast it.
        // This makes the auto-join vote visible to other nodes, resolving the
        // parity split where two groups of validators alternate even/odd views.
        if (newAutoJoin)
        {
            Span<byte> viewBytes = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(viewBytes, viewChange.ProposedView);
            var signature = new BlsSignature(_blsSigner.Sign(_privateKey, viewBytes));
            var publicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));

            return new ViewChangeMessage
            {
                SenderId = _localPeerId,
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
                _logger.LogInformation("PREPARE quorum reached ({Count}/{Threshold}), moving to PRE-COMMIT",
                    votes.Count, _validatorSet.QuorumThreshold);

                // Self-count PRE-COMMIT before broadcast
                var vote = CreateVote(VotePhase.PreCommit, blockHash);
                HandlePreCommitVote(_localPeerId, view, blockHash);
                return vote;
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
                _logger.LogInformation("PRE-COMMIT quorum reached ({Count}/{Threshold}), moving to COMMIT",
                    votes.Count, _validatorSet.QuorumThreshold);

                // Self-count COMMIT before broadcast
                var vote = CreateVote(VotePhase.Commit, blockHash);
                HandleCommitVote(_localPeerId, view, blockHash);
                return vote;
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

                OnBlockFinalized?.Invoke(blockHash, _currentProposalData ?? []);
                return null;
            }
        }

        return null;
    }

    private ConsensusVoteMessage CreateVote(VotePhase phase, Hash256 blockHash)
    {
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        blockHash.WriteTo(hashBytes);
        var signatureBytes = _blsSigner.Sign(_privateKey, hashBytes);
        var signature = new BlsSignature(signatureBytes);
        var publicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));

        // Track signature for aggregation
        var sigKey = (_currentView, phase);
        var sigList = _voteSignatures.GetOrAdd(sigKey, _ => new List<(byte[], byte[])>());
        lock (sigList)
        {
            sigList.Add((signatureBytes, publicKey.ToArray()));
        }

        return new ConsensusVoteMessage
        {
            SenderId = _localPeerId,
            ViewNumber = _currentView,
            BlockNumber = _currentBlockNumber,
            BlockHash = blockHash,
            Phase = phase,
            VoterSignature = signature,
            VoterPublicKey = publicKey,
        };
    }

    private ViewChangeMessage RequestViewChange()
    {
        var proposedView = _currentView + 1;
        Span<byte> viewBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(viewBytes, proposedView);
        var signature = new BlsSignature(_blsSigner.Sign(_privateKey, viewBytes));
        var publicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));

        return new ViewChangeMessage
        {
            SenderId = _localPeerId,
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
