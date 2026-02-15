using System.Text;
using Basalt.Confidentiality.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Confidentiality.Tests;

public class PairingEngineTests
{
    [Fact]
    public void G1Generator_Returns48Bytes()
    {
        var g1 = PairingEngine.G1Generator;
        g1.Should().HaveCount(PairingEngine.G1CompressedSize);
    }

    [Fact]
    public void G2Generator_Returns96Bytes()
    {
        var g2 = PairingEngine.G2Generator;
        g2.Should().HaveCount(PairingEngine.G2CompressedSize);
    }

    [Fact]
    public void G1Generator_IsNotIdentity()
    {
        var g1 = PairingEngine.G1Generator;
        PairingEngine.IsG1Identity(g1).Should().BeFalse();
    }

    [Fact]
    public void ScalarMultG1_ByOne_ReturnsGenerator()
    {
        var g1 = PairingEngine.G1Generator;
        var one = new byte[32];
        one[31] = 1;

        var result = PairingEngine.ScalarMultG1(g1, one);
        result.Should().Equal(g1);
    }

    [Fact]
    public void ScalarMultG1_ByZero_ReturnsIdentity()
    {
        var g1 = PairingEngine.G1Generator;
        var zero = new byte[32];

        var result = PairingEngine.ScalarMultG1(g1, zero);
        PairingEngine.IsG1Identity(result).Should().BeTrue();
    }

    [Fact]
    public void AddG1_Commutative()
    {
        var g1 = PairingEngine.G1Generator;

        var scalarA = new byte[32];
        scalarA[31] = 5;
        var scalarB = new byte[32];
        scalarB[31] = 7;

        var a = PairingEngine.ScalarMultG1(g1, scalarA);
        var b = PairingEngine.ScalarMultG1(g1, scalarB);

        var ab = PairingEngine.AddG1(a, b);
        var ba = PairingEngine.AddG1(b, a);

        ab.Should().Equal(ba);
    }

    [Fact]
    public void NegG1_AddToSelf_IsIdentity()
    {
        var g1 = PairingEngine.G1Generator;

        var scalar = new byte[32];
        scalar[31] = 42;
        var p = PairingEngine.ScalarMultG1(g1, scalar);

        var negP = PairingEngine.NegG1(p);
        var sum = PairingEngine.AddG1(p, negP);

        PairingEngine.IsG1Identity(sum).Should().BeTrue();
    }

    [Fact]
    public void HashToG1_Deterministic()
    {
        var message = Encoding.UTF8.GetBytes("test message");
        var dst = "BASALT_TEST_DST";

        var h1 = PairingEngine.HashToG1(message, dst);
        var h2 = PairingEngine.HashToG1(message, dst);

        h1.Should().Equal(h2);
    }

    [Fact]
    public void HashToG1_DifferentMessages_DifferentPoints()
    {
        var dst = "BASALT_TEST_DST";

        var h1 = PairingEngine.HashToG1(Encoding.UTF8.GetBytes("message A"), dst);
        var h2 = PairingEngine.HashToG1(Encoding.UTF8.GetBytes("message B"), dst);

        h1.Should().NotEqual(h2);
    }

    [Fact]
    public void PairingCheck_BilinearityProperty()
    {
        // Bilinearity: e(a*G1, G2) should equal e(G1, G2)^a.
        // Since we cannot do scalar mult in G2, we verify the simpler property
        // that equal pairings are detected as equal and unequal ones are not.
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;

        // e(G1, G2) == e(G1, G2) must hold
        PairingEngine.PairingCheck(g1, g2, g1, g2).Should().BeTrue();

        // e(2*G1, G2) != e(G1, G2) demonstrates non-trivial pairing behavior
        var two = new byte[32];
        two[31] = 2;
        var twoG1 = PairingEngine.ScalarMultG1(g1, two);

        PairingEngine.PairingCheck(twoG1, g2, g1, g2).Should().BeFalse();
    }

