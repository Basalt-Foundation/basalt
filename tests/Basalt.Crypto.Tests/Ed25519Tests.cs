using Basalt.Core;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Crypto.Tests;

public class Ed25519Tests
{
    [Fact]
    public void GenerateKeyPair_ProducesValidKeyPair()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        privateKey.Should().HaveCount(32);
        publicKey.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void SignAndVerify_ValidMessage()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var message = "Hello, Basalt!"u8;

        var signature = Ed25519Signer.Sign(privateKey, message);
        signature.IsEmpty.Should().BeFalse();

        var valid = Ed25519Signer.Verify(publicKey, message, signature);
        valid.Should().BeTrue();
    }

    [Fact]
    public void Verify_TamperedMessage_Fails()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var message = "Original"u8;
        var tampered = "Tampered"u8;

        var signature = Ed25519Signer.Sign(privateKey, message);

        Ed25519Signer.Verify(publicKey, tampered, signature).Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongKey_Fails()
    {
        var (privateKey1, _) = Ed25519Signer.GenerateKeyPair();
        var (_, publicKey2) = Ed25519Signer.GenerateKeyPair();
        var message = "Hello"u8;

        var signature = Ed25519Signer.Sign(privateKey1, message);

        Ed25519Signer.Verify(publicKey2, message, signature).Should().BeFalse();
    }

    [Fact]
    public void DeriveAddress_ProducesValidAddress()
    {
        var (_, publicKey) = Ed25519Signer.GenerateKeyPair();
        var address = Ed25519Signer.DeriveAddress(publicKey);
        address.IsZero.Should().BeFalse();
    }

    [Fact]
    public void DeriveAddress_IsDeterministic()
    {
        var (_, publicKey) = Ed25519Signer.GenerateKeyPair();
        var addr1 = Ed25519Signer.DeriveAddress(publicKey);
        var addr2 = Ed25519Signer.DeriveAddress(publicKey);
        addr1.Should().Be(addr2);
    }

    [Fact]
    public void GetPublicKey_MatchesGeneratedPair()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var derivedPubKey = Ed25519Signer.GetPublicKey(privateKey);
        derivedPubKey.Should().Be(publicKey);
    }

    [Fact]
    public void BatchVerify_AllValid()
    {
        var count = 5;
        var publicKeys = new PublicKey[count];
        var messages = new byte[count][];
        var signatures = new Signature[count];

        for (int i = 0; i < count; i++)
        {
            var (priv, pub) = Ed25519Signer.GenerateKeyPair();
            publicKeys[i] = pub;
            messages[i] = new byte[] { (byte)i };
            signatures[i] = Ed25519Signer.Sign(priv, messages[i]);
        }

        Ed25519Signer.BatchVerify(publicKeys, messages, signatures).Should().BeTrue();
    }

    [Fact]
    public void BatchVerify_OneInvalid_Fails()
    {
        var count = 3;
        var publicKeys = new PublicKey[count];
        var messages = new byte[count][];
        var signatures = new Signature[count];

        for (int i = 0; i < count; i++)
        {
            var (priv, pub) = Ed25519Signer.GenerateKeyPair();
            publicKeys[i] = pub;
            messages[i] = new byte[] { (byte)i };
            signatures[i] = Ed25519Signer.Sign(priv, messages[i]);
        }

        // Tamper with last signature
        signatures[count - 1] = new Signature(new byte[64]);

        Ed25519Signer.BatchVerify(publicKeys, messages, signatures).Should().BeFalse();
    }
}
