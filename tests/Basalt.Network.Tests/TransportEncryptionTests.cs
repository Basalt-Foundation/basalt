using System.Security.Cryptography;
using Basalt.Crypto;
using Basalt.Network.Transport;
using FluentAssertions;
using Xunit;

namespace Basalt.Network.Tests;

public class TransportEncryptionTests
{
    private static byte[] GenerateSharedSecret()
    {
        var secret = new byte[32];
        RandomNumberGenerator.Fill(secret);
        return secret;
    }

    [Fact]
    public void RoundTrip_EncryptDecrypt_ReturnsOriginalPlaintext()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);
        using var responder = new TransportEncryption(secret, isInitiator: false);

        var plaintext = "Hello, Basalt P2P!"u8.ToArray();
        var encrypted = initiator.Encrypt(plaintext);
        var decrypted = responder.Decrypt(encrypted);

        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public void RoundTrip_ResponderToInitiator_Works()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);
        using var responder = new TransportEncryption(secret, isInitiator: false);

        var plaintext = "Response from responder"u8.ToArray();
        var encrypted = responder.Encrypt(plaintext);
        var decrypted = initiator.Decrypt(encrypted);

        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public void DirectionalKeys_InitiatorCannotDecryptOwnMessages()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);

        var plaintext = "test"u8.ToArray();
        var encrypted = initiator.Encrypt(plaintext);

        // Initiator uses initiator→responder key to encrypt, but its recv key is responder→initiator
        var act = () => initiator.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void DirectionalKeys_ResponderCannotDecryptOwnMessages()
    {
        var secret = GenerateSharedSecret();
        using var responder = new TransportEncryption(secret, isInitiator: false);

        var plaintext = "test"u8.ToArray();
        var encrypted = responder.Encrypt(plaintext);

        var act = () => responder.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void NonceMonotonicity_ReplayAttackRejected()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);
        using var responder = new TransportEncryption(secret, isInitiator: false);

        var plaintext = "first message"u8.ToArray();
        var encrypted1 = initiator.Encrypt(plaintext);
        var encrypted2 = initiator.Encrypt("second message"u8.ToArray());

        // Decrypt in order — should succeed
        responder.Decrypt(encrypted1);
        responder.Decrypt(encrypted2);

        // Replay encrypted1 — nonce is now stale, should fail
        var act = () => responder.Decrypt(encrypted1);
        act.Should().Throw<CryptographicException>().WithMessage("*replay*");
    }

    [Fact]
    public void NonceMonotonicity_OutOfOrderRejected()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);
        using var responder = new TransportEncryption(secret, isInitiator: false);

        var encrypted1 = initiator.Encrypt("msg1"u8.ToArray());
        var encrypted2 = initiator.Encrypt("msg2"u8.ToArray());

        // Decrypt second first (skip first)
        responder.Decrypt(encrypted2);

        // Now try first — nonce 1 < last seen 2, should be rejected
        var act = () => responder.Decrypt(encrypted1);
        act.Should().Throw<CryptographicException>().WithMessage("*replay*");
    }

    [Fact]
    public void TamperedCiphertext_DetectedByGcm()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);
        using var responder = new TransportEncryption(secret, isInitiator: false);

        var plaintext = "sensitive data"u8.ToArray();
        var encrypted = initiator.Encrypt(plaintext);

        // Tamper with a ciphertext byte (after the 12-byte nonce)
        encrypted[TransportEncryption.NonceSize + 2] ^= 0xFF;

        var act = () => responder.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void TamperedTag_DetectedByGcm()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);
        using var responder = new TransportEncryption(secret, isInitiator: false);

        var plaintext = "sensitive data"u8.ToArray();
        var encrypted = initiator.Encrypt(plaintext);

        // Tamper with the GCM tag (last 16 bytes)
        encrypted[^1] ^= 0xFF;

        var act = () => responder.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void EnvelopeTooShort_Throws()
    {
        var secret = GenerateSharedSecret();
        using var enc = new TransportEncryption(secret, isInitiator: true);

        var act = () => enc.Decrypt(new byte[TransportEncryption.Overhead - 1]);
        act.Should().Throw<CryptographicException>().WithMessage("*too short*");
    }

    [Fact]
    public void MultipleMessages_AllDecryptCorrectly()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);
        using var responder = new TransportEncryption(secret, isInitiator: false);

        for (int i = 0; i < 100; i++)
        {
            var data = new byte[i + 1];
            RandomNumberGenerator.Fill(data);

            var encrypted = initiator.Encrypt(data);
            var decrypted = responder.Decrypt(encrypted);
            decrypted.Should().Equal(data);
        }
    }

    [Fact]
    public void EncryptedSize_EqualsPlaintextPlusOverhead()
    {
        var secret = GenerateSharedSecret();
        using var enc = new TransportEncryption(secret, isInitiator: true);

        var plaintext = new byte[42];
        var encrypted = enc.Encrypt(plaintext);

        encrypted.Length.Should().Be(42 + TransportEncryption.Overhead);
    }

    [Fact]
    public void EmptyPlaintext_EncryptsAndDecryptsCorrectly()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);
        using var responder = new TransportEncryption(secret, isInitiator: false);

        var encrypted = initiator.Encrypt(ReadOnlySpan<byte>.Empty);
        var decrypted = responder.Decrypt(encrypted);

        decrypted.Should().BeEmpty();
    }

    [Fact]
    public void DifferentSharedSecrets_CannotDecrypt()
    {
        using var enc1 = new TransportEncryption(GenerateSharedSecret(), isInitiator: true);
        using var enc2 = new TransportEncryption(GenerateSharedSecret(), isInitiator: false);

        var encrypted = enc1.Encrypt("test"u8.ToArray());

        var act = () => enc2.Decrypt(encrypted);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Disposed_EncryptThrows()
    {
        var secret = GenerateSharedSecret();
        var enc = new TransportEncryption(secret, isInitiator: true);
        enc.Dispose();

        var act = () => enc.Encrypt("test"u8.ToArray());
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Disposed_DecryptThrows()
    {
        var secret = GenerateSharedSecret();
        var enc = new TransportEncryption(secret, isInitiator: true);
        enc.Dispose();

        var act = () => enc.Decrypt(new byte[TransportEncryption.Overhead]);
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void InvalidSharedSecretLength_Throws()
    {
        var act = () => new TransportEncryption(new byte[16], isInitiator: true);
        act.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void BidirectionalCommunication_WorksCorrectly()
    {
        var secret = GenerateSharedSecret();
        using var initiator = new TransportEncryption(secret, isInitiator: true);
        using var responder = new TransportEncryption(secret, isInitiator: false);

        // Simulate bidirectional communication
        var msg1 = "Hello from initiator"u8.ToArray();
        var msg2 = "Hello from responder"u8.ToArray();
        var msg3 = "Second from initiator"u8.ToArray();
        var msg4 = "Second from responder"u8.ToArray();

        // Interleaved sends
        var enc1 = initiator.Encrypt(msg1);
        var enc2 = responder.Encrypt(msg2);
        var enc3 = initiator.Encrypt(msg3);
        var enc4 = responder.Encrypt(msg4);

        // Decrypt in any order (within same direction)
        responder.Decrypt(enc1).Should().Equal(msg1);
        initiator.Decrypt(enc2).Should().Equal(msg2);
        responder.Decrypt(enc3).Should().Equal(msg3);
        initiator.Decrypt(enc4).Should().Equal(msg4);
    }
}

public class X25519Tests
{
    [Fact]
    public void GenerateKeyPair_Returns32ByteKeys()
    {
        var (privateKey, publicKey) = X25519.GenerateKeyPair();
        privateKey.Length.Should().Be(32);
        publicKey.Length.Should().Be(32);
    }

    [Fact]
    public void DeriveSharedSecret_BothSidesDeriveSameSecret()
    {
        var (privA, pubA) = X25519.GenerateKeyPair();
        var (privB, pubB) = X25519.GenerateKeyPair();

        var secretA = X25519.DeriveSharedSecret(privA, pubB);
        var secretB = X25519.DeriveSharedSecret(privB, pubA);

        secretA.Should().Equal(secretB);
    }

    [Fact]
    public void DeriveSharedSecret_DifferentForDifferentPeers()
    {
        var (privA, _) = X25519.GenerateKeyPair();
        var (_, pubB) = X25519.GenerateKeyPair();
        var (_, pubC) = X25519.GenerateKeyPair();

        var secretAB = X25519.DeriveSharedSecret(privA, pubB);
        var secretAC = X25519.DeriveSharedSecret(privA, pubC);

        secretAB.Should().NotEqual(secretAC);
    }

    [Fact]
    public void SignAndVerifyPublicKey_ValidSignature()
    {
        var (x25519Priv, x25519Pub) = X25519.GenerateKeyPair();
        var (ed25519Priv, ed25519Pub) = Ed25519Signer.GenerateKeyPair();

        var sig = X25519.SignPublicKey(x25519Pub, ed25519Priv);
        var valid = X25519.VerifyPublicKey(x25519Pub, ed25519Pub, sig);

        valid.Should().BeTrue();
    }

    [Fact]
    public void VerifyPublicKey_WrongEd25519Key_ReturnsFalse()
    {
        var (_, x25519Pub) = X25519.GenerateKeyPair();
        var (ed25519Priv1, _) = Ed25519Signer.GenerateKeyPair();
        var (_, ed25519Pub2) = Ed25519Signer.GenerateKeyPair();

        var sig = X25519.SignPublicKey(x25519Pub, ed25519Priv1);
        var valid = X25519.VerifyPublicKey(x25519Pub, ed25519Pub2, sig);

        valid.Should().BeFalse();
    }

    [Fact]
    public void VerifyPublicKey_TamperedX25519Key_ReturnsFalse()
    {
        var (_, x25519Pub) = X25519.GenerateKeyPair();
        var (ed25519Priv, ed25519Pub) = Ed25519Signer.GenerateKeyPair();

        var sig = X25519.SignPublicKey(x25519Pub, ed25519Priv);

        // Tamper with the X25519 key
        var tampered = (byte[])x25519Pub.Clone();
        tampered[0] ^= 0xFF;

        var valid = X25519.VerifyPublicKey(tampered, ed25519Pub, sig);
        valid.Should().BeFalse();
    }

    [Fact]
    public void EndToEnd_KeyExchangeAndEncryption()
    {
        // Simulate full key exchange: both sides generate X25519 keys, sign them, exchange, verify, DH
        var (ed25519PrivA, ed25519PubA) = Ed25519Signer.GenerateKeyPair();
        var (ed25519PrivB, ed25519PubB) = Ed25519Signer.GenerateKeyPair();

        var (x25519PrivA, x25519PubA) = X25519.GenerateKeyPair();
        var (x25519PrivB, x25519PubB) = X25519.GenerateKeyPair();

        // Sign X25519 keys
        var sigA = X25519.SignPublicKey(x25519PubA, ed25519PrivA);
        var sigB = X25519.SignPublicKey(x25519PubB, ed25519PrivB);

        // Verify peer's X25519 keys
        X25519.VerifyPublicKey(x25519PubB, ed25519PubB, sigB).Should().BeTrue();
        X25519.VerifyPublicKey(x25519PubA, ed25519PubA, sigA).Should().BeTrue();

        // Derive shared secrets
        var secretA = X25519.DeriveSharedSecret(x25519PrivA, x25519PubB);
        var secretB = X25519.DeriveSharedSecret(x25519PrivB, x25519PubA);
        secretA.Should().Equal(secretB);

        // Use for transport encryption
        using var encA = new TransportEncryption(secretA, isInitiator: true);
        using var encB = new TransportEncryption(secretB, isInitiator: false);

        var msg = "End-to-end encrypted!"u8.ToArray();
        var encrypted = encA.Encrypt(msg);
        var decrypted = encB.Decrypt(encrypted);
        decrypted.Should().Equal(msg);
    }
}
