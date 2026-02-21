using System.Buffers.Binary;
using Basalt.Consensus;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Consensus.Tests;

/// <summary>
/// Tests for view change handling in BasaltBft consensus engine.
/// </summary>
public class ViewChangeTests
{
    private readonly (byte[] PrivateKey, PublicKey PublicKey, PeerId PeerId, Address Address)[] _validators;
    private readonly ValidatorSet _validatorSet;
    private readonly IBlsSigner _blsSigner = new BlsSigner();

    public ViewChangeTests()
    {
        _validators = Enumerable.Range(0, 4).Select(i =>
        {
            var privateKey = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(privateKey);
            privateKey[0] &= 0x3F;
            if (privateKey[0] == 0) privateKey[0] = 1;
            var publicKey = Ed25519Signer.GetPublicKey(privateKey);
            var peerId = PeerId.FromPublicKey(publicKey);
            var address = Ed25519Signer.DeriveAddress(publicKey);
            return (privateKey, publicKey, peerId, address);
        }).ToArray();

        _validatorSet = new ValidatorSet(_validators.Select((v, i) => new ValidatorInfo
        {
            PeerId = v.PeerId,
            PublicKey = v.PublicKey,
            BlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(v.PrivateKey)),
            Address = v.Address,
            Index = i,
        }));
    }

    private BasaltBft CreateBft(int validatorIndex, TimeSpan? viewTimeout = null)
    {
        var v = _validators[validatorIndex];
        return new BasaltBft(
            _validatorSet,
            v.PeerId,
            v.PrivateKey,
            NullLogger<BasaltBft>.Instance,
            viewTimeout: viewTimeout);
    }

    /// <summary>
    /// Create a properly BLS-signed ViewChangeMessage for a validator.
    /// Payload format: [0xFF || proposedView LE 8 bytes]
    /// </summary>
    private ViewChangeMessage CreateSignedViewChange(int validatorIndex, ulong proposedView, ulong currentView = 0)
    {
        var v = _validators[validatorIndex];
        Span<byte> payload = stackalloc byte[9];
        payload[0] = 0xFF;
        BinaryPrimitives.WriteUInt64LittleEndian(payload[1..], proposedView);
        var signature = _blsSigner.Sign(v.PrivateKey, payload);
        var publicKey = _blsSigner.GetPublicKey(v.PrivateKey);
        return new ViewChangeMessage
        {
            SenderId = v.PeerId,
            CurrentView = currentView,
            ProposedView = proposedView,
            VoterSignature = new BlsSignature(signature),
            VoterPublicKey = new BlsPublicKey(publicKey),
        };
    }

    // --- H-01: ViewChange signature verification ---

    [Fact]
    public void HandleViewChange_InvalidSignature_Rejected()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Create a VC with an invalid (zero) BLS signature
        var vc = new ViewChangeMessage
        {
            SenderId = _validators[1].PeerId,
            CurrentView = 1,
            ProposedView = 2,
            VoterSignature = new BlsSignature(new byte[96]),
            VoterPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_validators[1].PrivateKey)),
        };

        var result = bft.HandleViewChange(vc);
        result.Should().BeNull("invalid BLS signature should be rejected");
        bft.CurrentView.Should().Be(1, "view should not change on invalid signature");
    }

    [Fact]
    public void HandleViewChange_WrongKeySignature_Rejected()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Sign with validator 2's key but claim to be validator 1
        Span<byte> payload = stackalloc byte[9];
        payload[0] = 0xFF;
        BinaryPrimitives.WriteUInt64LittleEndian(payload[1..], 2UL);
        var wrongSig = _blsSigner.Sign(_validators[2].PrivateKey, payload);

        var vc = new ViewChangeMessage
        {
            SenderId = _validators[1].PeerId,
            CurrentView = 1,
            ProposedView = 2,
            VoterSignature = new BlsSignature(wrongSig),
            VoterPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_validators[1].PrivateKey)),
        };

        var result = bft.HandleViewChange(vc);
        result.Should().BeNull("signature mismatch should be rejected");
    }

    // --- M-06: Non-validator rejection ---

    [Fact]
    public void HandleViewChange_FromNonValidator_Rejected()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Create a non-validator identity
        var fakePrivateKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(fakePrivateKey);
        fakePrivateKey[0] &= 0x3F;
        if (fakePrivateKey[0] == 0) fakePrivateKey[0] = 1;
        var fakePublicKey = Ed25519Signer.GetPublicKey(fakePrivateKey);
        var fakePeerId = PeerId.FromPublicKey(fakePublicKey);

        // Properly sign it — but the sender is not a validator
        Span<byte> payload = stackalloc byte[9];
        payload[0] = 0xFF;
        BinaryPrimitives.WriteUInt64LittleEndian(payload[1..], 2UL);
        var sig = _blsSigner.Sign(fakePrivateKey, payload);
        var blsPub = _blsSigner.GetPublicKey(fakePrivateKey);

        var vc = new ViewChangeMessage
        {
            SenderId = fakePeerId,
            CurrentView = 1,
            ProposedView = 2,
            VoterSignature = new BlsSignature(sig),
            VoterPublicKey = new BlsPublicKey(blsPub),
        };

        var result = bft.HandleViewChange(vc);
        result.Should().BeNull("non-validator view change should be rejected");
        bft.CurrentView.Should().Be(1);
    }

    // --- ViewChange quorum detection ---

    [Fact]
    public void HandleViewChange_BelowQuorum_DoesNotChangeView()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // With 4 validators, quorum = 3
        // Send 1 view change message. Auto-join does NOT fire (node hasn't timed out),
        // so total = 1, still below quorum of 3.
        bft.HandleViewChange(CreateSignedViewChange(1, 2, 1));

        // View should NOT have changed (1 external vote, no auto-join = 1, below quorum of 3)
        bft.CurrentView.Should().Be(1);
    }

    [Fact]
    public void HandleViewChange_AtQuorum_ChangesView()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Send 3 view change messages (quorum for 4 validators)
        for (int i = 1; i <= 3; i++)
            bft.HandleViewChange(CreateSignedViewChange(i, 2, 1));

        bft.CurrentView.Should().Be(2);
    }

    [Fact]
    public void HandleViewChange_Quorum_ResetsStateToProposing()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Move to Proposing state
        bft.State.Should().Be(ConsensusState.Proposing);

        // Trigger view change with quorum
        for (int i = 0; i < 3; i++)
            bft.HandleViewChange(CreateSignedViewChange(i, 5, 1));

        bft.State.Should().Be(ConsensusState.Proposing);
        bft.CurrentView.Should().Be(5);
    }

    [Fact]
    public void HandleViewChange_FiresOnViewChangeEvent()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        ulong? viewChangedTo = null;
        bft.OnViewChange += view => viewChangedTo = view;

        // Quorum of view change messages
        for (int i = 0; i < 3; i++)
            bft.HandleViewChange(CreateSignedViewChange(i, 10, 1));

        viewChangedTo.Should().Be(10);
    }

    [Fact]
    public void HandleViewChange_DuplicateSender_CountedOnce()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Same sender sends view change twice
        for (int repeat = 0; repeat < 3; repeat++)
            bft.HandleViewChange(CreateSignedViewChange(1, 2, 1));

        // Only 1 unique sender, below quorum of 3
        bft.CurrentView.Should().Be(1);
    }

    [Fact]
    public void HandleViewChange_OnlyAdvancesForward()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // First, advance to view 5
        for (int i = 0; i < 3; i++)
            bft.HandleViewChange(CreateSignedViewChange(i, 5, 1));
        bft.CurrentView.Should().Be(5);

        // Try to go back to view 3 (should not work)
        for (int i = 0; i < 3; i++)
            bft.HandleViewChange(CreateSignedViewChange(i, 3, 5));

        bft.CurrentView.Should().Be(5, "view should not go backwards");
    }

    [Fact]
    public void HandleViewChange_SuccessiveViewChanges()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // View change to 2
        for (int i = 0; i < 3; i++)
            bft.HandleViewChange(CreateSignedViewChange(i, 2, 1));
        bft.CurrentView.Should().Be(2);

        // View change to 3
        for (int i = 0; i < 3; i++)
            bft.HandleViewChange(CreateSignedViewChange(i, 3, 2));
        bft.CurrentView.Should().Be(3);
    }

    // --- CheckViewTimeout ---

    [Fact]
    public void CheckViewTimeout_InIdleState_ReturnsNull()
    {
        var bft = CreateBft(0);
        // Not started, state is Idle
        bft.State.Should().Be(ConsensusState.Idle);

        var msg = bft.CheckViewTimeout();
        msg.Should().BeNull();
    }

    [Fact]
    public void CheckViewTimeout_BeforeTimeout_ReturnsNull()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Immediately after starting, should not be timed out
        var msg = bft.CheckViewTimeout();
        msg.Should().BeNull();
    }

    // --- Vote handling after view change ---

    [Fact]
    public void HandleVote_WrongView_Rejected()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Create a vote for view 999 (wrong view)
        var vote = new ConsensusVoteMessage
        {
            SenderId = _validators[1].PeerId,
            ViewNumber = 999,
            BlockNumber = 1,
            BlockHash = Hash256.Zero,
            Phase = VotePhase.Prepare,
            VoterSignature = new BlsSignature(new byte[96]),
            VoterPublicKey = new BlsPublicKey(new byte[48]),
        };

        var result = bft.HandleVote(vote);
        result.Should().BeNull();
    }

    [Fact]
    public void HandleVote_FromNonValidator_Rejected()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Create a non-validator PeerId
        var fakePrivateKey = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(fakePrivateKey);
        fakePrivateKey[0] &= 0x3F;
        if (fakePrivateKey[0] == 0) fakePrivateKey[0] = 1;
        var fakePublicKey = Ed25519Signer.GetPublicKey(fakePrivateKey);
        var fakePeerId = PeerId.FromPublicKey(fakePublicKey);

        var vote = new ConsensusVoteMessage
        {
            SenderId = fakePeerId,
            ViewNumber = 1,
            BlockNumber = 1,
            BlockHash = Hash256.Zero,
            Phase = VotePhase.Prepare,
            VoterSignature = new BlsSignature(new byte[96]),
            VoterPublicKey = new BlsPublicKey(new byte[48]),
        };

        var result = bft.HandleVote(vote);
        result.Should().BeNull();
    }

    // --- IsLeader property ---

    [Fact]
    public void IsLeader_CorrectForCurrentView()
    {
        for (int i = 0; i < 4; i++)
        {
            var bft = CreateBft(i);
            bft.StartRound(1);

            var expectedLeader = _validatorSet.GetLeader(1);
            var isLeader = bft.IsLeader;

            if (_validators[i].PeerId == expectedLeader.PeerId)
                isLeader.Should().BeTrue();
            else
                isLeader.Should().BeFalse();
        }
    }

    // --- State transitions ---

    [Fact]
    public void InitialState_IsIdle()
    {
        var bft = CreateBft(0);
        bft.State.Should().Be(ConsensusState.Idle);
    }

    [Fact]
    public void StartRound_SetsViewEqualToBlockNumber()
    {
        var bft = CreateBft(0);
        bft.StartRound(42);

        bft.CurrentView.Should().Be(42);
        bft.CurrentBlockNumber.Should().Be(42);
    }

    [Fact]
    public void ProposeBlock_LeaderCanPropose()
    {
        // Find the leader for view 1
        var leader = _validatorSet.GetLeader(1);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader.PeerId);

        var bft = CreateBft(leaderIndex);
        bft.StartRound(1);

        var blockHash = Blake3Hasher.Hash([0x01, 0x02]);
        var proposal = bft.ProposeBlock([0x01, 0x02], blockHash);

        proposal.Should().NotBeNull();
        proposal!.BlockHash.Should().Be(blockHash);
        proposal.BlockNumber.Should().Be(1);
        proposal.SenderId.Should().Be(_validators[leaderIndex].PeerId);
    }

    [Fact]
    public void ProposeBlock_SetsStateToPreparing()
    {
        var leader = _validatorSet.GetLeader(1);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader.PeerId);

        var bft = CreateBft(leaderIndex);
        bft.StartRound(1);

        bft.ProposeBlock([0x01], Blake3Hasher.Hash([0x01]));

        bft.State.Should().Be(ConsensusState.Preparing);
    }

    [Fact]
    public void HandleProposal_FromLeader_ReturnsVote()
    {
        // Leader proposes, then another node handles the proposal
        var leader = _validatorSet.GetLeader(1);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader.PeerId);
        var otherIndex = (leaderIndex + 1) % 4;

        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(1);
        var proposal = leaderBft.ProposeBlock([0x01, 0x02], Blake3Hasher.Hash([0x01, 0x02]));

        var otherBft = CreateBft(otherIndex);
        otherBft.StartRound(1);
        var vote = otherBft.HandleProposal(proposal!);

        vote.Should().NotBeNull();
        vote!.Phase.Should().Be(VotePhase.Prepare);
        vote.SenderId.Should().Be(_validators[otherIndex].PeerId);
    }

    [Fact]
    public void HandleProposal_WrongView_ReturnsNull()
    {
        var leader = _validatorSet.GetLeader(1);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader.PeerId);

        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(1);
        var proposal = leaderBft.ProposeBlock([0x01], Blake3Hasher.Hash([0x01]));

        // Other node is at a different view
        var otherBft = CreateBft((leaderIndex + 1) % 4);
        otherBft.StartRound(999); // Different view

        var vote = otherBft.HandleProposal(proposal!);
        vote.Should().BeNull();
    }

    // --- Auto-join broadcast ---

    [Fact]
    public void HandleViewChange_AutoJoin_ReturnsViewChangeMessage()
    {
        var bft = CreateBft(0, viewTimeout: TimeSpan.FromMilliseconds(1));
        bft.StartRound(1);

        // Simulate timeout so the node has independently requested a view change
        Thread.Sleep(10);
        var timeout = bft.CheckViewTimeout();
        timeout.Should().NotBeNull("timeout should fire after 1ms");

        // Receive a VC from another validator for a higher view.
        // Since proposedView 5 > currentView 1 AND the node timed out, it auto-joins.
        var autoJoin = bft.HandleViewChange(CreateSignedViewChange(1, 5, 1));

        autoJoin.Should().NotBeNull("should return VC for broadcast when auto-joining after timeout");
        autoJoin!.SenderId.Should().Be(_validators[0].PeerId);
        autoJoin.ProposedView.Should().Be(5);
    }

    [Fact]
    public void HandleViewChange_NoAutoJoin_ForCurrentOrPastView()
    {
        var bft = CreateBft(0, viewTimeout: TimeSpan.FromMilliseconds(1));
        bft.StartRound(5);

        // Trigger timeout so auto-join is eligible
        Thread.Sleep(10);
        bft.CheckViewTimeout();

        // VC for current view (5) — should not auto-join (proposedView == currentView)
        var result1 = bft.HandleViewChange(CreateSignedViewChange(1, 5, 5));
        result1.Should().BeNull("should not auto-join for current view");

        // VC for past view (3) — should not auto-join
        var result2 = bft.HandleViewChange(CreateSignedViewChange(2, 3, 3));
        result2.Should().BeNull("should not auto-join for past view");
    }

    [Fact]
    public void HandleViewChange_AutoJoin_OnlyOnce()
    {
        var bft = CreateBft(0, viewTimeout: TimeSpan.FromMilliseconds(1));
        bft.StartRound(1);

        // Trigger timeout so auto-join is eligible
        Thread.Sleep(10);
        bft.CheckViewTimeout();

        // First VC for view 5 — auto-join (returns message)
        var first = bft.HandleViewChange(CreateSignedViewChange(1, 5, 1));
        first.Should().NotBeNull();

        // Second VC for same view 5 — already auto-joined, should not return again
        var second = bft.HandleViewChange(CreateSignedViewChange(2, 5, 1));
        second.Should().BeNull("should not auto-join twice for the same view");
    }

    [Fact]
    public void HandleViewChange_ParitySplit_Resolved_WithBroadcast()
    {
        // Simulates the parity split scenario:
        // Group A (V0, V2) at view 20. Group B (V1, V3) at view 21.
        // Both groups have independently timed out (this is realistic — they're stuck).
        // Group B sends VCs for view 22. Group A auto-joins and broadcasts.
        // Group B receives Group A's auto-join VCs → reaches quorum for 22.
        // All 4 converge on view 22.

        var bfts = new BasaltBft[4];
        for (int i = 0; i < 4; i++)
            bfts[i] = CreateBft(i, viewTimeout: TimeSpan.FromMilliseconds(1));

        // Group A at view 20, Group B at view 21
        bfts[0].StartRound(20);
        bfts[2].StartRound(20);
        bfts[1].StartRound(21);
        bfts[3].StartRound(21);

        // Both groups time out (they're stuck, which is why they need auto-join)
        Thread.Sleep(10);
        bfts[0].CheckViewTimeout();
        bfts[2].CheckViewTimeout();
        bfts[1].CheckViewTimeout();
        bfts[3].CheckViewTimeout();

        // Group B (V1, V3) send VCs for view 22
        var vcFromV1 = CreateSignedViewChange(1, 22, 21);
        var vcFromV3 = CreateSignedViewChange(3, 22, 21);

        // V0 receives V1's VC for 22 → auto-joins, returns broadcast VC
        var autoJoinV0 = bfts[0].HandleViewChange(vcFromV1);
        autoJoinV0.Should().NotBeNull("V0 should auto-join for view 22");

        // V0 receives V3's VC for 22 → {V1, V0, V3} = 3 = quorum → V0 jumps to 22
        bfts[0].HandleViewChange(vcFromV3);
        bfts[0].CurrentView.Should().Be(22);

        // V2 similarly receives VCs and jumps to 22
        var autoJoinV2 = bfts[2].HandleViewChange(vcFromV1);
        autoJoinV2.Should().NotBeNull();
        bfts[2].HandleViewChange(vcFromV3);
        bfts[2].CurrentView.Should().Be(22);

        // Now V1 receives V0's auto-join VC (the broadcast)
        // V1 already sent its own VC for 22 (counted locally) and has V3's VC.
        // With V0's auto-join: {V1, V3, V0} = 3 = quorum → V1 jumps to 22.
        bfts[1].HandleViewChange(vcFromV1); // self-handle
        bfts[1].HandleViewChange(vcFromV3);
        bfts[1].HandleViewChange(autoJoinV0!); // broadcast from V0
        bfts[1].CurrentView.Should().Be(22, "V1 should converge to 22 via auto-join broadcast");

        // V3 similarly converges
        bfts[3].HandleViewChange(vcFromV3); // self-handle
        bfts[3].HandleViewChange(vcFromV1);
        bfts[3].HandleViewChange(autoJoinV2!); // broadcast from V2
        bfts[3].CurrentView.Should().Be(22, "V3 should converge to 22 via auto-join broadcast");
    }

    [Fact]
    public void HandleViewChange_NoAutoJoin_WithoutTimeout()
    {
        // Without timeout, auto-join should NOT fire even for a higher view.
        // This prevents the cascade where a single timeout propagates to all validators.
        var bft = CreateBft(0);
        bft.StartRound(1);

        var result = bft.HandleViewChange(CreateSignedViewChange(1, 5, 1));
        result.Should().BeNull("should not auto-join when node hasn't timed out");
    }

    // --- Block number validation in HandleProposal ---

    [Fact]
    public void HandleProposal_SameViewWrongBlockNumber_Rejected()
    {
        // After a view change, the leader may have finalized block N and propose block N+1.
        // This node is still deciding block N. Even though views match, the proposal must
        // be rejected to prevent chain desync.
        var leader = _validatorSet.GetLeader(5);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader.PeerId);

        // Leader at view 5, block 5 (simulating they finalized block 4 and started round 5)
        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(5);
        var proposal = leaderBft.ProposeBlock([0xAA], Blake3Hasher.Hash([0xAA]));
        proposal.Should().NotBeNull();
        proposal!.BlockNumber.Should().Be(5);

        // Other node at view 5 but block 3 (behind — view changed without block finalization)
        var otherIndex = (leaderIndex + 1) % 4;
        var otherBft = CreateBft(otherIndex);
        otherBft.StartRound(3); // Block 3, view 3
        AdvanceViewViaViewChange(otherBft, 5); // View 5, block still 3
        otherBft.CurrentView.Should().Be(5);
        otherBft.CurrentBlockNumber.Should().Be(3);

        var vote = otherBft.HandleProposal(proposal);
        vote.Should().BeNull("should reject proposal for block 5 when deciding block 3");
    }

    [Fact]
    public void HandleProposal_WrongBlockNumber_FiresOnBehindDetected()
    {
        var leader = _validatorSet.GetLeader(5);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader.PeerId);

        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(5);
        var proposal = leaderBft.ProposeBlock([0xBB], Blake3Hasher.Hash([0xBB]));

        var otherIndex = (leaderIndex + 1) % 4;
        var otherBft = CreateBft(otherIndex);
        otherBft.StartRound(3);
        AdvanceViewViaViewChange(otherBft, 5);

        ulong? behindBlockNumber = null;
        otherBft.OnBehindDetected += (bn) => behindBlockNumber = bn;

        otherBft.HandleProposal(proposal!);
        behindBlockNumber.Should().Be(5, "should fire OnBehindDetected with the leader's block number");
    }

    // --- Fast-forward (proposal from future view) ---

    /// <summary>
    /// Advance a BFT instance's view via view change quorum (without changing block number).
    /// </summary>
    private void AdvanceViewViaViewChange(BasaltBft bft, ulong targetView)
    {
        for (int i = 0; i < 3; i++)
            bft.HandleViewChange(CreateSignedViewChange(i, targetView, bft.CurrentView));
    }

    [Fact]
    public void HandleProposal_NextView_FastForwards()
    {
        // Both nodes are working on block 1. Leader's view advanced to 2 via view change.
        // Other node is still at view 1. The proposal should be accepted via fast-forward.
        var leaderIndex = 0;
        for (int v = 2; v < 100; v++)
        {
            var leader = _validatorSet.GetLeader((ulong)v);
            var idx = _validators.ToList().FindIndex(x => x.PeerId == leader.PeerId);
            if (idx >= 0) { leaderIndex = idx; break; }
        }

        // Find the view where this validator is leader (starting from view 2)
        ulong leaderView = 2;
        for (ulong v = 2; v < 100; v++)
        {
            if (_validatorSet.GetLeader(v).PeerId == _validators[leaderIndex].PeerId)
            {
                leaderView = v;
                break;
            }
        }

        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(1); // Block 1
        AdvanceViewViaViewChange(leaderBft, leaderView); // Advance view without changing block number
        leaderBft.CurrentView.Should().Be(leaderView);
        leaderBft.CurrentBlockNumber.Should().Be(1);

        var proposal = leaderBft.ProposeBlock([0xAA, 0xBB], Blake3Hasher.Hash([0xAA, 0xBB]));
        proposal.Should().NotBeNull();
        proposal!.BlockNumber.Should().Be(1);

        var otherIndex = (leaderIndex + 1) % 4;
        var otherBft = CreateBft(otherIndex);
        otherBft.StartRound(1); // Same block, view 1
        otherBft.CurrentView.Should().Be(1);

        var vote = otherBft.HandleProposal(proposal);
        vote.Should().NotBeNull("fast-forward should accept proposal for same block from future view");
        vote!.Phase.Should().Be(VotePhase.Prepare);
        otherBft.CurrentView.Should().Be(leaderView, "node should have fast-forwarded");
        otherBft.State.Should().Be(ConsensusState.Preparing);
    }

    [Fact]
    public void HandleProposal_PastView_ReturnsNull()
    {
        // Node at view 5 receives proposal for view 3 → reject (past view).
        var leader3 = _validatorSet.GetLeader(3);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader3.PeerId);

        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(3);
        var proposal = leaderBft.ProposeBlock([0xCC], Blake3Hasher.Hash([0xCC]));

        var otherBft = CreateBft((leaderIndex + 1) % 4);
        otherBft.StartRound(5); // Already ahead

        var vote = otherBft.HandleProposal(proposal!);
        vote.Should().BeNull("past-view proposals should be rejected");
    }

    [Fact]
    public void HandleProposal_FutureView_DifferentBlockNumber_Rejected()
    {
        // Leader already finalized block 1 and proposes block 2.
        // Lagging validator still at block 1 should NOT fast-forward.
        var leader2 = _validatorSet.GetLeader(2);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader2.PeerId);

        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(2); // Block 2, view 2
        var proposal = leaderBft.ProposeBlock([0xDD], Blake3Hasher.Hash([0xDD]));
        proposal.Should().NotBeNull();
        proposal!.BlockNumber.Should().Be(2);

        var otherBft = CreateBft((leaderIndex + 1) % 4);
        otherBft.StartRound(1); // Block 1, view 1

        var vote = otherBft.HandleProposal(proposal);
        vote.Should().BeNull("proposals for different block numbers should be rejected");
        otherBft.CurrentView.Should().Be(1, "view should not have changed");
    }

    [Fact]
    public void HandleProposal_FutureView_ActiveConsensus_NotInterrupted()
    {
        // Node is in Preparing state (active consensus). Future view proposal should NOT
        // interrupt the in-progress consensus — it might be about to finalize.
        var leader1 = _validatorSet.GetLeader(1);
        var leader1Index = _validators.ToList().FindIndex(v => v.PeerId == leader1.PeerId);

        // Set up: leader proposes for view 1, other node receives and enters Preparing
        var leaderBft = CreateBft(leader1Index);
        leaderBft.StartRound(1);
        var proposal1 = leaderBft.ProposeBlock([0x01], Blake3Hasher.Hash([0x01]));

        var otherIndex = (leader1Index + 1) % 4;
        var otherBft = CreateBft(otherIndex);
        otherBft.StartRound(1);
        var vote = otherBft.HandleProposal(proposal1!);
        vote.Should().NotBeNull();
        otherBft.State.Should().Be(ConsensusState.Preparing);

        // Now find the leader for a higher view of block 1
        ulong futureView = 2;
        for (ulong v = 2; v < 100; v++)
        {
            var l = _validatorSet.GetLeader(v);
            if (l.PeerId != _validators[otherIndex].PeerId) { futureView = v; break; }
        }

        var futureLeaderIndex = _validators.ToList().FindIndex(v =>
            v.PeerId == _validatorSet.GetLeader(futureView).PeerId);
        var futureBft = CreateBft(futureLeaderIndex);
        futureBft.StartRound(1);
        AdvanceViewViaViewChange(futureBft, futureView);
        var proposal2 = futureBft.ProposeBlock([0x02], Blake3Hasher.Hash([0x02]));

        // Other node receives future view proposal while in Preparing → should be rejected
        var vote2 = otherBft.HandleProposal(proposal2!);
        vote2.Should().BeNull("should not interrupt active consensus");
        otherBft.CurrentView.Should().Be(1, "view should not have changed");
    }

    [Fact]
    public void HandleVote_NextView_PreCounted()
    {
        // A PREPARE vote for _currentView + 1 should be stored (not dropped).
        // After fast-forward via proposal, the pre-counted vote helps reach quorum.

        // Find the leader for view 2 of block 1
        ulong leaderView = 2;
        int leaderIndex = 0;
        for (ulong v = 2; v < 100; v++)
        {
            var leader = _validatorSet.GetLeader(v);
            var idx = _validators.ToList().FindIndex(x => x.PeerId == leader.PeerId);
            if (idx >= 0) { leaderIndex = idx; leaderView = v; break; }
        }

        // Leader at view leaderView (block 1), proposes
        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(1);
        AdvanceViewViaViewChange(leaderBft, leaderView);
        var blockData = new byte[] { 0xEE };
        var blockHash = Blake3Hasher.Hash(blockData);
        var proposal = leaderBft.ProposeBlock(blockData, blockHash);
        proposal.Should().NotBeNull();

        // Another validator at the same view generates a valid PREPARE vote
        var voterIndex = (leaderIndex + 1) % 4;
        var voterBft = CreateBft(voterIndex);
        voterBft.StartRound(1);
        AdvanceViewViaViewChange(voterBft, leaderView);
        var voterPrepare = voterBft.HandleProposal(proposal!);
        voterPrepare.Should().NotBeNull();

        // Target node at view 1 receives the vote BEFORE the proposal
        var targetIndex = (leaderIndex + 2) % 4;
        var targetBft = CreateBft(targetIndex);
        targetBft.StartRound(1);

        // Vote arrives while target is at view 1 — pre-counted for view leaderView
        targetBft.HandleVote(voterPrepare!);

        // Now the proposal arrives — target fast-forwards
        var targetVote = targetBft.HandleProposal(proposal!);
        targetVote.Should().NotBeNull("fast-forward should accept proposal");
        targetBft.CurrentView.Should().Be(leaderView);
    }

    [Fact]
    public void FastForward_FullConsensus_4Validators()
    {
        // Simulates the Docker devnet scenario:
        // All 4 validators on block 1. Leader for view 2 processed view change first.
        // Others still at view 1. Leader proposes. Others fast-forward.
        // Full PREPARE→PRE-COMMIT→COMMIT→finalized.

        // Find the leader for some view > 1 (for block 1)
        ulong leaderView = 2;
        int leaderIndex = 0;
        for (ulong v = 2; v < 100; v++)
        {
            var leader = _validatorSet.GetLeader(v);
            var idx = _validators.ToList().FindIndex(x => x.PeerId == leader.PeerId);
            if (idx >= 0) { leaderIndex = idx; leaderView = v; break; }
        }

        // Create all 4 BFT instances, all at block 1
        var bfts = new BasaltBft[4];
        for (int i = 0; i < 4; i++)
        {
            bfts[i] = CreateBft(i);
            bfts[i].StartRound(1);
        }

        // Track finalization
        var finalizedOn = new HashSet<int>();
        for (int i = 0; i < 4; i++)
        {
            var idx = i;
            bfts[i].OnBlockFinalized += (_, _, _) => finalizedOn.Add(idx);
        }

        // Leader advances view via view change (simulates processing view change first)
        AdvanceViewViaViewChange(bfts[leaderIndex], leaderView);
        bfts[leaderIndex].CurrentView.Should().Be(leaderView);
        bfts[leaderIndex].CurrentBlockNumber.Should().Be(1);

        // Leader proposes
        var blockData = new byte[] { 0x01, 0x02, 0x03 };
        var blockHash = Blake3Hasher.Hash(blockData);
        var proposal = bfts[leaderIndex].ProposeBlock(blockData, blockHash);
        proposal.Should().NotBeNull();

        // Capture aggregate QCs from leader
        var aggregates = new List<AggregateVoteMessage>();
        bfts[leaderIndex].OnAggregateVote += agg => aggregates.Add(agg);

        // Non-leaders handle proposal (fast-forward from view 1 → leaderView)
        var prepareVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIndex) continue;
            var vt = bfts[i].HandleProposal(proposal!);
            vt.Should().NotBeNull($"validator {i} should fast-forward and vote PREPARE");
            bfts[i].CurrentView.Should().Be(leaderView);
            prepareVotes.Add(vt!);
        }

        // Send PREPARE votes to leader only (leader-collected)
        foreach (var pv in prepareVotes)
            bfts[leaderIndex].HandleVote(pv);

        // Leader should have built a PREPARE aggregate QC
        aggregates.Should().Contain(a => a.Phase == VotePhase.Prepare);
        var prepareQC = aggregates.First(a => a.Phase == VotePhase.Prepare);

        // Non-leaders receive PREPARE QC and return PRE-COMMIT votes
        var preCommitVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIndex) continue;
            var resp = bfts[i].HandleAggregateVote(prepareQC);
            if (resp != null) preCommitVotes.Add(resp);
        }

        preCommitVotes.Should().NotBeEmpty("at least one node should produce PRE-COMMIT");

        // Send PRE-COMMIT votes to leader
        aggregates.Clear();
        foreach (var pcv in preCommitVotes)
            bfts[leaderIndex].HandleVote(pcv);

        aggregates.Should().Contain(a => a.Phase == VotePhase.PreCommit);
        var preCommitQC = aggregates.First(a => a.Phase == VotePhase.PreCommit);

        // Non-leaders receive PRE-COMMIT QC and return COMMIT votes
        var commitVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIndex) continue;
            var resp = bfts[i].HandleAggregateVote(preCommitQC);
            if (resp != null) commitVotes.Add(resp);
        }

        commitVotes.Should().NotBeEmpty("at least one node should produce COMMIT");

        // Send COMMIT votes to leader
        aggregates.Clear();
        foreach (var cv in commitVotes)
            bfts[leaderIndex].HandleVote(cv);

        aggregates.Should().Contain(a => a.Phase == VotePhase.Commit);
        var commitQC = aggregates.First(a => a.Phase == VotePhase.Commit);

        // Non-leaders receive COMMIT QC and finalize
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIndex) continue;
            bfts[i].HandleAggregateVote(commitQC);
        }

        finalizedOn.Should().NotBeEmpty("block should be finalized on at least one validator");
    }

    // --- 7-validator set quorum ---

    [Fact]
    public void SevenValidators_QuorumOf5_FullConsensus()
    {
        var validators7 = Enumerable.Range(0, 7).Select(i =>
        {
            var privateKey = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(privateKey);
            privateKey[0] &= 0x3F;
            if (privateKey[0] == 0) privateKey[0] = 1;
            var publicKey = Ed25519Signer.GetPublicKey(privateKey);
            var peerId = PeerId.FromPublicKey(publicKey);
            var address = Ed25519Signer.DeriveAddress(publicKey);
            return (privateKey, publicKey, peerId, address);
        }).ToArray();

        var validatorSet7 = new ValidatorSet(validators7.Select((v, i) => new ValidatorInfo
        {
            PeerId = v.peerId,
            PublicKey = v.publicKey,
            BlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(v.privateKey)),
            Address = v.address,
            Index = i,
        }));

        validatorSet7.QuorumThreshold.Should().Be(5); // (7*2/3)+1 = 5
        validatorSet7.MaxFaults.Should().Be(2);

        var bfts = Enumerable.Range(0, 7).Select(i =>
            new BasaltBft(validatorSet7, validators7[i].peerId, validators7[i].privateKey,
                NullLogger<BasaltBft>.Instance)).ToArray();

        foreach (var bft in bfts) bft.StartRound(1);

        var leader = validatorSet7.GetLeader(1);
        var leaderIdx = validators7.ToList().FindIndex(v => v.peerId == leader.PeerId);

        var blockHash = Blake3Hasher.Hash([0xAA, 0xBB]);
        var proposal = bfts[leaderIdx].ProposeBlock([0xAA, 0xBB], blockHash);
        proposal.Should().NotBeNull();

        // Capture aggregate QCs from leader
        var aggregates = new List<AggregateVoteMessage>();
        bfts[leaderIdx].OnAggregateVote += agg => aggregates.Add(agg);

        // Handle proposal on non-leaders (6 non-leaders produce PREPARE votes)
        var prepareVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 7; i++)
        {
            if (i == leaderIdx) continue;
            var vt = bfts[i].HandleProposal(proposal!);
            if (vt != null) prepareVotes.Add(vt);
        }

        prepareVotes.Should().HaveCount(6);

        // Send PREPARE votes to leader only
        foreach (var vt in prepareVotes)
            bfts[leaderIdx].HandleVote(vt);

        aggregates.Should().Contain(a => a.Phase == VotePhase.Prepare);
        var prepareQC = aggregates.First(a => a.Phase == VotePhase.Prepare);

        // Non-leaders receive PREPARE QC and return PRE-COMMIT votes
        var preCommitVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 7; i++)
        {
            if (i == leaderIdx) continue;
            var resp = bfts[i].HandleAggregateVote(prepareQC);
            if (resp != null) preCommitVotes.Add(resp);
        }
        preCommitVotes.Should().NotBeEmpty();

        // Send PRE-COMMIT votes to leader
        aggregates.Clear();
        foreach (var vt in preCommitVotes)
            bfts[leaderIdx].HandleVote(vt);

        aggregates.Should().Contain(a => a.Phase == VotePhase.PreCommit);
        var preCommitQC = aggregates.First(a => a.Phase == VotePhase.PreCommit);

        // Non-leaders receive PRE-COMMIT QC and return COMMIT votes
        var commitVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 7; i++)
        {
            if (i == leaderIdx) continue;
            var resp = bfts[i].HandleAggregateVote(preCommitQC);
            if (resp != null) commitVotes.Add(resp);
        }
        commitVotes.Should().NotBeEmpty();

        // Send COMMIT votes to leader
        aggregates.Clear();
        bool finalized = false;
        foreach (var bft in bfts)
            bft.OnBlockFinalized += (_, _, _) => finalized = true;

        foreach (var vt in commitVotes)
            bfts[leaderIdx].HandleVote(vt);

        aggregates.Should().Contain(a => a.Phase == VotePhase.Commit);
        var commitQC = aggregates.First(a => a.Phase == VotePhase.Commit);

        // Non-leaders receive COMMIT QC and finalize
        for (int i = 0; i < 7; i++)
        {
            if (i == leaderIdx) continue;
            bfts[i].HandleAggregateVote(commitQC);
        }

        finalized.Should().BeTrue();
        bfts.Any(b => b.State == ConsensusState.Finalized).Should().BeTrue();
    }

    // --- 3-validator set quorum properties ---

    [Fact]
    public void ThreeValidators_QuorumIs3_MaxFaultsIs0()
    {
        var validators3 = Enumerable.Range(0, 3).Select(i =>
        {
            var privateKey = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(privateKey);
            privateKey[0] &= 0x3F;
            if (privateKey[0] == 0) privateKey[0] = 1;
            var publicKey = Ed25519Signer.GetPublicKey(privateKey);
            var peerId = PeerId.FromPublicKey(publicKey);
            var address = Ed25519Signer.DeriveAddress(publicKey);
            return (privateKey, publicKey, peerId, address);
        }).ToArray();

        var validatorSet3 = new ValidatorSet(validators3.Select((v, i) => new ValidatorInfo
        {
            PeerId = v.peerId,
            PublicKey = v.publicKey,
            BlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(v.privateKey)),
            Address = v.address,
            Index = i,
        }));

        // With 3 validators: quorum = (3*2/3)+1 = 3, maxFaults = (3-1)/3 = 0
        validatorSet3.QuorumThreshold.Should().Be(3);
        validatorSet3.MaxFaults.Should().Be(0);
    }
}
