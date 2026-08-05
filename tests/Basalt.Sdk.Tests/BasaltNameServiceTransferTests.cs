using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Handing a name to someone else.
///
/// A registry without this is one where a name can never be sold, never moved off a key that leaked,
/// and never passed from whoever claimed it to the treasury that should hold it. The reserved-name path
/// makes that concrete: an owner proves control of a domain from whatever address was at hand.
/// </summary>
public class BasaltNameServiceTransferTests
{
    private const ulong RegistrationFee = 1_000_000_000;
    private const ulong StartMs = 1_700_000_000_000;

    private readonly BasaltTestHost _host = new();
    private readonly BasaltNameService _bns = new(RegistrationFee);
    private readonly byte[] _seller = BasaltTestHost.CreateAddress(11);
    private readonly byte[] _buyer = BasaltTestHost.CreateAddress(12);
    private readonly byte[] _tenant = BasaltTestHost.CreateAddress(13);

    public BasaltNameServiceTransferTests()
    {
        _host.SetBlockTimestamp(StartMs);
        _host.SetCaller(_seller);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register("acme"));
    }

    [Fact]
    public void The_owner_can_hand_the_name_over()
    {
        _host.SetCaller(_seller);
        _host.Call(() => _bns.TransferName("acme", _buyer));

        _host.Call(() => _bns.OwnerOf("acme")).Should().BeEquivalentTo(_buyer);
        _host.Call(() => _bns.Resolve("acme")).Should().BeEquivalentTo(_buyer);
    }

    [Fact]
    public void Nobody_else_can()
    {
        _host.SetCaller(_buyer);
        _host.Invoking(h => h.Call(() => _bns.TransferName("acme", _buyer)))
            .Should().Throw<Exception>().WithMessage("*not owner*");
    }

    // The same permanence as an attested claim: assignment is the whole record, so a name sent to an
    // address nobody holds is gone, and a mistyped or placeholder destination should not cost it.
    [Fact]
    public void It_cannot_be_sent_to_the_zero_address()
    {
        _host.SetCaller(_seller);
        _host.Invoking(h => h.Call(() => _bns.TransferName("acme", new byte[20])))
            .Should().Throw<Exception>().WithMessage("*zero address*");
    }

    /// <summary>
    /// The part a buyer cannot check for themselves.
    ///
    /// Subdomain delegations are keyed by name, so without invalidation they survive the sale and the
    /// seller keeps publishing under a name they no longer own. A buyer has no way to enumerate them,
    /// which makes this exactly the kind of thing that has to be handled by the transfer itself.
    /// </summary>
    [Fact]
    public void Delegated_subdomains_do_not_survive_the_sale()
    {
        _host.SetCaller(_seller);
        _host.Call(() => _bns.SetSubdomainOwner("blog.acme", _tenant));
        _host.Call(() => _bns.SubdomainOwner("blog.acme")).Should().BeEquivalentTo(_tenant);

        _host.Call(() => _bns.TransferName("acme", _buyer));

        _host.Call(() => _bns.SubdomainOwner("blog.acme")).Should().BeEmpty();
    }

    [Fact]
    public void The_buyer_can_delegate_the_same_subdomain_afresh()
    {
        _host.SetCaller(_seller);
        _host.Call(() => _bns.SetSubdomainOwner("blog.acme", _tenant));
        _host.Call(() => _bns.TransferName("acme", _buyer));

        _host.SetCaller(_buyer);
        _host.Call(() => _bns.SetSubdomainOwner("blog.acme", _seller));
        _host.Call(() => _bns.SubdomainOwner("blog.acme")).Should().BeEquivalentTo(_seller);
    }

    [Fact]
    public void A_subdomain_is_not_a_thing_you_transfer()
    {
        _host.SetCaller(_seller);
        _host.Invoking(h => h.Call(() => _bns.TransferName("blog.acme", _buyer)))
            .Should().Throw<Exception>().WithMessage("*subdomains follow it*");
    }
}
