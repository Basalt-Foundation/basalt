using Basalt.Sdk.Contracts;
using Basalt.Core;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// The .bslt naming rules: one canonical spelling, labels that survive being written in a URL, and
/// subdomains that belong to whoever bought the label above them.
/// </summary>
public class BasaltNameServiceNamingTests
{
    private const ulong RegistrationFee = 1_000_000_000;

    private readonly BasaltTestHost _host = new();
    private readonly BasaltNameService _bns;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    public BasaltNameServiceNamingTests()
    {
        _bns = new BasaltNameService(RegistrationFee);
        _alice = BasaltTestHost.CreateAddress(1);
        _bob = BasaltTestHost.CreateAddress(2);
    }

    private void RegisterAs(byte[] caller, string name)
    {
        _host.SetCaller(caller);
        Context.TxValue = RegistrationFee;
        _host.Call(() => _bns.Register(name));
    }

    // A registry where alice and alice.bslt are two names guarantees that sooner or later one person
    // owns one and someone else owns the other.
    [Theory]
    [InlineData("alice", "alice.bslt")]
    [InlineData("alice.bslt", "alice")]
    [InlineData("ALICE", "alice.bslt")]
    [InlineData("Alice.BSLT", "alice")]
    public void The_same_name_written_differently_is_the_same_registration(string registered, string lookedUp)
    {
        RegisterAs(_alice, registered);
        _host.Call(() => _bns.OwnerOf(lookedUp)).Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void A_second_registration_of_the_same_name_in_another_spelling_is_refused()
    {
        RegisterAs(_alice, "alice.bslt");

        _host.SetCaller(_bob);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.Register("ALICE")))
            .Should().Throw<Exception>().WithMessage("*taken*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("-alice")]
    [InlineData("alice-")]
    [InlineData("ali ce")]
    [InlineData("alice_bob")]
    [InlineData("alice..bob")]
    [InlineData(".alice")]
    [InlineData("alice.")]
    [InlineData("aliceé")]
    public void A_name_that_cannot_be_written_plainly_is_refused(string name)
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;
        _host.Invoking(h => h.Call(() => _bns.Register(name)))
            .Should().Throw<Exception>();
    }

    [Fact]
    public void Only_the_label_is_registrable_and_subdomains_come_with_it()
    {
        _host.SetCaller(_alice);
        Context.TxValue = RegistrationFee;

        _host.Invoking(h => h.Call(() => _bns.Register("blog.alice.bslt")))
            .Should().Throw<Exception>().WithMessage("*subdomains come with it*");
    }

    [Fact]
    public void The_label_owner_controls_records_under_it()
    {
        RegisterAs(_alice, "alice.bslt");

        _host.SetCaller(_alice);
        _host.Call(() => _bns.SetContentRecord("blog.alice.bslt", "trilith://" + new string('a', 64)));

        _host.Call(() => _bns.ResolveContent("blog.alice.bslt"))
            .Should().StartWith("trilith://");
    }

    [Fact]
    public void Someone_else_cannot_set_records_under_a_label_they_do_not_own()
    {
        RegisterAs(_alice, "alice.bslt");

        _host.SetCaller(_bob);
        _host.Invoking(h => h.Call(() => _bns.SetContentRecord("blog.alice.bslt", "trilith://" + new string('a', 64))))
            .Should().Throw<Exception>().WithMessage("*not owner*");
    }

    [Fact]
    public void A_delegated_subdomain_can_publish_and_only_under_itself()
    {
        RegisterAs(_alice, "alice.bslt");

        _host.SetCaller(_alice);
        _host.Call(() => _bns.SetSubdomainOwner("blog.alice.bslt", _bob));

        // Bob publishes under the subdomain he was given.
        _host.SetCaller(_bob);
        _host.Call(() => _bns.SetContentRecord("blog.alice.bslt", "trilith://" + new string('b', 64)));
        _host.Call(() => _bns.ResolveContent("blog.alice.bslt")).Should().StartWith("trilith://");

        // And nowhere else. A delegation is one subdomain, not a foothold in the tree.
        _host.Invoking(h => h.Call(() => _bns.SetContentRecord("shop.alice.bslt", "trilith://" + new string('c', 64))))
            .Should().Throw<Exception>().WithMessage("*not owner*");
        _host.Invoking(h => h.Call(() => _bns.SetContentRecord("alice.bslt", "trilith://" + new string('c', 64))))
            .Should().Throw<Exception>().WithMessage("*not owner*");
    }

    [Fact]
    public void A_delegation_can_be_taken_back()
    {
        RegisterAs(_alice, "alice.bslt");
        _host.SetCaller(_alice);
        _host.Call(() => _bns.SetSubdomainOwner("blog.alice.bslt", _bob));
        _host.Call(() => _bns.ClearSubdomainOwner("blog.alice.bslt"));

        _host.SetCaller(_bob);
        _host.Invoking(h => h.Call(() => _bns.SetContentRecord("blog.alice.bslt", "trilith://" + new string('b', 64))))
            .Should().Throw<Exception>().WithMessage("*not owner*");
    }

    [Fact]
    public void A_delegate_cannot_re_delegate()
    {
        RegisterAs(_alice, "alice.bslt");
        _host.SetCaller(_alice);
        _host.Call(() => _bns.SetSubdomainOwner("blog.alice.bslt", _bob));

        _host.SetCaller(_bob);
        _host.Invoking(h => h.Call(() => _bns.SetSubdomainOwner("blog.alice.bslt", _alice)))
            .Should().Throw<Exception>().WithMessage("*not owner*");
    }

    [Fact]
    public void Transferring_a_label_carries_its_subdomains()
    {
        RegisterAs(_alice, "alice.bslt");
        _host.SetCaller(_alice);
        _host.Call(() => _bns.SetContentRecord("blog.alice.bslt", "trilith://" + new string('a', 64)));
        _host.Call(() => _bns.TransferName("alice.bslt", _bob));

        // Bob now controls the tree, and Alice does not.
        _host.SetCaller(_bob);
        _host.Call(() => _bns.SetContentRecord("blog.alice.bslt", "trilith://" + new string('b', 64)));

        _host.SetCaller(_alice);
        _host.Invoking(h => h.Call(() => _bns.SetContentRecord("blog.alice.bslt", "trilith://" + new string('c', 64))))
            .Should().Throw<Exception>().WithMessage("*not owner*");
    }

    [Fact]
    public void A_subdomain_cannot_be_transferred_on_its_own()
    {
        RegisterAs(_alice, "alice.bslt");
        _host.SetCaller(_alice);

        _host.Invoking(h => h.Call(() => _bns.TransferName("blog.alice.bslt", _bob)))
            .Should().Throw<Exception>().WithMessage("*subdomains follow it*");
    }

    [Fact]
    public void What_a_user_sees_carries_the_suffix()
    {
        RegisterAs(_alice, "alice");
        _host.SetCaller(_alice);
        _host.Call(() => _bns.SetReverse("alice"));

        _host.Call(() => _bns.ReverseLookup(_alice)).Should().Be("alice.bslt");
        _host.Call(() => _bns.LabelOf("blog.alice.bslt")).Should().Be("alice");
    }
}
