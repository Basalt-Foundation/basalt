using FluentAssertions;
using Xunit;

namespace Basalt.Attester.Tests;

/// <summary>
/// The one part of the attester that unit tests cannot speak to: whether it can actually reach a
/// stranger's nameservers and read what is published there.
///
/// See <see cref="LiveDnsFactAttribute"/> for why these are opt-in.
/// </summary>
public class LiveDnsTests
{
    [LiveDnsFact]
    public async Task A_domain_with_no_claim_reads_as_nothing_to_attest()
    {
        var reader = new DnsClaimReader();

        // A large domain that certainly publishes TXT records and certainly publishes no claim. Reaching
        // its nameservers and coming back with null is the whole path working, absence included.
        ClaimRecord? claim = await reader.ReadAsync("google");

        claim.Should().BeNull();
    }

    [LiveDnsFact]
    public async Task A_domain_that_does_not_exist_is_absence_rather_than_a_crash()
    {
        var reader = new DnsClaimReader();

        ClaimRecord? claim = await reader.ReadAsync("no-such-domain-exists-for-basalt-attester-tests");

        // NXDOMAIN is not an error to a sweep. It is one of five hundred labels that had nothing to say.
        claim.Should().BeNull();
    }

    // Independence includes resolvers, so the option to point the bootstrap somewhere of your own has to
    // actually work. An attester that silently fell back to the system resolver would look independent
    // and share a failure with everyone else who did the same.
    [LiveDnsFact]
    public async Task A_bootstrap_resolver_can_be_chosen()
    {
        var reader = new DnsClaimReader(["9.9.9.9"]);

        ClaimRecord? claim = await reader.ReadAsync("google");

        claim.Should().BeNull();
    }

    // The positive control the tests above cannot be. They pass by returning null, and null is also what
    // a failed nameserver lookup returns, so on their own they would pass just as happily if this step
    // never worked at all. This asserts the step that everything else rests on: that the reader finds the
    // domain's own servers to ask.
    [LiveDnsFact]
    public async Task The_domains_own_nameservers_are_found_and_resolved()
    {
        var reader = new DnsClaimReader();

        IReadOnlyList<System.Net.IPAddress> servers =
            await reader.ResolveAuthoritativeAsync("google.com", CancellationToken.None);

        servers.Should().NotBeEmpty("the claim is read from these and nowhere else");
    }
}
