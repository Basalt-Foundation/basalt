using System.Collections.Concurrent;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using Microsoft.Extensions.Logging;

namespace Basalt.Consensus;

/// <summary>
/// Pipelined consensus that overlaps phases across consecutive blocks.
/// While block N is in COMMIT phase, block N+1 can already be in PREPARE phase.
/// This improves throughput by reducing the effective block finalization time.
///
/// Enhancements over BasaltBft:
/// - Multiple concurrent rounds (up to MaxPipelineDepth)
/// - Sequential finalization ordering (block N must finalize before N+1 is released)
/// - Per-round view change with timeout
/// - BLS signature aggregation per round
/// </summary>
public sealed class PipelinedConsensus
{
    private ValidatorSet _validatorSet;
    private readonly PeerId _localPeerId;
    private readonly byte[] _privateKey;
    private readonly IBlsSigner _blsSigner;
    private readonly ILogger<PipelinedConsensus> _logger;
    private readonly uint _chainId;

    // Active consensus rounds (one per block height)
    private readonly ConcurrentDictionary<ulong, ConsensusRound> _activeRounds = new();

    // Maximum concurrent pipelined rounds
    private const int MaxPipelineDepth = 3;

    // View change tracking per view
    private readonly ConcurrentDictionary<ulong, HashSet<PeerId>> _viewChangeVotes = new();

    // Per-round view timeout
    private readonly TimeSpan _roundTimeout = TimeSpan.FromSeconds(2);

    // Finalization ordering
    private ulong _lastFinalizedBlock;
    private readonly ConcurrentDictionary<ulong, (Hash256 Hash, byte[] Data, ulong Bitmap)> _pendingFinalizations = new();

    // View advancement: after a view change, new rounds must use a view >= _minNextView
    // to avoid reusing the same view number (which causes false double-sign detection).
    private ulong _minNextView;

    // Callbacks
    public event Action<Hash256, byte[], ulong>? OnBlockFinalized;
    public event Action<ulong>? OnViewChange;

    /// <summary>
    /// Fired when a proposal arrives for a block far beyond our pipeline,
    /// indicating this node is behind and needs to sync.
    /// </summary>
    public event Action<ulong>? OnBehindDetected;

    public PipelinedConsensus(
        ValidatorSet validatorSet,
        PeerId localPeerId,
        byte[] privateKey,
        IBlsSigner blsSigner,
        ILogger<PipelinedConsensus> logger,
        ulong lastFinalizedBlock = 0,
        uint chainId = 0)
    {
        _validatorSet = validatorSet;
        _localPeerId = localPeerId;
        _privateKey = privateKey;
        _chainId = chainId;
        _blsSigner = blsSigner;
        _logger = logger;
        _lastFinalizedBlock = lastFinalizedBlock;
    }

    /// <summary>
    /// Minimum view number for new rounds. After a view change advances the view,
    /// this ensures new rounds don't reuse the old view number (which would cause
    /// false double-sign detection in NodeCoordinator).
    /// </summary>
    public ulong MinNextView => _minNextView;

    /// <summary>
    /// Atomically replace the validator set (e.g., on epoch transition).
    /// Clears all in-flight rounds, view change votes, and pending finalizations.
    /// </summary>
    public void UpdateValidatorSet(ValidatorSet newSet)
    {
        _validatorSet = newSet;
        _activeRounds.Clear();
        _viewChangeVotes.Clear();
        _pendingFinalizations.Clear();
        _minNextView = 0;
    }

    /// <summary>
    /// Update the last finalized block number after sync.
    /// Clears all in-flight rounds that are now stale.
    /// </summary>
    public void UpdateLastFinalizedBlock(ulong blockNumber)
    {
        _lastFinalizedBlock = blockNumber;

        // Remove all rounds for blocks that are already finalized
        foreach (var key in _activeRounds.Keys)
        {
            if (key <= blockNumber)
                _activeRounds.TryRemove(key, out _);
        }

        _pendingFinalizations.Clear();
        _minNextView = 0;
    }

