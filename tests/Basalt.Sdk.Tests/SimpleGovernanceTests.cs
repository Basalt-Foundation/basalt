using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class SimpleGovernanceTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly SimpleGovernance _gov;
    private readonly byte[] _alice;
    private readonly byte[] _bob;
    private readonly byte[] _charlie;
    private readonly byte[] _dave;

    public SimpleGovernanceTests()
    {
        _gov = new SimpleGovernance(quorumThreshold: 3);
        _alice = BasaltTestHost.CreateAddress(1);
        _bob = BasaltTestHost.CreateAddress(2);
        _charlie = BasaltTestHost.CreateAddress(3);
        _dave = BasaltTestHost.CreateAddress(4);
    }

    [Fact]
    public void CreateProposal_Returns_Id()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);

        var id = _host.Call(() => _gov.CreateProposal("Upgrade protocol", 100));

        id.Should().Be(0);
    }

    [Fact]
    public void CreateProposal_Increments_Id()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);

        var id0 = _host.Call(() => _gov.CreateProposal("First proposal", 100));
        var id1 = _host.Call(() => _gov.CreateProposal("Second proposal", 200));

        id0.Should().Be(0);
        id1.Should().Be(1);
    }

    [Fact]
    public void CreateProposal_Sets_Status_Active()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.Call(() => _gov.GetStatus(id)).Should().Be("active");
    }

    [Fact]
    public void CreateProposal_Emits_Event()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        _host.ClearEvents();

        _host.Call(() => _gov.CreateProposal("Upgrade protocol", 100));

        var events = _host.GetEvents<ProposalCreatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].ProposalId.Should().Be(0);
        events[0].Description.Should().Be("Upgrade protocol");
        events[0].EndBlock.Should().Be(100);
    }

    [Fact]
    public void CreateProposal_With_EndBlock_Not_In_Future_Fails()
    {
        _host.SetBlockHeight(100);
        _host.SetCaller(_alice);

        var msg = _host.ExpectRevert(() => _gov.CreateProposal("Proposal", 50));
        msg.Should().Contain("end block must be in future");
    }

    [Fact]
    public void CreateProposal_With_Empty_Description_Fails()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);

        var msg = _host.ExpectRevert(() => _gov.CreateProposal("", 100));
        msg.Should().Contain("description required");
    }

    [Fact]
    public void Vote_For_Increases_VotesFor()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));

        _host.Call(() => _gov.GetVotesFor(id)).Should().Be(1);
        _host.Call(() => _gov.GetVotesAgainst(id)).Should().Be(0);
    }

    [Fact]
    public void Vote_Against_Increases_VotesAgainst()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, false));

        _host.Call(() => _gov.GetVotesFor(id)).Should().Be(0);
        _host.Call(() => _gov.GetVotesAgainst(id)).Should().Be(1);
    }

    [Fact]
    public void Cannot_Vote_Twice()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));

        var msg = _host.ExpectRevert(() => _gov.Vote(id, false));
        msg.Should().Contain("already voted");
    }

    [Fact]
    public void Cannot_Vote_After_EndBlock()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.SetBlockHeight(101);
        _host.SetCaller(_bob);

        var msg = _host.ExpectRevert(() => _gov.Vote(id, true));
        msg.Should().Contain("voting ended");
    }

    [Fact]
    public void Vote_At_EndBlock_Succeeds()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.SetBlockHeight(100);
        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));

        _host.Call(() => _gov.GetVotesFor(id)).Should().Be(1);
    }

    [Fact]
    public void ExecuteProposal_Passes_With_Quorum_And_Majority()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        // 3 votes for (meets quorum of 3, all for)
        _host.SetCaller(_alice);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_charlie);
        _host.Call(() => _gov.Vote(id, true));

        // Advance past end block
        _host.SetBlockHeight(101);
        _host.SetCaller(_alice);
        _host.Call(() => _gov.ExecuteProposal(id));

        _host.Call(() => _gov.GetStatus(id)).Should().Be("executed");
    }

    [Fact]
    public void ExecuteProposal_Emits_Passed_Event()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.SetCaller(_alice);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_charlie);
        _host.Call(() => _gov.Vote(id, true));

        _host.SetBlockHeight(101);
        _host.ClearEvents();
        _host.SetCaller(_alice);
        _host.Call(() => _gov.ExecuteProposal(id));

        var events = _host.GetEvents<ProposalExecutedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Passed.Should().BeTrue();
    }

    [Fact]
    public void ExecuteProposal_Rejected_Insufficient_Votes()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        // Only 2 votes (below quorum of 3)
        _host.SetCaller(_alice);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));

        _host.SetBlockHeight(101);
        _host.SetCaller(_alice);
        _host.Call(() => _gov.ExecuteProposal(id));

        _host.Call(() => _gov.GetStatus(id)).Should().Be("rejected");
    }

    [Fact]
    public void ExecuteProposal_Rejected_More_Against_Than_For()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        // 1 for, 2 against (meets quorum of 3 total, but more against)
        _host.SetCaller(_alice);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, false));
        _host.SetCaller(_charlie);
        _host.Call(() => _gov.Vote(id, false));

        _host.SetBlockHeight(101);
        _host.SetCaller(_alice);
        _host.Call(() => _gov.ExecuteProposal(id));

        _host.Call(() => _gov.GetStatus(id)).Should().Be("rejected");
    }

    [Fact]
    public void ExecuteProposal_Rejected_Equal_Votes()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        // 2 for, 2 against (meets quorum of 3, but equal -- not strictly more for)
        _host.SetCaller(_alice);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_charlie);
        _host.Call(() => _gov.Vote(id, false));
        _host.SetCaller(_dave);
        _host.Call(() => _gov.Vote(id, false));

        _host.SetBlockHeight(101);
        _host.SetCaller(_alice);
        _host.Call(() => _gov.ExecuteProposal(id));

        _host.Call(() => _gov.GetStatus(id)).Should().Be("rejected");
    }

    [Fact]
    public void Cannot_Execute_Before_EndBlock()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.SetCaller(_alice);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_charlie);
        _host.Call(() => _gov.Vote(id, true));

        // Still at block 10, before end block 100
        var msg = _host.ExpectRevert(() => _gov.ExecuteProposal(id));
        msg.Should().Contain("voting not ended");
    }

    [Fact]
    public void Cannot_Execute_At_EndBlock()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.SetCaller(_alice);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_charlie);
        _host.Call(() => _gov.Vote(id, true));

        // At exactly end block 100
        _host.SetBlockHeight(100);
        var msg = _host.ExpectRevert(() => _gov.ExecuteProposal(id));
        msg.Should().Contain("voting not ended");
    }

    [Fact]
    public void Cannot_Execute_Already_Executed_Proposal()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_alice);
        var id = _host.Call(() => _gov.CreateProposal("Proposal", 100));

        _host.SetCaller(_alice);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_bob);
        _host.Call(() => _gov.Vote(id, true));
        _host.SetCaller(_charlie);
        _host.Call(() => _gov.Vote(id, true));

        _host.SetBlockHeight(101);
        _host.SetCaller(_alice);
        _host.Call(() => _gov.ExecuteProposal(id));

        var msg = _host.ExpectRevert(() => _gov.ExecuteProposal(id));
        msg.Should().Contain("proposal not active");
    }

    [Fact]
    public void GetStatus_Returns_Unknown_For_Nonexistent_Proposal()
    {
        _host.Call(() => _gov.GetStatus(999)).Should().Be("unknown");
    }

    public void Dispose() => _host.Dispose();
}
