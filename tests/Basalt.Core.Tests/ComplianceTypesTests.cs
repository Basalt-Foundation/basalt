using FluentAssertions;
using Xunit;

namespace Basalt.Core.Tests;

public class ComplianceTypesTests
{
    // ===== AUDIT H-08: ComplianceProof safe defaults =====

    [Fact]
    public void ComplianceProof_Default_HasSafeDefaults()
    {
        var proof = new ComplianceProof();

        proof.Proof.Should().NotBeNull().And.BeEmpty();
        proof.PublicInputs.Should().NotBeNull().And.BeEmpty();
        proof.SchemaId.Should().Be(Hash256.Zero);
        proof.Nullifier.Should().Be(Hash256.Zero);
    }

    [Fact]
    public void ComplianceProof_InitSyntax_HasSafeDefaults()
    {
        var proof = new ComplianceProof
        {
            SchemaId = Hash256.Zero,
            Nullifier = Hash256.Zero,
        };

        proof.Proof.Should().NotBeNull().And.BeEmpty();
        proof.PublicInputs.Should().NotBeNull().And.BeEmpty();
    }

    // ===== AUDIT M-13: ComplianceCheckOutcome.Reason safe default =====

    [Fact]
    public void ComplianceCheckOutcome_Default_ReasonIsNotNull()
    {
        var outcome = new ComplianceCheckOutcome();

        outcome.Reason.Should().NotBeNull().And.BeEmpty();
        outcome.Allowed.Should().BeFalse();
        outcome.ErrorCode.Should().Be(BasaltErrorCode.Success);
    }

    [Fact]
    public void ComplianceCheckOutcome_Success_HasEmptyReason()
    {
        var outcome = ComplianceCheckOutcome.Success;

        outcome.Allowed.Should().BeTrue();
        outcome.Reason.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void ComplianceCheckOutcome_Fail_HasReason()
    {
        var outcome = ComplianceCheckOutcome.Fail(
            BasaltErrorCode.ComplianceProofInvalid,
            "test failure reason");

        outcome.Allowed.Should().BeFalse();
        outcome.Reason.Should().Be("test failure reason");
        outcome.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofInvalid);
    }
}