    /// <summary>
    /// Number of active pipelined rounds.
    /// </summary>
    public int ActiveRoundCount => _activeRounds.Count;

    /// <summary>
    /// Last finalized block number.
    /// </summary>
    public ulong LastFinalizedBlock => _lastFinalizedBlock;

    /// <summary>
    /// Get the state of a specific block's consensus round.
    /// </summary>
    public ConsensusState? GetRoundState(ulong blockNumber)
    {
        return _activeRounds.TryGetValue(blockNumber, out var round) ? round.State : null;
    }

    /// <summary>
    /// Start a new consensus round for a block. Can be called while previous blocks
    /// are still in consensus, enabling pipelining.
    /// </summary>
    public ConsensusProposalMessage? StartRound(ulong blockNumber, byte[] blockData, Hash256 blockHash)
    {
        if (_activeRounds.Count >= MaxPipelineDepth)
        {
            _logger.LogWarning("Pipeline depth exceeded ({Count}/{Max}), deferring block {Block}",
                _activeRounds.Count, MaxPipelineDepth, blockNumber);
            return null;
        }

        // Use the higher of blockNumber and _minNextView to ensure view advances
        // after view changes (prevents false double-sign detection and rotates leaders).
        var effectiveView = Math.Max(blockNumber, _minNextView);

        var round = new ConsensusRound
        {
            BlockNumber = blockNumber,
            View = effectiveView,
            State = ConsensusState.Proposing,
            BlockHash = blockHash,
            BlockData = blockData,
            StartTime = DateTimeOffset.UtcNow,
        };

        if (!_activeRounds.TryAdd(blockNumber, round))
            return null; // Round already exists

        var leader = _validatorSet.GetLeader(round.View);
        if (leader.PeerId != _localPeerId)
        {
            round.State = ConsensusState.Preparing;
            return null; // Not leader, wait for proposal
        }

        round.State = ConsensusState.Preparing;

        // Sign and propose (domain-separated: phase || view || blockNumber || blockHash)
        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, _chainId, VotePhase.Prepare, round.View, blockNumber, blockHash);
        var signatureBytes = _blsSigner.Sign(_privateKey, sigPayload);
        var signature = new BlsSignature(signatureBytes);

        // Self-vote PREPARE with signature tracking
        RecordVote(round, VotePhase.Prepare, _localPeerId, signatureBytes,
            _blsSigner.GetPublicKey(_privateKey));

        // Cascade through phases if self-vote alone meets quorum (e.g., single validator)
        TryCascadeFromPrepare(round);

