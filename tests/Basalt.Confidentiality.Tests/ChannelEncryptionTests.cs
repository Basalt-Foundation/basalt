using System.Security.Cryptography;
using System.Text;
using Basalt.Confidentiality.Channels;
using FluentAssertions;
using Xunit;

namespace Basalt.Confidentiality.Tests;

public class ChannelEncryptionTests
{
    // ── Argument validation ─────────────────────────────────────────────────

    [Fact]
    public void Encrypt_WrongKeySize_Throws()
    {
        var badKey = new byte[31]; // should be 32
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("hello");

        var act = () => ChannelEncryption.Encrypt(badKey, nonce, plaintext);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Encrypt_WrongNonceSize_Throws()
    {
        var key = new byte[32];
        var badNonce = new byte[11]; // should be 12
        var plaintext = Encoding.UTF8.GetBytes("hello");

        var act = () => ChannelEncryption.Encrypt(key, badNonce, plaintext);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decrypt_WrongKeySize_Throws()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("hello");

        var ciphertext = ChannelEncryption.Encrypt(key, nonce, plaintext);

        var badKey = new byte[31];
        var act = () => ChannelEncryption.Decrypt(badKey, nonce, ciphertext);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decrypt_WrongNonceSize_Throws()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("hello");

        var ciphertext = ChannelEncryption.Encrypt(key, nonce, plaintext);

        var badNonce = new byte[11];
        var act = () => ChannelEncryption.Decrypt(key, badNonce, ciphertext);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decrypt_CiphertextTooShort_Throws()
    {
        var key = new byte[32];
        var nonce = ChannelEncryption.BuildNonce(0);
        var tooShort = new byte[15]; // shorter than TagSize (16)

        var act = () => ChannelEncryption.Decrypt(key, nonce, tooShort);
        act.Should().Throw<ArgumentException>();
    }

    // ── Wrong key decryption ────────────────────────────────────────────────

    [Fact]
    public void Decrypt_WrongKey_ThrowsCryptographicException()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("hello world");

        var ciphertext = ChannelEncryption.Encrypt(key, nonce, plaintext);

        var wrongKey = new byte[32];
        Array.Fill(wrongKey, (byte)0x02);

        var act = () => ChannelEncryption.Decrypt(wrongKey, nonce, ciphertext);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_WrongNonce_ThrowsCryptographicException()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("hello world");

        var ciphertext = ChannelEncryption.Encrypt(key, nonce, plaintext);

        var wrongNonce = ChannelEncryption.BuildNonce(1);

        var act = () => ChannelEncryption.Decrypt(key, wrongNonce, ciphertext);
        act.Should().Throw<CryptographicException>();
    }

    // ── Empty plaintext ─────────────────────────────────────────────────────

    [Fact]
    public void EncryptDecrypt_EmptyPlaintext_RoundTrips()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Array.Empty<byte>();

        var ciphertext = ChannelEncryption.Encrypt(key, nonce, plaintext);
        // Ciphertext should be just the tag (16 bytes)
        ciphertext.Should().HaveCount(ChannelEncryption.TagSize);

        var decrypted = ChannelEncryption.Decrypt(key, nonce, ciphertext);
        decrypted.Should().BeEmpty();
    }

    // ── Ciphertext properties ───────────────────────────────────────────────

    [Fact]
    public void Encrypt_OutputSize_IsCiphertextPlusTag()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("test message");

        var ciphertext = ChannelEncryption.Encrypt(key, nonce, plaintext);

