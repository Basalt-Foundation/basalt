using Basalt.Codec;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

/// <summary>
/// Whether a passed proposal can call a method that takes arguments.
///
/// It could not, and the consequence was quiet: an executable proposal invoked its target with no
/// arguments at all, so governance could only ever reach zero-argument methods. Every setter on the name
/// registry takes arguments and is governance-only, which made the whole reservation and attestation
/// system unreachable on a real chain while its own unit tests passed, because those set the caller
/// directly instead of going through a proposal.
/// </summary>
public class GovernanceCallArgumentTests
{
    private const ulong RegistrationFee = 1_000_000_000;
    private const ulong StartMs = 1_700_000_000_000;

    private readonly BasaltTestHost _host = new();
    private readonly BasaltNameService _bns;
    private readonly byte[] _governance;

    public GovernanceCallArgumentTests()
    {
        _bns = new BasaltNameService(RegistrationFee);
        _governance = new byte[20];
        _governance[18] = 0x10;
        _governance[19] = 0x03;
        _host.SetBlockTimestamp(StartMs);
        _host.Deploy(BnsAddress(), _bns);
    }

    private static byte[] BnsAddress()
    {
        var address = new byte[20];
        address[18] = 0x10;
        address[19] = 0x02;
        return address;
    }

    private static byte[] EncodeString(string value)
    {
        var buffer = new byte[10 + System.Text.Encoding.UTF8.GetByteCount(value)];
        var writer = new BasaltWriter(buffer);
        writer.WriteString(value);
        return buffer[..writer.Position];
    }

    private static byte[] EncodeInt(int value)
    {
        var buffer = new byte[4];
        var writer = new BasaltWriter(buffer);
        writer.WriteInt32(value);
        return buffer[..writer.Position];
    }

    /// <summary>
    /// The call governance has to be able to make: reserve a name for its rightful owner. It takes one
    /// string, which is one argument more than the old path could carry.
    /// </summary>
    [Fact]
    public void An_executable_call_carries_its_arguments_to_the_target()
    {
        _host.SetCaller(_governance);
        _host.Call(() => _bns.SetReservationDeadline((long)(StartMs / 1000) + 31_536_000));

        // CallContractEncoded makes the calling contract the caller, which on a real chain is the
        // governance contract executing the proposal. That is the only way Caller ever equals 0x1003.
        Context.Self = _governance;
        Context.CallContractEncoded(BnsAddress(), "ReserveName", EncodeString("google"));

        _host.Call(() => _bns.IsReserved("google.bslt")).Should().BeTrue();
    }

    /// <summary>
    /// The user-facing promise for the registration cap was that governance can change it. That is only
    /// true if a proposal can carry the new number.
    /// </summary>
    [Fact]
    public void Governance_can_change_a_numeric_setting_it_was_promised_control_of()
    {
        _host.SetCaller(_governance);
        _host.Call(() => _bns.SetRegistrationLimit(100, 31_536_000));
        _host.Call(() => _bns.RegistrationsRemaining(BasaltTestHost.CreateAddress(9))).Should().Be(100);

        var args = EncodeInt(250).Concat(EncodeLong(31_536_000)).ToArray();
        Context.Self = _governance;
        Context.CallContractEncoded(BnsAddress(), "SetRegistrationLimit", args);

        _host.Call(() => _bns.RegistrationsRemaining(BasaltTestHost.CreateAddress(9))).Should().Be(250);
    }

    private static byte[] EncodeLong(long value)
    {
        var buffer = new byte[8];
        var writer = new BasaltWriter(buffer);
        writer.WriteInt64(value);
        return buffer[..writer.Position];
    }

    /// <summary>
    /// Arguments that do not match the target's parameter list must fail rather than execute. A proposal
    /// whose bytes decode into something other than what voters read is worse than one that reverts.
    /// </summary>
    [Fact]
    public void Arguments_that_do_not_fit_the_target_method_are_refused()
    {
        _host.SetCaller(_governance);

        Context.Self = _governance;
        var act = () => Context.CallContractEncoded(BnsAddress(), "ReserveName", EncodeInt(42));

        act.Should().Throw<Exception>();
    }
}
