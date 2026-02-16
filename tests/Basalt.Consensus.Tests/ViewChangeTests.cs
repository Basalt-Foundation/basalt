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

    private BasaltBft CreateBft(int validatorIndex)
    {
        var v = _validators[validatorIndex];
        return new BasaltBft(
            _validatorSet,
            v.PeerId,
            v.PrivateKey,
            NullLogger<BasaltBft>.Instance);
    }

    // --- ViewChange quorum detection ---

    [Fact]
    public void HandleViewChange_BelowQuorum_DoesNotChangeView()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // With 4 validators, quorum = 3
        // Send 1 view change message. Auto-join adds local validator's vote
        // (proposedView 2 > currentView 1), so total = 2, still below quorum of 3.
        bft.HandleViewChange(new ViewChangeMessage
        {
            SenderId = _validators[1].PeerId,
            CurrentView = 1,
            ProposedView = 2,
            VoterSignature = new BlsSignature(new byte[96]),
            VoterPublicKey = new BlsPublicKey(new byte[48]),
        });

        // View should NOT have changed (1 external + 1 auto-join = 2, below quorum of 3)
        bft.CurrentView.Should().Be(1);
    }

    [Fact]
    public void HandleViewChange_AtQuorum_ChangesView()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Send 3 view change messages (quorum for 4 validators)
        for (int i = 1; i <= 3; i++)
        {
            bft.HandleViewChange(new ViewChangeMessage
            {
                SenderId = _validators[i].PeerId,
                CurrentView = 1,
                ProposedView = 2,
                VoterSignature = new BlsSignature(new byte[96]),
                VoterPublicKey = new BlsPublicKey(new byte[48]),
            });
        }

        bft.CurrentView.Should().Be(2);
    }

    [Fact]
    public void HandleViewChange_Quorum_ResetsStateToProposing()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Move to Preparing state
        bft.State.Should().Be(ConsensusState.Proposing);

        // Trigger view change with quorum
        for (int i = 0; i < 3; i++)
        {
            bft.HandleViewChange(new ViewChangeMessage
            {
                SenderId = _validators[i].PeerId,
                CurrentView = 1,
                ProposedView = 5,
                VoterSignature = new BlsSignature(new byte[96]),
                VoterPublicKey = new BlsPublicKey(new byte[48]),
            });
        }

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
        {
            bft.HandleViewChange(new ViewChangeMessage
            {
                SenderId = _validators[i].PeerId,
                CurrentView = 1,
                ProposedView = 10,
                VoterSignature = new BlsSignature(new byte[96]),
                VoterPublicKey = new BlsPublicKey(new byte[48]),
            });
        }

        viewChangedTo.Should().Be(10);
    }

    [Fact]
    public void HandleViewChange_DuplicateSender_CountedOnce()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Same sender sends view change twice
        for (int repeat = 0; repeat < 3; repeat++)
        {
            bft.HandleViewChange(new ViewChangeMessage
            {
                SenderId = _validators[1].PeerId,
                CurrentView = 1,
                ProposedView = 2,
                VoterSignature = new BlsSignature(new byte[96]),
                VoterPublicKey = new BlsPublicKey(new byte[48]),
            });
        }

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
        {
            bft.HandleViewChange(new ViewChangeMessage
            {
                SenderId = _validators[i].PeerId,
                CurrentView = 1,
                ProposedView = 5,
                VoterSignature = new BlsSignature(new byte[96]),
                VoterPublicKey = new BlsPublicKey(new byte[48]),
            });
        }
        bft.CurrentView.Should().Be(5);

        // Try to go back to view 3 (should not work)
        for (int i = 0; i < 3; i++)
        {
            bft.HandleViewChange(new ViewChangeMessage
            {
                SenderId = _validators[i].PeerId,
                CurrentView = 5,
                ProposedView = 3,
                VoterSignature = new BlsSignature(new byte[96]),
                VoterPublicKey = new BlsPublicKey(new byte[48]),
            });
        }

        bft.CurrentView.Should().Be(5, "view should not go backwards");
    }

    [Fact]
    public void HandleViewChange_SuccessiveViewChanges()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // View change to 2
        for (int i = 0; i < 3; i++)
        {
            bft.HandleViewChange(new ViewChangeMessage
            {
                SenderId = _validators[i].PeerId,
                CurrentView = 1,
                ProposedView = 2,
                VoterSignature = new BlsSignature(new byte[96]),
                VoterPublicKey = new BlsPublicKey(new byte[48]),
            });
        }
        bft.CurrentView.Should().Be(2);

        // View change to 3
        for (int i = 0; i < 3; i++)
        {
            bft.HandleViewChange(new ViewChangeMessage
            {
                SenderId = _validators[i].PeerId,
                CurrentView = 2,
                ProposedView = 3,
                VoterSignature = new BlsSignature(new byte[96]),
                VoterPublicKey = new BlsPublicKey(new byte[48]),
            });
        }
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

    // --- Fast-forward (proposal from future view) ---

    [Fact]
    public void HandleProposal_NextView_FastForwards()
    {
        // Leader for view 2 proposes; other node is at view 1.
        // The proposal should be accepted via fast-forward.
        var leader2 = _validatorSet.GetLeader(2);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader2.PeerId);

        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(2); // Leader starts at view 2
        var proposal = leaderBft.ProposeBlock([0xAA, 0xBB], Blake3Hasher.Hash([0xAA, 0xBB]));
        proposal.Should().NotBeNull();

        var otherIndex = (leaderIndex + 1) % 4;
        var otherBft = CreateBft(otherIndex);
        otherBft.StartRound(1); // Other node is at view 1 (one behind)
        otherBft.CurrentView.Should().Be(1);

        var vote = otherBft.HandleProposal(proposal!);
        vote.Should().NotBeNull("fast-forward should accept proposal from next view");
        vote!.Phase.Should().Be(VotePhase.Prepare);
        otherBft.CurrentView.Should().Be(2, "node should have fast-forwarded to view 2");
        otherBft.State.Should().Be(ConsensusState.Preparing);
    }

    [Fact]
    public void HandleProposal_PastView_ReturnsNull()
    {
        // Node at view 5 receives proposal for view 3 → reject.
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
    public void HandleProposal_FutureView_NonLeader_Rejected()
    {
        // Non-leader tries to propose for a future view → reject.
        var leader2 = _validatorSet.GetLeader(2);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader2.PeerId);

        // Use the leader's BFT to generate a valid proposal
        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(2);
        var proposal = leaderBft.ProposeBlock([0xDD], Blake3Hasher.Hash([0xDD]));
        proposal.Should().NotBeNull();

        // Tamper: change the sender to a non-leader
        var fakeIndex = (leaderIndex + 1) % 4;
        var tamperedProposal = new ConsensusProposalMessage
        {
            SenderId = _validators[fakeIndex].PeerId,
            ViewNumber = proposal!.ViewNumber,
            BlockNumber = proposal.BlockNumber,
            BlockHash = proposal.BlockHash,
            BlockData = proposal.BlockData,
            ProposerSignature = proposal.ProposerSignature,
        };

        var otherIndex = (leaderIndex + 2) % 4;
        var otherBft = CreateBft(otherIndex);
        otherBft.StartRound(1); // One view behind

        var vote = otherBft.HandleProposal(tamperedProposal);
        vote.Should().BeNull("fast-forward should reject proposals from non-leaders");
        otherBft.CurrentView.Should().Be(1, "view should not have changed");
    }

    [Fact]
    public void HandleVote_NextView_PreCounted()
    {
        // A PREPARE vote for _currentView + 1 should be stored (not dropped).
        // After fast-forward via proposal, the pre-counted vote helps reach quorum.
        var leader2 = _validatorSet.GetLeader(2);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader2.PeerId);

        // Create leader BFT at view 2, propose a block
        var leaderBft = CreateBft(leaderIndex);
        leaderBft.StartRound(2);
        var blockData = new byte[] { 0xEE };
        var blockHash = Blake3Hasher.Hash(blockData);
        var proposal = leaderBft.ProposeBlock(blockData, blockHash);
        proposal.Should().NotBeNull();

        // Create another validator at view 2 to generate a valid PREPARE vote
        var voterIndex = (leaderIndex + 1) % 4;
        var voterBft = CreateBft(voterIndex);
        voterBft.StartRound(2);
        var voterPrepare = voterBft.HandleProposal(proposal!);
        voterPrepare.Should().NotBeNull();

        // Target node is at view 1 — receives the vote BEFORE the proposal
        var targetIndex = (leaderIndex + 2) % 4;
        var targetBft = CreateBft(targetIndex);
        targetBft.StartRound(1);

        // Vote for view 2 arrives while target is at view 1
        var preResult = targetBft.HandleVote(voterPrepare!);
        // Pre-counted (won't trigger phase transition, returns null)
        // But the vote should not be outright rejected

        // Now the proposal arrives — target fast-forwards to view 2
        var targetVote = targetBft.HandleProposal(proposal!);
        targetVote.Should().NotBeNull("fast-forward should accept proposal");
        targetBft.CurrentView.Should().Be(2);
    }

    [Fact]
    public void FastForward_FullConsensus_4Validators()
    {
        // Simulates the Docker devnet scenario:
        // Leader is at view 2 (processed view change first), other 3 still at view 1.
        // Leader proposes. Others fast-forward. Full PREPARE→PRE-COMMIT→COMMIT→finalized.

        var leader2 = _validatorSet.GetLeader(2);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader2.PeerId);

        // Create all 4 BFT instances
        var bfts = new BasaltBft[4];
        for (int i = 0; i < 4; i++)
            bfts[i] = CreateBft(i);

        // Track finalization
        var finalizedOn = new HashSet<int>();
        for (int i = 0; i < 4; i++)
        {
            var idx = i;
            bfts[i].OnBlockFinalized += (_, _) => finalizedOn.Add(idx);
        }

        // Leader at view 2, others at view 1
        bfts[leaderIndex].StartRound(2);
        for (int i = 0; i < 4; i++)
        {
            if (i != leaderIndex)
                bfts[i].StartRound(1); // One view behind
        }

        // Leader proposes
        var blockData = new byte[] { 0x01, 0x02, 0x03 };
        var blockHash = Blake3Hasher.Hash(blockData);
        var proposal = bfts[leaderIndex].ProposeBlock(blockData, blockHash);
        proposal.Should().NotBeNull();

        // Non-leaders handle proposal (fast-forward from view 1 → 2)
        var prepareVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIndex) continue;
            var vote = bfts[i].HandleProposal(proposal!);
            vote.Should().NotBeNull($"validator {i} should fast-forward and vote PREPARE");
            bfts[i].CurrentView.Should().Be(2);
            prepareVotes.Add(vote!);
        }

        // Broadcast all PREPARE votes to all validators → triggers PRE-COMMIT
        var preCommitVotes = new List<ConsensusVoteMessage>();
        foreach (var pv in prepareVotes)
        {
            for (int i = 0; i < 4; i++)
            {
                if (pv.SenderId == _validators[i].PeerId) continue; // Skip self
                var resp = bfts[i].HandleVote(pv);
                if (resp != null)
                    preCommitVotes.Add(resp);
            }
        }

        preCommitVotes.Should().NotBeEmpty("at least one node should produce PRE-COMMIT");
        preCommitVotes[0].Phase.Should().Be(VotePhase.PreCommit);

        // Broadcast all PRE-COMMIT votes → triggers COMMIT
        var commitVotes = new List<ConsensusVoteMessage>();
        foreach (var pcv in preCommitVotes)
        {
            for (int i = 0; i < 4; i++)
            {
                var resp = bfts[i].HandleVote(pcv);
                if (resp != null)
                    commitVotes.Add(resp);
            }
        }

        commitVotes.Should().NotBeEmpty("at least one node should produce COMMIT");
        commitVotes[0].Phase.Should().Be(VotePhase.Commit);

        // Broadcast all COMMIT votes → triggers finalization
        foreach (var cv in commitVotes)
        {
            for (int i = 0; i < 4; i++)
                bfts[i].HandleVote(cv);
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

        // Handle proposal on non-leaders (6 non-leaders produce PREPARE votes)
        var prepareVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 7; i++)
        {
            if (i == leaderIdx) continue;
            var vote = bfts[i].HandleProposal(proposal!);
            if (vote != null) prepareVotes.Add(vote);
        }

        prepareVotes.Should().HaveCount(6);

        // Distribute PREPARE votes to all validators
        var preCommitVotes = new List<ConsensusVoteMessage>();
        foreach (var vote in prepareVotes)
        {
            for (int i = 0; i < 7; i++)
            {
                var result = bfts[i].HandleVote(vote);
                if (result != null) preCommitVotes.Add(result);
            }
        }
        preCommitVotes.Should().NotBeEmpty();

        // Distribute PRE-COMMIT votes
        var commitVotes = new List<ConsensusVoteMessage>();
        foreach (var vote in preCommitVotes)
        {
            for (int i = 0; i < 7; i++)
            {
                var result = bfts[i].HandleVote(vote);
                if (result != null) commitVotes.Add(result);
            }
        }
        commitVotes.Should().NotBeEmpty();

        // Distribute COMMIT votes
        bool finalized = false;
        foreach (var bft in bfts)
            bft.OnBlockFinalized += (_, _) => finalized = true;

        foreach (var vote in commitVotes)
        {
            for (int i = 0; i < 7; i++)
                bfts[i].HandleVote(vote);
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
