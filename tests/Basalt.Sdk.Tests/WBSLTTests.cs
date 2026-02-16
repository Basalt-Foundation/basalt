using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class WBSLTTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly WBSLT _wbslt;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    public WBSLTTests()
    {
        _wbslt = new WBSLT();
        _alice = BasaltTestHost.CreateAddress(1);
        _bob = BasaltTestHost.CreateAddress(2);

        // Wire up native transfer handler for Withdraw support
        Context.NativeTransferHandler = (to, amount) => { };
    }

    [Fact]
    public void Name_Returns_Wrapped_BSLT()
    {
        _host.Call(() => _wbslt.Name()).Should().Be("Wrapped BSLT");
    }

    [Fact]
    public void Symbol_Returns_WBSLT()
    {
        _host.Call(() => _wbslt.Symbol()).Should().Be("WBSLT");
    }

    [Fact]
    public void Decimals_Returns_18()
    {
        _host.Call(() => _wbslt.Decimals()).Should().Be(18);
    }

    [Fact]
    public void Deposit_Mints_Tokens_Equal_To_TxValue()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _wbslt.Deposit());

        _host.Call(() => _wbslt.BalanceOf(_alice)).Should().Be(5000);
    }

    [Fact]
    public void Deposit_Increases_TotalSupply()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 3000;
        _host.Call(() => _wbslt.Deposit());

        _host.Call(() => _wbslt.TotalSupply()).Should().Be(3000);
    }

    [Fact]
    public void Deposit_Multiple_Times_Accumulates_Balance()
    {
        _host.SetCaller(_alice);

        Context.TxValue = 1000;
        _host.Call(() => _wbslt.Deposit());

        Context.TxValue = 2000;
        _host.Call(() => _wbslt.Deposit());

        _host.Call(() => _wbslt.BalanceOf(_alice)).Should().Be(3000);
        _host.Call(() => _wbslt.TotalSupply()).Should().Be(3000);
    }

    [Fact]
    public void Deposit_With_Zero_Value_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _wbslt.Deposit());
        msg.Should().Contain("must send value");
    }

    [Fact]
    public void Withdraw_Decreases_Balance_And_Supply()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _wbslt.Deposit());

        Context.TxValue = 0;
        _host.Call(() => _wbslt.Withdraw(2000));

        _host.Call(() => _wbslt.BalanceOf(_alice)).Should().Be(3000);
        _host.Call(() => _wbslt.TotalSupply()).Should().Be(3000);
    }

    [Fact]
    public void Withdraw_Full_Balance()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 5000;
        _host.Call(() => _wbslt.Deposit());

        Context.TxValue = 0;
        _host.Call(() => _wbslt.Withdraw(5000));

        _host.Call(() => _wbslt.BalanceOf(_alice)).Should().Be(0);
        _host.Call(() => _wbslt.TotalSupply()).Should().Be(0);
    }

    [Fact]
    public void Withdraw_Fails_With_Insufficient_Balance()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 1000;
        _host.Call(() => _wbslt.Deposit());

        Context.TxValue = 0;
        var msg = _host.ExpectRevert(() => _wbslt.Withdraw(2000));
        msg.Should().Contain("burn exceeds balance");
    }

    [Fact]
    public void Withdraw_Zero_Amount_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 1000;
        _host.Call(() => _wbslt.Deposit());

        Context.TxValue = 0;
        var msg = _host.ExpectRevert(() => _wbslt.Withdraw(0));
        msg.Should().Contain("amount must be > 0");
    }

    [Fact]
    public void Deposit_Emits_Transfer_Event()
    {
        _host.SetCaller(_alice);
        _host.ClearEvents();
        Context.TxValue = 1000;
        _host.Call(() => _wbslt.Deposit());

        var events = _host.GetEvents<TransferEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Amount.Should().Be(1000);
    }

    public void Dispose() => _host.Dispose();
}
