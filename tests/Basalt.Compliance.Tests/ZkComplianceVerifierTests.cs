using Basalt.Compliance;
using Basalt.Core;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

public class ZkComplianceVerifierTests
{
    private static Hash256 SchemaId(byte seed)
    {
        var bytes = new byte[32];
        bytes[0] = seed;
        return new Hash256(bytes);
    }

    private static Hash256 Nullifier(byte seed)
    {
        var bytes = new byte[32];
        bytes[31] = seed;
        return new Hash256(bytes);
    }

    /// <summary>
    /// Build a syntactically valid ComplianceProof (192-byte proof, 32-byte public inputs).
    /// The Groth16 verification will fail because these are not real curve points,
    /// but all pre-verification checks (size, nullifier, VK lookup) will pass.
    /// </summary>
    private static ComplianceProof MakeProof(Hash256 schemaId, Hash256 nullifier,
        byte[]? proof = null, byte[]? publicInputs = null)
    {
        return new ComplianceProof
        {
            SchemaId = schemaId,
            Nullifier = nullifier,
            Proof = proof ?? new byte[ComplianceProof.Groth16ProofSize],
            PublicInputs = publicInputs ?? new byte[32],
        };
    }

    private static ProofRequirement MakeRequirement(Hash256 schemaId, byte tier = 1)
    {
        return new ProofRequirement
        {
            SchemaId = schemaId,
            MinIssuerTier = tier,
        };
    }

    // ---------------------------------------------------------------
    // ZkComplianceVerifier tests
    // ---------------------------------------------------------------

    [Fact]
    public void NoRequirements_ReturnsSuccess()
    {
        var verifier = new ZkComplianceVerifier(_ => null);

        var result = verifier.VerifyProofs(
            Array.Empty<ComplianceProof>(),
            Array.Empty<ProofRequirement>(),
            blockTimestamp: 1000);

        result.Allowed.Should().BeTrue();
        result.ErrorCode.Should().Be(BasaltErrorCode.Success);
    }

