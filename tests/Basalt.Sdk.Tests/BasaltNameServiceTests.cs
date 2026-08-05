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

    // --- BNS v2: content records, expiry, renewal, grace, reclaim ---

    // Default host block time is 1_000_000 ms = 1000 s; a fresh registration expires at 1000 + 31_536_000.
    private const long RegisterExpirySeconds = 1000 + 31_536_000; // 31_537_000
    private const ulong InGraceMs = (RegisterExpirySeconds + 100) * 1000UL;                 // just past expiry, in grace
    private const ulong PastGraceMs = (RegisterExpirySeconds + 2_592_000 + 100) * 1000UL;   // past the 30-day grace
    private const string ContentUri = "trilith://alicekeyauthority/blog";

    private void RegisterAlice(string name = "alice.bslt")
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register(name));
    }

    [Fact]
    public void Register_Sets_Expiry_One_Period_Out()
    {
        RegisterAlice();
        _host.Call(() => _bns.ExpiryOf("alice.bslt")).Should().Be(RegisterExpirySeconds);
    }

    [Fact]
    public void SetContentRecord_And_ResolveContent_Roundtrip()
    {
        RegisterAlice();
        _host.Call(() => _bns.SetContentRecord("alice.bslt", ContentUri));
        _host.Call(() => _bns.ResolveContent("alice.bslt")).Should().Be(ContentUri);
    }

    [Fact]
    public void SetContentRecord_Rejects_Non_Trilith_Uri()
    {
        RegisterAlice();
        var msg = _host.ExpectRevert(() => _bns.SetContentRecord("alice.bslt", "https://example.com"));
        msg.Should().Contain("trilith://");
    }

    [Fact]
    public void SetContentRecord_Rejects_Too_Long()
    {
        RegisterAlice();
        var tooLong = "trilith://" + new string('a', 512);
        var msg = _host.ExpectRevert(() => _bns.SetContentRecord("alice.bslt", tooLong));
        msg.Should().Contain("too long");
    }

    [Fact]
    public void SetContentRecord_By_NonOwner_Fails()
    {
        RegisterAlice();
        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _bns.SetContentRecord("alice.bslt", ContentUri));
        msg.Should().Contain("not owner");
    }

    [Fact]
    public void ClearContentRecord_Removes_It()
    {
        RegisterAlice();
        _host.Call(() => _bns.SetContentRecord("alice.bslt", ContentUri));
        _host.Call(() => _bns.ClearContentRecord("alice.bslt"));
        _host.Call(() => _bns.ResolveContent("alice.bslt")).Should().Be("");
    }

    [Fact]
    public void SetContentRecord_Emits_Event()
    {
        RegisterAlice();
        _host.ClearEvents();
        _host.Call(() => _bns.SetContentRecord("alice.bslt", ContentUri));

        var events = _host.GetEvents<ContentRecordSetEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Name.Should().Be("alice.bslt");
        events[0].ContentUri.Should().Be(ContentUri);
    }

    [Fact]
    public void Early_Renew_Extends_From_Current_Expiry_So_No_Time_Is_Lost()
    {
        RegisterAlice();
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Renew("alice.bslt")); // renew well before expiry

        // Extended from the current expiry (not "now"), so an early renewal loses no time.
        _host.Call(() => _bns.ExpiryOf("alice.bslt")).Should().Be(RegisterExpirySeconds + 31_536_000);
    }

    [Fact]
    public void Renew_In_Grace_Extends_From_Now()
    {
        RegisterAlice();
        _host.SetBlockTimestamp(InGraceMs); // already expired, within the grace window
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Renew("alice.bslt"));

        // The name was already expired, so the new term runs a full period from now.
        _host.Call(() => _bns.ExpiryOf("alice.bslt")).Should().Be((long)(InGraceMs / 1000) + 31_536_000);
    }

    [Fact]
    public void Renew_Is_Callable_By_Anyone()
    {
        RegisterAlice();
        _host.SetCaller(_bob); // not the owner
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Renew("alice.bslt")); // must not revert

        _host.Call(() => _bns.OwnerOf("alice.bslt")).Should().BeEquivalentTo(_alice); // ownership unchanged
    }

    [Fact]
    public void Name_In_Grace_Is_Still_Taken_But_Resolves()
    {
        RegisterAlice();
        _host.SetBlockTimestamp(InGraceMs);

        _host.Call(() => _bns.Resolve("alice.bslt")).Should().BeEquivalentTo(_alice);
        _host.SetCaller(_bob);
        Context.TxValue = RegistrationFee;
        var msg = _host.ExpectRevert(() => _bns.Register("alice.bslt"));
        msg.Should().Contain("name taken");
    }

    [Fact]
    public void Name_Past_Grace_Is_Available_And_Reregisters_Clearing_Content()
    {
        RegisterAlice();
        _host.Call(() => _bns.SetContentRecord("alice.bslt", ContentUri));
        _host.SetBlockTimestamp(PastGraceMs);

        _host.SetCaller(_bob);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("alice.bslt")); // bob takes the expired name

        _host.Call(() => _bns.OwnerOf("alice.bslt")).Should().BeEquivalentTo(_bob);
        _host.Call(() => _bns.ResolveContent("alice.bslt")).Should().Be(""); // alice's record was cleared
    }

    [Fact]
    public void Resolve_And_OwnerOf_Past_Grace_Report_Unregistered()
    {
        RegisterAlice();
        _host.SetBlockTimestamp(PastGraceMs);

        _host.Call(() => _bns.OwnerOf("alice.bslt")).Should().BeEquivalentTo(new byte[20]);
        _host.Call(() => _bns.ResolveContent("alice.bslt")).Should().Be("");
        var msg = _host.ExpectRevert(() => _bns.Resolve("alice.bslt"));
        msg.Should().Contain("expired");
    }

    [Fact]
    public void Reclaim_Clears_A_Past_Grace_Name()
    {
        RegisterAlice();
        _host.SetBlockTimestamp(PastGraceMs);

        _host.SetCaller(_bob); // anyone may garbage-collect
        _host.Call(() => _bns.Reclaim("alice.bslt"));

        _host.Call(() => _bns.OwnerOf("alice.bslt")).Should().BeEquivalentTo(new byte[20]);
        _host.Call(() => _bns.ExpiryOf("alice.bslt")).Should().Be(0);
    }

    [Fact]
    public void Reclaim_Rejects_A_Live_Name()
    {
        RegisterAlice();
        var msg = _host.ExpectRevert(() => _bns.Reclaim("alice.bslt"));
        msg.Should().Contain("not expired past grace");
    }

    public void Dispose() => _host.Dispose();
}
