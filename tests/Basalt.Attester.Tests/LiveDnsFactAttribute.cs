using Xunit;

namespace Basalt.Attester.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips, visibly and with a reason, unless the run opted into real
/// DNS traffic with <c>ATTESTER_LIVE_DNS=1</c>.
///
/// Off by default on purpose. A suite that goes red when a network is unavailable teaches people to
/// ignore red, and these tests query nameservers that owe this project nothing. Turn them on before
/// putting an attester in front of real domains, because a resolver path that works on a laptop and not
/// on a hosted box is an ordinary thing to find out.
/// </summary>
public sealed class LiveDnsFactAttribute : FactAttribute
{
    public LiveDnsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ATTESTER_LIVE_DNS") != "1")
            Skip = "Live DNS tests are opt-in (set ATTESTER_LIVE_DNS=1).";
    }
}