        return new ConsensusProposalMessage
        {
            SenderId = _localPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ViewNumber = round.View,
            BlockNumber = blockNumber,
            BlockHash = blockHash,
            BlockData = blockData,
            ProposerSignature = signature,
        };
    }

    /// <summary>
    /// Handle a proposal for a pipelined block.
    /// </summary>
    public ConsensusVoteMessage? HandleProposal(ConsensusProposalMessage proposal)
    {
        // Detect if we are too far behind to participate — trigger sync instead
        // of creating orphaned rounds we can never finalize.
        if (proposal.BlockNumber > _lastFinalizedBlock + (ulong)MaxPipelineDepth + 1)
        {
            _logger.LogWarning("Proposal for block {Block} but last finalized is {Last} — we are behind",
                proposal.BlockNumber, _lastFinalizedBlock);
            OnBehindDetected?.Invoke(proposal.BlockNumber);
            return null;
        }

        var round = _activeRounds.GetOrAdd(proposal.BlockNumber, _ => new ConsensusRound
        {
            BlockNumber = proposal.BlockNumber,
            View = proposal.ViewNumber,
            State = ConsensusState.Proposing,
            BlockHash = proposal.BlockHash,
            BlockData = proposal.BlockData,
            StartTime = DateTimeOffset.UtcNow,
        });

        if (round.State == ConsensusState.Finalized)
            return null;

        // M-01: Reject conflicting proposals for the same block
        // If round already has a different block hash at the same or lower view, this is
        // either equivocation (same view) or a stale proposal (lower view). Only accept
        // proposals from a strictly higher view (view change rotated the leader).
        if (round.BlockHash != Hash256.Zero && round.BlockHash != proposal.BlockHash
            && proposal.ViewNumber <= round.View)
        {
            _logger.LogWarning(
                "Rejecting conflicting proposal for block {Block}: existing view {ExistingView} hash {ExistingHash}, proposed view {ProposedView} hash {ProposedHash}",
                proposal.BlockNumber, round.View, round.BlockHash, proposal.ViewNumber, proposal.BlockHash);
            return null;
        }

        // Verify leader
        var leader = _validatorSet.GetLeader(proposal.ViewNumber);
        if (proposal.SenderId != leader.PeerId)
            return null;

        // Verify signature (domain-separated)
        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, _chainId, VotePhase.Prepare, proposal.ViewNumber, proposal.BlockNumber, proposal.BlockHash);
        if (!_blsSigner.Verify(leader.BlsPublicKey.ToArray(), sigPayload, proposal.ProposerSignature.ToArray()))
            return null;

        round.View = proposal.ViewNumber;
        round.BlockHash = proposal.BlockHash;
        round.BlockData = proposal.BlockData;
        round.State = ConsensusState.Preparing;

        // Count leader's implicit PREPARE vote (the leader self-voted locally)
        RecordVote(round, VotePhase.Prepare, proposal.SenderId,
            proposal.ProposerSignature.ToArray(), leader.BlsPublicKey.ToArray());

        return CreateVote(round, VotePhase.Prepare);
    }

    /// <summary>
    /// Handle a vote for a pipelined block.
    /// </summary>
    public ConsensusVoteMessage? HandleVote(ConsensusVoteMessage vote)
    {
        if (!_activeRounds.TryGetValue(vote.BlockNumber, out var round))
            return null;

        if (round.State == ConsensusState.Finalized)
            return null;

        if (!_validatorSet.IsValidator(vote.SenderId))
            return null;

        // MED-01: Verify VoterPublicKey matches the registered BLS public key for the voter.
        // Without this check, an attacker could submit votes with valid signatures from a
        // different key than the one registered for their validator identity.
        var voterInfo = _validatorSet.GetByPeerId(vote.SenderId);
        if (voterInfo == null || voterInfo.BlsPublicKey.ToArray().AsSpan().SequenceEqual(vote.VoterPublicKey.ToArray()) == false)
            return null;

        // F-CON-02: Verify vote is for the correct block hash
        if (vote.BlockHash != round.BlockHash)
            return null;

        // Verify signature (domain-separated)
        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, _chainId, vote.Phase, vote.ViewNumber, vote.BlockNumber, vote.BlockHash);
        if (!_blsSigner.Verify(vote.VoterPublicKey.ToArray(), sigPayload, vote.VoterSignature.ToArray()))
            return null;

        // Record vote with signature for aggregation
        RecordVote(round, vote.Phase, vote.SenderId,
            vote.VoterSignature.ToArray(), vote.VoterPublicKey.ToArray());

        return vote.Phase switch
        {
            VotePhase.Prepare => CheckPhaseTransition(round, VotePhase.Prepare, ConsensusState.Preparing,
                ConsensusState.PreCommitting, VotePhase.PreCommit),
            VotePhase.PreCommit => CheckPhaseTransition(round, VotePhase.PreCommit, ConsensusState.PreCommitting,
                ConsensusState.Committing, VotePhase.Commit),
            VotePhase.Commit => CheckCommitQuorum(round),
            _ => null,
        };
    }

    /// <summary>
    /// Check for timed-out rounds and return a view change message if needed.
    /// </summary>
    public ViewChangeMessage? CheckViewTimeout()
    {
        foreach (var (blockNumber, round) in _activeRounds)
        {
            if (round.State == ConsensusState.Finalized || round.State == ConsensusState.Idle)
                continue;

            // Don't send duplicate view change for a round that already requested one
            if (round.ViewChangeRequested)
                continue;

            if (DateTimeOffset.UtcNow - round.StartTime > _roundTimeout)
            {
                _logger.LogWarning("Round for block {Block} timed out in state {State}", blockNumber, round.State);
                round.ViewChangeRequested = true;
                round.StartTime = DateTimeOffset.UtcNow; // Reset timer
                return RequestViewChange(round);
            }
        }

        return null;
    }

    /// <summary>
    /// Handle a view change message. On quorum, abort all in-flight rounds and restart.
    /// Returns a ViewChangeMessage if this node auto-joined (so the caller can broadcast it).
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
        WriteViewChangeSigningPayload(sigPayload, _chainId, viewChange.ProposedView);
        if (!_blsSigner.Verify(viewChange.VoterPublicKey.ToArray(), sigPayload, viewChange.VoterSignature.ToArray()))
        {
            _logger.LogWarning("Invalid view change signature from {Sender}", viewChange.SenderId);
            return null;
        }

        var votes = _viewChangeVotes.GetOrAdd(viewChange.ProposedView, _ => new HashSet<PeerId>());
        bool newAutoJoin = false;

        lock (votes)
        {
            votes.Add(viewChange.SenderId);

            // Auto-join: if the proposed view is higher than our highest active round
            // AND at least one round has independently timed out, add our own vote.
            // The timeout guard prevents a cascade where a single validator's timeout
            // instantly propagates to all others, racing against proposals.
            ulong maxActiveView = 0;
            bool anyRoundTimedOut = false;
            foreach (var round in _activeRounds.Values)
            {
                if (round.State != ConsensusState.Finalized)
                {
                    if (round.View > maxActiveView)
                        maxActiveView = round.View;
                    if (round.ViewChangeRequested)
                        anyRoundTimedOut = true;
                }
            }
            if (viewChange.ProposedView > maxActiveView && anyRoundTimedOut)
                newAutoJoin = votes.Add(_localPeerId);

            if (votes.Count >= _validatorSet.QuorumThreshold)
            {
                _logger.LogInformation("View change quorum reached for view {View}, aborting in-flight rounds",
                    viewChange.ProposedView);

                // Advance minimum view so new rounds use a higher view number,
                // which rotates the leader and avoids false double-sign detection.
                _minNextView = Math.Max(_minNextView, viewChange.ProposedView);

                // Abort all non-finalized rounds
                var toRemove = new List<ulong>();
                foreach (var (blockNumber, round) in _activeRounds)
                {
                    if (round.State != ConsensusState.Finalized)
                        toRemove.Add(blockNumber);
                }

                foreach (var bn in toRemove)
                    _activeRounds.TryRemove(bn, out _);

                // Clean up view change votes for old views
                foreach (var key in _viewChangeVotes.Keys)
                {
                    if (key <= viewChange.ProposedView)
                        _viewChangeVotes.TryRemove(key, out _);
                }

                OnViewChange?.Invoke(viewChange.ProposedView);
            }
        }

        // Broadcast auto-join to make the vote visible to other nodes
        if (newAutoJoin)
        {
            Span<byte> viewPayload = stackalloc byte[ViewChangePayloadSize];
            WriteViewChangeSigningPayload(viewPayload, _chainId, viewChange.ProposedView);
            var signature = new BlsSignature(_blsSigner.Sign(_privateKey, viewPayload));
            var publicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));

            return new ViewChangeMessage
            {
                SenderId = _localPeerId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CurrentView = 0, // Not meaningful in pipelined mode
                ProposedView = viewChange.ProposedView,
                VoterSignature = signature,
                VoterPublicKey = publicKey,
            };
        }

        return null;
    }

    /// <summary>
    /// Clean up finalized rounds.
    /// </summary>
    public void CleanupFinalizedRounds()
    {
        foreach (var (blockNumber, round) in _activeRounds)
        {
            if (round.State == ConsensusState.Finalized)
                _activeRounds.TryRemove(blockNumber, out _);
        }
    }

    /// <summary>
    /// Get the aggregated commit signatures for a finalized round.
    /// </summary>
    public byte[]? GetAggregateSignature(ulong blockNumber)
    {
        if (!_activeRounds.TryGetValue(blockNumber, out var round))
            return null;

        if (round.State != ConsensusState.Finalized)
            return null;

        lock (round.CommitSignatures)
        {
            if (round.CommitSignatures.Count == 0)
                return null;

            var sigs = round.CommitSignatures.Select(s => s.Signature).ToArray();
            return _blsSigner.AggregateSignatures(sigs);
        }
    }

    private void RecordVote(ConsensusRound round, VotePhase phase, PeerId voter,
        byte[]? signature = null, byte[]? publicKey = null)
    {
        var votes = phase switch
        {
            VotePhase.Prepare => round.PrepareVotes,
            VotePhase.PreCommit => round.PreCommitVotes,
            VotePhase.Commit => round.CommitVotes,
            _ => throw new ArgumentException($"Unknown phase: {phase}"),
        };

        bool isNew;
        lock (votes)
            isNew = votes.Add(voter);

        // M-02: Only track signatures for new votes (prevent duplicate aggregation)
        if (isNew && signature != null && publicKey != null)
        {
            var sigList = phase switch
            {
                VotePhase.Prepare => round.PrepareSignatures,
                VotePhase.PreCommit => round.PreCommitSignatures,
                VotePhase.Commit => round.CommitSignatures,
                _ => throw new ArgumentException($"Unknown phase: {phase}"),
            };

            lock (sigList)
                sigList.Add((signature, publicKey));
        }
    }

    private ConsensusVoteMessage? CheckPhaseTransition(
        ConsensusRound round, VotePhase currentPhase,
        ConsensusState requiredState, ConsensusState nextState,
        VotePhase nextPhase)
    {
        var votes = currentPhase switch
        {
            VotePhase.Prepare => round.PrepareVotes,
            VotePhase.PreCommit => round.PreCommitVotes,
            _ => round.CommitVotes,
        };

        lock (votes)
        {
            if (votes.Count >= _validatorSet.QuorumThreshold && round.State == requiredState)
            {
                round.State = nextState;
                _logger.LogDebug("Block {Block}: {Phase} quorum -> {NextState}",
                    round.BlockNumber, currentPhase, nextState);
                return CreateVote(round, nextPhase);
            }
        }

        return null;
    }

    private ConsensusVoteMessage? CheckCommitQuorum(ConsensusRound round)
    {
        lock (round.CommitVotes)
        {
            if (round.CommitVotes.Count >= _validatorSet.QuorumThreshold && round.State == ConsensusState.Committing)
            {
                round.State = ConsensusState.Finalized;
                _logger.LogInformation("Block {Block} reached COMMIT quorum via pipeline", round.BlockNumber);

                var commitBitmap = ComputeCommitBitmap(round.CommitVotes);
                TryFinalizeSequential(round.BlockNumber, round.BlockHash, round.BlockData ?? [], commitBitmap);
            }
        }

        return null;
    }

    private void TryFinalizeSequential(ulong blockNumber, Hash256 hash, byte[] data, ulong commitBitmap)
    {
        // If this is the next expected block, finalize immediately
        if (blockNumber == _lastFinalizedBlock + 1)
        {
            _lastFinalizedBlock = blockNumber;
            OnBlockFinalized?.Invoke(hash, data, commitBitmap);

            // Drain any buffered blocks that are now sequential
            while (_pendingFinalizations.TryRemove(_lastFinalizedBlock + 1, out var pending))
            {
                _lastFinalizedBlock = _lastFinalizedBlock + 1;
                OnBlockFinalized?.Invoke(pending.Hash, pending.Data, pending.Bitmap);
                _logger.LogInformation("Drained buffered finalization for block {Block}", _lastFinalizedBlock);
            }
        }
        else if (blockNumber > _lastFinalizedBlock + 1)
        {
            // Buffer for later — a previous block hasn't finalized yet
            _pendingFinalizations.TryAdd(blockNumber, (hash, data, commitBitmap));
            _logger.LogDebug("Buffered finalization for block {Block} (waiting for {Expected})",
                blockNumber, _lastFinalizedBlock + 1);
        }
    }

    /// <summary>
    /// After a self-vote, cascade through phases if quorum is already met.
    /// This handles the single-validator (or low-quorum) case where the self-vote
    /// alone is enough to advance through PREPARE → PRE-COMMIT → COMMIT → FINALIZED.
    /// </summary>
    private void TryCascadeFromPrepare(ConsensusRound round)
    {
        // Check PREPARE → PRE-COMMIT
        lock (round.PrepareVotes)
        {
            if (round.PrepareVotes.Count >= _validatorSet.QuorumThreshold && round.State == ConsensusState.Preparing)
            {
                round.State = ConsensusState.PreCommitting;
                RecordSelfVote(round, VotePhase.PreCommit);
            }
            else return;
        }

        // Check PRE-COMMIT → COMMIT
        lock (round.PreCommitVotes)
        {
            if (round.PreCommitVotes.Count >= _validatorSet.QuorumThreshold && round.State == ConsensusState.PreCommitting)
            {
                round.State = ConsensusState.Committing;
                RecordSelfVote(round, VotePhase.Commit);
            }
            else return;
        }

        // Check COMMIT → FINALIZED
        lock (round.CommitVotes)
        {
            if (round.CommitVotes.Count >= _validatorSet.QuorumThreshold && round.State == ConsensusState.Committing)
            {
                round.State = ConsensusState.Finalized;
                _logger.LogInformation("Block {Block} reached COMMIT quorum via pipeline (cascade)", round.BlockNumber);
                var commitBitmap = ComputeCommitBitmap(round.CommitVotes);
                TryFinalizeSequential(round.BlockNumber, round.BlockHash, round.BlockData ?? [], commitBitmap);
            }
        }
    }

    /// <summary>
    /// Compute a voter bitmap from the set of commit voters.
    /// Bit i is set if the validator at index i committed.
    /// </summary>
    private ulong ComputeCommitBitmap(HashSet<PeerId> commitVoters)
    {
        ulong bitmap = 0;
        foreach (var voter in commitVoters)
        {
            var idx = _validatorSet.GetValidatorIndex(voter);
            if (idx >= 0 && idx < 64)
                bitmap |= 1UL << idx;
        }
        return bitmap;
    }

    private void RecordSelfVote(ConsensusRound round, VotePhase phase)
    {
        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, _chainId, phase, round.View, round.BlockNumber, round.BlockHash);
        var signatureBytes = _blsSigner.Sign(_privateKey, sigPayload);
        var publicKeyBytes = _blsSigner.GetPublicKey(_privateKey);
        RecordVote(round, phase, _localPeerId, signatureBytes, publicKeyBytes);
    }

    private ConsensusVoteMessage CreateVote(ConsensusRound round, VotePhase phase)
    {
        Span<byte> sigPayload = stackalloc byte[ConsensusPayloadSize];
        WriteConsensusSigningPayload(sigPayload, _chainId, phase, round.View, round.BlockNumber, round.BlockHash);
        var signatureBytes = _blsSigner.Sign(_privateKey, sigPayload);
        var signature = new BlsSignature(signatureBytes);
        var publicKeyBytes = _blsSigner.GetPublicKey(_privateKey);
        var publicKey = new BlsPublicKey(publicKeyBytes);

        var vote = new ConsensusVoteMessage
        {
            SenderId = _localPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ViewNumber = round.View,
            BlockNumber = round.BlockNumber,
            BlockHash = round.BlockHash,
            Phase = phase,
            VoterSignature = signature,
            VoterPublicKey = publicKey,
        };

        // Self-record with signature
        RecordVote(round, phase, _localPeerId, signatureBytes, publicKeyBytes);

        return vote;
    }

    private ViewChangeMessage RequestViewChange(ConsensusRound round)
    {
        var proposedView = round.View + 1;
        Span<byte> viewPayload = stackalloc byte[ViewChangePayloadSize];
        WriteViewChangeSigningPayload(viewPayload, _chainId, proposedView);
        var signature = new BlsSignature(_blsSigner.Sign(_privateKey, viewPayload));
        var publicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));

        return new ViewChangeMessage
        {
            SenderId = _localPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CurrentView = round.View,
            ProposedView = proposedView,
            VoterSignature = signature,
            VoterPublicKey = publicKey,
        };
    }

    /// <summary>
    /// Build the domain-separated signing payload for BLS consensus signatures.
    /// Format: [4-byte chainId LE || 1-byte phase tag || 8-byte view LE || 8-byte blockNumber LE || 32-byte blockHash]
    /// L-03: Chain ID prevents cross-chain signature replay (F-CON-01).
    /// </summary>
    private const int ConsensusPayloadSize = 4 + 1 + 8 + 8 + Hash256.Size; // 53 bytes

    private static void WriteConsensusSigningPayload(Span<byte> buffer, uint chainId, VotePhase phase, ulong view, ulong blockNumber, Hash256 blockHash)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, chainId);
        buffer[4] = (byte)phase;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[5..], view);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[13..], blockNumber);
        blockHash.WriteTo(buffer[21..]);
    }

    /// <summary>
    /// Build the domain-separated signing payload for view change messages.
    /// Format: [4-byte chainId LE || 0xFF tag || 8-byte proposedView LE]
    /// L-04: Chain ID prevents cross-chain view change replay.
    /// </summary>
    private const int ViewChangePayloadSize = 4 + 1 + 8; // 13 bytes

    private static void WriteViewChangeSigningPayload(Span<byte> buffer, uint chainId, ulong proposedView)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer, chainId);
        buffer[4] = 0xFF;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[5..], proposedView);
    }

    /// <summary>
    /// Internal state for a single consensus round.
    /// LOW-04: State and ViewChangeRequested use volatile backing fields for thread-safe access.
    /// </summary>
    private sealed class ConsensusRound
    {
        public ulong BlockNumber { get; init; }
        public ulong View { get; set; }

        private int _state;
        public ConsensusState State
        {
            get => (ConsensusState)Volatile.Read(ref _state);
            set => Volatile.Write(ref _state, (int)value);
        }

        public Hash256 BlockHash { get; set; }
        public byte[]? BlockData { get; set; }
        public DateTimeOffset StartTime { get; set; }

        private int _viewChangeRequested;
        public bool ViewChangeRequested
        {
            get => Volatile.Read(ref _viewChangeRequested) != 0;
            set => Volatile.Write(ref _viewChangeRequested, value ? 1 : 0);
        }
        public HashSet<PeerId> PrepareVotes { get; } = new();
        public HashSet<PeerId> PreCommitVotes { get; } = new();
        public HashSet<PeerId> CommitVotes { get; } = new();

        // Signature tracking for BLS aggregation
        public List<(byte[] Signature, byte[] PublicKey)> PrepareSignatures { get; } = new();
        public List<(byte[] Signature, byte[] PublicKey)> PreCommitSignatures { get; } = new();
        public List<(byte[] Signature, byte[] PublicKey)> CommitSignatures { get; } = new();
    }
}
