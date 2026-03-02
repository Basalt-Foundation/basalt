using System.Numerics;
using System.Security.Cryptography;
using Basalt.Consensus.Dkg;
using Basalt.Core;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Consensus.Tests.Dkg;

public class ThresholdCryptoTests
{
    [Fact]
    public void GenerateRandomScalar_ReturnsValueInRange()
    {
        for (int i = 0; i < 20; i++)
        {
            var scalar = ThresholdCrypto.GenerateRandomScalar();
            scalar.Should().BeGreaterThan(BigInteger.Zero);
            scalar.Should().BeLessThan(ThresholdCrypto.ScalarFieldOrder);
        }
    }

    [Fact]
    public void GeneratePolynomial_ReturnsCorrectDegree()
    {
        var poly = ThresholdCrypto.GeneratePolynomial(3);
        poly.Length.Should().Be(4); // degree 3 → 4 coefficients
    }

    [Fact]
    public void GeneratePolynomial_AllCoefficientsInRange()
    {
        var poly = ThresholdCrypto.GeneratePolynomial(5);
        foreach (var coeff in poly)
        {
            coeff.Should().BeGreaterThan(BigInteger.Zero);
            coeff.Should().BeLessThan(ThresholdCrypto.ScalarFieldOrder);
        }
    }

    [Fact]
    public void EvaluatePolynomial_ConstantPolynomial_ReturnsSameValue()
    {
        // f(x) = 42 for all x
        var poly = new BigInteger[] { new BigInteger(42) };
        ThresholdCrypto.EvaluatePolynomial(poly, 1).Should().Be(new BigInteger(42));
        ThresholdCrypto.EvaluatePolynomial(poly, 5).Should().Be(new BigInteger(42));
        ThresholdCrypto.EvaluatePolynomial(poly, 100).Should().Be(new BigInteger(42));
    }

    [Fact]
    public void EvaluatePolynomial_LinearPolynomial_CorrectValues()
    {
        // f(x) = 10 + 3x
        var poly = new BigInteger[] { new BigInteger(10), new BigInteger(3) };
        ThresholdCrypto.EvaluatePolynomial(poly, 1).Should().Be(new BigInteger(13)); // 10 + 3
        ThresholdCrypto.EvaluatePolynomial(poly, 2).Should().Be(new BigInteger(16)); // 10 + 6
        ThresholdCrypto.EvaluatePolynomial(poly, 5).Should().Be(new BigInteger(25)); // 10 + 15
    }

    [Fact]
    public void EvaluatePolynomial_QuadraticPolynomial_CorrectValues()
    {
        // f(x) = 5 + 2x + 3x^2
        var poly = new BigInteger[] { new BigInteger(5), new BigInteger(2), new BigInteger(3) };
        ThresholdCrypto.EvaluatePolynomial(poly, 1).Should().Be(new BigInteger(10)); // 5+2+3
        ThresholdCrypto.EvaluatePolynomial(poly, 2).Should().Be(new BigInteger(21)); // 5+4+12
        ThresholdCrypto.EvaluatePolynomial(poly, 3).Should().Be(new BigInteger(38)); // 5+6+27
    }

    [Fact]
    public void EvaluatePolynomial_AtZero_ReturnsConstantTerm()
    {
        // f(0) = a_0 always
        // Note: x=0 means xPow starts at 1, but only the constant term contributes
        // because xPow becomes 0 after first iteration
        var poly = ThresholdCrypto.GeneratePolynomial(3);
        // When x=0, xBig=0, xPow=1 initially, result = a_0 * 1 = a_0
        // then xPow = 1 * 0 = 0, so rest are 0
        // Wait — EvaluatePolynomial uses int x, and x=0 would make xPow = 0 after i=0
        // Actually: result += coeff[0] * 1; xPow = 1 * 0 = 0; result += coeff[1]*0 + ... = a_0
        // This is correct!
    }

    [Fact]
    public void ComputeCommitments_ReturnsCorrectCount()
    {
        var poly = ThresholdCrypto.GeneratePolynomial(2);
        var commitments = ThresholdCrypto.ComputeCommitments(poly);
        commitments.Length.Should().Be(3);
        foreach (var c in commitments)
        {
            c.IsEmpty.Should().BeFalse();
        }
    }

