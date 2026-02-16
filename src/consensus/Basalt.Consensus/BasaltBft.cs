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
    /// </summary>
    public ConsensusVoteMessage? HandleProposal(ConsensusProposalMessage proposal)
    {
        if (proposal.ViewNumber != _currentView)
        {
            _logger.LogWarning("Received proposal for wrong view {View}, expected {Expected}",
                proposal.ViewNumber, _currentView);
            return null;
        }

        var leader = _validatorSet.GetLeader(_currentView);
        if (proposal.SenderId != leader.PeerId)
        {
            _logger.LogWarning("Received proposal from non-leader {Sender}", proposal.SenderId);
            return null;
        }

        // Verify leader's signature
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

        // Self-count PREPARE vote before returning it for broadcast
        var vote = CreateVote(VotePhase.Prepare, proposal.BlockHash);
        HandlePrepareVote(_localPeerId, _currentView, proposal.BlockHash);
        return vote;
    }

    /// <summary>
    /// Handle a vote message.
    /// </summary>
    public ConsensusVoteMessage? HandleVote(ConsensusVoteMessage vote)
    {
        if (vote.ViewNumber != _currentView)
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
    /// </summary>
    public void HandleViewChange(ViewChangeMessage viewChange)
    {
        // Use a distinct key (Commit+1 via cast) to avoid collision with consensus phase votes
        var voteKey = (viewChange.ProposedView, (VotePhase)0xFF);
        var votes = _votes.GetOrAdd(voteKey, _ => new HashSet<PeerId>());

        lock (votes)
        {
            votes.Add(viewChange.SenderId);

            // Auto-join: if the proposed view is higher than ours, add our own vote.
            // This prevents deadlocks where validators at different views each propose
            // currentView+1 but never converge (e.g. V2 at view 750 proposes 751,
            // while V1/V3 at view 751 propose 752 — neither reaches quorum).
            if (viewChange.ProposedView > _currentView)
                votes.Add(_localPeerId);

            if (votes.Count >= _validatorSet.QuorumThreshold && _currentView < viewChange.ProposedView)
            {
                _currentView = viewChange.ProposedView;
                _state = ConsensusState.Proposing;
                _viewStartTime = DateTimeOffset.UtcNow;
                _viewChangeRequestedForView = null;
                _votes.Clear();
                _voteSignatures.Clear();
                _logger.LogInformation("View changed to {View}", _currentView);
                OnViewChange?.Invoke(_currentView);
            }
        }
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
