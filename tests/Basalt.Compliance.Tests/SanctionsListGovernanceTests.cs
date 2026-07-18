using Basalt.Compliance;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

/// <summary>
/// COMPL-C02: a governance-bound <see cref="SanctionsList"/> must only let the governance address edit
/// the list. The node wires this via <c>GenesisContractDeployer.Addresses.Governance</c>; the
/// parameterless ctor leaves the guard off (backward-compatible), which is the hole COMPL-C02 closes for
/// the consensus wiring.
/// </summary>
public class SanctionsListGovernanceTests
{
    private static byte[] Addr(byte seed) { var a = new byte[20]; a[19] = seed; return a; }

    private readonly byte[] _governance = Addr(0xFF);
    private readonly byte[] _target = Addr(0x01);

    [Fact]
    public void Governance_CanAddAndRemoveSanction()
    {
        var list = new SanctionsList(_governance);

        list.AddSanction(_target, "test", _governance).Should().BeTrue();
        list.IsSanctioned(_target).Should().BeTrue();

        list.RemoveSanction(_target, "test", _governance).Should().BeTrue();
        list.IsSanctioned(_target).Should().BeFalse();
    }

    [Fact]
    public void NonGovernanceCaller_CannotAddSanction()
    {
        var list = new SanctionsList(_governance);

        list.AddSanction(_target, "test", Addr(0x02)).Should().BeFalse();
        list.IsSanctioned(_target).Should().BeFalse();
    }

    [Fact]
    public void NullCaller_CannotAddSanction_WhenGovernanceBound()
    {
        var list = new SanctionsList(_governance);

        list.AddSanction(_target, "test", caller: null).Should().BeFalse();
        list.IsSanctioned(_target).Should().BeFalse();
    }

    [Fact]
    public void NonGovernanceCaller_CannotRemoveSanction()
    {
        var list = new SanctionsList(_governance);
        list.AddSanction(_target, "test", _governance).Should().BeTrue();

        list.RemoveSanction(_target, "test", Addr(0x02)).Should().BeFalse();
        list.IsSanctioned(_target).Should().BeTrue(); // still sanctioned
    }

    [Fact]
    public void NoGovernanceAddress_BackwardCompatible_AnyCallerCanEdit()
    {
        // The parameterless ctor leaves the guard off: this is the pre-COMPL-C02 behavior that the node
        // no longer relies on (it now passes the governance address).
        var list = new SanctionsList();

        list.AddSanction(_target, "test", caller: null).Should().BeTrue();
        list.IsSanctioned(_target).Should().BeTrue();
    }
}