        ciphertext.Should().HaveCount(plaintext.Length + ChannelEncryption.TagSize);
    }

    [Fact]
    public void Encrypt_SamePlaintextDifferentNonces_DifferentCiphertext()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var plaintext = Encoding.UTF8.GetBytes("same plaintext");

        var nonce0 = ChannelEncryption.BuildNonce(0);
        var nonce1 = ChannelEncryption.BuildNonce(1);

        var ct0 = ChannelEncryption.Encrypt(key, nonce0, plaintext);
        var ct1 = ChannelEncryption.Encrypt(key, nonce1, plaintext);

        ct0.Should().NotEqual(ct1);
    }

    [Fact]
    public void Encrypt_SamePlaintextDifferentKeys_DifferentCiphertext()
    {
        var key1 = new byte[32]; Array.Fill(key1, (byte)0x01);
        var key2 = new byte[32]; Array.Fill(key2, (byte)0x02);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("same plaintext");

        var ct1 = ChannelEncryption.Encrypt(key1, nonce, plaintext);
        var ct2 = ChannelEncryption.Encrypt(key2, nonce, plaintext);

        ct1.Should().NotEqual(ct2);
    }

    // ── BuildNonce ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildNonce_Zero_Returns12Bytes()
    {
        var nonce = ChannelEncryption.BuildNonce(0);
        nonce.Should().HaveCount(ChannelEncryption.NonceSize);

        // First 4 bytes should be zero, last 8 should be big-endian 0
        nonce.Should().AllSatisfy(b => b.Should().Be(0));
    }

    [Fact]
    public void BuildNonce_MaxUlong_Returns12Bytes()
    {
        var nonce = ChannelEncryption.BuildNonce(ulong.MaxValue);
        nonce.Should().HaveCount(ChannelEncryption.NonceSize);

        // First 4 bytes are zero padding
        nonce[0].Should().Be(0);
        nonce[1].Should().Be(0);
        nonce[2].Should().Be(0);
        nonce[3].Should().Be(0);

        // Last 8 bytes should be 0xFF (ulong.MaxValue in big-endian)
        for (int i = 4; i < 12; i++)
        {
            nonce[i].Should().Be(0xFF);
        }
    }

    [Fact]
    public void BuildNonce_One_EncodesCorrectly()
    {
        var nonce = ChannelEncryption.BuildNonce(1);

        // First 4 bytes zero, then 0x0000000000000001 big-endian
        for (int i = 0; i < 11; i++)
        {
            nonce[i].Should().Be(0);
        }
        nonce[11].Should().Be(1);
    }

    [Fact]
    public void BuildNonce_ConsecutiveNumbers_AreDifferent()
    {
        var nonces = new List<byte[]>();
        for (ulong i = 0; i < 10; i++)
        {
            nonces.Add(ChannelEncryption.BuildNonce(i));
        }

        // All nonces should be distinct
        for (int i = 0; i < nonces.Count; i++)
        {
            for (int j = i + 1; j < nonces.Count; j++)
            {
                nonces[i].Should().NotEqual(nonces[j]);
            }
        }
    }

    // ── Tampered tag ────────────────────────────────────────────────────────

    [Fact]
    public void Decrypt_TamperedTag_ThrowsCryptographicException()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("hello world");

        var ciphertext = ChannelEncryption.Encrypt(key, nonce, plaintext);

        // Tamper with the tag (last 16 bytes)
        ciphertext[^1] ^= 0xFF;

        var act = () => ChannelEncryption.Decrypt(key, nonce, ciphertext);
        act.Should().Throw<CryptographicException>();
    }

    // ── Constants ───────────────────────────────────────────────────────────

    [Fact]
    public void Constants_HaveExpectedValues()
    {
        ChannelEncryption.NonceSize.Should().Be(12);
        ChannelEncryption.TagSize.Should().Be(16);
        ChannelEncryption.KeySize.Should().Be(32);
    }
}

public class X25519KeyExchangeTests
{
    [Fact]
    public void GenerateKeyPair_ProducesUniqueKeys()
    {
        var (priv1, pub1) = X25519KeyExchange.GenerateKeyPair();
        var (priv2, pub2) = X25519KeyExchange.GenerateKeyPair();

        priv1.Should().NotEqual(priv2);
        pub1.Should().NotEqual(pub2);
    }

    [Fact]
    public void DeriveSharedSecret_WrongPrivateKeySize_Throws()
    {
        var badPriv = new byte[31]; // should be 32
        var (_, pubKey) = X25519KeyExchange.GenerateKeyPair();

        var act = () => X25519KeyExchange.DeriveSharedSecret(badPriv, pubKey);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeriveSharedSecret_WrongPublicKeySize_Throws()
    {
        var (privKey, _) = X25519KeyExchange.GenerateKeyPair();
        var badPub = new byte[31]; // should be 32

        var act = () => X25519KeyExchange.DeriveSharedSecret(privKey, badPub);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeriveSharedSecret_DifferentPairs_DifferentSecrets()
    {
        var (alicePriv, alicePub) = X25519KeyExchange.GenerateKeyPair();
        var (bobPriv, bobPub) = X25519KeyExchange.GenerateKeyPair();
        var (charliePriv, charliePub) = X25519KeyExchange.GenerateKeyPair();

        var secretAliceBob = X25519KeyExchange.DeriveSharedSecret(alicePriv, bobPub);
        var secretAliceCharlie = X25519KeyExchange.DeriveSharedSecret(alicePriv, charliePub);

        secretAliceBob.Should().NotEqual(secretAliceCharlie);
    }

    [Fact]
    public void DeriveSharedSecret_IsCommutative()
    {
        var (alicePriv, alicePub) = X25519KeyExchange.GenerateKeyPair();
        var (bobPriv, bobPub) = X25519KeyExchange.GenerateKeyPair();

        var secretAlice = X25519KeyExchange.DeriveSharedSecret(alicePriv, bobPub);
        var secretBob = X25519KeyExchange.DeriveSharedSecret(bobPriv, alicePub);

        secretAlice.Should().Equal(secretBob);
    }

    [Fact]
    public void DeriveSharedSecret_Returns32Bytes()
    {
        var (alicePriv, _) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobPub) = X25519KeyExchange.GenerateKeyPair();

        var secret = X25519KeyExchange.DeriveSharedSecret(alicePriv, bobPub);

        secret.Should().HaveCount(32);
    }

    [Fact]
    public void DeriveSharedSecret_Deterministic()
    {
        var (alicePriv, _) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobPub) = X25519KeyExchange.GenerateKeyPair();

        var secret1 = X25519KeyExchange.DeriveSharedSecret(alicePriv, bobPub);
        var secret2 = X25519KeyExchange.DeriveSharedSecret(alicePriv, bobPub);

        secret1.Should().Equal(secret2);
    }

    [Fact]
    public void KeySize_Is32()
    {
        X25519KeyExchange.KeySize.Should().Be(32);
    }
}
