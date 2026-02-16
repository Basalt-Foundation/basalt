using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class EscrowTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly Escrow _escrow;
    private readonly byte[] _depositor;
    private readonly byte[] _recipient;
    private readonly byte[] _stranger;

    public EscrowTests()
    {
        _escrow = new Escrow();
        _depositor = BasaltTestHost.CreateAddress(1);
        _recipient = BasaltTestHost.CreateAddress(2);
        _stranger = BasaltTestHost.CreateAddress(3);

        // Wire up native transfer handler (no-op for testing)
        Context.NativeTransferHandler = (to, amount) => { };
    }

    [Fact]
    public void Create_Escrow_Returns_Id_And_Locks()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;

        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        id.Should().Be(0);
        _host.Call(() => _escrow.GetStatus(id)).Should().Be("locked");
        _host.Call(() => _escrow.GetAmount(id)).Should().Be(5000);
    }

    [Fact]
    public void Create_Escrow_Increments_Id()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);

        Context.TxValue = 1000;
        var id0 = _host.Call(() => _escrow.Create(_recipient, 100));
        Context.TxValue = 2000;
        var id1 = _host.Call(() => _escrow.Create(_recipient, 200));

        id0.Should().Be(0);
        id1.Should().Be(1);
    }

    [Fact]
    public void Create_With_Zero_Value_Fails()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _escrow.Create(_recipient, 100));
        msg.Should().Contain("must send value");
    }

    [Fact]
    public void Create_With_ReleaseBlock_Not_In_Future_Fails()
    {
        _host.SetBlockHeight(100);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;

        var msg = _host.ExpectRevert(() => _escrow.Create(_recipient, 50));
        msg.Should().Contain("release must be in future");
    }

    [Fact]
    public void Create_Emits_EscrowCreatedEvent()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        _host.ClearEvents();
        Context.TxValue = 5000;

        _host.Call(() => _escrow.Create(_recipient, 100));

        var events = _host.GetEvents<EscrowCreatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].EscrowId.Should().Be(0);
        events[0].Depositor.Should().BeEquivalentTo(_depositor);
        events[0].Recipient.Should().BeEquivalentTo(_recipient);
        events[0].Amount.Should().Be(5000);
        events[0].ReleaseBlock.Should().Be(100);
    }

    [Fact]
    public void Release_By_Recipient_After_ReleaseBlock()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(100);
        _host.SetCaller(_recipient);
        Context.TxValue = 0;
        _host.Call(() => _escrow.Release(id));

        _host.Call(() => _escrow.GetStatus(id)).Should().Be("released");
    }

    [Fact]
    public void Release_By_Depositor_After_ReleaseBlock()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(100);
        _host.SetCaller(_depositor);
        Context.TxValue = 0;
        _host.Call(() => _escrow.Release(id));

        _host.Call(() => _escrow.GetStatus(id)).Should().Be("released");
    }

    [Fact]
    public void Release_Emits_EscrowReleasedEvent()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(100);
        _host.SetCaller(_recipient);
        _host.ClearEvents();
        Context.TxValue = 0;
        _host.Call(() => _escrow.Release(id));

        var events = _host.GetEvents<EscrowReleasedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].EscrowId.Should().Be(id);
        events[0].Amount.Should().Be(5000);
    }

    [Fact]
    public void Cannot_Release_Before_ReleaseBlock()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(50);
        _host.SetCaller(_recipient);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _escrow.Release(id));
        msg.Should().Contain("not yet releasable");
    }

    [Fact]
    public void Cannot_Release_By_Stranger()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(100);
        _host.SetCaller(_stranger);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _escrow.Release(id));
        msg.Should().Contain("not authorized");
    }

    [Fact]
    public void Refund_By_Depositor_Before_ReleaseBlock()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(50);
        _host.SetCaller(_depositor);
        Context.TxValue = 0;
        _host.Call(() => _escrow.Refund(id));

        _host.Call(() => _escrow.GetStatus(id)).Should().Be("refunded");
    }

    [Fact]
    public void Refund_Emits_EscrowRefundedEvent()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(50);
        _host.SetCaller(_depositor);
        _host.ClearEvents();
        Context.TxValue = 0;
        _host.Call(() => _escrow.Refund(id));

        var events = _host.GetEvents<EscrowRefundedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].EscrowId.Should().Be(id);
        events[0].Amount.Should().Be(5000);
    }

    [Fact]
    public void Cannot_Refund_After_ReleaseBlock()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(100);
        _host.SetCaller(_depositor);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _escrow.Refund(id));
        msg.Should().Contain("already releasable");
    }

    [Fact]
    public void Cannot_Refund_By_NonDepositor()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(50);
        _host.SetCaller(_recipient);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _escrow.Refund(id));
        msg.Should().Contain("only depositor");
    }

    [Fact]
    public void Cannot_Refund_By_Stranger()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(50);
        _host.SetCaller(_stranger);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _escrow.Refund(id));
        msg.Should().Contain("only depositor");
    }

    [Fact]
    public void Cannot_Release_Already_Released()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(100);
        _host.SetCaller(_recipient);
        Context.TxValue = 0;
        _host.Call(() => _escrow.Release(id));

        var msg = _host.ExpectRevert(() => _escrow.Release(id));
        msg.Should().Contain("not locked");
    }

    [Fact]
    public void Cannot_Refund_Already_Refunded()
    {
        _host.SetBlockHeight(10);
        _host.SetCaller(_depositor);
        Context.TxValue = 5000;
        var id = _host.Call(() => _escrow.Create(_recipient, 100));

        _host.SetBlockHeight(50);
        _host.SetCaller(_depositor);
        Context.TxValue = 0;
        _host.Call(() => _escrow.Refund(id));

        var msg = _host.ExpectRevert(() => _escrow.Refund(id));
        msg.Should().Contain("not locked");
    }

    [Fact]
    public void GetStatus_Returns_Unknown_For_Nonexistent()
    {
        _host.Call(() => _escrow.GetStatus(999)).Should().Be("unknown");
    }

    public void Dispose() => _host.Dispose();
}
