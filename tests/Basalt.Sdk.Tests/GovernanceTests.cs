using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Comprehensive tests for the Governance contract: stake-weighted quadratic voting,
/// delegation, timelock, and executable proposals.
///
/// The Governance contract calls StakingPool.GetDelegation via cross-contract call.
/// We mock this by setting Context.CrossContractCallHandler to return pre-configured
/// stake values from an in-memory dictionary, avoiding the need for a deployed StakingPool.
/// </summary>
public class GovernanceTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly byte[] _proposer;
    private readonly byte[] _alice;
    private readonly byte[] _bob;
    private readonly byte[] _charlie;
    private readonly byte[] _dave;
    private readonly byte[] _targetContract;

    // Pre-configured stakes for cross-contract call mock
    private readonly Dictionary<string, UInt256> _stakes = new();

    // Total pool stake for cross-contract call mock (C-7: read from StakingPool)
    private UInt256 _poolTotalStake = new UInt256(100_000);

    // Track executable proposal cross-contract calls
    private readonly List<(string TargetHex, string Method)> _executedCalls = new();

    // StakingPool address (must match Governance constructor: 0x...1005)
    private static readonly byte[] StakingPoolAddress = CreateStakingPoolAddress();

    private const ulong DefaultPoolId = 0;

    public GovernanceTests()
    {
        _proposer = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);
        _charlie = BasaltTestHost.CreateAddress(4);
        _dave = BasaltTestHost.CreateAddress(5);
        _targetContract = BasaltTestHost.CreateAddress(0xCC);
    }

    private static byte[] CreateStakingPoolAddress()
    {
        var addr = new byte[20];
        addr[18] = 0x10;
        addr[19] = 0x05;
        return addr;
    }

    /// <summary>
    /// Create a Governance contract with small voting period and timelock for testing,
    /// and wire up the cross-contract call mock.
    /// </summary>
    private Governance CreateGov(
        ulong quorumBps = 400,
        UInt256 proposalThreshold = default,
        ulong votingPeriod = 10,
        ulong timelockDelay = 5)
    {
        if (proposalThreshold.IsZero) proposalThreshold = new UInt256(1000);
        var gov = new Governance(quorumBps, proposalThreshold, votingPeriod, timelockDelay);
        WireMock();

        // C-8: Proposer needs enough stake to pass the proposal threshold
        SetStake(_proposer, new UInt256(10_000));

        return gov;
    }

    /// <summary>
    /// Wire up the cross-contract call mock for StakingPool.GetDelegation
    /// and executable proposal calls.
    /// </summary>
    private void WireMock()
    {
        Context.CrossContractCallHandler = (targetAddr, method, args) =>
        {
            var targetHex = Convert.ToHexString(targetAddr);
            var stakingHex = Convert.ToHexString(StakingPoolAddress);

            if (targetHex == stakingHex && method == "GetDelegation" && args.Length >= 2)
            {
                var delegator = (byte[])args[1]!;
                var key = Convert.ToHexString(delegator);
                return _stakes.TryGetValue(key, out var stake) ? stake : UInt256.Zero;
            }

            // C-7: Handle GetPoolStake cross-contract call
            if (targetHex == stakingHex && method == "GetPoolStake")
            {
                return _poolTotalStake;
            }

            // Track executable proposal calls
            _executedCalls.Add((targetHex, method));
            return null;
        };
    }

    /// <summary>
    /// Set stake for a given address in the mock.
    /// </summary>
    private void SetStake(byte[] addr, UInt256 amount)
    {
        _stakes[Convert.ToHexString(addr)] = amount;
    }

    // ====================================================================
    // 1. Constructor / Config (4 tests)
    // ====================================================================

    [Fact]
    public void Constructor_QuorumBps_DefaultValue()
    {
        var gov = CreateGov();
        gov.GetQuorumBps().Should().Be(400);
    }

    [Fact]
    public void Constructor_ProposalThreshold_DefaultValue()
    {
        var gov = CreateGov();
        gov.GetProposalThreshold().Should().Be((UInt256)1000);
    }

    [Fact]
    public void Constructor_VotingPeriod_CustomValue()
    {
        var gov = CreateGov(votingPeriod: 50);
        gov.GetVotingPeriod().Should().Be(50);
    }

    [Fact]
    public void Constructor_TimelockDelay_CustomValue()
    {
        var gov = CreateGov(timelockDelay: 20);
        gov.GetTimelockDelay().Should().Be(20);
    }

    // ====================================================================
    // 2. CreateProposal (5 tests)
    // ====================================================================

    [Fact]
    public void CreateProposal_ReturnsIncrementingId()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);

        var id0 = _host.Call(() => gov.CreateProposal("Proposal A", 10));
        var id1 = _host.Call(() => gov.CreateProposal("Proposal B", 10));

        id0.Should().Be(0);
        id1.Should().Be(1);
    }

    [Fact]
    public void CreateProposal_SetsActiveStatus()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);

        var id = _host.Call(() => gov.CreateProposal("Test proposal", 10));

        gov.GetStatus(id).Should().Be("active");
    }

    [Fact]
    public void CreateProposal_SetsTextType()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);

        var id = _host.Call(() => gov.CreateProposal("Text proposal", 10));

        gov.GetProposalType(id).Should().Be("text");
    }

    [Fact]
    public void CreateProposal_EmitsProposalCreatedEvent()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.ClearEvents();
        _host.SetBlockHeight(100);

        var id = _host.Call(() => gov.CreateProposal("Emit test", 10));

        var events = _host.GetEvents<ProposalCreatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].ProposalId.Should().Be(id);
        events[0].Proposer.Should().BeEquivalentTo(_proposer);
        events[0].Description.Should().Be("Emit test");
        events[0].EndBlock.Should().Be(110); // BlockHeight(100) + votingPeriod(10)
        events[0].ProposalType.Should().Be("text");
    }

    [Fact]
    public void CreateProposal_EmptyDescription_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);

        var msg = _host.ExpectRevert(() => gov.CreateProposal("", 10));
        msg.Should().Contain("description required");
    }

    // ====================================================================
    // 3. CreateExecutableProposal (4 tests)
    // ====================================================================

    [Fact]
    public void CreateExecutableProposal_StoresTargetAndMethod()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);

        var id = _host.Call(() => gov.CreateExecutableProposal(
            "Exec proposal", 10, _targetContract, "DoSomething"));

        gov.GetProposalType(id).Should().Be("executable");
    }

    [Fact]
    public void CreateExecutableProposal_SetsExecutableType()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);

        var id = _host.Call(() => gov.CreateExecutableProposal(
            "Exec type test", 10, _targetContract, "Execute"));

        gov.GetProposalType(id).Should().Be("executable");
        gov.GetStatus(id).Should().Be("active");
    }

    [Fact]
    public void CreateExecutableProposal_EmptyTarget_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);

        var msg = _host.ExpectRevert(() => gov.CreateExecutableProposal(
            "Bad proposal", 10, [], "DoSomething"));
        msg.Should().Contain("target required");
    }

    [Fact]
    public void CreateExecutableProposal_EmptyMethod_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);

        var msg = _host.ExpectRevert(() => gov.CreateExecutableProposal(
            "Bad proposal", 10, _targetContract, ""));
        msg.Should().Contain("method required");
    }

    // ====================================================================
    // 4. Vote (10 tests)
    // ====================================================================

    [Fact]
    public void Vote_For_IncreasesVotesFor()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Vote test", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        // isqrt(10000) = 100
        gov.GetVotesFor(id).Should().Be((UInt256)100);
        gov.GetVotesAgainst(id).Should().Be((UInt256)0);
    }

    [Fact]
    public void Vote_Against_IncreasesVotesAgainst()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Against test", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, false, DefaultPoolId));

        gov.GetVotesFor(id).Should().Be((UInt256)0);
        // isqrt(10000) = 100
        gov.GetVotesAgainst(id).Should().Be((UInt256)100);
    }

    [Fact]
    public void Vote_QuadraticWeight_StakeWeighted()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Weight test", 10));

        // Stake 1000000 -> isqrt = 1000
        SetStake(_alice, 1_000_000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        gov.GetVotesFor(id).Should().Be((UInt256)1000);
    }

    [Fact]
    public void Vote_EmitsVoteCastEvent_WithCorrectWeight()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Event test", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.ClearEvents();
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        var events = _host.GetEvents<VoteCastEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].ProposalId.Should().Be(id);
        events[0].Voter.Should().BeEquivalentTo(_alice);
        events[0].Support.Should().BeTrue();
        events[0].Weight.Should().Be((UInt256)100); // isqrt(10000)
    }

    [Fact]
    public void Vote_DoubleVote_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Double vote", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        var msg = _host.ExpectRevert(() => gov.Vote(id, false, DefaultPoolId));
        msg.Should().Contain("already voted");
    }

    [Fact]
    public void Vote_AfterVotingPeriod_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Expired", 10));
        // endBlock = 10 + 10 = 20

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(21); // Past endBlock
        var msg = _host.ExpectRevert(() => gov.Vote(id, true, DefaultPoolId));
        msg.Should().Contain("voting ended");
    }

    [Fact]
    public void Vote_AtEndBlock_Succeeds()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("At end", 10));
        // endBlock = 10 + 10 = 20

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(20); // Exactly at endBlock
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        gov.GetVotesFor(id).Should().Be((UInt256)100);
    }

    [Fact]
    public void Vote_DelegatedVoter_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Delegated", 10));

        // Alice delegates to Bob
        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.Call(() => gov.DelegateVote(_bob, DefaultPoolId));

        // Alice tries to vote directly
        _host.SetBlockHeight(15);
        var msg = _host.ExpectRevert(() => gov.Vote(id, true, DefaultPoolId));
        msg.Should().Contain("vote delegated");
    }

    [Fact]
    public void Vote_ZeroStake_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("No stake", 10));

        // Alice has no stake (default 0)
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        var msg = _host.ExpectRevert(() => gov.Vote(id, true, DefaultPoolId));
        msg.Should().Contain("no voting power");
    }

    [Fact]
    public void Vote_RecordsVoterWeight()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Weight record", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        gov.GetVoterWeight(id, _alice).Should().Be((UInt256)100);
    }

    // ====================================================================
    // 5. Delegation (8 tests)
    // ====================================================================

    [Fact]
    public void DelegateVote_StoresDelegation()
    {
        var gov = CreateGov();
        SetStake(_alice, 5000);
        _host.SetCaller(_alice);

        _host.Call(() => gov.DelegateVote(_bob, DefaultPoolId));

        gov.GetDelegate(_alice).Should().Be(Convert.ToHexString(_bob));
    }

    [Fact]
    public void DelegateVote_BoostsDelegateePower()
    {
        var gov = CreateGov();
        SetStake(_alice, 5000);
        _host.SetCaller(_alice);

        _host.Call(() => gov.DelegateVote(_bob, DefaultPoolId));

        gov.GetDelegatedPower(_bob).Should().Be((UInt256)5000);
    }

    [Fact]
    public void DelegateVote_DelegateeGetsEnhancedVotingWeight()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Delegation boost", 10));

        // Alice has 5000 stake, delegates to Bob
        SetStake(_alice, 5000);
        _host.SetCaller(_alice);
        _host.Call(() => gov.DelegateVote(_bob, DefaultPoolId));

        // Bob has 5000 own stake + 5000 delegated = 10000 total
        SetStake(_bob, 5000);
        _host.SetCaller(_bob);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        // isqrt(10000) = 100
        gov.GetVotesFor(id).Should().Be((UInt256)100);
    }

    [Fact]
    public void Undelegate_ReversesDelegation()
    {
        var gov = CreateGov();
        SetStake(_alice, 5000);
        _host.SetCaller(_alice);

        _host.Call(() => gov.DelegateVote(_bob, DefaultPoolId));
        gov.GetDelegatedPower(_bob).Should().Be((UInt256)5000);

        _host.Call(() => gov.Undelegate());
        gov.GetDelegatedPower(_bob).Should().Be((UInt256)0);
        gov.GetDelegate(_alice).Should().BeEmpty();
    }

    [Fact]
    public void DelegateVote_ToSelf_Reverts()
    {
        var gov = CreateGov();
        SetStake(_alice, 5000);
        _host.SetCaller(_alice);

        var msg = _host.ExpectRevert(() => gov.DelegateVote(_alice, DefaultPoolId));
        msg.Should().Contain("cannot delegate to self");
    }

    [Fact]
    public void DelegateVote_CannotVoteWhenDelegated()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("No vote when delegated", 10));

        SetStake(_alice, 5000);
        _host.SetCaller(_alice);
        _host.Call(() => gov.DelegateVote(_bob, DefaultPoolId));

        _host.SetBlockHeight(15);
        var msg = _host.ExpectRevert(() => gov.Vote(id, true, DefaultPoolId));
        msg.Should().Contain("vote delegated");
    }

    [Fact]
    public void DelegateVote_Redelegate_UpdatesPowerCorrectly()
    {
        var gov = CreateGov();
        SetStake(_alice, 5000);

        // Alice delegates to Bob
        _host.SetCaller(_alice);
        _host.Call(() => gov.DelegateVote(_bob, DefaultPoolId));
        gov.GetDelegatedPower(_bob).Should().Be((UInt256)5000);
        gov.GetDelegatedPower(_charlie).Should().Be((UInt256)0);

        // Alice re-delegates to Charlie (should remove from Bob, add to Charlie)
        _host.Call(() => gov.DelegateVote(_charlie, DefaultPoolId));
        gov.GetDelegatedPower(_bob).Should().Be((UInt256)0);
        gov.GetDelegatedPower(_charlie).Should().Be((UInt256)5000);
        gov.GetDelegate(_alice).Should().Be(Convert.ToHexString(_charlie));
    }

    [Fact]
    public void DelegateVote_EmitsDelegateChangedEvent()
    {
        var gov = CreateGov();
        SetStake(_alice, 5000);
        _host.SetCaller(_alice);
        _host.ClearEvents();

        _host.Call(() => gov.DelegateVote(_bob, DefaultPoolId));

        var events = _host.GetEvents<DelegateChangedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Delegator.Should().BeEquivalentTo(_alice);
        events[0].Delegatee.Should().BeEquivalentTo(_bob);
    }

    // ====================================================================
    // 6. QueueProposal (6 tests)
    // ====================================================================

    [Fact]
    public void QueueProposal_SetsQueuedStatusAndTimelock()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Queue test", 10));
        // endBlock = 20

        // Vote with enough stake: isqrt(100_000 * 400 / 10000) = isqrt(4000) = 63
        // We need totalVotes >= 63, so a stake of 10000 gives isqrt(10000) = 100
        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        // Queue after voting ends
        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));

        gov.GetStatus(id).Should().Be("queued");
        gov.GetTimelockExpiry(id).Should().Be(26); // 21 + 5
    }

    [Fact]
    public void QueueProposal_EmitsProposalQueuedEvent()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Queue event", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.ClearEvents();
        _host.Call(() => gov.QueueProposal(id));

        var events = _host.GetEvents<ProposalQueuedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].ProposalId.Should().Be(id);
        events[0].TimelockExpiry.Should().Be(26);
    }

    [Fact]
    public void QueueProposal_VotingNotEnded_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Too early", 10));
        // endBlock = 20

        _host.SetBlockHeight(19); // Voting still ongoing
        var msg = _host.ExpectRevert(() => gov.QueueProposal(id));
        msg.Should().Contain("voting not ended");
    }

    [Fact]
    public void QueueProposal_InsufficientQuorum_Rejected()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Low quorum", 10));
        // quorumThreshold = isqrt(100_000 * 400 / 10000) = isqrt(4000) = 63

        // Vote with tiny stake: isqrt(1) = 1 (well below 63)
        SetStake(_alice, 1);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));

        gov.GetStatus(id).Should().Be("rejected");
    }

    [Fact]
    public void QueueProposal_MoreAgainst_Rejected()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Against majority", 10));

        // Alice votes for with small stake
        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        // Bob votes against with larger stake
        SetStake(_bob, 40000);
        _host.SetCaller(_bob);
        _host.Call(() => gov.Vote(id, false, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));

        gov.GetStatus(id).Should().Be("rejected");
    }

    [Fact]
    public void QueueProposal_AlreadyQueued_Reverts()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Double queue", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));
        gov.GetStatus(id).Should().Be("queued");

        // Try to queue again
        var msg = _host.ExpectRevert(() => gov.QueueProposal(id));
        msg.Should().Contain("proposal not active");
    }

    // ====================================================================
    // 7. ExecuteProposal (5 tests)
    // ====================================================================

    [Fact]
    public void ExecuteProposal_AfterTimelock_Succeeds()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Execute test", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));
        // timelockExpiry = 21 + 5 = 26

        _host.SetBlockHeight(26);
        _host.Call(() => gov.ExecuteProposal(id));

        gov.GetStatus(id).Should().Be("executed");
    }

    [Fact]
    public void ExecuteProposal_BeforeTimelock_Reverts()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Early exec", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));

        _host.SetBlockHeight(25); // Before timelockExpiry of 26
        var msg = _host.ExpectRevert(() => gov.ExecuteProposal(id));
        msg.Should().Contain("timelock not expired");
    }

    [Fact]
    public void ExecuteProposal_ExecutableType_CallsTarget()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateExecutableProposal(
            "Exec call", 10, _targetContract, "ActivateFeature"));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));

        _executedCalls.Clear();
        _host.SetBlockHeight(26);
        _host.Call(() => gov.ExecuteProposal(id));

        _executedCalls.Should().Contain(c =>
            c.TargetHex == Convert.ToHexString(_targetContract) && c.Method == "ActivateFeature");
    }

    [Fact]
    public void ExecuteProposal_EmitsProposalExecutedEvent()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Exec event", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));

        _host.SetBlockHeight(26);
        _host.ClearEvents();
        _host.Call(() => gov.ExecuteProposal(id));

        var events = _host.GetEvents<ProposalExecutedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].ProposalId.Should().Be(id);
        events[0].Passed.Should().BeTrue();
    }

    [Fact]
    public void ExecuteProposal_TextProposal_NoTargetCall()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Text only", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));

        _executedCalls.Clear();
        _host.SetBlockHeight(26);
        _host.Call(() => gov.ExecuteProposal(id));

        gov.GetStatus(id).Should().Be("executed");
        _executedCalls.Should().BeEmpty();
    }

    // ====================================================================
    // 8. CancelProposal (4 tests)
    // ====================================================================

    [Fact]
    public void CancelProposal_ProposerCancelsActive()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Cancel me", 10));

        _host.Call(() => gov.CancelProposal(id));

        gov.GetStatus(id).Should().Be("canceled");
    }

    [Fact]
    public void CancelProposal_ProposerCancelsQueued()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Cancel queued", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));
        gov.GetStatus(id).Should().Be("queued");

        _host.SetCaller(_proposer);
        _host.Call(() => gov.CancelProposal(id));

        gov.GetStatus(id).Should().Be("canceled");
    }

    [Fact]
    public void CancelProposal_NonProposer_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Cannot cancel", 10));

        _host.SetCaller(_alice); // Not the proposer
        var msg = _host.ExpectRevert(() => gov.CancelProposal(id));
        msg.Should().Contain("not proposer");
    }

    [Fact]
    public void CancelProposal_AlreadyExecuted_Reverts()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Executed no cancel", 10));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));

        _host.SetBlockHeight(26);
        _host.Call(() => gov.ExecuteProposal(id));

        _host.SetCaller(_proposer);
        var msg = _host.ExpectRevert(() => gov.CancelProposal(id));
        msg.Should().Contain("cannot cancel");
    }

    // ====================================================================
    // 9. IntegerSqrt verification (3 tests)
    // ====================================================================

    [Fact]
    public void IntegerSqrt_ZeroStake_ZeroWeight()
    {
        // Verified indirectly: zero stake results in "no voting power" revert
        // which means isqrt(0) = 0
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Sqrt zero", 10));

        _host.SetCaller(_alice);
        // No stake set -> stake = 0, isqrt(0) = 0
        _host.SetBlockHeight(15);
        var msg = _host.ExpectRevert(() => gov.Vote(id, true, DefaultPoolId));
        msg.Should().Contain("no voting power");
    }

    [Fact]
    public void IntegerSqrt_1000000_Gives1000()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Sqrt 1M", 10));

        SetStake(_alice, 1_000_000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        gov.GetVotesFor(id).Should().Be((UInt256)1000);
        gov.GetVoterWeight(id, _alice).Should().Be((UInt256)1000);
    }

    [Fact]
    public void IntegerSqrt_100_Gives10()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Sqrt 100", 10));

        SetStake(_alice, 100);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        gov.GetVotesFor(id).Should().Be((UInt256)10);
        gov.GetVoterWeight(id, _alice).Should().Be((UInt256)10);
    }

    // ====================================================================
    // 10. Full Lifecycle (2 tests)
    // ====================================================================

    [Fact]
    public void FullLifecycle_Create_Vote_Queue_Execute()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);

        // Step 1: Create proposal
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Full lifecycle", 10));
        gov.GetStatus(id).Should().Be("active");

        // Step 2: Multiple voters vote for
        SetStake(_alice, 10000);
        SetStake(_bob, 20000);

        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetCaller(_bob);
        _host.SetBlockHeight(16);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        // isqrt(10000) = 100, isqrt(20000) = 141
        gov.GetVotesFor(id).Should().Be((UInt256)(100 + 141));
        gov.GetVotesAgainst(id).Should().Be((UInt256)0);

        // Step 3: Queue after voting ends
        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));
        gov.GetStatus(id).Should().Be("queued");

        // Step 4: Execute after timelock
        _host.SetBlockHeight(26);
        _host.Call(() => gov.ExecuteProposal(id));
        gov.GetStatus(id).Should().Be("executed");
    }

    [Fact]
    public void FullLifecycle_Create_Vote_Reject()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);

        // Step 1: Create proposal
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Rejection lifecycle", 10));
        gov.GetStatus(id).Should().Be("active");

        // Step 2: Majority votes against
        SetStake(_alice, 10000);
        SetStake(_bob, 40000);

        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetCaller(_bob);
        _host.SetBlockHeight(16);
        _host.Call(() => gov.Vote(id, false, DefaultPoolId));

        // isqrt(10000) = 100 for, isqrt(40000) = 200 against
        gov.GetVotesFor(id).Should().Be((UInt256)100);
        gov.GetVotesAgainst(id).Should().Be((UInt256)200);

        // Step 3: Queue -> rejected
        _host.SetBlockHeight(21);
        _host.Call(() => gov.QueueProposal(id));
        gov.GetStatus(id).Should().Be("rejected");
    }

    // ====================================================================
    // Additional edge case tests
    // ====================================================================

    [Fact]
    public void Vote_OnNonActiveProposal_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Canceled proposal", 10));

        // Cancel it
        _host.Call(() => gov.CancelProposal(id));

        SetStake(_alice, 10000);
        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        var msg = _host.ExpectRevert(() => gov.Vote(id, true, DefaultPoolId));
        msg.Should().Contain("proposal not active");
    }

    [Fact]
    public void ExecuteProposal_NotQueued_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Not queued", 10));

        _host.SetBlockHeight(100);
        var msg = _host.ExpectRevert(() => gov.ExecuteProposal(id));
        msg.Should().Contain("proposal not queued");
    }

    [Fact]
    public void Undelegate_WhenNotDelegated_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_alice);

        var msg = _host.ExpectRevert(() => gov.Undelegate());
        msg.Should().Contain("not delegated");
    }

    [Fact]
    public void Undelegate_EmitsDelegateChangedEvent_WithEmptyDelegatee()
    {
        var gov = CreateGov();
        SetStake(_alice, 5000);
        _host.SetCaller(_alice);
        _host.Call(() => gov.DelegateVote(_bob, DefaultPoolId));

        _host.ClearEvents();
        _host.Call(() => gov.Undelegate());

        var events = _host.GetEvents<DelegateChangedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Delegator.Should().BeEquivalentTo(_alice);
        events[0].Delegatee.Should().BeEmpty();
    }

    [Fact]
    public void CancelProposal_EmitsProposalCanceledEvent()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Cancel event", 10));

        _host.ClearEvents();
        _host.Call(() => gov.CancelProposal(id));

        var events = _host.GetEvents<ProposalCanceledEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].ProposalId.Should().Be(id);
    }

    [Fact]
    public void QueueProposal_RejectedEmitsExecutedEventWithPassedFalse()
    {
        var gov = CreateGov(quorumBps: 400, timelockDelay: 5);
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Reject event", 10));

        // No votes -> rejected
        _host.SetBlockHeight(21);
        _host.ClearEvents();
        _host.Call(() => gov.QueueProposal(id));

        var events = _host.GetEvents<ProposalExecutedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].ProposalId.Should().Be(id);
        events[0].Passed.Should().BeFalse();
    }

    [Fact]
    public void CreateProposal_ZeroVotingPeriod_Reverts()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);

        var msg = _host.ExpectRevert(() => gov.CreateProposal("Zero period", 0));
        msg.Should().Contain("voting period must be > 0");
    }

    [Fact]
    public void MultipleVoters_AccumulateWeights()
    {
        var gov = CreateGov();
        _host.SetCaller(_proposer);
        _host.SetBlockHeight(10);
        var id = _host.Call(() => gov.CreateProposal("Multi-voter", 10));

        SetStake(_alice, 10000);  // isqrt = 100
        SetStake(_bob, 40000);    // isqrt = 200
        SetStake(_charlie, 90000); // isqrt = 300

        _host.SetCaller(_alice);
        _host.SetBlockHeight(15);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetCaller(_bob);
        _host.Call(() => gov.Vote(id, true, DefaultPoolId));

        _host.SetCaller(_charlie);
        _host.Call(() => gov.Vote(id, false, DefaultPoolId));

        gov.GetVotesFor(id).Should().Be((UInt256)(100 + 200));
        // isqrt(90000) = 300
        gov.GetVotesAgainst(id).Should().Be((UInt256)300);
    }

    [Fact]
    public void DelegateVote_ZeroStake_Reverts()
    {
        var gov = CreateGov();
        // Alice has no stake
        _host.SetCaller(_alice);

        var msg = _host.ExpectRevert(() => gov.DelegateVote(_bob, DefaultPoolId));
        msg.Should().Contain("no stake to delegate");
    }

    [Fact]
    public void GetStatus_UnknownProposal_ReturnsUnknown()
    {
        var gov = CreateGov();
        gov.GetStatus(999).Should().Be("unknown");
    }

    public void Dispose() => _host.Dispose();
}
