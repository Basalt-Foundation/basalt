using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class TrilithAnchorTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly TrilithAnchor _anchor;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    public TrilithAnchorTests()
    {
        _anchor = new TrilithAnchor(); // free anchoring by default
        _alice = BasaltTestHost.CreateAddress(1);
        _bob = BasaltTestHost.CreateAddress(2);
    }

    private static byte[] Digest(byte seed)
    {
        var d = new byte[32];
        Array.Fill(d, seed);
        return d;
    }

    [Fact]
    public void Anchor_Stores_The_Digest_And_Metadata()
    {
        var d = Digest(0xAB);
        _host.SetCaller(_alice);
        _host.Call(() => _anchor.Anchor(d, TrilithAnchor.KindDestructionSet, "cert-1"));

        _host.Call(() => _anchor.IsAnchored(d)).Should().BeTrue();
        _host.Call(() => _anchor.GetAnchor(d)).Should().BeGreaterThan(0);
        _host.Call(() => _anchor.GetAnchorKind(d)).Should().Be(TrilithAnchor.KindDestructionSet);
        _host.Call(() => _anchor.GetAnchorSubmitter(d)).Should().BeEquivalentTo(_alice);
        _host.Call(() => _anchor.GetAnchorLabel(d)).Should().Be("cert-1");
        _host.Call(() => _anchor.GetAnchorBlock(d)).Should().Be(Context.BlockHeight);
        _host.Call(() => _anchor.AnchorCount()).Should().Be(1UL);
    }

    [Fact]
    public void Anchor_Returns_The_Timestamp_In_Seconds()
    {
        var d = Digest(0x01);
        _host.SetBlockTimestamp(1_700_000_000_000); // ms
        _host.SetCaller(_alice);

        var ts = _host.Call(() => _anchor.Anchor(d, TrilithAnchor.KindCidRoot, ""));

        ts.Should().Be(1_700_000_000UL); // seconds
        _host.Call(() => _anchor.GetAnchor(d)).Should().Be(1_700_000_000UL);
    }

    [Fact]
    public void ReAnchor_Is_A_NoOp_First_Writer_Wins()
    {
        var d = Digest(0x02);
        _host.SetBlockTimestamp(1_700_000_000_000);
        _host.SetCaller(_alice);
        var original = _host.Call(() => _anchor.Anchor(d, TrilithAnchor.KindDestructionSet, "alice-cert"));
        _host.ClearEvents();

        // A later, different caller/kind/label re-anchoring the same digest must not overwrite anything.
        _host.SetBlockTimestamp(1_800_000_000_000);
        _host.SetCaller(_bob);
        var second = _host.Call(() => _anchor.Anchor(d, TrilithAnchor.KindBlocklist, "bob-cert"));

        second.Should().Be(original); // returns the original timestamp
        _host.Call(() => _anchor.GetAnchorSubmitter(d)).Should().BeEquivalentTo(_alice); // not bob
        _host.Call(() => _anchor.GetAnchorKind(d)).Should().Be(TrilithAnchor.KindDestructionSet);
        _host.Call(() => _anchor.GetAnchorLabel(d)).Should().Be("alice-cert");
        _host.Call(() => _anchor.AnchorCount()).Should().Be(1UL); // no new anchor
        _host.GetEvents<AnchoredEvent>().Should().BeEmpty(); // no second event
    }

    [Fact]
    public void Anchor_Counts_Distinct_Digests()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _anchor.Anchor(Digest(0x10), TrilithAnchor.KindCidRoot, ""));
        _host.Call(() => _anchor.Anchor(Digest(0x11), TrilithAnchor.KindKeyLog, ""));
        _host.Call(() => _anchor.Anchor(Digest(0x10), TrilithAnchor.KindCidRoot, "")); // duplicate

        _host.Call(() => _anchor.AnchorCount()).Should().Be(2UL);
    }

    [Fact]
    public void Anchor_Rejects_Wrong_Length_Digest()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _anchor.Anchor(new byte[31], TrilithAnchor.KindCidRoot, ""));
        msg.Should().Contain("32 bytes");
    }

    [Fact]
    public void Anchor_Rejects_Unknown_Kind()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _anchor.Anchor(Digest(0x20), 4, "")); // max kind is 3
        msg.Should().Contain("unknown kind");
    }

    [Fact]
    public void Anchor_Rejects_Overlong_Label()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _anchor.Anchor(Digest(0x21), TrilithAnchor.KindCidRoot, new string('a', 65)));
        msg.Should().Contain("label too long");
    }

    [Fact]
    public void Anchor_Emits_Event()
    {
        var d = Digest(0x30);
        _host.SetCaller(_alice);
        _host.ClearEvents();
        _host.Call(() => _anchor.Anchor(d, TrilithAnchor.KindDestructionSet, "cert"));

        var events = _host.GetEvents<AnchoredEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Digest.Should().BeEquivalentTo(d);
        events[0].Kind.Should().Be(TrilithAnchor.KindDestructionSet);
        events[0].Submitter.Should().BeEquivalentTo(_alice);
        events[0].Label.Should().Be("cert");
    }

    [Fact]
    public void Fee_Instance_Rejects_Insufficient_Payment()
    {
        var priced = new TrilithAnchor(new UInt256(1000));
        _host.SetCaller(_alice);
        Context.TxValue = 999;

        var msg = _host.ExpectRevert(() => priced.Anchor(Digest(0x40), TrilithAnchor.KindCidRoot, ""));
        msg.Should().Contain("insufficient fee");
    }

    [Fact]
    public void Unanchored_Digest_Reports_Defaults()
    {
        var d = Digest(0x50);
        _host.Call(() => _anchor.IsAnchored(d)).Should().BeFalse();
        _host.Call(() => _anchor.GetAnchor(d)).Should().Be(0UL);
        _host.Call(() => _anchor.GetAnchorSubmitter(d)).Should().BeEquivalentTo(new byte[20]);
        _host.Call(() => _anchor.AnchorCount()).Should().Be(0UL);
    }

    public void Dispose() => _host.Dispose();
}
