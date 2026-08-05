using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Commit-reveal registration, which exists because one-step registration is a race anyone can win by
/// watching the mempool and paying more gas.
/// </summary>
public class BasaltNameServiceCommitRevealTests
{
    private const ulong RegistrationFee = 1_000_000_000;
    private const ulong StartMs = 1_700_000_000_000;
    private const ulong MinAgeMs = 60_000;

    private readonly BasaltTestHost _host = new();
    private readonly BasaltNameService _bns;
    private readonly byte[] _alice;
    private readonly byte[] _frontRunner;
    private readonly byte[] _secret = new byte[32];

    public BasaltNameServiceCommitRevealTests()
    {
        _bns = new BasaltNameService(RegistrationFee);
        _alice = BasaltTestHost.CreateAddress(1);
        _frontRunner = BasaltTestHost.CreateAddress(9);
        for (int i = 0; i < 32; i++) _secret[i] = (byte)(i + 1);
        _host.SetBlockTimestamp(StartMs);
    }

    private void CommitAs(byte[] caller, string name, byte[] owner)
    {
        _host.SetCaller(caller);
        var commitment = _host.Call(() => _bns.MakeCommitment(name, owner, _secret));
        _host.Call(() => _bns.Commit(commitment));
    }

    private void Reveal(byte[] caller, string name)
    {
        _host.SetCaller(caller);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.RegisterCommitted(name, _secret));
    }

    [Fact]
    public void A_committed_name_can_be_registered_after_the_wait()
    {
        CommitAs(_alice, "alice.bslt", _alice);
        _host.SetBlockTimestamp(StartMs + MinAgeMs);
        Reveal(_alice, "alice.bslt");

        _host.Call(() => _bns.OwnerOf("alice.bslt")).Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void Revealing_immediately_is_refused()
    {
        CommitAs(_alice, "alice.bslt", _alice);

        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.RegisterCommitted("alice.bslt", _secret)))
            .Should().Throw<Exception>().WithMessage("*too new*");
    }

    // The reason the scheme exists. Watching the reveal is useless because the commitment binds the
    // owner, so replaying the same name and secret from another account matches no commitment.
    [Fact]
    public void Someone_watching_the_reveal_cannot_replay_it()
    {
        CommitAs(_alice, "alice.bslt", _alice);
        _host.SetBlockTimestamp(StartMs + MinAgeMs);

        _host.SetCaller(_frontRunner);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.RegisterCommitted("alice.bslt", _secret)))
            .Should().Throw<Exception>().WithMessage("*no matching commitment*");
    }

    [Fact]
    public void A_commitment_is_good_for_exactly_one_registration()
    {
        CommitAs(_alice, "alice.bslt", _alice);
        _host.SetBlockTimestamp(StartMs + MinAgeMs);
        Reveal(_alice, "alice.bslt");

        // Same secret, another name: the commitment was consumed and does not cover it.
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.RegisterCommitted("bob.bslt", _secret)))
            .Should().Throw<Exception>().WithMessage("*no matching commitment*");
    }

    [Fact]
    public void A_stale_commitment_is_refused()
    {
        CommitAs(_alice, "alice.bslt", _alice);
        _host.SetBlockTimestamp(StartMs + 86_401_000); // past the maximum age

        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.RegisterCommitted("alice.bslt", _secret)))
            .Should().Throw<Exception>().WithMessage("*expired*");
    }

    [Fact]
    public void A_pending_commitment_cannot_have_its_clock_reset()
    {
        CommitAs(_alice, "alice.bslt", _alice);
        _host.SetBlockTimestamp(StartMs + 30_000);

        _host.SetCaller(_alice);
        var commitment = _host.Call(() => _bns.MakeCommitment("alice.bslt", _alice, _secret));
        _host.Invoking(h => h.Call(() => _bns.Commit(commitment)))
            .Should().Throw<Exception>().WithMessage("*already pending*");
    }

    [Fact]
    public void The_commitment_reveals_nothing_about_which_name_is_being_registered()
    {
        _host.SetCaller(_alice);
        var forAlice = _host.Call(() => _bns.MakeCommitment("alice.bslt", _alice, _secret));
        var forBob = _host.Call(() => _bns.MakeCommitment("bob.bslt", _alice, _secret));

        forAlice.Should().NotBeEquivalentTo(forBob);
        forAlice.Should().HaveCount(32);
    }

    [Fact]
    public void A_reserved_name_cannot_be_taken_through_the_committed_path_either()
    {
        var governance = new byte[20];
        governance[18] = 0x10;
        governance[19] = 0x03;

        _host.SetCaller(governance);
        _host.Call(() => _bns.SetReservationDeadline((long)(StartMs / 1000) + 31_536_000));
        _host.Call(() => _bns.ReserveName("google"));

        CommitAs(_alice, "google.bslt", _alice);
        _host.SetBlockTimestamp(StartMs + MinAgeMs);

        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.RegisterCommitted("google.bslt", _secret)))
            .Should().Throw<Exception>().WithMessage("*reserved*");
    }
}