    [Fact]
    public void PairingCheck_EqualPairings_ReturnsTrue()
    {
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;

        PairingEngine.PairingCheck(g1, g2, g1, g2).Should().BeTrue();
    }

    [Fact]
    public void PairingCheck_UnequalPairings_ReturnsFalse()
    {
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;

        var two = new byte[32];
        two[31] = 2;
        var twoG1 = PairingEngine.ScalarMultG1(g1, two);

        PairingEngine.PairingCheck(twoG1, g2, g1, g2).Should().BeFalse();
    }

    [Fact]
    public void IsG1Identity_TrueForIdentity()
    {
        var identity = new byte[PairingEngine.G1CompressedSize];
        identity[0] = 0xC0;

        PairingEngine.IsG1Identity(identity).Should().BeTrue();
    }

    [Fact]
    public void IsG1Identity_FalseForGenerator()
    {
        var g1 = PairingEngine.G1Generator;
        PairingEngine.IsG1Identity(g1).Should().BeFalse();
    }

    [Fact]
    public void NegG2_Returns96Bytes()
    {
        var g2 = PairingEngine.G2Generator;
        var negG2 = PairingEngine.NegG2(g2);

        negG2.Should().HaveCount(PairingEngine.G2CompressedSize);
        negG2.Should().NotEqual(g2);
    }

    // ── Argument validation ─────────────────────────────────────────────────

