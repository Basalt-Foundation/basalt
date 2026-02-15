using Basalt.Confidentiality.Crypto;
using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Confidentiality.Tests;

public class PedersenCommitmentTests
{
    [Fact]
    public void Commit_Returns48Bytes()
    {
        UInt256 value = 100;
        var r = new byte[32];
        r[31] = 42;

        var commitment = PedersenCommitment.Commit(value, r);
        commitment.Should().HaveCount(PairingEngine.G1CompressedSize);
    }

    [Fact]
    public void Commit_DifferentBlindingFactors_DifferentCommitments()
    {
        UInt256 value = 100;

        var r1 = new byte[32];
        r1[31] = 10;
        var r2 = new byte[32];
        r2[31] = 20;

        var c1 = PedersenCommitment.Commit(value, r1);
        var c2 = PedersenCommitment.Commit(value, r2);

        c1.Should().NotEqual(c2);
    }

    [Fact]
    public void Open_ValidCommitment_ReturnsTrue()
    {
        UInt256 value = 42;
        var r = new byte[32];
        r[31] = 7;

        var commitment = PedersenCommitment.Commit(value, r);
        PedersenCommitment.Open(commitment, value, r).Should().BeTrue();
    }

    [Fact]
    public void Open_WrongValue_ReturnsFalse()
    {
        UInt256 value = 5;
        var r = new byte[32];
        r[31] = 7;

        var commitment = PedersenCommitment.Commit(value, r);
        UInt256 wrongValue = 6;
        PedersenCommitment.Open(commitment, wrongValue, r).Should().BeFalse();
    }

    [Fact]
    public void Open_WrongBlinding_ReturnsFalse()
    {
        UInt256 value = 5;
        var r1 = new byte[32];
        r1[31] = 7;
        var r2 = new byte[32];
        r2[31] = 8;

        var commitment = PedersenCommitment.Commit(value, r1);
        PedersenCommitment.Open(commitment, value, r2).Should().BeFalse();
    }

    [Fact]
    public void HomomorphicAddition()
    {
        UInt256 a = 5;
        UInt256 b = 10;

        var r1 = new byte[32];
        r1[31] = 10;
        var r2 = new byte[32];
        r2[31] = 20;

        var c1 = PedersenCommitment.Commit(a, r1);
        var c2 = PedersenCommitment.Commit(b, r2);

        var cSum = PedersenCommitment.AddCommitments(new[] { c1, c2 });

        // The sum commitment should open to (a + b) with blinding factor (r1 + r2)
        UInt256 valueSum = a + b; // 15
        var rSum = new byte[32];
        rSum[31] = 30; // 10 + 20

        PedersenCommitment.Open(cSum, valueSum, rSum).Should().BeTrue();
    }

    [Fact]
    public void SubtractCommitments_BalanceProof()
    {
        UInt256 value = 50;
        var r = new byte[32];
        r[31] = 25;

        var c = PedersenCommitment.Commit(value, r);

        // Subtracting a commitment from itself yields a commitment to (0, 0)
        var diff = PedersenCommitment.SubtractCommitments(c, c);

        var zeroBlinding = new byte[32];
        PedersenCommitment.Open(diff, UInt256.Zero, zeroBlinding).Should().BeTrue();
    }

    [Fact]
    public void HGenerator_Is48Bytes()
    {
        var h = PedersenCommitment.HGenerator;
        h.Should().HaveCount(PairingEngine.G1CompressedSize);
    }

    [Fact]
    public void HGenerator_IsNotG1Generator()
    {
        var h = PedersenCommitment.HGenerator;
        var g = PairingEngine.G1Generator;

        h.Should().NotEqual(g);
    }

    [Fact]
    public void Commit_ZeroValue_OnlyUsesBlinding()
    {
        // When value == 0, the commitment should be C = 0*G + r*H = r*H
        var r = new byte[32];
        r[31] = 42;

        var commitment = PedersenCommitment.Commit(UInt256.Zero, r);

        // Manually compute r*H to compare
        var h = PedersenCommitment.HGenerator;
        var rH = PairingEngine.ScalarMultG1(h, r);

        // The identity + r*H should equal r*H, so the commitment should equal
        // AddG1(identity, r*H). Since identity is the additive neutral element,
        // the result should match r*H.
        var identity = new byte[PairingEngine.G1CompressedSize];
        identity[0] = 0xC0;
        var expected = PairingEngine.AddG1(identity, rH);

        commitment.Should().Equal(expected);
    }

    // ── Argument validation ─────────────────────────────────────────────────

