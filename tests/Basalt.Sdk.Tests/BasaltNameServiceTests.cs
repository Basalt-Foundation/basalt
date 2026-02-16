using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class BasaltNameServiceTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BasaltNameService _bns;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    private const ulong RegistrationFee = 1_000_000_000;

    public BasaltNameServiceTests()
    {
        _bns = new BasaltNameService(RegistrationFee);
        _alice = BasaltTestHost.CreateAddress(1);
        _bob = BasaltTestHost.CreateAddress(2);
    }

    [Fact]
    public void Register_Name_Sets_Owner()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));

        _host.Call(() => _bns.OwnerOf("alice.bslt")).Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void Resolve_Returns_Caller_Address()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));

        _host.Call(() => _bns.Resolve("alice.bslt")).Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void Register_Duplicate_Name_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));

        _host.SetCaller(_bob);
        Context.TxValue = RegistrationFee;
        var msg = _host.ExpectRevert(() => _bns.Register("alice.bslt"));
        msg.Should().Contain("name taken");
    }

    [Fact]
    public void SetAddress_Changes_Resolution()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));

        _host.Call(() => _bns.SetAddress("alice.bslt", _bob));

        _host.Call(() => _bns.Resolve("alice.bslt")).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void SetAddress_By_NonOwner_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _bns.SetAddress("alice.bslt", _bob));
        msg.Should().Contain("not owner");
    }

    [Fact]
    public void TransferName_Changes_Owner()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));

        _host.Call(() => _bns.TransferName("alice.bslt", _bob));

        _host.Call(() => _bns.OwnerOf("alice.bslt")).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void TransferName_By_NonOwner_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _bns.TransferName("alice.bslt", _bob));
        msg.Should().Contain("not owner");
    }

    [Fact]
    public void TransferName_Emits_Event()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));
        _host.ClearEvents();

        _host.Call(() => _bns.TransferName("alice.bslt", _bob));

        var events = _host.GetEvents<NameTransferredEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Name.Should().Be("alice.bslt");
        events[0].NewOwner.Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void SetReverse_And_ReverseLookup_Roundtrip()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));

        _host.Call(() => _bns.SetReverse("alice.bslt"));

        _host.Call(() => _bns.ReverseLookup(_alice)).Should().Be("alice.bslt");
    }

    [Fact]
    public void ReverseLookup_Returns_Empty_For_Unset()
    {
        _host.Call(() => _bns.ReverseLookup(_alice)).Should().Be("");
    }

    [Fact]
    public void Register_With_Insufficient_Fee_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee - 1;

        var msg = _host.ExpectRevert(() => _bns.Register("alice.bslt"));
        msg.Should().Contain("insufficient fee");
    }

    [Fact]
    public void Register_With_Zero_Fee_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = 0;

        var msg = _host.ExpectRevert(() => _bns.Register("alice.bslt"));
        msg.Should().Contain("insufficient fee");
    }

    [Fact]
    public void Register_With_Empty_Name_Fails()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;

        var msg = _host.ExpectRevert(() => _bns.Register(""));
        msg.Should().Contain("name required");
    }

    [Fact]
    public void Register_Emits_NameRegisteredEvent()
    {
        _host.SetCaller(_alice);
        _host.ClearEvents();
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));

        var events = _host.GetEvents<NameRegisteredEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Name.Should().Be("alice.bslt");
        events[0].Owner.Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void OwnerOf_Returns_Zero_Address_For_Unregistered_Name()
    {
        _host.Call(() => _bns.OwnerOf("nonexistent.bslt")).Should().BeEquivalentTo(new byte[20]);
    }

    [Fact]
    public void New_Owner_Can_SetAddress_After_Transfer()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt"));
        _host.Call(() => _bns.TransferName("alice.bslt", _bob));

        _host.SetCaller(_bob);
        var thirdParty = BasaltTestHost.CreateAddress(3);
        _host.Call(() => _bns.SetAddress("alice.bslt", thirdParty));

        _host.Call(() => _bns.Resolve("alice.bslt")).Should().BeEquivalentTo(thirdParty);
    }

    public void Dispose() => _host.Dispose();
}
