using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Xunit;

namespace Basalt.Crypto.Tests;

public class BlsSignerTests
{
    private readonly BlsSigner _signer = new();

    private static (byte[] PrivateKey, byte[] PublicKey) GenerateBlsKeyPair()
    {
        var privateKey = new byte[32];
        RandomNumberGenerator.Fill(privateKey);
        // BLS12-381 scalar field modulus starts with 0x73..., so masking the
        // top byte to 0x3F guarantees the scalar is below the field order.
        privateKey[0] &= 0x3F;
        if (privateKey[0] == 0) privateKey[0] = 1; // Ensure non-zero
        var publicKey = BlsSigner.GetPublicKeyStatic(privateKey);
        return (privateKey, publicKey);
    }

    [Fact]
    public void Sign_Returns96Bytes()
    {
        var (privateKey, _) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("test message");

        var signature = _signer.Sign(privateKey, message);

        signature.Should().HaveCount(96);
    }

    [Fact]
    public void GetPublicKey_Returns48Bytes()
    {
        var (_, publicKey) = GenerateBlsKeyPair();

        publicKey.Should().HaveCount(48);
    }

    [Fact]
    public void Verify_ValidSignature_ReturnsTrue()
    {
        var (privateKey, publicKey) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("hello basalt");

        var signature = _signer.Sign(privateKey, message);
        var result = _signer.Verify(publicKey, message, signature);

        result.Should().BeTrue();
    }

    [Fact]
    public void Verify_WrongMessage_ReturnsFalse()
    {
        var (privateKey, publicKey) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("correct message");
        var wrongMessage = Encoding.UTF8.GetBytes("wrong message");

        var signature = _signer.Sign(privateKey, message);
        var result = _signer.Verify(publicKey, wrongMessage, signature);

        result.Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongKey_ReturnsFalse()
    {
        var (privateKey, _) = GenerateBlsKeyPair();
        var (_, otherPublicKey) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("some message");

        var signature = _signer.Sign(privateKey, message);
        var result = _signer.Verify(otherPublicKey, message, signature);

        result.Should().BeFalse();
    }

    [Fact]
    public void Verify_TamperedSignature_ReturnsFalse()
    {
        var (privateKey, publicKey) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("tamper test");

        var signature = _signer.Sign(privateKey, message);
        signature[10] ^= 0xFF;
        var result = _signer.Verify(publicKey, message, signature);

        result.Should().BeFalse();
    }

    [Fact]
    public void AggregateSignatures_Returns96Bytes()
    {
        var (sk1, _) = GenerateBlsKeyPair();
        var (sk2, _) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("aggregate me");

        var sig1 = _signer.Sign(sk1, message);
        var sig2 = _signer.Sign(sk2, message);
        var aggregated = _signer.AggregateSignatures(new[] { sig1, sig2 });

        aggregated.Should().HaveCount(96);
    }

    [Fact]
    public void AggregateSignatures_SingleSig_ReturnsSame()
    {
        var (privateKey, _) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("single sig");

        var signature = _signer.Sign(privateKey, message);
        var aggregated = _signer.AggregateSignatures(new[] { signature });

        aggregated.Should().BeEquivalentTo(signature);
    }

    [Fact]
    public void VerifyAggregate_ValidSignatures_ReturnsTrue()
    {
        var (sk1, pk1) = GenerateBlsKeyPair();
        var (sk2, pk2) = GenerateBlsKeyPair();
        var (sk3, pk3) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("shared message");

        var sig1 = _signer.Sign(sk1, message);
        var sig2 = _signer.Sign(sk2, message);
        var sig3 = _signer.Sign(sk3, message);
        var aggregated = _signer.AggregateSignatures(new[] { sig1, sig2, sig3 });

        var result = _signer.VerifyAggregate(new[] { pk1, pk2, pk3 }, message, aggregated);

        result.Should().BeTrue();
    }

    [Fact]
    public void VerifyAggregate_WrongMessage_ReturnsFalse()
    {
        var (sk1, pk1) = GenerateBlsKeyPair();
        var (sk2, pk2) = GenerateBlsKeyPair();
        var (sk3, pk3) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("correct message");
        var wrongMessage = Encoding.UTF8.GetBytes("wrong message");

        var sig1 = _signer.Sign(sk1, message);
        var sig2 = _signer.Sign(sk2, message);
        var sig3 = _signer.Sign(sk3, message);
        var aggregated = _signer.AggregateSignatures(new[] { sig1, sig2, sig3 });

        var result = _signer.VerifyAggregate(new[] { pk1, pk2, pk3 }, wrongMessage, aggregated);

        result.Should().BeFalse();
    }

    [Fact]
    public void VerifyAggregate_MissingKey_ReturnsFalse()
    {
        var (sk1, pk1) = GenerateBlsKeyPair();
        var (sk2, pk2) = GenerateBlsKeyPair();
        var (sk3, _) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("missing key test");

        var sig1 = _signer.Sign(sk1, message);
        var sig2 = _signer.Sign(sk2, message);
        var sig3 = _signer.Sign(sk3, message);
        var aggregated = _signer.AggregateSignatures(new[] { sig1, sig2, sig3 });

        // Only provide 2 of the 3 public keys
        var result = _signer.VerifyAggregate(new[] { pk1, pk2 }, message, aggregated);

        result.Should().BeFalse();
    }

    [Fact]
    public void Deterministic_SameKeyMessage_SameSignature()
    {
        var (privateKey, _) = GenerateBlsKeyPair();
        var message = Encoding.UTF8.GetBytes("deterministic test");

        var sig1 = _signer.Sign(privateKey, message);
        var sig2 = _signer.Sign(privateKey, message);

        sig1.Should().BeEquivalentTo(sig2);
    }
}
