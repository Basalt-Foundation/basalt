using Basalt.Compliance;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

public class ComplianceEngineTests
{
    private static byte[] Addr(byte seed) { var a = new byte[20]; a[19] = seed; return a; }

    private readonly IdentityRegistry _registry = new();
    private readonly SanctionsList _sanctions = new();
    private readonly ComplianceEngine _engine;

    private readonly byte[] _provider = Addr(1);
    private readonly byte[] _tokenAddr = Addr(10);
    private readonly byte[] _sender = Addr(20);
    private readonly byte[] _receiver = Addr(30);

    public ComplianceEngineTests()
    {
        _engine = new ComplianceEngine(_registry, _sanctions);
        _registry.ApproveProvider(_provider);
    }

    private void KycBoth(KycLevel level = KycLevel.Basic, ushort country = 840)
    {
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _sender, Issuer = _provider, IssuedAt = 1000,
            Level = level, CountryCode = country, ClaimHash = new byte[32],
        });
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _receiver, Issuer = _provider, IssuedAt = 1000,
            Level = level, CountryCode = country, ClaimHash = new byte[32],
        });
    }

    [Fact]
    public void NoPolicy_Allows_Transfer()
    {
        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Paused_Token_Blocks_Transfer()
    {
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            Paused = true,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.Paused);
    }

    [Fact]
    public void KYC_Check_Blocks_Unverified_Sender()
    {
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            RequiredSenderKycLevel = KycLevel.Basic,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.KycMissing);
        result.RuleId.Should().Be("KYC_SENDER");
    }

    [Fact]
    public void KYC_Check_Blocks_Unverified_Receiver()
    {
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _sender, Issuer = _provider, IssuedAt = 1000,
            Level = KycLevel.Basic, ClaimHash = new byte[32],
        });

        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            RequiredSenderKycLevel = KycLevel.Basic,
            RequiredReceiverKycLevel = KycLevel.Basic,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.KycMissing);
        result.RuleId.Should().Be("KYC_RECEIVER");
    }

    [Fact]
    public void KYC_Check_Passes_With_Valid_Attestations()
    {
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            RequiredSenderKycLevel = KycLevel.Basic,
            RequiredReceiverKycLevel = KycLevel.Basic,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Sanctions_Check_Blocks_Sanctioned_Sender()
    {
        KycBoth();
        _sanctions.AddSanction(_sender, "OFAC");

        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            SanctionsCheckEnabled = true,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.Sanctioned);
    }

    [Fact]
    public void Sanctions_Check_Blocks_Sanctioned_Receiver()
    {
        KycBoth();
        _sanctions.AddSanction(_receiver, "OFAC");

        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            SanctionsCheckEnabled = true,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.Sanctioned);
    }

    [Fact]
    public void GeoRestriction_Blocks_Blocked_Country_Sender()
    {
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _sender, Issuer = _provider, IssuedAt = 1000,
            Level = KycLevel.Basic, CountryCode = 408, ClaimHash = new byte[32], // North Korea
        });
        _registry.IssueAttestation(_provider, new IdentityAttestation
        {
            Subject = _receiver, Issuer = _provider, IssuedAt = 1000,
            Level = KycLevel.Basic, CountryCode = 840, ClaimHash = new byte[32], // US
        });

        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            BlockedCountries = [408, 364], // NK, Iran
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.GeoRestricted);
    }

    [Fact]
    public void GeoRestriction_Allows_NonBlocked_Country()
    {
        KycBoth(KycLevel.Basic, 840); // US for both

        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            BlockedCountries = [408, 364],
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void HoldingLimit_Blocks_Excess()
    {
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            MaxHoldingAmount = 1000,
        });

        // Receiver already has 800, trying to receive 300 = 1100 > 1000
        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 300, 2000,
            receiverCurrentBalance: 800);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.HoldingLimit);
    }

    [Fact]
    public void HoldingLimit_Overflow_Still_Blocked()
    {
        // H-01: ulong overflow bypass — receiverCurrentBalance + amount wraps to small value
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            MaxHoldingAmount = 1000,
        });

        // ulong.MaxValue + 500 would wrap to 499, which is < 1000
        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 500, 2000,
            receiverCurrentBalance: ulong.MaxValue);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.HoldingLimit);
    }

    [Fact]
    public void HoldingLimit_Allows_Under_Limit()
    {
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            MaxHoldingAmount = 1000,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000,
            receiverCurrentBalance: 500);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Lockup_Blocks_Before_End()
    {
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            LockupEndTimestamp = 5000,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 3000);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.Lockup);
    }

    [Fact]
    public void Lockup_Allows_After_End()
    {
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            LockupEndTimestamp = 5000,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 6000);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void TravelRule_Blocks_Large_Transfer_Without_Data()
    {
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            TravelRuleThreshold = 1000,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 1500, 2000,
            hasTravelRuleData: false);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.TravelRuleMissing);
    }

    [Fact]
    public void TravelRule_Allows_Large_Transfer_With_Data()
    {
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            TravelRuleThreshold = 1000,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 1500, 2000,
            hasTravelRuleData: true);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void TravelRule_Allows_Small_Transfer_Without_Data()
    {
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            TravelRuleThreshold = 1000,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 500, 2000,
            hasTravelRuleData: false);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Full_Pipeline_With_All_Checks_Passing()
    {
        KycBoth(KycLevel.Enhanced, 840);
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            RequiredSenderKycLevel = KycLevel.Enhanced,
            RequiredReceiverKycLevel = KycLevel.Basic,
            SanctionsCheckEnabled = true,
            BlockedCountries = [408],
            MaxHoldingAmount = 10000,
            LockupEndTimestamp = 1000,
            TravelRuleThreshold = 5000,
        });

        var result = _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 1000, 2000,
            receiverCurrentBalance: 500, hasTravelRuleData: false);
        result.Allowed.Should().BeTrue();
    }

    [Fact]
    public void AuditLog_Records_Transfer_Checks()
    {
        KycBoth();
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            RequiredSenderKycLevel = KycLevel.Basic,
        });

        _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);

        var log = _engine.GetAuditLog();
        log.Should().Contain(e => e.EventType == ComplianceEventType.TransferApproved);
    }

    [Fact]
    public void AuditLog_Records_Blocked_Transfer()
    {
        _engine.SetPolicy(_tokenAddr, new CompliancePolicy
        {
            TokenAddress = _tokenAddr,
            RequiredSenderKycLevel = KycLevel.Basic,
        });

        _engine.CheckTransfer(_tokenAddr, _sender, _receiver, 100, 2000);

        var blocked = _engine.GetAuditLog(ComplianceEventType.TransferBlocked);
        blocked.Should().HaveCount(1);
    }

    [Fact]
    public void GetPolicy_Returns_Null_For_Unknown_Token()
    {
        _engine.GetPolicy(Addr(99)).Should().BeNull();
    }
}
