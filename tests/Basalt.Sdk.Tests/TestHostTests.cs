using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class TestHostTests : IDisposable
{
    private readonly BasaltTestHost _host = new();

    [Fact]
    public void Snapshot_And_Restore_Reverts_Storage()
    {
        var token = new MintableBST20("Test", "T", 18);
        var alice = BasaltTestHost.CreateAddress(1);
        var owner = BasaltTestHost.CreateAddress(2);

        _host.SetCaller(owner);
        _host.Call(() => token.MintPublic(alice, 1000));
        _host.Call(() => token.BalanceOf(alice)).Should().Be(1000);

        _host.TakeSnapshot();

        _host.Call(() => token.MintPublic(alice, 5000));
        _host.Call(() => token.BalanceOf(alice)).Should().Be(6000);

        _host.RestoreSnapshot();
        _host.Call(() => token.BalanceOf(alice)).Should().Be(1000);
    }

    [Fact]
    public void RestoreSnapshot_Throws_Without_Snapshot()
    {
        var act = () => _host.RestoreSnapshot();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AdvanceBlocks_Updates_Height_And_Timestamp()
    {
        _host.SetBlockHeight(10);
        _host.SetBlockTimestamp(5000);

        _host.AdvanceBlocks(5);

        Context.BlockHeight.Should().Be(15);
        Context.BlockTimestamp.Should().Be(15000); // 5000 + 5*2000
    }

    [Fact]
    public void ExpectRevert_Returns_Null_On_Success()
    {
        var msg = _host.ExpectRevert(() => { /* no-op, no revert */ });
        msg.Should().BeNull();
    }

    [Fact]
    public void ExpectRevert_Returns_Message_On_Revert()
    {
        var msg = _host.ExpectRevert(() => Context.Require(false, "test error"));
        msg.Should().Be("test error");
    }

    [Fact]
    public void CreateAddress_From_Seed()
    {
        var addr = BasaltTestHost.CreateAddress(42);
        addr.Should().HaveCount(20);
        addr[19].Should().Be(42);
    }

    [Fact]
    public void CreateAddress_From_Hex()
    {
        var addr = BasaltTestHost.CreateAddress("0xABCD");
        addr.Should().HaveCount(20);
        addr[18].Should().Be(0xAB);
        addr[19].Should().Be(0xCD);
    }

    [Fact]
    public void ClearEvents_Removes_All_Events()
    {
        var token = new MintableBST20("Test", "T", 18);
        var alice = BasaltTestHost.CreateAddress(1);

        _host.SetCaller(BasaltTestHost.CreateAddress(2));
        _host.Call(() => token.MintPublic(alice, 100));
        _host.EmittedEvents.Should().NotBeEmpty();

        _host.ClearEvents();
        _host.EmittedEvents.Should().BeEmpty();
    }

    public void Dispose() => _host.Dispose();
}
