using Basalt.Consensus;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Basalt.Consensus.Tests;

public class BasaltBftTests
{
    private readonly (byte[] PrivateKey, PublicKey PublicKey, PeerId PeerId, Address Address)[] _validators;
    private readonly ValidatorSet _validatorSet;
    private readonly IBlsSigner _blsSigner = new BlsSigner();

    public BasaltBftTests()
    {
        _validators = Enumerable.Range(0, 4).Select(i =>
        {
            // Generate a private key that is valid for both Ed25519 and BLS12-381.
            // BLS requires the scalar to be below the field modulus (~0x73ed...).
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

    [Fact]
    public void ValidatorSet_QuorumThreshold_For4Validators()
    {
        // 4 validators: f=1, quorum=3
        _validatorSet.QuorumThreshold.Should().Be(3);
        _validatorSet.MaxFaults.Should().Be(1);
    }

    [Fact]
    public void ValidatorSet_LeaderSelection_RoundRobin()
    {
        var leaders = Enumerable.Range(0, 4)
            .Select(i => _validatorSet.GetLeader((ulong)i).PeerId)
            .ToList();

        // Each validator should be leader once in 4 views
        leaders.Distinct().Count().Should().Be(4);
    }

    [Fact]
    public void StartRound_SetsProposingState()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        bft.State.Should().Be(ConsensusState.Proposing);
        bft.CurrentBlockNumber.Should().Be(1);
    }

    [Fact]
    public void ProposeBlock_OnlyLeaderCanPropose()
    {
        // Find which validator is leader for view 1
        var leader = _validatorSet.GetLeader(1);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader.PeerId);
        var nonLeaderIndex = (leaderIndex + 1) % 4;

        var nonLeader = CreateBft(nonLeaderIndex);
        nonLeader.StartRound(1);

        var proposal = nonLeader.ProposeBlock([0x01], new Hash256(new byte[32]));
        proposal.Should().BeNull(); // Non-leader cannot propose
    }

    [Fact]
    public void FullConsensus_4Validators_FinalizeBlock()
    {
        // This test exercises the leader-collected voting path:
        // votes are sent only to the leader, leader builds aggregate QCs,
        // non-leaders advance via HandleAggregateVote.
        var bfts = Enumerable.Range(0, 4).Select(CreateBft).ToArray();

        // Capture aggregate votes from leader
        var aggregates = new List<AggregateVoteMessage>();
        foreach (var bft in bfts)
            bft.OnAggregateVote += agg => aggregates.Add(agg);

        // Start round on all validators
        foreach (var bft in bfts)
            bft.StartRound(1);

        // Find leader
        var leader = _validatorSet.GetLeader(1);
        var leaderIndex = _validators.ToList().FindIndex(v => v.PeerId == leader.PeerId);

        // Leader proposes
        var blockHash = Blake3Hasher.Hash([0x01, 0x02, 0x03]);
        var proposal = bfts[leaderIndex].ProposeBlock([0x01, 0x02, 0x03], blockHash);
        proposal.Should().NotBeNull();

        // Other validators handle proposal and return PREPARE votes (sent to leader only)
        var prepareVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIndex) continue;
            var vote = bfts[i].HandleProposal(proposal!);
            if (vote != null) prepareVotes.Add(vote);
        }

        prepareVotes.Should().HaveCount(3);

        // Send PREPARE votes to leader only (leader-collected)
        aggregates.Clear();
        foreach (var vote in prepareVotes)
            bfts[leaderIndex].HandleVote(vote);

        // Leader should have built a PREPARE aggregate QC
        aggregates.Should().ContainSingle(a => a.Phase == VotePhase.Prepare);
        var prepareQC = aggregates.First(a => a.Phase == VotePhase.Prepare);

        // Non-leaders receive PREPARE QC and return PRE-COMMIT votes
        var preCommitVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIndex) continue;
            var response = bfts[i].HandleAggregateVote(prepareQC);
            if (response != null) preCommitVotes.Add(response);
        }

        preCommitVotes.Should().HaveCount(3);

        // Send PRE-COMMIT votes to leader
        aggregates.Clear();
        foreach (var vote in preCommitVotes)
            bfts[leaderIndex].HandleVote(vote);

        // Leader should have built a PRE-COMMIT aggregate QC
        aggregates.Should().ContainSingle(a => a.Phase == VotePhase.PreCommit);
        var preCommitQC = aggregates.First(a => a.Phase == VotePhase.PreCommit);

        // Non-leaders receive PRE-COMMIT QC and return COMMIT votes
        var commitVotes = new List<ConsensusVoteMessage>();
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIndex) continue;
            var response = bfts[i].HandleAggregateVote(preCommitQC);
            if (response != null) commitVotes.Add(response);
        }

        commitVotes.Should().HaveCount(3);

        // Send COMMIT votes to leader
        aggregates.Clear();
        var finalizedCount = 0;
        foreach (var bft in bfts)
            bft.OnBlockFinalized += (hash, data, _) => finalizedCount++;

        foreach (var vote in commitVotes)
            bfts[leaderIndex].HandleVote(vote);

        // Leader should have finalized and built a COMMIT aggregate QC
        aggregates.Should().ContainSingle(a => a.Phase == VotePhase.Commit);
        var commitQC = aggregates.First(a => a.Phase == VotePhase.Commit);
        bfts[leaderIndex].State.Should().Be(ConsensusState.Finalized);

        // Non-leaders receive COMMIT QC and finalize
        for (int i = 0; i < 4; i++)
        {
            if (i == leaderIndex) continue;
            bfts[i].HandleAggregateVote(commitQC);
        }

        // All validators should be finalized
        foreach (var bft in bfts)
            bft.State.Should().Be(ConsensusState.Finalized);
    }

    [Fact]
    public void HandleProposal_WrongLeader_RejectsProposal()
    {
        var bft = CreateBft(0);
        bft.StartRound(1);

        // Create a proposal from a non-leader
        var nonLeader = _validatorSet.GetLeader(1).PeerId == _validators[1].PeerId ? 2 : 1;
        var fakeProposal = new ConsensusProposalMessage
        {
            SenderId = _validators[nonLeader].PeerId,
            ViewNumber = 1,
            BlockNumber = 1,
            BlockHash = Hash256.Zero,
            BlockData = [],
            ProposerSignature = new BlsSignature(new byte[96]),
        };

        var result = bft.HandleProposal(fakeProposal);
        result.Should().BeNull();
    }

    [Fact]
    public void PeerId_FromPublicKey_IsDeterministic()
    {
        var (_, publicKey) = Ed25519Signer.GenerateKeyPair();
        var id1 = PeerId.FromPublicKey(publicKey);
        var id2 = PeerId.FromPublicKey(publicKey);

        id1.Should().Be(id2);
    }
}
