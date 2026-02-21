using Basalt.Confidentiality.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Confidentiality.Tests;

public class Groth16CodecTests
{
    // ── Proof Codec ─────────────────────────────────────────────────────────

    [Fact]
    public void ProofCodec_RoundTrip()
    {
        // Create a proof from valid G1/G2 points.
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;
        var negG1 = PairingEngine.NegG1(g1);

        var original = new Groth16Proof
        {
            A = g1,
            B = g2,
            C = negG1,
        };

        byte[] encoded = Groth16Codec.EncodeProof(original);
        encoded.Should().HaveCount(Groth16Codec.ProofSize);

        Groth16Proof decoded = Groth16Codec.DecodeProof(encoded);

        decoded.A.Should().Equal(original.A);
        decoded.B.Should().Equal(original.B);
        decoded.C.Should().Equal(original.C);
    }

    [Fact]
    public void VerificationKeyCodec_RoundTrip()
    {
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;

        // Create different G1 points for IC entries via scalar multiplication.
        var scalar2 = new byte[32];
        scalar2[31] = 2;
        var scalar3 = new byte[32];
        scalar3[31] = 3;
        var scalar4 = new byte[32];
        scalar4[31] = 4;

        var ic0 = g1;
        var ic1 = PairingEngine.ScalarMultG1(g1, scalar2);
        var ic2 = PairingEngine.ScalarMultG1(g1, scalar3);

        var original = new VerificationKey
        {
            AlphaG1 = g1,
            BetaG2 = g2,
            GammaG2 = g2,
            DeltaG2 = PairingEngine.NegG2(g2),
            IC = new[] { ic0, ic1, ic2 },
        };

        byte[] encoded = Groth16Codec.EncodeVerificationKey(original);

        // Expected size: 48 + 96 + 96 + 96 + 4 + 3*48 = 484
        int expectedSize = 48 + 96 + 96 + 96 + 4 + 3 * 48;
        encoded.Should().HaveCount(expectedSize);

        VerificationKey decoded = Groth16Codec.DecodeVerificationKey(encoded);

        decoded.AlphaG1.Should().Equal(original.AlphaG1);
        decoded.BetaG2.Should().Equal(original.BetaG2);
        decoded.GammaG2.Should().Equal(original.GammaG2);
        decoded.DeltaG2.Should().Equal(original.DeltaG2);
        decoded.IC.Should().HaveCount(original.IC.Length);
        for (int i = 0; i < original.IC.Length; i++)
        {
            decoded.IC[i].Should().Equal(original.IC[i], $"IC[{i}] should match");
        }
    }

