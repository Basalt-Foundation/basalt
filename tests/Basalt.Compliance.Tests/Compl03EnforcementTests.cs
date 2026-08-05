using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

/// <summary>
/// COMPL-C03: <see cref="ZkComplianceVerifier"/> enforces credential expiry (against the block
/// timestamp, which the circuit cannot know), issuer tier, and nullifier binding for v1-layout proofs.
/// These checks are meaningful because the membership circuit binds expiry and tier into the credential,
/// so a prover cannot forge them (proven by <see cref="ComplianceMembershipTests"/>).
/// </summary>
public class Compl03EnforcementTests
{
    private static readonly Hash256 Schema = new(Enumerable.Repeat((byte)0x11, 32).ToArray());

    // Block timestamps are milliseconds; the credential expiry is 1893456000 unix seconds (2030).
    private const long BeforeExpiryMs = 1_700_000_000_000; // 2023 -> not expired
    private const long AfterExpiryMs = 2_000_000_000_000;  // 2033 -> expired

    // The VK resolver returns the membership VK for our schema, so the proof verifies.
    private static ZkComplianceVerifier Verifier() =>
        new(schemaId => schemaId == Schema ? MembershipVector.VkBytes() : null);

    private static ComplianceProof ValidProof() => new()
    {
        SchemaId = Schema,
        Proof = MembershipVector.ProofBytes(),
        PublicInputs = MembershipVector.FlatPublicInputs(),
        Nullifier = MembershipVector.CircuitNullifier(),
    };

    private static ProofRequirement Require(byte minTier) => new() { SchemaId = Schema, MinIssuerTier = minTier };

    [Fact]
    public void ValidCredential_Accepted()
    {
        var outcome = Verifier().VerifyProofs(
            new[] { ValidProof() }, new[] { Require(minTier: 2) }, BeforeExpiryMs);
        outcome.Allowed.Should().BeTrue(outcome.Reason);
    }

    [Fact]
    public void ExpiredCredential_Rejected()
    {
        var outcome = Verifier().VerifyProofs(
            new[] { ValidProof() }, new[] { Require(minTier: 2) }, AfterExpiryMs);
        outcome.Allowed.Should().BeFalse();
        outcome.Reason.Should().Contain("expired");
    }

    [Fact]
    public void IssuerTierBelowRequired_Rejected()
    {
        // Credential tier is 3; require 4.
        var outcome = Verifier().VerifyProofs(
            new[] { ValidProof() }, new[] { Require(minTier: 4) }, BeforeExpiryMs);
        outcome.Allowed.Should().BeFalse();
        outcome.Reason.Should().Contain("tier");
    }

    [Fact]
    public void NullifierPublicInputMismatch_Rejected()
    {
        // Carry a different tracked nullifier than the one the circuit proved: replay protection would be
        // bypassable otherwise. The proof still verifies, but COMPL-C03 rejects the mismatch.
        var proof = ValidProof() with { Nullifier = new Hash256(Enumerable.Repeat((byte)0xAB, 32).ToArray()) };
        var outcome = Verifier().VerifyProofs(
            new[] { proof }, new[] { Require(minTier: 2) }, BeforeExpiryMs);
        outcome.Allowed.Should().BeFalse();
        outcome.Reason.Should().Contain("ullifier");
    }
}
