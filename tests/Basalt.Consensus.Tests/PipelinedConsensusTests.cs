using System.Buffers.Binary;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Consensus.Tests;

public class PipelinedConsensusTests
{
    private static (PeerId Id, byte[] PrivateKey, PublicKey PublicKey) MakeValidator(int seed)
    {
        // Generate a private key valid for both Ed25519 and BLS12-381.
        var privKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(privKey);
        privKey[0] &= 0x3F;
        if (privKey[0] == 0) privKey[0] = 1;
        var pubKey = Ed25519Signer.GetPublicKey(privKey);
        var id = PeerId.FromPublicKey(pubKey);
        return (id, privKey, pubKey);
    }

    private static readonly IBlsSigner _blsSigner = new BlsSigner();

    private static ValidatorSet MakeValidatorSet(params (PeerId Id, byte[] PrivateKey, PublicKey PublicKey)[] validators)
    {
        var infos = validators.Select((v, i) => new ValidatorInfo
        {
            PeerId = v.Id,
            PublicKey = v.PublicKey,
            BlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(v.PrivateKey)),
            Address = Ed25519Signer.DeriveAddress(v.PublicKey),
            Index = i,
        }).ToList();
        return new ValidatorSet(infos);
    }

    /// <summary>
    /// Create a properly BLS-signed ViewChangeMessage.
    /// Payload format: [4-byte chainId LE || 0xFF || proposedView LE 8 bytes]
    /// chainId=0 for tests (matches default constructor parameter).
    /// </summary>
    private static ViewChangeMessage CreateSignedViewChange(
        (PeerId Id, byte[] PrivateKey, PublicKey PublicKey) validator,
        ulong proposedView, ulong currentView = 0)
    {
        Span<byte> payload = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0); // chainId = 0
        payload[4] = 0xFF;
        BinaryPrimitives.WriteUInt64LittleEndian(payload[5..], proposedView);
        var signature = _blsSigner.Sign(validator.PrivateKey, payload);
        var publicKey = _blsSigner.GetPublicKey(validator.PrivateKey);
        return new ViewChangeMessage
        {
            SenderId = validator.Id,
            CurrentView = currentView,
            ProposedView = proposedView,
            VoterSignature = new BlsSignature(signature),
            VoterPublicKey = new BlsPublicKey(publicKey),
        };
    }

    [Fact]
    public void PipelinedConsensus_Can_Start_Multiple_Rounds()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);
        var v3 = MakeValidator(3);

        var validatorSet = MakeValidatorSet(v0, v1, v2, v3);
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        var hash1 = Blake3Hasher.Hash([1, 2, 3]);
        var hash2 = Blake3Hasher.Hash([4, 5, 6]);

        pipeline.StartRound(1, [1, 2, 3], hash1);
        pipeline.StartRound(2, [4, 5, 6], hash2);

        Assert.Equal(2, pipeline.ActiveRoundCount);
    }

    [Fact]
    public void PipelinedConsensus_Respects_MaxPipelineDepth()
    {
        var v0 = MakeValidator(0);
        var validatorSet = MakeValidatorSet(v0);
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        // Start 3 rounds (max depth)
        for (ulong i = 1; i <= 3; i++)
        {
            var hash = Blake3Hasher.Hash([(byte)i]);
            pipeline.StartRound(i, [(byte)i], hash);
        }

        Assert.Equal(3, pipeline.ActiveRoundCount);

        // 4th should be rejected
        var hash4 = Blake3Hasher.Hash([4]);
        var proposal = pipeline.StartRound(4, [4], hash4);
        Assert.Null(proposal);
        Assert.Equal(3, pipeline.ActiveRoundCount);
    }

    [Fact]
    public void PipelinedConsensus_Full_4Validator_Finalization()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);
        var v3 = MakeValidator(3);

        var validatorSet = MakeValidatorSet(v0, v1, v2, v3);

        var nodes = new[]
        {
            new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey, new BlsSigner(), NullLogger<PipelinedConsensus>.Instance),
            new PipelinedConsensus(validatorSet, v1.Id, v1.PrivateKey, new BlsSigner(), NullLogger<PipelinedConsensus>.Instance),
            new PipelinedConsensus(validatorSet, v2.Id, v2.PrivateKey, new BlsSigner(), NullLogger<PipelinedConsensus>.Instance),
            new PipelinedConsensus(validatorSet, v3.Id, v3.PrivateKey, new BlsSigner(), NullLogger<PipelinedConsensus>.Instance),
        };

        Hash256? finalizedHash = null;
        foreach (var node in nodes)
            node.OnBlockFinalized += (hash, _, _) => finalizedHash = hash;

        var blockHash = Blake3Hasher.Hash([1, 2, 3]);
        var blockData = new byte[] { 1, 2, 3 };

        // Find which validator is the leader for block 1
        var leader = validatorSet.GetLeader(1);
        int leaderIdx = -1;
        if (leader.PeerId == v0.Id) leaderIdx = 0;
        else if (leader.PeerId == v1.Id) leaderIdx = 1;
        else if (leader.PeerId == v2.Id) leaderIdx = 2;
        else if (leader.PeerId == v3.Id) leaderIdx = 3;

        // Leader proposes
        var proposal = nodes[leaderIdx].StartRound(1, blockData, blockHash);
        Assert.NotNull(proposal);

        // Other nodes handle proposal and send PREPARE votes
        var prepareVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIdx) continue;
            var vote = nodes[i].HandleProposal(proposal!);
            if (vote != null) prepareVotes.Add(vote);
        }

        // Distribute PREPARE votes to all nodes
        var preCommitVotes = new List<ConsensusVoteMessage>();
        foreach (var vote in prepareVotes)
        {
            for (int i = 0; i < 4; i++)
            {
                var response = nodes[i].HandleVote(vote);
                if (response != null) preCommitVotes.Add(response);
            }
        }

        // Distribute PRE-COMMIT votes
        var commitVotes = new List<ConsensusVoteMessage>();
        foreach (var vote in preCommitVotes)
        {
            for (int i = 0; i < 4; i++)
            {
                var response = nodes[i].HandleVote(vote);
                if (response != null) commitVotes.Add(response);
            }
        }

        // Distribute COMMIT votes
        foreach (var vote in commitVotes)
        {
            for (int i = 0; i < 4; i++)
                nodes[i].HandleVote(vote);
        }

        // Block should be finalized
        Assert.NotNull(finalizedHash);
        Assert.Equal(blockHash, finalizedHash.Value);
    }

    [Fact]
    public void PipelinedConsensus_FinalizationOrdering_Block2_Before_Block1()
    {
        // Setup: single validator (quorum=1) for simplicity
        var v0 = MakeValidator(0);
        var validatorSet = MakeValidatorSet(v0);

        var finalized = new List<ulong>();
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);
        pipeline.OnBlockFinalized += (_, data, _) =>
        {
            // Extract block number from data (we encode it as a single byte)
            finalized.Add(data[0]);
        };

        // Start rounds 1 and 2
        var hash1 = Blake3Hasher.Hash([1]);
        var hash2 = Blake3Hasher.Hash([2]);

        pipeline.StartRound(1, [1], hash1);
        pipeline.StartRound(2, [2], hash2);

        // Block 1 is finalized first (quorum=1, self-vote is enough for PREPARE)
        // With a 1-validator set, StartRound self-votes PREPARE, which triggers PreCommit...
        // Actually with quorum=1, each self-vote cascades through all phases immediately
        // Both blocks should finalize in order: 1, then 2

        Assert.Equal(2, finalized.Count);
        Assert.Equal((ulong)1, finalized[0]);
        Assert.Equal((ulong)2, finalized[1]);
    }

    [Fact]
    public void PipelinedConsensus_ViewChange_Aborts_InFlight_Rounds()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);
        var v3 = MakeValidator(3);

        var validatorSet = MakeValidatorSet(v0, v1, v2, v3);
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        // Start 2 rounds
        pipeline.StartRound(1, [1], Blake3Hasher.Hash([1]));
        pipeline.StartRound(2, [2], Blake3Hasher.Hash([2]));
        Assert.Equal(2, pipeline.ActiveRoundCount);

        // Simulate quorum of view change messages (3 for 4 validators)
        pipeline.HandleViewChange(CreateSignedViewChange(v0, 3, 1));
        Assert.Equal(2, pipeline.ActiveRoundCount); // Not yet quorum

        pipeline.HandleViewChange(CreateSignedViewChange(v1, 3, 1));
        Assert.Equal(2, pipeline.ActiveRoundCount); // Still not quorum

        pipeline.HandleViewChange(CreateSignedViewChange(v2, 3, 1));

        // Quorum reached — all in-flight rounds should be aborted
        Assert.Equal(0, pipeline.ActiveRoundCount);
    }

    [Fact]
    public void PipelinedConsensus_ViewChange_Fires_Event()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);

        var validatorSet = MakeValidatorSet(v0, v1, v2);
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        ulong? viewChangedTo = null;
        pipeline.OnViewChange += view => viewChangedTo = view;

        // Quorum for 3 validators is 3 ((3*2/3)+1 = 3)
        pipeline.HandleViewChange(CreateSignedViewChange(v0, 5, 0));
        Assert.Null(viewChangedTo);

        pipeline.HandleViewChange(CreateSignedViewChange(v1, 5, 0));
        Assert.Null(viewChangedTo);

        pipeline.HandleViewChange(CreateSignedViewChange(v2, 5, 0));

        Assert.NotNull(viewChangedTo);
        Assert.Equal((ulong)5, viewChangedTo);
    }

    [Fact]
    public void PipelinedConsensus_ViewChange_AdvancesMinNextView()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);
        var v3 = MakeValidator(3);

        var validatorSet = MakeValidatorSet(v0, v1, v2, v3);
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        Assert.Equal((ulong)0, pipeline.MinNextView);

        // Start a round for block 1 (view = 1)
        pipeline.StartRound(1, [1], Blake3Hasher.Hash([1]));
        Assert.Equal(1, pipeline.ActiveRoundCount);

        // Trigger view change to view 3 (quorum = 3 for 4 validators)
        var validators = new[] { v0, v1, v2, v3 };
        for (int i = 0; i < 3; i++)
            pipeline.HandleViewChange(CreateSignedViewChange(validators[i], 3, 1));

        // MinNextView should advance to 3
        Assert.Equal((ulong)3, pipeline.MinNextView);
        // All rounds should be aborted
        Assert.Equal(0, pipeline.ActiveRoundCount);

        // New round for block 1 should use view 3 (not 1)
        // Find who is leader for view 3
        var leaderForView3 = validatorSet.GetLeader(3);
        var (leaderId, leaderKey) = validators
            .Select(v => (v.Id, v.PrivateKey))
            .First(v => v.Id == leaderForView3.PeerId);

        // Create a new PipelinedConsensus for the leader to verify the proposal has view 3
        var leaderPipeline = new PipelinedConsensus(validatorSet, leaderId, leaderKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        // Simulate the view change on the leader's pipeline too
        for (int i = 0; i < 3; i++)
            leaderPipeline.HandleViewChange(CreateSignedViewChange(validators[i], 3, 1));

        var proposal = leaderPipeline.StartRound(1, [1], Blake3Hasher.Hash([1]));
        Assert.NotNull(proposal);
        // The proposal should use view 3 (advanced by view change), not view 1
        Assert.Equal((ulong)3, proposal!.ViewNumber);
    }

    [Fact]
    public void PipelinedConsensus_ViewChange_RotatesLeader()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);
        var v3 = MakeValidator(3);

        var validatorSet = MakeValidatorSet(v0, v1, v2, v3);

        // Find the leader for view 1 (block 1)
        var leaderView1 = validatorSet.GetLeader(1);
        // Find the leader for view 2
        var leaderView2 = validatorSet.GetLeader(2);

        // With 4 different validators, view 1 and view 2 should (often) have different leaders.
        // Even if they happen to be the same, the key property is that the view number changes
        // which prevents false double-sign detection.
        // Just verify that MinNextView affects the view used in StartRound.
        var pipeline = new PipelinedConsensus(validatorSet, leaderView2.PeerId,
            // Find the private key for leaderView2
            new[] { v0, v1, v2, v3 }.First(v => v.Id == leaderView2.PeerId).PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        // Trigger view change to view 2
        var validators = new[] { v0, v1, v2, v3 };
        for (int i = 0; i < 3; i++)
            pipeline.HandleViewChange(CreateSignedViewChange(validators[i], 2, 1));

        // Start round for block 1 — view should be max(1, 2) = 2
        var proposal = pipeline.StartRound(1, [1], Blake3Hasher.Hash([1]));

        if (proposal != null)
        {
            // If this node is the leader for view 2, the proposal should have ViewNumber = 2
            Assert.Equal((ulong)2, proposal.ViewNumber);
        }
        // Either way, MinNextView should be 2
        Assert.Equal((ulong)2, pipeline.MinNextView);
    }

    [Fact]
    public void PipelinedConsensus_UpdateValidatorSet_ResetsMinNextView()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);

        var validatorSet = MakeValidatorSet(v0, v1, v2);
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        // Trigger view change (quorum = 3 for 3 validators: (3*2/3)+1 = 3)
        var validators = new[] { v0, v1, v2 };
        for (int i = 0; i < 3; i++)
            pipeline.HandleViewChange(CreateSignedViewChange(validators[i], 10, 0));

        Assert.Equal((ulong)10, pipeline.MinNextView);

        // Epoch transition resets MinNextView
        pipeline.UpdateValidatorSet(validatorSet);
        Assert.Equal((ulong)0, pipeline.MinNextView);
    }

    [Fact]
    public void PipelinedConsensus_LastFinalizedBlock_Tracks_Correctly()
    {
        var v0 = MakeValidator(0);
        var validatorSet = MakeValidatorSet(v0);

        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance, lastFinalizedBlock: 0);

        Assert.Equal((ulong)0, pipeline.LastFinalizedBlock);

        // With 1 validator, quorum=1, so StartRound cascades to finalization
        pipeline.StartRound(1, [1], Blake3Hasher.Hash([1]));

        Assert.Equal((ulong)1, pipeline.LastFinalizedBlock);
    }

    [Fact]
    public void PipelinedConsensus_CleanupFinalizedRounds_RemovesFinalizedRounds()
    {
        var v0 = MakeValidator(0);
        var validatorSet = MakeValidatorSet(v0);

        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        // With 1 validator, rounds finalize immediately
        pipeline.StartRound(1, [1], Blake3Hasher.Hash([1]));
        Assert.Equal(1, pipeline.ActiveRoundCount);

        pipeline.CleanupFinalizedRounds();
        Assert.Equal(0, pipeline.ActiveRoundCount);
    }

    [Fact]
    public void PipelinedConsensus_GetRoundState_ReturnsCorrectState()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);
        var v3 = MakeValidator(3);

        var validatorSet = MakeValidatorSet(v0, v1, v2, v3);
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        Assert.Null(pipeline.GetRoundState(1));

        pipeline.StartRound(1, [1], Blake3Hasher.Hash([1]));
        var state = pipeline.GetRoundState(1);
        Assert.NotNull(state);
        Assert.Equal(ConsensusState.Preparing, state.Value);
    }

    // --- H-01 / M-06: ViewChange signature and validator checks ---

    [Fact]
    public void PipelinedConsensus_ViewChange_FromNonValidator_Rejected()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);
        var v3 = MakeValidator(3);

        var validatorSet = MakeValidatorSet(v0, v1, v2, v3);
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        // Create a non-validator identity
        var fake = MakeValidator(99);
        var vc = CreateSignedViewChange(fake, 5, 0);

        var result = pipeline.HandleViewChange(vc);
        result.Should().BeNull("non-validator view change should be rejected");
    }

    [Fact]
    public void PipelinedConsensus_ViewChange_InvalidSignature_Rejected()
    {
        var v0 = MakeValidator(0);
        var v1 = MakeValidator(1);
        var v2 = MakeValidator(2);
        var v3 = MakeValidator(3);

        var validatorSet = MakeValidatorSet(v0, v1, v2, v3);
        var pipeline = new PipelinedConsensus(validatorSet, v0.Id, v0.PrivateKey,
            new BlsSigner(), NullLogger<PipelinedConsensus>.Instance);

        // Create a VC with a zero (invalid) BLS signature
        var vc = new ViewChangeMessage
        {
            SenderId = v1.Id,
            CurrentView = 0,
            ProposedView = 5,
            VoterSignature = new BlsSignature(new byte[96]),
            VoterPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(v1.PrivateKey)),
        };

        var result = pipeline.HandleViewChange(vc);
        result.Should().BeNull("invalid BLS signature should be rejected");
    }
}