    [Fact]
    public void RequirementsWithNoProofs_ReturnsMissing()
    {
        var verifier = new ZkComplianceVerifier(_ => null);
        var requirements = new[] { MakeRequirement(SchemaId(1)) };

        var result = verifier.VerifyProofs(
            Array.Empty<ComplianceProof>(),
            requirements,
            blockTimestamp: 1000);

        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofMissing);
        result.Reason.Should().Contain("none were provided");
    }

    [Fact]
    public void InvalidProofSize_ReturnsInvalid()
    {
        var schema = SchemaId(1);
        var verifier = new ZkComplianceVerifier(_ => new byte[128]); // VK exists

        // Proof is 100 bytes instead of 192
        var proof = MakeProof(schema, Nullifier(1), proof: new byte[100]);
        var requirements = new[] { MakeRequirement(schema) };

        var result = verifier.VerifyProofs(
            new[] { proof },
            requirements,
            blockTimestamp: 1000);

        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofInvalid);
        result.Reason.Should().Contain("192 bytes");
    }

    [Fact]
    public void InvalidPublicInputs_ReturnsInvalid()
    {
        var schema = SchemaId(2);
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);

        // Public inputs = 33 bytes, not a multiple of 32
        var proof = MakeProof(schema, Nullifier(2), publicInputs: new byte[33]);
        var requirements = new[] { MakeRequirement(schema) };

        var result = verifier.VerifyProofs(
            new[] { proof },
            requirements,
            blockTimestamp: 1000);

        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofInvalid);
        result.Reason.Should().Contain("multiple of 32");
    }

    [Fact]
    public void EmptyPublicInputs_ReturnsInvalid()
    {
        var schema = SchemaId(3);
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);

        // Empty public inputs
        var proof = MakeProof(schema, Nullifier(3), publicInputs: Array.Empty<byte>());
        var requirements = new[] { MakeRequirement(schema) };

        var result = verifier.VerifyProofs(
            new[] { proof },
            requirements,
            blockTimestamp: 1000);

        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofInvalid);
        result.Reason.Should().Contain("non-empty");
    }

    [Fact]
    public void DuplicateNullifier_ReturnsInvalid()
    {
        var schema = SchemaId(4);
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);

        var sharedNullifier = Nullifier(4);

        // Two requirements with the same schema, two proofs with the same nullifier
        var proofA = MakeProof(schema, sharedNullifier);
        var requirements = new[]
        {
            MakeRequirement(schema),
            MakeRequirement(schema),
        };

        // First call uses the nullifier
        var result1 = verifier.VerifyProofs(
            new[] { proofA },
            new[] { MakeRequirement(schema) },
            blockTimestamp: 1000);

        // The first call will hit VK decode or Groth16 verification failure,
        // but the nullifier is consumed before that step.
        // Second call with the same nullifier should fail on duplicate.
        var result2 = verifier.VerifyProofs(
            new[] { proofA },
            new[] { MakeRequirement(schema) },
            blockTimestamp: 1000);

        result2.Allowed.Should().BeFalse();
        result2.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofInvalid);
        result2.Reason.Should().Contain("Duplicate nullifier");
    }

    [Fact]
    public void ResetNullifiers_AllowsReuse()
    {
        var schema = SchemaId(5);
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);

        var nullifier = Nullifier(5);
        var proof = MakeProof(schema, nullifier);
        var requirements = new[] { MakeRequirement(schema) };

        // First use consumes the nullifier (even though Groth16 will fail)
        verifier.VerifyProofs(new[] { proof }, requirements, blockTimestamp: 1000);

        // Reset nullifiers
        verifier.ResetNullifiers();

        // After reset, the same nullifier should be accepted again (not duplicate).
        // It will still fail at Groth16 verification because the proof is synthetic,
        // but it should NOT fail with "Duplicate nullifier".
        var result = verifier.VerifyProofs(new[] { proof }, requirements, blockTimestamp: 1000);

        result.Reason.Should().NotContain("Duplicate nullifier");
    }

    [Fact]
    public void MissingVerificationKey_ReturnsInvalid()
    {
        var schema = SchemaId(6);
        // VK lookup always returns null
        var verifier = new ZkComplianceVerifier(_ => null);

        var proof = MakeProof(schema, Nullifier(6));
        var requirements = new[] { MakeRequirement(schema) };

        var result = verifier.VerifyProofs(
            new[] { proof },
            requirements,
            blockTimestamp: 1000);

        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofInvalid);
        result.Reason.Should().Contain("No verification key");
    }

    [Fact]
    public void MissingSchemaProof_ReturnsMissing()
    {
        var schemaX = SchemaId(7);
        var schemaY = SchemaId(8);
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);

        // Proof is for schema Y, but requirement is for schema X
        var proof = MakeProof(schemaY, Nullifier(7));
        var requirements = new[] { MakeRequirement(schemaX) };

        var result = verifier.VerifyProofs(
            new[] { proof },
            requirements,
            blockTimestamp: 1000);

        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofMissing);
        result.Reason.Should().Contain("Missing proof for schema");
    }

    // ---------------------------------------------------------------
    // Hybrid ComplianceEngine tests (ZK delegation path)
    // ---------------------------------------------------------------

    [Fact]
    public void Engine_WithZkVerifier_DelegatesProofVerification()
    {
        var registry = new IdentityRegistry();
        var sanctions = new SanctionsList();

        var schema = SchemaId(10);
        // VK lookup returns null so the ZK verifier will report missing VK
        var zkVerifier = new ZkComplianceVerifier(_ => null);
        var engine = new ComplianceEngine(registry, sanctions, zkVerifier);

        var proof = MakeProof(schema, Nullifier(10));
        var requirements = new[] { MakeRequirement(schema) };

        var result = engine.VerifyProofs(
            new[] { proof },
            requirements,
            blockTimestamp: 1000);

        // The engine should delegate to ZkComplianceVerifier.
        // With a null VK, the ZK verifier returns ComplianceProofInvalid.
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofInvalid);
        result.Reason.Should().Contain("No verification key");
    }

    [Fact]
    public void Engine_WithoutZkVerifier_CannotVerifyProofs()
    {
        var registry = new IdentityRegistry();
        var sanctions = new SanctionsList();
        var engine = new ComplianceEngine(registry, sanctions); // No ZK verifier

        var schema = SchemaId(11);
        var proof = MakeProof(schema, Nullifier(11));
        var requirements = new[] { MakeRequirement(schema) };

        var result = engine.VerifyProofs(
            new[] { proof },
            requirements,
            blockTimestamp: 1000);

        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.ComplianceProofInvalid);
        result.Reason.Should().Contain("ZK verification not available");
    }

    [Fact]
    public void Engine_NoRequirements_NoProofs_Succeeds()
    {
        var registry = new IdentityRegistry();
        var sanctions = new SanctionsList();
        var engine = new ComplianceEngine(registry, sanctions);

        var result = engine.VerifyProofs(
            Array.Empty<ComplianceProof>(),
            Array.Empty<ProofRequirement>(),
            blockTimestamp: 1000);

        result.Allowed.Should().BeTrue();
        result.ErrorCode.Should().Be(BasaltErrorCode.Success);
    }
}
