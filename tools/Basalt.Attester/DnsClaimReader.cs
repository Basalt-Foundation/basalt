using System.Net;
using DnsClient;
using DnsClient.Protocol;

namespace Basalt.Attester;

/// <summary>What a claim record says, once found and parsed.</summary>
/// <param name="Address">The 20-byte address the record names.</param>
/// <param name="Evidence">Where the record was found and what it said, recorded on chain so the
/// attestation can be re-checked later against public DNS rather than believed.</param>
public sealed record ClaimRecord(byte[] Address, string Evidence);

/// <summary>
/// Reads the TXT record by which someone proves they control a domain.
///
/// It resolves against the domain's own authoritative nameservers rather than a shared recursive
/// resolver, and that is the single most important line in this program. Attesters exist to be
/// independent, and independence includes their resolvers: if every attester asks the same public
/// resolver, one poisoned cache produces several agreeing and identical wrong answers, and the
/// threshold protects nothing while appearing to.
/// </summary>
public sealed class DnsClaimReader
{
    private const string ClaimPrefix = "bslt-claim=";
    private const string RecordLabel = "_bslt-claim";

    private readonly LookupClient _bootstrap;
    private readonly TimeSpan _timeout;
    private readonly string _suffix;

    /// <param name="bootstrapResolvers">Resolvers used only to find authoritative servers. Leave empty
    /// for the system resolver, and pick something different from your fellow attesters if you can.</param>
    /// <param name="timeout">Per-query timeout.</param>
    /// <param name="domainSuffix">The registry this attester reads, ".com" for the reserved list. Exists
    /// so an operator can rehearse the whole path against a domain they own before pointing it at
    /// domains they do not.</param>
    public DnsClaimReader(
        IReadOnlyList<string>? bootstrapResolvers = null,
        TimeSpan? timeout = null,
        string domainSuffix = ".com")
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _suffix = domainSuffix;

        // Only used to find the authoritative servers. The claim itself is never read from here.
        var options = bootstrapResolvers is { Count: > 0 }
            ? new LookupClientOptions(bootstrapResolvers.Select(IPAddress.Parse).ToArray())
            : new LookupClientOptions();
        options.Timeout = _timeout;
        options.UseCache = false; // a cached answer is someone else's observation, not ours
        _bootstrap = new LookupClient(options);
    }

    /// <summary>
    /// Looks for the claim record on <c>_bslt-claim.&lt;label&gt;.com</c> and returns what it names, or
    /// null when there is nothing to attest.
    ///
    /// Absence is not an error. Most reserved names will have no record for most of the window, so the
    /// caller loops over all of them and acts only on the few that do.
    /// </summary>
    public async Task<ClaimRecord?> ReadAsync(string label, CancellationToken cancellationToken = default)
    {
        var domain = label + _suffix;
        var name = RecordLabel + "." + domain;

        IReadOnlyList<IPAddress> authoritative = await ResolveAuthoritativeAsync(domain, cancellationToken)
            .ConfigureAwait(false);
        if (authoritative.Count == 0)
        {
            return null;
        }

        var options = new LookupClientOptions([.. authoritative]) { Timeout = _timeout, UseCache = false };
        var direct = new LookupClient(options);

        IDnsQueryResponse response;
        try
        {
            response = await direct.QueryAsync(name, QueryType.TXT, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DnsResponseException)
        {
            return null;
        }

        if (response.HasError)
        {
            return null;
        }

        // A domain may publish several TXT records, and only one of them can be the claim. Two different
        // claims on one domain is not a tie to be broken, it is a domain whose DNS does not say one thing,
        // and attesting on either one would be picking a winner.
        byte[]? found = null;
        foreach (TxtRecord txt in response.Answers.TxtRecords())
        {
            foreach (string chunk in txt.Text)
            {
                if (!TryParseClaim(chunk, out byte[]? address))
                {
                    continue;
                }

                if (found is not null && !found.AsSpan().SequenceEqual(address))
                {
                    return null;
                }

                found = address;
            }
        }

        if (found is null)
        {
            return null;
        }

        var hex = Convert.ToHexStringLower(found);
        return new ClaimRecord(found, $"dns:{name} TXT {ClaimPrefix}0x{hex} @{authoritative[0]}");
    }

    /// <summary>
    /// Reads one TXT string and returns the address it claims, if it is a claim at all.
    ///
    /// A malformed record is not a claim. Attesting on a value nobody can parse would put an address on
    /// chain that the domain owner never wrote, so anything short of an exact 20-byte hex address is
    /// treated as absent rather than guessed at.
    /// </summary>
    internal static bool TryParseClaim(string txtValue, out byte[] address)
    {
        address = [];

        var value = txtValue.Trim();
        if (!value.StartsWith(ClaimPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hex = value[ClaimPrefix.Length..].Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (hex.Length != 40 || !IsHex(hex))
        {
            return false;
        }

        // The zero address is well formed and still not a claim. The contract refuses it, because a name
        // assigned to an address with no key is gone for good, and reading it as absent here means an
        // unedited template costs a domain owner a delay rather than a transaction that goes nowhere.
        if (hex.All(c => c == '0'))
        {
            return false;
        }

        address = Convert.FromHexString(hex);
        return true;
    }

    /// <summary>
    /// Finds the addresses of the domain's authoritative nameservers, so the claim can be read from the
    /// source rather than from whatever a recursive resolver happens to be holding.
    /// </summary>
    internal async Task<IReadOnlyList<IPAddress>> ResolveAuthoritativeAsync(string domain, CancellationToken ct)
    {
        try
        {
            IDnsQueryResponse ns = await _bootstrap.QueryAsync(domain, QueryType.NS, cancellationToken: ct)
                .ConfigureAwait(false);
            if (ns.HasError)
            {
                return [];
            }

            var addresses = new List<IPAddress>();
            foreach (NsRecord record in ns.Answers.NsRecords())
            {
                IDnsQueryResponse a = await _bootstrap
                    .QueryAsync(record.NSDName.Value, QueryType.A, cancellationToken: ct).ConfigureAwait(false);
                foreach (ARecord address in a.Answers.ARecords())
                {
                    addresses.Add(address.Address);
                }
            }

            return addresses;
        }
        catch (DnsResponseException)
        {
            return [];
        }
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
