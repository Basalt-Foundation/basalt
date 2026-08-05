using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class OracleNetworkTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly OracleNetwork _oracle;
    private readonly byte[] _owner;
    private readonly byte[] _reporter1;
    private readonly byte[] _reporter2;
    private readonly byte[] _reporter3;
    private readonly byte[] _reporter4;
    private readonly byte[] _reporter5;
    private readonly byte[] _stranger;

    public OracleNetworkTests()
    {
        _oracle = new OracleNetwork(slashPercentageBps: 500); // 5% slash
        _owner = BasaltTestHost.CreateAddress(1);
        _reporter1 = BasaltTestHost.CreateAddress(2);
        _reporter2 = BasaltTestHost.CreateAddress(3);
        _reporter3 = BasaltTestHost.CreateAddress(4);
        _reporter4 = BasaltTestHost.CreateAddress(5);
        _reporter5 = BasaltTestHost.CreateAddress(6);
        _stranger = BasaltTestHost.CreateAddress(99);

        Context.NativeTransferHandler = (to, amount) => { };
    }

    // ===================== Feed Management =====================

    [Fact]
    public void CreateFeed_Returns_Incrementing_Ids()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;

        var id0 = _host.Call(() => _oracle.CreateFeed("BST/USD", 100, 100, 3, new UInt256(1000)));
        var id1 = _host.Call(() => _oracle.CreateFeed("ETH/USD", 50, 200, 5, new UInt256(2000)));

        id0.Should().Be(0);
        id1.Should().Be(1);
    }

    [Fact]
    public void CreateFeed_Stores_Parameters()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;

        var id = _host.Call(() => _oracle.CreateFeed("BST/USD", 100, 150, 3, new UInt256(500)));

        _host.Call(() => _oracle.GetFeedName(id)).Should().Be("BST/USD");
        _host.Call(() => _oracle.GetQueryFee(id)).Should().Be(new UInt256(500));
        _host.Call(() => _oracle.GetFeedHeartbeatBlocks(id)).Should().Be(100);
        _host.Call(() => _oracle.GetFeedDeviationThresholdBps(id)).Should().Be(150u);
        _host.Call(() => _oracle.GetFeedMinReporters(id)).Should().Be(3u);
        _host.Call(() => _oracle.GetFeedCount()).Should().Be(1);
    }

    [Fact]
    public void CreateFeed_Emits_Event()
    {
        _host.SetCaller(_owner);
        _host.ClearEvents();
        Context.TxValue = 0;

        _host.Call(() => _oracle.CreateFeed("BST/USD", 100, 100, 3, new UInt256(1000)));

        var events = _host.GetEvents<FeedCreatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].FeedId.Should().Be(0);
        events[0].Name.Should().Be("BST/USD");
        events[0].MinReporters.Should().Be(3);
    }

    [Fact]
    public void CreateFeed_With_Empty_Name_Fails()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _oracle.CreateFeed("", 100, 100, 3, new UInt256(1000)));
        msg.Should().Contain("name required");
    }

    [Fact]
    public void CreateFeed_With_Less_Than_3_Reporters_Fails()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _oracle.CreateFeed("BST/USD", 100, 100, 2, new UInt256(1000)));
        msg.Should().Contain("at least 3 reporters");
    }

    [Fact]
    public void CreateFeed_With_Zero_Heartbeat_Fails()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _oracle.CreateFeed("BST/USD", 0, 100, 3, new UInt256(1000)));
        msg.Should().Contain("invalid heartbeat");
    }

    [Fact]
    public void CreateFeed_With_Invalid_Deviation_Fails()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _oracle.CreateFeed("BST/USD", 100, 0, 3, new UInt256(1000)));
        msg.Should().Contain("invalid deviation");
    }

    [Fact]
    public void PauseFeed_And_UnpauseFeed()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;
        var id = _host.Call(() => _oracle.CreateFeed("BST/USD", 100, 100, 3, new UInt256(1000)));

        _host.Call(() => _oracle.IsFeedPaused(id)).Should().BeFalse();

        _host.Call(() => _oracle.PauseFeed(id));
        _host.Call(() => _oracle.IsFeedPaused(id)).Should().BeTrue();

        _host.Call(() => _oracle.UnpauseFeed(id));
        _host.Call(() => _oracle.IsFeedPaused(id)).Should().BeFalse();
    }

    [Fact]
    public void PauseFeed_By_NonOwner_Fails()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;
        var id = _host.Call(() => _oracle.CreateFeed("BST/USD", 100, 100, 3, new UInt256(1000)));

        _host.SetCaller(_stranger);
        var msg = _host.ExpectRevert(() => _oracle.PauseFeed(id));
        msg.Should().Contain("not feed owner");
    }

    [Fact]
    public void UpdateFeedParameters_By_Owner()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;
        var id = _host.Call(() => _oracle.CreateFeed("BST/USD", 100, 100, 3, new UInt256(1000)));

        _host.Call(() => _oracle.UpdateFeedParameters(id, 200, 300, new UInt256(5000)));

        _host.Call(() => _oracle.GetFeedHeartbeatBlocks(id)).Should().Be(200);
        _host.Call(() => _oracle.GetFeedDeviationThresholdBps(id)).Should().Be(300u);
        _host.Call(() => _oracle.GetQueryFee(id)).Should().Be(new UInt256(5000));
    }

    [Fact]
    public void UpdateFeedParameters_By_NonOwner_Fails()
    {
        _host.SetCaller(_owner);
        Context.TxValue = 0;
        var id = _host.Call(() => _oracle.CreateFeed("BST/USD", 100, 100, 3, new UInt256(1000)));

        _host.SetCaller(_stranger);
        var msg = _host.ExpectRevert(() => _oracle.UpdateFeedParameters(id, 200, 300, new UInt256(5000)));
        msg.Should().Contain("not feed owner");
    }

    // ===================== Reporter Management =====================

    [Fact]
    public void RegisterReporter_With_Sufficient_Stake()
    {
        _host.SetCaller(_reporter1);
        Context.TxValue = new UInt256(10_000);

        _host.Call(() => _oracle.RegisterReporter());

        _host.Call(() => _oracle.IsReporterActive(_reporter1)).Should().BeTrue();
        _host.Call(() => _oracle.GetReporterStake(_reporter1)).Should().Be(new UInt256(10_000));
        _host.Call(() => _oracle.GetReporterCount()).Should().Be(1);
    }

    [Fact]
    public void RegisterReporter_Emits_Event()
    {
        _host.SetCaller(_reporter1);
        _host.ClearEvents();
        Context.TxValue = new UInt256(50_000);

        _host.Call(() => _oracle.RegisterReporter());

        var events = _host.GetEvents<ReporterRegisteredEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Reporter.Should().BeEquivalentTo(_reporter1);
        events[0].Stake.Should().Be(new UInt256(50_000));
    }

    [Fact]
    public void RegisterReporter_Below_MinStake_Fails()
    {
        _host.SetCaller(_reporter1);
        Context.TxValue = new UInt256(9_999);

        var msg = _host.ExpectRevert(() => _oracle.RegisterReporter());
        msg.Should().Contain("below min stake");
    }

    [Fact]
    public void RegisterReporter_With_Zero_Value_Fails()
    {
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;

        var msg = _host.ExpectRevert(() => _oracle.RegisterReporter());
        msg.Should().Contain("must stake");
    }

    [Fact]
    public void RegisterReporter_Twice_Fails()
    {
        _host.SetCaller(_reporter1);
        Context.TxValue = new UInt256(10_000);
        _host.Call(() => _oracle.RegisterReporter());

        Context.TxValue = new UInt256(10_000);
        var msg = _host.ExpectRevert(() => _oracle.RegisterReporter());
        msg.Should().Contain("already registered");
    }

    [Fact]
    public void IncreaseStake_Adds_To_Existing()
    {
        _host.SetCaller(_reporter1);
        Context.TxValue = new UInt256(10_000);
        _host.Call(() => _oracle.RegisterReporter());

        Context.TxValue = new UInt256(5_000);
        _host.Call(() => _oracle.IncreaseStake());

        _host.Call(() => _oracle.GetReporterStake(_reporter1)).Should().Be(new UInt256(15_000));
    }

    [Fact]
    public void UnregisterReporter_Returns_Stake()
    {
        _host.SetCaller(_reporter1);
        Context.TxValue = new UInt256(10_000);
        _host.Call(() => _oracle.RegisterReporter());

        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.UnregisterReporter());

        _host.Call(() => _oracle.IsReporterActive(_reporter1)).Should().BeFalse();
        _host.Call(() => _oracle.GetReporterStake(_reporter1)).Should().Be(UInt256.Zero);
        _host.Call(() => _oracle.GetReporterCount()).Should().Be(0);
    }

    [Fact]
    public void UnregisterReporter_When_Not_Registered_Fails()
    {
        _host.SetCaller(_stranger);
        Context.TxValue = UInt256.Zero;

        var msg = _host.ExpectRevert(() => _oracle.UnregisterReporter());
        msg.Should().Contain("not registered");
    }

    // ===================== Round Lifecycle =====================

    [Fact]
    public void OpenRound_On_First_Round()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;

        var round = _host.Call(() => _oracle.OpenRound(feedId));

        round.Should().Be(1);
        _host.Call(() => _oracle.GetCurrentRound(feedId)).Should().Be(1);
        _host.Call(() => _oracle.GetRoundStatus(feedId, 1)).Should().Be("open");
    }

    [Fact]
    public void OpenRound_Emits_Event()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        _host.ClearEvents();
        Context.TxValue = UInt256.Zero;

        _host.Call(() => _oracle.OpenRound(feedId));

        var events = _host.GetEvents<RoundOpenedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].FeedId.Should().Be(feedId);
        events[0].Round.Should().Be(1);
    }

    [Fact]
    public void OpenRound_When_Feed_Paused_Fails()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetCaller(_owner);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.PauseFeed(feedId));

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        var msg = _host.ExpectRevert(() => _oracle.OpenRound(feedId));
        msg.Should().Contain("feed paused");
    }

    [Fact]
    public void OpenRound_When_Previous_Still_Open_Fails()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        var msg = _host.ExpectRevert(() => _oracle.OpenRound(feedId));
        msg.Should().Contain("previous round still open");
    }

    [Fact]
    public void OpenRound_Before_Heartbeat_Expired_Fails()
    {
        var feedId = SetupFeedAndReporters();

        // Complete a round first
        CompleteRound(feedId, 1, 10, new UInt256(1000), new UInt256(1000), new UInt256(1000));

        // Try to open another round before heartbeat (100 blocks) expires
        _host.SetBlockHeight(50);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;

        var msg = _host.ExpectRevert(() => _oracle.OpenRound(feedId));
        msg.Should().Contain("heartbeat not expired");
    }

    [Fact]
    public void OpenRound_After_Heartbeat_Expired_Succeeds()
    {
        var feedId = SetupFeedAndReporters();

        // Complete a round at block 10
        CompleteRound(feedId, 1, 10, new UInt256(1000), new UInt256(1000), new UInt256(1000));

        // Open new round after heartbeat (100 blocks)
        _host.SetBlockHeight(120);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;

        var round = _host.Call(() => _oracle.OpenRound(feedId));
        round.Should().Be(2);
    }

    [Fact]
    public void OpenRound_By_NonReporter_Fails()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetBlockHeight(1);
        _host.SetCaller(_stranger);
        Context.TxValue = UInt256.Zero;

        var msg = _host.ExpectRevert(() => _oracle.OpenRound(feedId));
        msg.Should().Contain("not active reporter");
    }

    [Fact]
    public void OpenRound_On_Nonexistent_Feed_Fails()
    {
        RegisterReporter(_reporter1, 10_000);

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;

        var msg = _host.ExpectRevert(() => _oracle.OpenRound(999));
        msg.Should().Contain("feed does not exist");
    }

    [Fact]
    public void SubmitValue_Records_Submission()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        _host.Call(() => _oracle.SubmitValue(feedId, new UInt256(1000)));

        _host.Call(() => _oracle.GetRoundSubmissionCount(feedId, 1)).Should().Be(1);
    }

    [Fact]
    public void SubmitValue_Duplicate_Fails()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));
        _host.Call(() => _oracle.SubmitValue(feedId, new UInt256(1000)));

        var msg = _host.ExpectRevert(() => _oracle.SubmitValue(feedId, new UInt256(1001)));
        msg.Should().Contain("already submitted");
    }

    [Fact]
    public void SubmitValue_When_Round_Not_Open_Fails()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;

        var msg = _host.ExpectRevert(() => _oracle.SubmitValue(feedId, new UInt256(1000)));
        msg.Should().Contain("no active round");
    }

    // ===================== FinalizeRound =====================

    [Fact]
    public void FinalizeRound_Computes_Median_Odd_Count()
    {
        var feedId = SetupFeedAndReporters();

        // 3 submissions: 900, 1000, 1100 → median = 1000
        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        SubmitFrom(_reporter1, feedId, 900);
        SubmitFrom(_reporter2, feedId, 1100);
        SubmitFrom(_reporter3, feedId, 1000);

        _host.SetBlockHeight(10);
        var median = _host.Call(() => _oracle.FinalizeRound(feedId));

        median.Should().Be(new UInt256(1000));
        _host.Call(() => _oracle.GetLatestValue(feedId)).Should().Be(new UInt256(1000));
        _host.Call(() => _oracle.GetRoundStatus(feedId, 1)).Should().Be("finalized");
        _host.Call(() => _oracle.GetRoundMedian(feedId, 1)).Should().Be(new UInt256(1000));
    }

    [Fact]
    public void FinalizeRound_Computes_Median_Even_Count()
    {
        var feedId = SetupFeedWithReporters(4, minReporters: 4);

        // 4 submissions: 800, 900, 1000, 1100 → median = avg(900, 1000) = 950
        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        SubmitFrom(_reporter1, feedId, 800);
        SubmitFrom(_reporter2, feedId, 1100);
        SubmitFrom(_reporter3, feedId, 900);
        SubmitFrom(_reporter4, feedId, 1000);

        _host.SetBlockHeight(10);
        var median = _host.Call(() => _oracle.FinalizeRound(feedId));

        median.Should().Be(new UInt256(950));
    }

    [Fact]
    public void FinalizeRound_Emits_Event()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        SubmitFrom(_reporter1, feedId, 1000);
        SubmitFrom(_reporter2, feedId, 1000);
        SubmitFrom(_reporter3, feedId, 1000);

        _host.SetBlockHeight(10);
        _host.ClearEvents();
        _host.Call(() => _oracle.FinalizeRound(feedId));

        var events = _host.GetEvents<RoundFinalizedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].FeedId.Should().Be(feedId);
        events[0].Round.Should().Be(1);
        events[0].MedianValue.Should().Be(new UInt256(1000));
        events[0].SubmissionCount.Should().Be(3);
        events[0].HonestCount.Should().Be(3);
        events[0].TotalSlashed.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void FinalizeRound_Before_MinWindow_Fails()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        SubmitFrom(_reporter1, feedId, 1000);
        SubmitFrom(_reporter2, feedId, 1000);
        SubmitFrom(_reporter3, feedId, 1000);

        // Still at block 1, window is 5 blocks
        var msg = _host.ExpectRevert(() => _oracle.FinalizeRound(feedId));
        msg.Should().Contain("round window not elapsed");
    }

    [Fact]
    public void FinalizeRound_With_Insufficient_Submissions_Fails()
    {
        var feedId = SetupFeedAndReporters();

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        SubmitFrom(_reporter1, feedId, 1000);
        SubmitFrom(_reporter2, feedId, 1000);

        _host.SetBlockHeight(10);
        var msg = _host.ExpectRevert(() => _oracle.FinalizeRound(feedId));
        msg.Should().Contain("insufficient submissions");
    }

    // ===================== Slashing =====================

    [Fact]
    public void FinalizeRound_Slashes_Deviant_Reporter()
    {
        var feedId = SetupFeedAndReporters(); // threshold = 100 bps (1%), slash = 5%

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        // Reporter1 submits wildly off: 5000 (500% of median ~1000)
        SubmitFrom(_reporter1, feedId, 5000);
        SubmitFrom(_reporter2, feedId, 1000);
        SubmitFrom(_reporter3, feedId, 1010);

        _host.SetBlockHeight(10);
        _host.ClearEvents();
        _host.Call(() => _oracle.FinalizeRound(feedId));

        // Median = 1010. Reporter1 (5000) deviates by ~394%, well above 1% threshold
        // Slash: 5% of 10,000 = 500
        var stake = _host.Call(() => _oracle.GetReporterStake(_reporter1));
        stake.Should().Be(new UInt256(9_500));

        var slashEvents = _host.GetEvents<ReporterSlashedEvent>().ToList();
        slashEvents.Should().HaveCount(1);
        slashEvents[0].Reporter.Should().BeEquivalentTo(_reporter1);
        slashEvents[0].SlashedAmount.Should().Be(new UInt256(500));
    }

    [Fact]
    public void FinalizeRound_Does_Not_Slash_Within_Threshold()
    {
        // Create feed with 500 bps (5%) threshold
        _host.SetCaller(_owner);
        Context.TxValue = UInt256.Zero;
        var feedId = _host.Call(() => _oracle.CreateFeed("BST/USD", 100, 500, 3, UInt256.Zero));

        RegisterReporter(_reporter1, 10_000);
        RegisterReporter(_reporter2, 10_000);
        RegisterReporter(_reporter3, 10_000);

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        // All within 5% of each other
        SubmitFrom(_reporter1, feedId, 1000);
        SubmitFrom(_reporter2, feedId, 1040); // 4% deviation from median ~1020
        SubmitFrom(_reporter3, feedId, 1020);

        _host.SetBlockHeight(10);
        _host.ClearEvents();
        _host.Call(() => _oracle.FinalizeRound(feedId));

        // No slashing should occur
        _host.GetEvents<ReporterSlashedEvent>().Should().BeEmpty();
        _host.Call(() => _oracle.GetReporterStake(_reporter1)).Should().Be(new UInt256(10_000));
        _host.Call(() => _oracle.GetReporterStake(_reporter2)).Should().Be(new UInt256(10_000));
        _host.Call(() => _oracle.GetReporterStake(_reporter3)).Should().Be(new UInt256(10_000));
    }

    [Fact]
    public void Slashed_Funds_Distributed_To_Honest_Reporters()
    {
        var feedId = SetupFeedAndReporters(); // 1% threshold, 5% slash

        _host.SetBlockHeight(1);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        // Reporter1 deviates wildly, reporters 2+3 are honest
        SubmitFrom(_reporter1, feedId, 5000);
        SubmitFrom(_reporter2, feedId, 1000);
        SubmitFrom(_reporter3, feedId, 1000);

        _host.SetBlockHeight(10);
        _host.Call(() => _oracle.FinalizeRound(feedId));

        // Slash: 500 from reporter1, distributed to 2 honest reporters → 250 each
        var fees2 = _host.Call(() => _oracle.GetReporterEarnedFees(_reporter2));
        var fees3 = _host.Call(() => _oracle.GetReporterEarnedFees(_reporter3));
        fees2.Should().Be(new UInt256(250));
        fees3.Should().Be(new UInt256(250));
    }

    // ===================== Fee Management =====================

    [Fact]
    public void QueryLatestValue_Requires_Fee_And_Accumulates()
    {
        var feedId = SetupFeedAndReporters();

        // Complete a round to set latest value
        CompleteRound(feedId, 1, 10, new UInt256(1000), new UInt256(1000), new UInt256(1000));

        _host.SetCaller(_stranger);
        Context.TxValue = new UInt256(1000);
        var value = _host.Call(() => _oracle.QueryLatestValue(feedId));

        value.Should().Be(new UInt256(1000));
        _host.Call(() => _oracle.GetFeedAccumulatedFees(feedId)).Should().Be(new UInt256(1000));
    }

    [Fact]
    public void QueryLatestValue_With_Insufficient_Fee_Fails()
    {
        var feedId = SetupFeedAndReporters();
        CompleteRound(feedId, 1, 10, new UInt256(1000), new UInt256(1000), new UInt256(1000));

        _host.SetCaller(_stranger);
        Context.TxValue = new UInt256(999); // Fee is 1000

        var msg = _host.ExpectRevert(() => _oracle.QueryLatestValue(feedId));
        msg.Should().Contain("insufficient query fee");
    }

    [Fact]
    public void Accumulated_Fees_Distributed_On_FinalizeRound()
    {
        var feedId = SetupFeedAndReporters();

        // Complete round 1
        CompleteRound(feedId, 1, 10, new UInt256(1000), new UInt256(1000), new UInt256(1000));

        // Consumer pays query fee
        _host.SetCaller(_stranger);
        Context.TxValue = new UInt256(3000);
        _host.Call(() => _oracle.QueryLatestValue(feedId));

        // Open and complete round 2 — fees should be distributed
        _host.SetBlockHeight(120);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        SubmitFrom(_reporter1, feedId, 2000);
        SubmitFrom(_reporter2, feedId, 2000);
        SubmitFrom(_reporter3, feedId, 2000);

        _host.SetBlockHeight(130);
        _host.Call(() => _oracle.FinalizeRound(feedId));

        // 3000 fees / 3 honest reporters = 1000 each
        _host.Call(() => _oracle.GetReporterEarnedFees(_reporter1)).Should().Be(new UInt256(1000));
        _host.Call(() => _oracle.GetReporterEarnedFees(_reporter2)).Should().Be(new UInt256(1000));
        _host.Call(() => _oracle.GetReporterEarnedFees(_reporter3)).Should().Be(new UInt256(1000));
        _host.Call(() => _oracle.GetFeedAccumulatedFees(feedId)).Should().Be(UInt256.Zero);
    }

    [Fact]
    public void ClaimReporterFees_Transfers_And_Zeroes()
    {
        var feedId = SetupFeedAndReporters();

        // Pay query fee and finalize to distribute
        _host.SetCaller(_stranger);
        Context.TxValue = new UInt256(3000);
        _host.Call(() => _oracle.QueryLatestValue(feedId));

        CompleteRound(feedId, 1, 10, new UInt256(1000), new UInt256(1000), new UInt256(1000));

        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.ClaimReporterFees());

        _host.Call(() => _oracle.GetReporterEarnedFees(_reporter1)).Should().Be(UInt256.Zero);
    }

    [Fact]
    public void ClaimReporterFees_With_No_Fees_Fails()
    {
        RegisterReporter(_reporter1, 10_000);

        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;

        var msg = _host.ExpectRevert(() => _oracle.ClaimReporterFees());
        msg.Should().Contain("no fees to claim");
    }

    // ===================== View Queries =====================

    [Fact]
    public void GetLatestValue_Returns_Zero_Before_Any_Round()
    {
        var feedId = SetupFeedAndReporters();
        _host.Call(() => _oracle.GetLatestValue(feedId)).Should().Be(UInt256.Zero);
    }

    [Fact]
    public void GetRoundStatus_Returns_Unknown_For_Nonexistent()
    {
        var feedId = SetupFeedAndReporters();
        _host.Call(() => _oracle.GetRoundStatus(feedId, 999)).Should().Be("unknown");
    }

    [Fact]
    public void GetLastUpdateBlock_Updates_After_Finalization()
    {
        var feedId = SetupFeedAndReporters();
        CompleteRound(feedId, 1, 10, new UInt256(1000), new UInt256(1000), new UInt256(1000));

        _host.Call(() => _oracle.GetLastUpdateBlock(feedId)).Should().Be(15);
    }

    // ===================== Median Computation (unit) =====================

    [Fact]
    public void ComputeMedian_Odd_Count()
    {
        var values = new UInt256[] { 300, 100, 200 };
        OracleNetwork.ComputeMedian(values).Should().Be(new UInt256(200));
    }

    [Fact]
    public void ComputeMedian_Even_Count()
    {
        var values = new UInt256[] { 400, 100, 200, 300 };
        // Sorted: 100, 200, 300, 400. Average of 200 and 300 = 250
        OracleNetwork.ComputeMedian(values).Should().Be(new UInt256(250));
    }

    [Fact]
    public void ComputeMedian_All_Same()
    {
        var values = new UInt256[] { 500, 500, 500, 500, 500 };
        OracleNetwork.ComputeMedian(values).Should().Be(new UInt256(500));
    }

    [Fact]
    public void ComputeMedian_Single_Value()
    {
        var values = new UInt256[] { 42 };
        OracleNetwork.ComputeMedian(values).Should().Be(new UInt256(42));
    }

    [Fact]
    public void ComputeMedian_Large_Values()
    {
        var big = UInt256.Parse("1000000000000000000"); // 10^18
        var values = new UInt256[] { big, big + new UInt256(100), big - new UInt256(100) };
        OracleNetwork.ComputeMedian(values).Should().Be(big);
    }

    // ===================== IsWithinThreshold (unit) =====================

    [Fact]
    public void IsWithinThreshold_Exact_Match()
    {
        OracleNetwork.IsWithinThreshold(new UInt256(1000), new UInt256(1000), 100).Should().BeTrue();
    }

    [Fact]
    public void IsWithinThreshold_At_Boundary()
    {
        // 1% threshold, median 1000: max deviation = 10
        OracleNetwork.IsWithinThreshold(new UInt256(1010), new UInt256(1000), 100).Should().BeTrue();
        OracleNetwork.IsWithinThreshold(new UInt256(990), new UInt256(1000), 100).Should().BeTrue();
    }

    [Fact]
    public void IsWithinThreshold_Beyond_Boundary()
    {
        OracleNetwork.IsWithinThreshold(new UInt256(1011), new UInt256(1000), 100).Should().BeFalse();
        OracleNetwork.IsWithinThreshold(new UInt256(989), new UInt256(1000), 100).Should().BeFalse();
    }

    [Fact]
    public void IsWithinThreshold_Zero_Median()
    {
        OracleNetwork.IsWithinThreshold(UInt256.Zero, UInt256.Zero, 100).Should().BeTrue();
        OracleNetwork.IsWithinThreshold(new UInt256(1), UInt256.Zero, 100).Should().BeFalse();
    }

    // ===================== Multi-Round Lifecycle =====================

    [Fact]
    public void Full_Multi_Round_Lifecycle()
    {
        var feedId = SetupFeedAndReporters();

        // Round 1: price = 1000
        CompleteRound(feedId, 1, 10, new UInt256(1000), new UInt256(1000), new UInt256(1000));
        _host.Call(() => _oracle.GetLatestValue(feedId)).Should().Be(new UInt256(1000));

        // Round 2: price moves to 2000 (after heartbeat)
        _host.SetBlockHeight(120);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        SubmitFrom(_reporter1, feedId, 2000);
        SubmitFrom(_reporter2, feedId, 2010);
        SubmitFrom(_reporter3, feedId, 1990);

        _host.SetBlockHeight(130);
        var median = _host.Call(() => _oracle.FinalizeRound(feedId));

        median.Should().Be(new UInt256(2000));
        _host.Call(() => _oracle.GetLatestValue(feedId)).Should().Be(new UInt256(2000));
        _host.Call(() => _oracle.GetCurrentRound(feedId)).Should().Be(2);

        // Historical data preserved
        _host.Call(() => _oracle.GetRoundMedian(feedId, 1)).Should().Be(new UInt256(1000));
        _host.Call(() => _oracle.GetRoundMedian(feedId, 2)).Should().Be(new UInt256(2000));
    }

    // ===================== Helpers =====================

    private void RegisterReporter(byte[] reporter, ulong stake)
    {
        _host.SetCaller(reporter);
        Context.TxValue = new UInt256(stake);
        _host.Call(() => _oracle.RegisterReporter());
        Context.TxValue = UInt256.Zero;
    }

    private ulong SetupFeedAndReporters()
    {
        return SetupFeedWithReporters(3, minReporters: 3);
    }

    private ulong SetupFeedWithReporters(int count, uint minReporters)
    {
        _host.SetCaller(_owner);
        Context.TxValue = UInt256.Zero;
        var feedId = _host.Call(() => _oracle.CreateFeed(
            "BST/USD", 100, 100, minReporters, new UInt256(1000)));

        var reporters = new[] { _reporter1, _reporter2, _reporter3, _reporter4, _reporter5 };
        for (int i = 0; i < count; i++)
            RegisterReporter(reporters[i], 10_000);

        return feedId;
    }

    private void SubmitFrom(byte[] reporter, ulong feedId, ulong value)
    {
        _host.SetCaller(reporter);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.SubmitValue(feedId, new UInt256(value)));
    }

    private void CompleteRound(ulong feedId, ulong startBlock, ulong finalizeBlock,
        UInt256 v1, UInt256 v2, UInt256 v3)
    {
        _host.SetBlockHeight(startBlock);
        _host.SetCaller(_reporter1);
        Context.TxValue = UInt256.Zero;
        _host.Call(() => _oracle.OpenRound(feedId));

        _host.SetCaller(_reporter1);
        _host.Call(() => _oracle.SubmitValue(feedId, v1));
        _host.SetCaller(_reporter2);
        _host.Call(() => _oracle.SubmitValue(feedId, v2));
        _host.SetCaller(_reporter3);
        _host.Call(() => _oracle.SubmitValue(feedId, v3));

        _host.SetBlockHeight(finalizeBlock + MinRoundWindowBlocks);
        _host.Call(() => _oracle.FinalizeRound(feedId));
    }

    private const uint MinRoundWindowBlocks = 5;

    public void Dispose() => _host.Dispose();
}