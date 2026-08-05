using Basalt.Sdk.Contracts;
using Basalt.Core;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Launch reservations: well-known names are held by the registry rather than handed to whoever scripts
/// the fastest transaction, released free to whoever proves control, and let go when the window closes.
/// </summary>
public class BasaltNameServiceReservationTests
{
    private const ulong RegistrationFee = 1_000_000_000;
    private const long OneYear = 31_536_000;

    private readonly BasaltTestHost _host = new();
    private readonly BasaltNameService _bns;
    private readonly byte[] _governance;
    private readonly byte[] _google;
    private readonly byte[] _squatter;

    public BasaltNameServiceReservationTests()
    {
        _bns = new BasaltNameService(RegistrationFee);
        _governance = new byte[20];
        _governance[18] = 0x10;
        _governance[19] = 0x03;
        _google = BasaltTestHost.CreateAddress(7);
        _squatter = BasaltTestHost.CreateAddress(8);
    }

    // The host resets Context.BlockTimestamp on every call, so time is moved through the host rather
    // than by touching Context directly.
    private const ulong StartMs = 1_700_000_000_000;

    private void OpenWindow(long seconds = OneYear)
    {
        _host.SetBlockTimestamp(StartMs);
        _host.SetCaller(_governance);
        _host.Call(() => _bns.SetReservationDeadline((long)(StartMs / 1000) + seconds));
    }

    private void Reserve(string name)
    {
        _host.SetCaller(_governance);
        _host.Call(() => _bns.ReserveName(name));
    }

    [Fact]
    public void A_reserved_name_cannot_be_registered_by_anyone()
    {
        OpenWindow();
        Reserve("google");

        _host.SetCaller(_squatter);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.Register("google.bslt")))
            .Should().Throw<Exception>().WithMessage("*reserved*");
    }

    [Fact]
    public void Reserving_requires_a_deadline_first()
    {
        _host.SetCaller(_governance);
        _host.Invoking(h => h.Call(() => _bns.ReserveName("google")))
            .Should().Throw<Exception>().WithMessage("*deadline first*");
    }

    [Fact]
    public void Only_governance_reserves_or_assigns()
    {
        OpenWindow();

        _host.SetCaller(_squatter);
        _host.Invoking(h => h.Call(() => _bns.ReserveName("google")))
            .Should().Throw<Exception>().WithMessage("*governance only*");
        _host.Invoking(h => h.Call(() => _bns.ClaimReserved("google", _squatter, "dns:_bslt-claim.google.com")))
            .Should().Throw<Exception>().WithMessage("*governance only*");
    }

    [Fact]
    public void The_claimant_receives_the_name_and_the_proof_is_recorded()
    {
        OpenWindow();
        Reserve("google");

        _host.SetCaller(_governance);
        _host.Call(() => _bns.ClaimReserved("google.bslt", _google, "dns:_bslt-claim.google.com TXT"));

        _host.Call(() => _bns.OwnerOf("google.bslt")).Should().BeEquivalentTo(_google);
        _host.Call(() => _bns.IsReserved("google.bslt")).Should().BeFalse();

        var claimed = _host.GetEvents<ReservedNameClaimedEvent>().ToList();
        claimed.Should().ContainSingle();
        claimed[0].Name.Should().Be("google.bslt");
        // The evidence is the point: an assignment nobody can re-check is an assignment taken on trust.
        claimed[0].Evidence.Should().Contain("google.com");
    }

    [Fact]
    public void A_claim_without_evidence_is_refused()
    {
        OpenWindow();
        Reserve("google");

        _host.SetCaller(_governance);
        _host.Invoking(h => h.Call(() => _bns.ClaimReserved("google", _google, "")))
            .Should().Throw<Exception>().WithMessage("*evidence required*");
    }

    [Fact]
    public void A_name_that_was_never_reserved_cannot_be_assigned()
    {
        OpenWindow();

        _host.SetCaller(_governance);
        _host.Invoking(h => h.Call(() => _bns.ClaimReserved("notreserved", _google, "dns:whatever")))
            .Should().Throw<Exception>().WithMessage("*not reserved*");
    }

    // A registry that holds recognisable names forever is a registry with a permanently empty shelf.
    [Fact]
    public void An_unclaimed_reservation_lapses_when_the_window_closes()
    {
        OpenWindow(seconds: 100);
        Reserve("google");

        _host.Call(() => _bns.IsReserved("google")).Should().BeTrue();

        _host.SetBlockTimestamp(StartMs + 101_000); // past the deadline
        _host.Call(() => _bns.IsReserved("google")).Should().BeFalse();

        _host.SetCaller(_squatter);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("google.bslt"));
        _host.Call(() => _bns.OwnerOf("google.bslt")).Should().BeEquivalentTo(_squatter);
    }

    [Fact]
    public void Reservation_covers_every_spelling_of_the_same_name()
    {
        OpenWindow();
        Reserve("google.bslt");

        _host.Call(() => _bns.IsReserved("google")).Should().BeTrue();
        _host.Call(() => _bns.IsReserved("GOOGLE.BSLT")).Should().BeTrue();
        // And the subdomains under it, since the label is what is held.
        _host.Call(() => _bns.IsReserved("mail.google.bslt")).Should().BeTrue();
    }

    [Fact]
    public void An_already_registered_name_cannot_be_reserved_after_the_fact()
    {
        OpenWindow();

        _host.SetCaller(_squatter);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("taken"));

        _host.SetCaller(_governance);
        _host.Invoking(h => h.Call(() => _bns.ReserveName("taken")))
            .Should().Throw<Exception>().WithMessage("*already registered*");
    }
}