    [Fact]
    public void ScalarMultG1_WrongPointSize_Throws()
    {
        var badPoint = new byte[47]; // should be 48
        var scalar = new byte[32];
        scalar[31] = 1;

        var act = () => PairingEngine.ScalarMultG1(badPoint, scalar);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ScalarMultG1_WrongScalarSize_Throws()
    {
        var g1 = PairingEngine.G1Generator;
        var badScalar = new byte[31]; // should be 32

        var act = () => PairingEngine.ScalarMultG1(g1, badScalar);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddG1_WrongSizeA_Throws()
    {
        var g1 = PairingEngine.G1Generator;
        var badA = new byte[47];

        var act = () => PairingEngine.AddG1(badA, g1);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddG1_WrongSizeB_Throws()
    {
        var g1 = PairingEngine.G1Generator;
        var badB = new byte[49];

        var act = () => PairingEngine.AddG1(g1, badB);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NegG1_WrongSize_Throws()
    {
        var badPoint = new byte[10];

        var act = () => PairingEngine.NegG1(badPoint);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NegG2_WrongSize_Throws()
    {
        var badPoint = new byte[48]; // should be 96

        var act = () => PairingEngine.NegG2(badPoint);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void IsG1Identity_WrongSize_Throws()
    {
        var badPoint = new byte[10];

        var act = () => PairingEngine.IsG1Identity(badPoint);
        act.Should().Throw<ArgumentException>();
    }

    // ── Algebraic properties ────────────────────────────────────────────────

    [Fact]
    public void NegG1_DoubleNegation_ReturnsOriginal()
    {
        var g1 = PairingEngine.G1Generator;
        var scalar = new byte[32];
        scalar[31] = 13;
        var p = PairingEngine.ScalarMultG1(g1, scalar);

        var negP = PairingEngine.NegG1(p);
        var doubleNeg = PairingEngine.NegG1(negP);

        doubleNeg.Should().Equal(p);
    }

    [Fact]
    public void AddG1_Associative()
    {
        var g1 = PairingEngine.G1Generator;

        var s3 = new byte[32]; s3[31] = 3;
        var s5 = new byte[32]; s5[31] = 5;
        var s7 = new byte[32]; s7[31] = 7;

        var a = PairingEngine.ScalarMultG1(g1, s3);
        var b = PairingEngine.ScalarMultG1(g1, s5);
        var c = PairingEngine.ScalarMultG1(g1, s7);

        // (a + b) + c
        var ab = PairingEngine.AddG1(a, b);
        var ab_c = PairingEngine.AddG1(ab, c);

        // a + (b + c)
        var bc = PairingEngine.AddG1(b, c);
        var a_bc = PairingEngine.AddG1(a, bc);

        ab_c.Should().Equal(a_bc);
    }

    [Fact]
    public void ScalarMultG1_DistributiveProperty()
    {
        // (a + b) * G == a*G + b*G
        var g1 = PairingEngine.G1Generator;

        var sA = new byte[32]; sA[31] = 3;
        var sB = new byte[32]; sB[31] = 5;
        var sSum = new byte[32]; sSum[31] = 8; // 3 + 5

        var aG = PairingEngine.ScalarMultG1(g1, sA);
        var bG = PairingEngine.ScalarMultG1(g1, sB);
        var sumG = PairingEngine.AddG1(aG, bG);

        var directSumG = PairingEngine.ScalarMultG1(g1, sSum);

        sumG.Should().Equal(directSumG);
    }

    [Fact]
    public void AddG1_IdentityIsNeutral()
    {
        var g1 = PairingEngine.G1Generator;
        var scalar = new byte[32]; scalar[31] = 42;
        var p = PairingEngine.ScalarMultG1(g1, scalar);

        var identity = new byte[PairingEngine.G1CompressedSize];
        identity[0] = 0xC0;

        var sum = PairingEngine.AddG1(p, identity);
        sum.Should().Equal(p);

        // Also test identity + p
        var sum2 = PairingEngine.AddG1(identity, p);
        sum2.Should().Equal(p);
    }

    [Fact]
    public void HashToG1_EmptyMessage_ProducesValidPoint()
    {
        var dst = "BASALT_TEST_DST";
        var empty = Array.Empty<byte>();

        var result = PairingEngine.HashToG1(empty, dst);

        result.Should().HaveCount(PairingEngine.G1CompressedSize);
        PairingEngine.IsG1Identity(result).Should().BeFalse();
    }

    [Fact]
    public void HashToG1_DifferentDSTs_DifferentPoints()
    {
        var message = Encoding.UTF8.GetBytes("same message");

        var h1 = PairingEngine.HashToG1(message, "DST_ONE");
        var h2 = PairingEngine.HashToG1(message, "DST_TWO");

        h1.Should().NotEqual(h2);
    }

    [Fact]
    public void NegG2_DoubleNegation_ReturnsOriginal()
    {
        var g2 = PairingEngine.G2Generator;

        var negG2 = PairingEngine.NegG2(g2);
        var doubleNeg = PairingEngine.NegG2(negG2);

        doubleNeg.Should().Equal(g2);
    }

    [Fact]
    public void ScalarMultG1_LargerScalar_ProducesDifferentPoint()
    {
        var g1 = PairingEngine.G1Generator;

        var s100 = new byte[32]; s100[31] = 100;
        var s200 = new byte[32]; s200[31] = 200;

        var p100 = PairingEngine.ScalarMultG1(g1, s100);
        var p200 = PairingEngine.ScalarMultG1(g1, s200);

        p100.Should().NotEqual(p200);
    }

    [Fact]
    public void ComputeMillerLoop_WrongG1Size_Throws()
    {
        var badG1 = new byte[47];
        var g2 = PairingEngine.G2Generator;

        Action act = () => PairingEngine.ComputeMillerLoop(badG1, g2);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputeMillerLoop_WrongG2Size_Throws()
    {
        var g1 = PairingEngine.G1Generator;
        var badG2 = new byte[95];

        Action act = () => PairingEngine.ComputeMillerLoop(g1, badG2);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void G1Generator_ConsistentAcrossInvocations()
    {
        var g1a = PairingEngine.G1Generator;
        var g1b = PairingEngine.G1Generator;

        g1a.Should().Equal(g1b);
        // Defensive copies should not be the same reference
        g1a.Should().NotBeSameAs(g1b);
    }

    [Fact]
    public void G2Generator_ConsistentAcrossInvocations()
    {
        var g2a = PairingEngine.G2Generator;
        var g2b = PairingEngine.G2Generator;

        g2a.Should().Equal(g2b);
        g2a.Should().NotBeSameAs(g2b);
    }
}
