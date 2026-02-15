using Basalt.Compliance;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

public class SanctionsListTests
{
    private static byte[] Addr(byte seed) { var a = new byte[20]; a[19] = seed; return a; }

    private readonly SanctionsList _sanctions = new();

    [Fact]
    public void IsSanctioned_Returns_False_By_Default()
    {
        _sanctions.IsSanctioned(Addr(1)).Should().BeFalse();
    }

    [Fact]
    public void AddSanction_Makes_Address_Sanctioned()
    {
        var addr = Addr(1);
        _sanctions.AddSanction(addr, "OFAC SDN");
        _sanctions.IsSanctioned(addr).Should().BeTrue();
    }

    [Fact]
    public void RemoveSanction_Clears_Sanction()
    {
        var addr = Addr(1);
        _sanctions.AddSanction(addr, "OFAC SDN");
        _sanctions.RemoveSanction(addr, "Delisted");
        _sanctions.IsSanctioned(addr).Should().BeFalse();
    }

    [Fact]
    public void AuditLog_Records_Sanctions_Operations()
    {
        var addr = Addr(1);
        _sanctions.AddSanction(addr, "OFAC");
        _sanctions.RemoveSanction(addr, "Delisted");

        var log = _sanctions.GetAuditLog();
        log.Should().HaveCount(2);
        log[0].EventType.Should().Be(ComplianceEventType.AddressBlocked);
        log[1].EventType.Should().Be(ComplianceEventType.AddressUnblocked);
    }

    [Fact]
    public void Multiple_Addresses_Tracked_Independently()
    {
        var addr1 = Addr(1);
        var addr2 = Addr(2);

        _sanctions.AddSanction(addr1, "Test");
        _sanctions.IsSanctioned(addr1).Should().BeTrue();
        _sanctions.IsSanctioned(addr2).Should().BeFalse();
    }
}