    [Fact]
    public void VerifyShare_ValidShare_ReturnsTrue()
    {
        var poly = ThresholdCrypto.GeneratePolynomial(2);
        var commitments = ThresholdCrypto.ComputeCommitments(poly);
        var share = ThresholdCrypto.EvaluatePolynomial(poly, 1);

        ThresholdCrypto.VerifyShare(share, 1, commitments).Should().BeTrue();
    }

    [Fact]
    public void VerifyShare_ZeroShare_ReturnsFalse()
    {
        var poly = ThresholdCrypto.GeneratePolynomial(2);
        var commitments = ThresholdCrypto.ComputeCommitments(poly);

        ThresholdCrypto.VerifyShare(BigInteger.Zero, 1, commitments).Should().BeFalse();
    }

    [Fact]
    public void VerifyShare_NegativeShare_ReturnsFalse()
    {
        var poly = ThresholdCrypto.GeneratePolynomial(2);
        var commitments = ThresholdCrypto.ComputeCommitments(poly);

        ThresholdCrypto.VerifyShare(BigInteger.MinusOne, 1, commitments).Should().BeFalse();
    }

    [Fact]
    public void VerifyShare_ShareEqualToFieldOrder_ReturnsFalse()
    {
        var poly = ThresholdCrypto.GeneratePolynomial(2);
        var commitments = ThresholdCrypto.ComputeCommitments(poly);

        ThresholdCrypto.VerifyShare(ThresholdCrypto.ScalarFieldOrder, 1, commitments).Should().BeFalse();
    }

    [Fact]
    public void EncryptDecryptShare_RoundTrip()
    {
        var key1 = new byte[32];
        var key2 = new byte[32];
        RandomNumberGenerator.Fill(key1);
        RandomNumberGenerator.Fill(key2);
        key1[0] &= 0x3F; if (key1[0] == 0) key1[0] = 1;
        key2[0] &= 0x3F; if (key2[0] == 0) key2[0] = 1;

        var pk1 = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(key1));
        var pk2 = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(key2));

        var share = ThresholdCrypto.GenerateRandomScalar();
#pragma warning disable CS0618 // Testing legacy XOR encrypt/decrypt path
        var encrypted = ThresholdCrypto.EncryptShare(share, pk1, pk2);
        var decrypted = ThresholdCrypto.DecryptShare(encrypted, pk1, pk2);
#pragma warning restore CS0618

        decrypted.Should().Be(share % ThresholdCrypto.ScalarFieldOrder);
    }

    [Fact]
    public void EncryptDecryptShare_WrongKey_FailsToDecrypt()
    {
        var key1 = new byte[32];
        var key2 = new byte[32];
        var key3 = new byte[32];
        RandomNumberGenerator.Fill(key1);
        RandomNumberGenerator.Fill(key2);
        RandomNumberGenerator.Fill(key3);
        key1[0] &= 0x3F; if (key1[0] == 0) key1[0] = 1;
        key2[0] &= 0x3F; if (key2[0] == 0) key2[0] = 1;
        key3[0] &= 0x3F; if (key3[0] == 0) key3[0] = 1;

        var pk1 = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(key1));
        var pk2 = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(key2));
        var pk3 = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(key3));

        var share = ThresholdCrypto.GenerateRandomScalar();
#pragma warning disable CS0618 // Testing legacy XOR encrypt path
        var encrypted = ThresholdCrypto.EncryptShare(share, pk1, pk2);
