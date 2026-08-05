using Basalt.Attester;

// basalt-attester: reports whether a domain's DNS says who should receive the matching .bslt name.
//
// It decides nothing. It resolves one TXT record per reserved name and, when it finds a well-formed
// one, says so on chain. The name is handed over by the contract once enough independent attesters have
// said the same thing, so no single operator, including whoever runs this, can assign a name.
//
// There is deliberately no request step. A claimant publishes their record and waits, and this loops
// over the whole reserved list. Five hundred TXT lookups an hour is nothing, and it means there is no
// public endpoint for anyone to spam.

static string Required(string name)
    => Environment.GetEnvironmentVariable(name)
       ?? throw new InvalidOperationException($"{name} must be set.");

string endpoint = Required("ATTESTER_ENDPOINT");
byte[] signingKey = Convert.FromHexString(Required("ATTESTER_SIGNING_KEY"));
uint chainId = uint.Parse(Required("ATTESTER_CHAIN_ID"));
string listPath = Required("ATTESTER_RESERVED_LIST");

// Independence includes resolvers. Leave this unset to use the system resolver only to find the
// authoritative servers, and set it to something different from your fellow attesters if you can.
var bootstrap = (Environment.GetEnvironmentVariable("ATTESTER_BOOTSTRAP_RESOLVERS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToList();

int intervalSeconds = int.TryParse(Environment.GetEnvironmentVariable("ATTESTER_INTERVAL_SECONDS"), out int i)
    ? i : 3600;

IReadOnlyList<string> labels = ReservedList.Load(listPath, Console.Error.WriteLine);
Console.Error.WriteLine(
    $"basalt-attester watching {labels.Count} reserved labels every {intervalSeconds}s, chain {chainId} at {endpoint}");
if (bootstrap.Count > 0)
{
    Console.Error.WriteLine($"bootstrap resolvers: {string.Join(", ", bootstrap)}");
}

using var http = new HttpClient { BaseAddress = new Uri(endpoint) };
var chain = new AttesterChainClient(http, signingKey, chainId);
var dns = new DnsClaimReader(bootstrap);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

while (!cts.IsCancellationRequested)
{
    int seen = 0, attested = 0, failed = 0;

    foreach (string label in labels)
    {
        if (cts.IsCancellationRequested)
        {
            break;
        }

        try
        {
            // Ask the chain first. Most labels are still unclaimed, and skipping them costs one cheap
            // view call instead of a DNS round trip to a nameserver that owes us nothing.
            if (!await chain.IsUnclaimedAsync(label, cts.Token))
            {
                continue;
            }

            ClaimRecord? claim = await dns.ReadAsync(label, cts.Token);
            if (claim is null)
            {
                continue;
            }

            seen++;

            if (await chain.HasAttestedAsync(label, claim.Address, cts.Token))
            {
                continue;
            }

            await chain.AttestAsync(label, claim.Address, claim.Evidence, cts.Token);
            attested++;
            Console.Error.WriteLine($"attested {label}.bslt -> 0x{Convert.ToHexStringLower(claim.Address)}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreachable nameserver or one rejected transaction must not stop the sweep. The
            // contract counts attesters, so a missed round costs a delay and nothing else.
            failed++;
            Console.Error.WriteLine($"{label}: {ex.Message}");
        }
    }

    Console.Error.WriteLine($"sweep done: {seen} record(s) found, {attested} attested, {failed} error(s)");

    try
    {
        await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

Console.Error.WriteLine("basalt-attester stopped");
return 0;
