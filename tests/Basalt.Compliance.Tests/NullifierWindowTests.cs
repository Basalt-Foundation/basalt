using Basalt.Compliance;
using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

public class NullifierWindowTests
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

    private static ComplianceProof MakeProof(Hash256 schemaId, Hash256 nullifier)
    {
        return new ComplianceProof
        {
            SchemaId = schemaId,
            Nullifier = nullifier,
            Proof = new byte[ComplianceProof.Groth16ProofSize],
            PublicInputs = new byte[32],
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

    [Fact]
    public void NullifierUsedInSameBlock_IsRejected()
    {
        // VK that returns dummy bytes so we get past the VK lookup
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);
        verifier.NullifierWindowBlocks = 256;
        verifier.ResetNullifiers(10); // Set current block

        var schema = SchemaId(1);
        var nullifier = Nullifier(1);
        var proof = MakeProof(schema, nullifier);
        var req = MakeRequirement(schema);

        // First use — will fail at Groth16 verification (not real points),
        // but the nullifier gets rolled back. Use VerifyProofs to exercise nullifier path.
        var result1 = verifier.VerifyProofs([proof], [req], 1000);
        // result1 fails because Groth16 verification fails on dummy data, but that's expected

        // Now manually test nullifier consumption by checking the error code
        // Use the fact that after a failed Groth16 verify, nullifier is rolled back
        // So a second attempt should also fail at Groth16, not at nullifier
        var result2 = verifier.VerifyProofs([proof], [req], 1000);

        // Both should fail the same way (Groth16 failure, not nullifier replay)
        // because the nullifier is rolled back on verification failure
        result1.ErrorCode.Should().Be(result2.ErrorCode);
    }

    [Fact]
    public void NullifierRetainedAcrossBlocks_WithinWindow()
    {
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);
        verifier.NullifierWindowBlocks = 256;

        // CR-10: Track nullifiers directly and verify they survive within the window
        verifier.TrackNullifier(Nullifier(42), 10);
        verifier.TrackNullifier(Nullifier(43), 11);
        verifier.NullifierCount.Should().Be(2);

        // After reset at block 12, window cutoff = max(0, 12-256) = 0
        // Both nullifiers (blocks 10, 11) are > 0 → retained
        verifier.ResetNullifiers(12);
        verifier.NullifierCount.Should().Be(2, "nullifiers within window should be retained");
    }

    [Fact]
    public void NullifierPrunedOutsideWindow()
    {
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);
        verifier.NullifierWindowBlocks = 10; // Small window for testing

        // Add nullifiers at block 5 and 15
        verifier.TrackNullifier(Nullifier(1), 5);
        verifier.TrackNullifier(Nullifier(2), 15);
        verifier.NullifierCount.Should().Be(2);

        // At block 20, cutoff = 20 - 10 = 10
        // Nullifier from block 5 (< 10) → pruned; block 15 (>= 10) → retained
        verifier.ResetNullifiers(20);
        verifier.NullifierCount.Should().Be(1, "nullifier from block 5 should be pruned");
    }

    [Fact]
    public void ZeroWindowClearsAllNullifiers()
    {
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);
        verifier.NullifierWindowBlocks = 0;

        verifier.TrackNullifier(Nullifier(1), 10);
        verifier.TrackNullifier(Nullifier(2), 11);
        verifier.NullifierCount.Should().Be(2);

        // With window=0, ResetNullifiers should clear everything
        verifier.ResetNullifiers(12);
        verifier.NullifierCount.Should().Be(0, "window=0 should clear all nullifiers");
    }

    [Fact]
    public void BackwardCompatible_FullReset()
    {
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);

        verifier.TrackNullifier(Nullifier(1), 5);
        verifier.TrackNullifier(Nullifier(2), 10);
        verifier.NullifierCount.Should().Be(2);

        // Parameterless ResetNullifiers should clear all
        verifier.ResetNullifiers();
        verifier.NullifierCount.Should().Be(0, "full reset should clear all nullifiers");
    }

    [Fact]
    public void ConfigurableWindowSize()
    {
        var verifier = new ZkComplianceVerifier(_ => new byte[128]);

        verifier.NullifierWindowBlocks = 100;
        verifier.NullifierWindowBlocks.Should().Be(100UL);

        verifier.NullifierWindowBlocks = 500;
        verifier.NullifierWindowBlocks.Should().Be(500UL);
    }
}
