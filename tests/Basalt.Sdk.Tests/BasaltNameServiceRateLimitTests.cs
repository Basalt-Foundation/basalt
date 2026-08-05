using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// The per-account registration cap. It does not stop bulk registration, since an attacker can use more
/// accounts. It changes the cost of doing so, which on a network where funds are rate-limited at the
/// source is the difference between minutes and days.
/// </summary>
public class BasaltNameServiceRateLimitTests
{
    private const ulong RegistrationFee = 1_000_000_000;
    private const ulong StartMs = 1_700_000_000_000;

    private readonly BasaltTestHost _host = new();
    private readonly BasaltNameService _bns;
    private readonly byte[] _governance;
    private readonly byte[] _squatter;
    private readonly byte[] _other;

    public BasaltNameServiceRateLimitTests()
    {
        _bns = new BasaltNameService(RegistrationFee);
        _governance = new byte[20];
        _governance[18] = 0x10;
        _governance[19] = 0x03;
        _squatter = BasaltTestHost.CreateAddress(41);
        _other = BasaltTestHost.CreateAddress(42);
        _host.SetBlockTimestamp(StartMs);
    }

    private void Limit(int max, long windowSeconds)
    {
        _host.SetCaller(_governance);
        _host.Call(() => _bns.SetRegistrationLimit(max, windowSeconds));
    }

    private void Register(byte[] caller, string name)
    {
        _host.SetCaller(caller);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register(name));
    }

    // The default has to hold without anyone setting it, because the genesis deploy path never runs the
    // constructor's init block, so a cap that only exists when configured would not exist at launch.
    [Fact]
    public void A_hundred_a_year_applies_without_anyone_configuring_it()
    {
        _host.Call(() => _bns.RegistrationsRemaining(_squatter)).Should().Be(100);

        Register(_squatter, "one");
        _host.Call(() => _bns.RegistrationsRemaining(_squatter)).Should().Be(99);
    }

    [Fact]
    public void Governance_can_remove_the_cap_explicitly()
    {
        Limit(-1, 0);

        for (int i = 0; i < 5; i++)
            Register(_squatter, $"name{i}");

        _host.Call(() => _bns.RegistrationsRemaining(_squatter)).Should().Be(-1);
    }

    [Fact]
    public void Zero_is_refused_because_it_is_indistinguishable_from_unset()
    {
        _host.SetCaller(_governance);
        _host.Invoking(h => h.Call(() => _bns.SetRegistrationLimit(0, 3600)))
            .Should().Throw<Exception>().WithMessage("*must be positive*");
    }

    [Fact]
    public void The_cap_stops_the_run()
    {
        Limit(2, 3600);

        Register(_squatter, "one");
        Register(_squatter, "two");

        _host.SetCaller(_squatter);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.Register("three")))
            .Should().Throw<Exception>().WithMessage("*limit reached*");
    }

    [Fact]
    public void The_cap_is_per_account()
    {
        Limit(1, 3600);

        Register(_squatter, "one");
        // Another account is unaffected, which is exactly the limitation worth being honest about.
        Register(_other, "two");

        _host.Call(() => _bns.OwnerOf("two")).Should().BeEquivalentTo(_other);
    }

    [Fact]
    public void The_window_rolls_over()
    {
        Limit(1, 3600);
        Register(_squatter, "one");

        _host.SetBlockTimestamp(StartMs + 3_600_000);
        Register(_squatter, "two");

        _host.Call(() => _bns.OwnerOf("two")).Should().BeEquivalentTo(_squatter);
    }

    [Fact]
    public void Remaining_reports_what_is_left()
    {
        Limit(3, 3600);
        _host.Call(() => _bns.RegistrationsRemaining(_squatter)).Should().Be(3);

        Register(_squatter, "one");
        _host.Call(() => _bns.RegistrationsRemaining(_squatter)).Should().Be(2);

        Register(_squatter, "two");
        Register(_squatter, "three");
        _host.Call(() => _bns.RegistrationsRemaining(_squatter)).Should().Be(0);
    }

    // A claimant receiving the name they proved they control is not registering names in bulk, so the
    // cap must not be able to block a legitimate claim.
    [Fact]
    public void Claiming_a_reserved_name_does_not_consume_the_cap()
    {
        Limit(1, 3600);
        Register(_squatter, "used-my-one");

        _host.SetCaller(_governance);
        _host.Call(() => _bns.SetReservationDeadline((long)(StartMs / 1000) + 31_536_000));
        _host.Call(() => _bns.ReserveName("google"));
        _host.Call(() => _bns.ClaimReserved("google", _squatter, "dns:_bslt-claim.google.com"));

        _host.Call(() => _bns.OwnerOf("google.bslt")).Should().BeEquivalentTo(_squatter);
    }

    [Fact]
    public void Only_governance_sets_the_cap()
    {
        _host.SetCaller(_squatter);
        _host.Invoking(h => h.Call(() => _bns.SetRegistrationLimit(1, 3600)))
            .Should().Throw<Exception>().WithMessage("*governance only*");
    }

    [Fact]
    public void A_cap_with_no_window_is_refused()
    {
        _host.SetCaller(_governance);
        _host.Invoking(h => h.Call(() => _bns.SetRegistrationLimit(5, 0)))
            .Should().Throw<Exception>().WithMessage("*window must be positive*");
    }

    [Fact]
    public void The_default_window_is_the_registration_term()
    {
        // A hundred a year, so the cap and the term a name is held for line up.
        for (int i = 0; i < 100; i++)
            Register(_squatter, $"n{i}");

        _host.SetCaller(_squatter);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.Register("one-too-many")))
            .Should().Throw<Exception>().WithMessage("*limit reached*");

        _host.SetBlockTimestamp(StartMs + 31_536_000_000);
        Register(_squatter, "next-year");
        _host.Call(() => _bns.OwnerOf("next-year")).Should().BeEquivalentTo(_squatter);
    }
}
