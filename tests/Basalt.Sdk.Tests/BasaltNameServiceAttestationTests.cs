using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Claims settled by independent attesters rather than by a person.
///
/// An attester reports one mechanical fact: whether a DNS TXT record names the claiming address. Several
/// of them check the same public record the same way, so the name is handed over when they agree, and
/// nobody is asked for an opinion.
/// </summary>
public class BasaltNameServiceAttestationTests
{
    private const ulong RegistrationFee = 1_000_000_000;
    private const ulong StartMs = 1_700_000_000_000;
    private const string Evidence = "dns:_bslt-claim.google.com TXT bslt-claim=0x07";

    private readonly BasaltTestHost _host = new();
    private readonly BasaltNameService _bns;
    private readonly byte[] _governance;
    private readonly byte[] _google;
    private readonly byte[] _impostor;
    private readonly byte[][] _attesters;

    public BasaltNameServiceAttestationTests()
    {
        _bns = new BasaltNameService(RegistrationFee);
        _governance = new byte[20];
        _governance[18] = 0x10;
        _governance[19] = 0x03;
        _google = BasaltTestHost.CreateAddress(7);
        _impostor = BasaltTestHost.CreateAddress(66);
        _attesters = [BasaltTestHost.CreateAddress(21), BasaltTestHost.CreateAddress(22), BasaltTestHost.CreateAddress(23)];
        _host.SetBlockTimestamp(StartMs);
    }

    private void Setup(int threshold = 2, int attesterCount = 3)
    {
        _host.SetCaller(_governance);
        _host.Call(() => _bns.SetReservationDeadline((long)(StartMs / 1000) + 31_536_000));
        _host.Call(() => _bns.ReserveName("google"));
        for (int i = 0; i < attesterCount; i++)
        {
            var a = _attesters[i];
            _host.Call(() => _bns.SetAttester(a, true));
        }

        _host.Call(() => _bns.SetAttestationThreshold(threshold));
    }

    private void Attest(byte[] attester, byte[] owner)
    {
        _host.SetCaller(attester);
        _host.Call(() => _bns.AttestClaim("google.bslt", owner, Evidence));
    }

    [Fact]
    public void Agreement_hands_the_name_over_with_no_one_deciding()
    {
        Setup(threshold: 2);

        Attest(_attesters[0], _google);
        _host.Call(() => _bns.OwnerOf("google.bslt")).Should().BeEquivalentTo(new byte[20]);

        Attest(_attesters[1], _google);
        _host.Call(() => _bns.OwnerOf("google.bslt")).Should().BeEquivalentTo(_google);
        _host.Call(() => _bns.IsReserved("google.bslt")).Should().BeFalse();
    }

    // A single compromised attester is the failure this design has to survive.
    [Fact]
    public void One_attester_cannot_reach_the_threshold_alone()
    {
        Setup(threshold: 2);

        Attest(_attesters[0], _impostor);
        _host.SetCaller(_attesters[0]);
        _host.Invoking(h => h.Call(() => _bns.AttestClaim("google.bslt", _impostor, Evidence)))
            .Should().Throw<Exception>().WithMessage("*already attested*");

        _host.Call(() => _bns.OwnerOf("google.bslt")).Should().BeEquivalentTo(new byte[20]);
    }

    [Fact]
    public void Attestations_for_different_claimants_do_not_add_up()
    {
        Setup(threshold: 2);

        Attest(_attesters[0], _google);
        Attest(_attesters[1], _impostor);

        // Two attestations exist, but they disagree about who should get the name, so neither wins.
        _host.Call(() => _bns.OwnerOf("google.bslt")).Should().BeEquivalentTo(new byte[20]);
        _host.Call(() => _bns.AttestationCount("google", _google)).Should().Be(1);
        _host.Call(() => _bns.AttestationCount("google", _impostor)).Should().Be(1);
    }

    [Fact]
    public void Only_authorised_attesters_are_heard()
    {
        Setup(threshold: 2);

        _host.SetCaller(_impostor);
        _host.Invoking(h => h.Call(() => _bns.AttestClaim("google.bslt", _impostor, Evidence)))
            .Should().Throw<Exception>().WithMessage("*not an attester*");
    }

    [Fact]
    public void Governance_can_set_the_bar_but_cannot_attest_by_setting_it_to_zero()
    {
        Setup(threshold: 2);

        _host.SetCaller(_governance);
        _host.Invoking(h => h.Call(() => _bns.SetAttestationThreshold(0)))
            .Should().Throw<Exception>().WithMessage("*must be positive*");
    }

    [Fact]
    public void A_threshold_no_one_could_ever_meet_is_refused()
    {
        _host.SetCaller(_governance);
        var a = _attesters[0];
        _host.Call(() => _bns.SetAttester(a, true));

        _host.Invoking(h => h.Call(() => _bns.SetAttestationThreshold(5)))
            .Should().Throw<Exception>().WithMessage("*exceeds attester count*");
    }

    [Fact]
    public void Withdrawing_an_attester_lowers_the_count()
    {
        Setup(threshold: 3, attesterCount: 3);

        _host.SetCaller(_governance);
        var a = _attesters[2];
        _host.Call(() => _bns.SetAttester(a, false));

        // The threshold of 3 now exceeds the two remaining attesters, so it can no longer be re-set to 3.
        _host.Invoking(h => h.Call(() => _bns.SetAttestationThreshold(3)))
            .Should().Throw<Exception>().WithMessage("*exceeds attester count*");
    }

    [Fact]
    public void Attesting_requires_the_name_to_be_reserved()
    {
        Setup(threshold: 2);

        _host.SetCaller(_attesters[0]);
        _host.Invoking(h => h.Call(() => _bns.AttestClaim("notreserved", _google, Evidence)))
            .Should().Throw<Exception>().WithMessage("*not reserved*");
    }

    [Fact]
    public void Every_attestation_records_the_record_it_saw()
    {
        Setup(threshold: 2);
        Attest(_attesters[0], _google);

        var attested = _host.GetEvents<ClaimAttestedEvent>().ToList();
        attested.Should().ContainSingle();
        attested[0].Evidence.Should().Contain("_bslt-claim.google.com");
        attested[0].Tally.Should().Be(1);
    }

    // A name assigned to an address nobody holds is gone for good: the assignment deletes the
    // reservation, and there is no key to move it with. A placeholder left in a DNS template must not be
    // able to burn google.bslt forever, so the chain refuses it rather than trusting attesters to.
    [Fact]
    public void The_zero_address_is_refused_because_the_loss_would_be_permanent()
    {
        Setup(threshold: 2);

        _host.SetCaller(_attesters[0]);
        _host.Invoking(h => h.Call(() => _bns.AttestClaim("google.bslt", new byte[20], Evidence)))
            .Should().Throw<Exception>().WithMessage("*zero address*");
    }

    [Fact]
    public void Governance_cannot_hand_a_reserved_name_to_the_zero_address_either()
    {
        Setup(threshold: 2);

        _host.SetCaller(_governance);
        _host.Invoking(h => h.Call(() => _bns.ClaimReserved("google", new byte[20], Evidence)))
            .Should().Throw<Exception>().WithMessage("*zero address*");
    }
}