    [Fact]
    public void Commit_WrongSizeBlindingFactor_Throws()
    {
        var badBlinding = new byte[31]; // should be 32
        UInt256 value = 10;

        var act = () => PedersenCommitment.Commit(value, badBlinding);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Commit_EmptyBlindingFactor_Throws()
    {
        var empty = Array.Empty<byte>();
        UInt256 value = 10;

        var act = () => PedersenCommitment.Commit(value, empty);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Open_WrongSizeCommitment_ReturnsFalse()
    {
        UInt256 value = 42;
        var r = new byte[32];
        r[31] = 7;

        // Commitment that is too short
        var shortCommitment = new byte[47];
        PedersenCommitment.Open(shortCommitment, value, r).Should().BeFalse();

        // Commitment that is too long
        var longCommitment = new byte[49];
        PedersenCommitment.Open(longCommitment, value, r).Should().BeFalse();
    }

    [Fact]
    public void Open_EmptyCommitment_ReturnsFalse()
    {
        UInt256 value = 42;
        var r = new byte[32];
        r[31] = 7;

        PedersenCommitment.Open(Array.Empty<byte>(), value, r).Should().BeFalse();
    }

    // ── Homomorphic subtraction ─────────────────────────────────────────────

    [Fact]
    public void SubtractCommitments_DifferentValues_OpensCorrectly()
    {
        // C1 = Commit(30, r1=10), C2 = Commit(20, r2=6)
        // C1 - C2 should open to (30-20=10, r1-r2=10-6=4)
        UInt256 v1 = 30;
        UInt256 v2 = 20;
        var r1 = new byte[32]; r1[31] = 10;
        var r2 = new byte[32]; r2[31] = 6;

        var c1 = PedersenCommitment.Commit(v1, r1);
        var c2 = PedersenCommitment.Commit(v2, r2);

        var diff = PedersenCommitment.SubtractCommitments(c1, c2);

        UInt256 expectedValue = v1 - v2; // 10
        var expectedBlinding = new byte[32]; expectedBlinding[31] = 4; // 10 - 6

        PedersenCommitment.Open(diff, expectedValue, expectedBlinding).Should().BeTrue();
    }

    [Fact]
    public void SubtractCommitments_IdentityMinusPoint_EqualsNegation()
    {
        UInt256 value = 25;
        var r = new byte[32]; r[31] = 15;

        var c = PedersenCommitment.Commit(value, r);

        // Identity commitment: Commit(0, 0)
        var zeroBlinding = new byte[32];
        var identity = PedersenCommitment.Commit(UInt256.Zero, zeroBlinding);

        var diff = PedersenCommitment.SubtractCommitments(identity, c);
        // diff = Commit(0, 0) - Commit(25, 15) = Commit(-25, -15)

        // Subtracting from identity and adding back the original should yield identity
        var sum = PairingEngine.AddG1(diff, c);
        PedersenCommitment.Open(sum, UInt256.Zero, zeroBlinding).Should().BeTrue();
    }

    // ── AddCommitments edge cases ───────────────────────────────────────────

    [Fact]
    public void AddCommitments_SingleCommitment_ReturnsClone()
    {
        UInt256 value = 77;
        var r = new byte[32]; r[31] = 33;

        var c = PedersenCommitment.Commit(value, r);
        var result = PedersenCommitment.AddCommitments(new[] { c });

        result.Should().Equal(c);
        // Should be a clone, not same reference
        result.Should().NotBeSameAs(c);
    }

    [Fact]
    public void AddCommitments_EmptyArray_Throws()
    {
        var act = () => PedersenCommitment.AddCommitments(Array.Empty<byte[]>());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddCommitments_ThreeCommitments_OpensCorrectly()
    {
        UInt256 v1 = 10, v2 = 20, v3 = 30;
        var r1 = new byte[32]; r1[31] = 5;
        var r2 = new byte[32]; r2[31] = 7;
        var r3 = new byte[32]; r3[31] = 3;

        var c1 = PedersenCommitment.Commit(v1, r1);
        var c2 = PedersenCommitment.Commit(v2, r2);
        var c3 = PedersenCommitment.Commit(v3, r3);

        var sum = PedersenCommitment.AddCommitments(new[] { c1, c2, c3 });

        UInt256 expectedValue = v1 + v2 + v3; // 60
        var expectedBlinding = new byte[32]; expectedBlinding[31] = 15; // 5 + 7 + 3

        PedersenCommitment.Open(sum, expectedValue, expectedBlinding).Should().BeTrue();
    }

    // ── Commitment determinism ──────────────────────────────────────────────

    [Fact]
    public void Commit_SameInputs_SameOutput()
    {
        UInt256 value = 123;
        var r = new byte[32]; r[31] = 99;

        var c1 = PedersenCommitment.Commit(value, r);
        var c2 = PedersenCommitment.Commit(value, r);

        c1.Should().Equal(c2);
    }

    [Fact]
    public void Commit_ZeroBlinding_DifferentFromNonZeroBlinding()
    {
        UInt256 value = 50;
        var zeroBlinding = new byte[32];
        var nonZeroBlinding = new byte[32]; nonZeroBlinding[31] = 1;

        var cZero = PedersenCommitment.Commit(value, zeroBlinding);
        var cOne = PedersenCommitment.Commit(value, nonZeroBlinding);

        cZero.Should().NotEqual(cOne);
    }

    [Fact]
    public void HGenerator_IsNotIdentity()
    {
        var h = PedersenCommitment.HGenerator;
        PairingEngine.IsG1Identity(h).Should().BeFalse();
    }

    [Fact]
    public void HGenerator_ConsistentAcrossInvocations()
    {
        var h1 = PedersenCommitment.HGenerator;
        var h2 = PedersenCommitment.HGenerator;

        h1.Should().Equal(h2);
        h1.Should().NotBeSameAs(h2); // defensive copies
    }
}