    [Fact]
    public void ProofCodec_WrongSize_Throws()
    {
        // Too short
        var tooShort = new byte[Groth16Codec.ProofSize - 1];
        var actShort = () => Groth16Codec.DecodeProof(tooShort);
        actShort.Should().Throw<ArgumentException>();

        // Too long
        var tooLong = new byte[Groth16Codec.ProofSize + 1];
        var actLong = () => Groth16Codec.DecodeProof(tooLong);
        actLong.Should().Throw<ArgumentException>();

        // Empty
        var empty = Array.Empty<byte>();
        var actEmpty = () => Groth16Codec.DecodeProof(empty);
        actEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void VerificationKeyCodec_TooShort_Throws()
    {
        // VkFixedSize = 48 + 96 + 96 + 96 + 4 = 340
        // Anything shorter than that should throw.
        var tooShort = new byte[339];
        var act = () => Groth16Codec.DecodeVerificationKey(tooShort);
        act.Should().Throw<ArgumentException>();

        var empty = Array.Empty<byte>();
        var actEmpty = () => Groth16Codec.DecodeVerificationKey(empty);
        actEmpty.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EncodeProof_Null_Throws()
    {
        var act = () => Groth16Codec.EncodeProof(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void EncodeVerificationKey_Null_Throws()
    {
        var act = () => Groth16Codec.EncodeVerificationKey(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void VerificationKeyCodec_ZeroICEntries_RoundTrips()
    {
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;

        var vk = new VerificationKey
        {
            AlphaG1 = g1,
            BetaG2 = g2,
            GammaG2 = g2,
            DeltaG2 = g2,
            IC = Array.Empty<byte[]>(),
        };

        byte[] encoded = Groth16Codec.EncodeVerificationKey(vk);

        // Expected size: 48 + 96 + 96 + 96 + 4 + 0 = 340
        encoded.Should().HaveCount(340);

        VerificationKey decoded = Groth16Codec.DecodeVerificationKey(encoded);
        decoded.IC.Should().BeEmpty();
        decoded.AlphaG1.Should().Equal(g1);
    }

    [Fact]
    public void VerificationKeyCodec_SingleIC_RoundTrips()
    {
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;

        var vk = new VerificationKey
        {
            AlphaG1 = g1,
            BetaG2 = g2,
            GammaG2 = g2,
            DeltaG2 = g2,
            IC = new[] { g1 },
        };

        byte[] encoded = Groth16Codec.EncodeVerificationKey(vk);
        VerificationKey decoded = Groth16Codec.DecodeVerificationKey(encoded);

        decoded.IC.Should().HaveCount(1);
        decoded.IC[0].Should().Equal(g1);
    }

    [Fact]
    public void VerificationKeyCodec_TruncatedICData_Throws()
    {
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;

        // Create a VK with 2 IC entries and encode it
        var vk = new VerificationKey
        {
            AlphaG1 = g1,
            BetaG2 = g2,
            GammaG2 = g2,
            DeltaG2 = g2,
            IC = new[] { g1, g1 },
        };

        byte[] encoded = Groth16Codec.EncodeVerificationKey(vk);

        // Truncate the last IC entry (remove last 48 bytes)
        var truncated = encoded.AsSpan(0, encoded.Length - 24).ToArray();

        var act = () => Groth16Codec.DecodeVerificationKey(truncated);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ProofCodec_WithIdentityPoints_RoundTrips()
    {
        var identityG1 = new byte[PairingEngine.G1CompressedSize];
        identityG1[0] = 0xC0;

        var g2 = PairingEngine.G2Generator;

        var proof = new Groth16Proof
        {
            A = identityG1,
            B = g2,
            C = identityG1,
        };

        byte[] encoded = Groth16Codec.EncodeProof(proof);
        Groth16Proof decoded = Groth16Codec.DecodeProof(encoded);

        decoded.A.Should().Equal(identityG1);
        decoded.C.Should().Equal(identityG1);
        decoded.B.Should().Equal(g2);
    }

    [Fact]
    public void ProofCodec_EncodeSize_Is192()
    {
        // 48 (A/G1) + 96 (B/G2) + 48 (C/G1) = 192
        Groth16Codec.ProofSize.Should().Be(192);
    }

    [Fact]
    public void ProofCodec_DifferentProofs_EncodeDifferently()
    {
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;
        var negG1 = PairingEngine.NegG1(g1);

        var s2 = new byte[32]; s2[31] = 2;
        var twoG1 = PairingEngine.ScalarMultG1(g1, s2);

        var proof1 = new Groth16Proof { A = g1, B = g2, C = negG1 };
        var proof2 = new Groth16Proof { A = twoG1, B = g2, C = negG1 };

        var encoded1 = Groth16Codec.EncodeProof(proof1);
        var encoded2 = Groth16Codec.EncodeProof(proof2);

        encoded1.Should().NotEqual(encoded2);
    }
}

public class Groth16VerifierTests
{
    /// <summary>
    /// Helper to build a trivially valid Groth16 proof and verification key.
    ///
    /// Construction:
    ///   alpha = G1, beta = G2, gamma = G2, delta = G2
    ///   IC = [G1]  (single element, zero public inputs)
    ///   vk_x = IC[0] = G1
    ///
    /// The verifier checks:
    ///   e(A, B) * e(-alpha, beta) * e(-vk_x, gamma) * e(-C, delta) == 1_GT
    ///
    /// Setting A = G1, B = G2:
    ///   e(G1, G2) * e(-G1, G2) * e(-G1, G2) * e(-C, G2) == 1_GT
    ///   e(-G1, G2) * e(-C, G2) == 1_GT
    ///   e(-(G1 + C), G2) == 1_GT
    ///   G1 + C = identity  =>  C = -G1
    /// </summary>
    private static (VerificationKey vk, Groth16Proof proof) BuildValidTestVector()
    {
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;
        var negG1 = PairingEngine.NegG1(g1);

        var vk = new VerificationKey
        {
            AlphaG1 = g1,
            BetaG2 = g2,
            GammaG2 = g2,
            DeltaG2 = g2,
            IC = new[] { g1 },
        };

        var proof = new Groth16Proof
        {
            A = g1,
            B = g2,
            C = negG1,
        };

        return (vk, proof);
    }

    [Fact]
    public void Verify_ValidProof_ReturnsTrue()
    {
        var (vk, proof) = BuildValidTestVector();
        var publicInputs = Array.Empty<byte[]>();

        bool result = Groth16Verifier.Verify(vk, proof, publicInputs);

        result.Should().BeTrue("the proof was mathematically constructed to satisfy the pairing equation");
    }

    [Fact]
    public void Verify_NullVk_ReturnsFalse()
    {
        var (_, proof) = BuildValidTestVector();
        Groth16Verifier.Verify(null!, proof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_NullProof_ReturnsFalse()
    {
        var (vk, _) = BuildValidTestVector();
        Groth16Verifier.Verify(vk, null!, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_NullPublicInputs_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();
        Groth16Verifier.Verify(vk, proof, null!).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongPublicInputCount_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();

        // IC has 1 element, so we need 0 public inputs.
        // Provide 1 instead -- should fail.
        var wrongInputs = new byte[][] { new byte[32] };

        Groth16Verifier.Verify(vk, proof, wrongInputs).Should().BeFalse();
    }

    [Fact]
    public void Verify_TamperedProofA_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();
        var publicInputs = Array.Empty<byte[]>();

        // Confirm the original proof is valid first.
        Groth16Verifier.Verify(vk, proof, publicInputs).Should().BeTrue();

        // Tamper with A by using a different G1 point (2*G1).
        var scalar2 = new byte[32];
        scalar2[31] = 2;
        var tamperedA = PairingEngine.ScalarMultG1(PairingEngine.G1Generator, scalar2);

        var tamperedProof = new Groth16Proof
        {
            A = tamperedA,
            B = proof.B,
            C = proof.C,
        };

        Groth16Verifier.Verify(vk, tamperedProof, publicInputs).Should().BeFalse();
    }

    [Fact]
    public void Verify_TamperedProofC_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();
        var publicInputs = Array.Empty<byte[]>();

        // Tamper with C by using the generator instead of -G1.
        var tamperedProof = new Groth16Proof
        {
            A = proof.A,
            B = proof.B,
            C = PairingEngine.G1Generator,
        };

        Groth16Verifier.Verify(vk, tamperedProof, publicInputs).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongSizeProofA_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();

        // A is supposed to be 48 bytes; provide 47.
        var badProof = new Groth16Proof
        {
            A = new byte[47],
            B = proof.B,
            C = proof.C,
        };

        Groth16Verifier.Verify(vk, badProof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongSizeProofB_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();

        // B is supposed to be 96 bytes; provide 48.
        var badProof = new Groth16Proof
        {
            A = proof.A,
            B = new byte[48],
            C = proof.C,
        };

        Groth16Verifier.Verify(vk, badProof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongSizeProofC_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();

        // C is supposed to be 48 bytes; provide 0.
        var badProof = new Groth16Proof
        {
            A = proof.A,
            B = proof.B,
            C = Array.Empty<byte>(),
        };

        Groth16Verifier.Verify(vk, badProof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongSizeAlphaG1_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();

        var badVk = new VerificationKey
        {
            AlphaG1 = new byte[47], // wrong size
            BetaG2 = vk.BetaG2,
            GammaG2 = vk.GammaG2,
            DeltaG2 = vk.DeltaG2,
            IC = vk.IC,
        };

        Groth16Verifier.Verify(badVk, proof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongSizeBetaG2_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();

        var badVk = new VerificationKey
        {
            AlphaG1 = vk.AlphaG1,
            BetaG2 = new byte[48], // wrong size (should be 96)
            GammaG2 = vk.GammaG2,
            DeltaG2 = vk.DeltaG2,
            IC = vk.IC,
        };

        Groth16Verifier.Verify(badVk, proof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongSizeGammaG2_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();

        var badVk = new VerificationKey
        {
            AlphaG1 = vk.AlphaG1,
            BetaG2 = vk.BetaG2,
            GammaG2 = new byte[48], // wrong size (should be 96)
            DeltaG2 = vk.DeltaG2,
            IC = vk.IC,
        };

        Groth16Verifier.Verify(badVk, proof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongSizeDeltaG2_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();

        var badVk = new VerificationKey
        {
            AlphaG1 = vk.AlphaG1,
            BetaG2 = vk.BetaG2,
            GammaG2 = vk.GammaG2,
            DeltaG2 = new byte[48], // wrong size (should be 96)
            IC = vk.IC,
        };

        Groth16Verifier.Verify(badVk, proof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_TamperedVkAlpha_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();
        var publicInputs = Array.Empty<byte[]>();

        // Tamper with AlphaG1 by using 2*G1 instead of G1
        var scalar2 = new byte[32]; scalar2[31] = 2;
        var tamperedAlpha = PairingEngine.ScalarMultG1(PairingEngine.G1Generator, scalar2);

        var tamperedVk = new VerificationKey
        {
            AlphaG1 = tamperedAlpha,
            BetaG2 = vk.BetaG2,
            GammaG2 = vk.GammaG2,
            DeltaG2 = vk.DeltaG2,
            IC = vk.IC,
        };

        Groth16Verifier.Verify(tamperedVk, proof, publicInputs).Should().BeFalse();
    }

    [Fact]
    public void Verify_TamperedProofB_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();
        var publicInputs = Array.Empty<byte[]>();

        // Tamper with B by using -G2 instead of G2
        var negG2 = PairingEngine.NegG2(PairingEngine.G2Generator);

        var tamperedProof = new Groth16Proof
        {
            A = proof.A,
            B = negG2,
            C = proof.C,
        };

        Groth16Verifier.Verify(vk, tamperedProof, publicInputs).Should().BeFalse();
    }

    // ── C-02: Identity element rejection ────────────────────────────────────

    [Fact]
    public void Verify_IdentityA_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();
        var identityG1 = new byte[PairingEngine.G1CompressedSize];
        identityG1[0] = 0xC0;

        var badProof = new Groth16Proof { A = identityG1, B = proof.B, C = proof.C };
        Groth16Verifier.Verify(vk, badProof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_IdentityB_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();
        var identityG2 = new byte[PairingEngine.G2CompressedSize];
        identityG2[0] = 0xC0;

        var badProof = new Groth16Proof { A = proof.A, B = identityG2, C = proof.C };
        Groth16Verifier.Verify(vk, badProof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void Verify_IdentityC_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();
        var identityG1 = new byte[PairingEngine.G1CompressedSize];
        identityG1[0] = 0xC0;

        var badProof = new Groth16Proof { A = proof.A, B = proof.B, C = identityG1 };
        Groth16Verifier.Verify(vk, badProof, Array.Empty<byte[]>()).Should().BeFalse();
    }

    // ── C-01: Subgroup validation ───────────────────────────────────────────

    [Fact]
    public void Verify_ValidProofPoints_PassSubgroupCheck()
    {
        // Generators are always in the subgroup, so the valid test vector should pass
        var (vk, proof) = BuildValidTestVector();
        Groth16Verifier.Verify(vk, proof, Array.Empty<byte[]>()).Should().BeTrue();
    }

    [Fact]
    public void Verify_InvalidG2BetaInVk_ReturnsFalse()
    {
        var (vk, proof) = BuildValidTestVector();
        // Create a 96-byte G2 point that's not a valid curve point
        var badG2 = new byte[PairingEngine.G2CompressedSize];
        badG2[0] = 0x80; // compression flag
        badG2[1] = 0x01; // invalid coordinates

        var badVk = new VerificationKey
        {
            AlphaG1 = vk.AlphaG1,
            BetaG2 = badG2,
            GammaG2 = vk.GammaG2,
            DeltaG2 = vk.DeltaG2,
            IC = vk.IC,
        };

        Groth16Verifier.Verify(badVk, proof, Array.Empty<byte[]>()).Should().BeFalse();
    }
}