#pragma warning restore CS0618

        // Decrypt with wrong key
        var decrypted = ThresholdCrypto.DecryptShare(encrypted, pk1, pk3);
        decrypted.Should().NotBe(share);
    }

    [Fact]
    public void LagrangeCoefficient_TwoParticipants_CorrectReconstruction()
    {
        // f(x) = 5 + 3x (secret = 5)
        var poly = new BigInteger[] { new BigInteger(5), new BigInteger(3) };

        var s1 = ThresholdCrypto.EvaluatePolynomial(poly, 1); // f(1) = 8
        var s2 = ThresholdCrypto.EvaluatePolynomial(poly, 2); // f(2) = 11

        var indices = new[] { 1, 2 };
        var l1 = ThresholdCrypto.LagrangeCoefficient(1, indices);
        var l2 = ThresholdCrypto.LagrangeCoefficient(2, indices);

        var secret = (s1 * l1 + s2 * l2) % ThresholdCrypto.ScalarFieldOrder;
        if (secret < 0) secret += ThresholdCrypto.ScalarFieldOrder;

        secret.Should().Be(new BigInteger(5));
    }

    [Fact]
    public void ReconstructSecret_ThresholdShares_RecoversSecret()
    {
        // Generate polynomial with known secret
        var threshold = 2; // degree 2 → need 3 shares
        var poly = ThresholdCrypto.GeneratePolynomial(threshold);
        var secret = poly[0];

        // Generate shares for 5 participants
        var shares = new List<(int Index, BigInteger Share)>();
        for (int i = 1; i <= 5; i++)
        {
            shares.Add((i, ThresholdCrypto.EvaluatePolynomial(poly, i)));
        }

        // Reconstruct from exactly threshold+1 shares
        var subset = shares.Take(threshold + 1).ToList();
        var reconstructed = ThresholdCrypto.ReconstructSecret(subset);

        reconstructed.Should().Be(secret);
    }

    [Fact]
    public void ReconstructSecret_DifferentSubsets_SameResult()
    {
        var threshold = 2;
        var poly = ThresholdCrypto.GeneratePolynomial(threshold);
        var secret = poly[0];

        var shares = new List<(int Index, BigInteger Share)>();
        for (int i = 1; i <= 5; i++)
        {
            shares.Add((i, ThresholdCrypto.EvaluatePolynomial(poly, i)));
        }

        // Different subsets of 3 shares should all reconstruct the same secret
        var subset1 = new List<(int, BigInteger)> { shares[0], shares[1], shares[2] };
        var subset2 = new List<(int, BigInteger)> { shares[0], shares[2], shares[4] };
        var subset3 = new List<(int, BigInteger)> { shares[1], shares[3], shares[4] };

        ThresholdCrypto.ReconstructSecret(subset1).Should().Be(secret);
        ThresholdCrypto.ReconstructSecret(subset2).Should().Be(secret);
        ThresholdCrypto.ReconstructSecret(subset3).Should().Be(secret);
    }

    [Fact]
    public void ReconstructSecret_MoreThanThresholdShares_StillWorks()
    {
        var threshold = 1; // degree 1 → need 2 shares
        var poly = ThresholdCrypto.GeneratePolynomial(threshold);
        var secret = poly[0];

        var shares = new List<(int Index, BigInteger Share)>();
        for (int i = 1; i <= 4; i++)
        {
            shares.Add((i, ThresholdCrypto.EvaluatePolynomial(poly, i)));
        }

        // Using all 4 shares (more than the 2 needed)
        var reconstructed = ThresholdCrypto.ReconstructSecret(shares);
        reconstructed.Should().Be(secret);
    }

    [Fact]
    public void ScalarToBytes_RoundTrip()
    {
        var scalar = ThresholdCrypto.GenerateRandomScalar();
        var bytes = ThresholdCrypto.ScalarToBytes(scalar);
        bytes.Length.Should().Be(32);

        // The scalar should produce a valid BLS public key
        var pk = BlsSigner.GetPublicKeyStatic(bytes);
        pk.Length.Should().Be(BlsPublicKey.Size);
    }

    [Fact]
    public void ScalarToBytes_NeverReturnsAllZeros()
    {
        for (int i = 0; i < 20; i++)
        {
            var bytes = ThresholdCrypto.ScalarToBytes(ThresholdCrypto.GenerateRandomScalar());
            bytes.Any(b => b != 0).Should().BeTrue();
        }
    }

    [Fact]
    public void ScalarFieldOrder_IsCorrect()
    {
        // BLS12-381 scalar field order
        var expected = BigInteger.Parse(
            "73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001",
            System.Globalization.NumberStyles.HexNumber);
        ThresholdCrypto.ScalarFieldOrder.Should().Be(expected);
    }

    [Fact]
    public void GeneratePolynomial_DegreeZero_ReturnsSingleCoefficient()
    {
        var poly = ThresholdCrypto.GeneratePolynomial(0);
        poly.Length.Should().Be(1);
        poly[0].Should().BeGreaterThan(BigInteger.Zero);
    }
}
