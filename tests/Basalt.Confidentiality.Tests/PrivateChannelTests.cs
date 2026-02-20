using System.Security.Cryptography;
using System.Text;
using Basalt.Confidentiality.Channels;
using Basalt.Core;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Confidentiality.Tests;

public class PrivateChannelTests
{
    [Fact]
    public void X25519_GenerateKeyPair_Returns32ByteKeys()
    {
        var (privateKey, publicKey) = X25519KeyExchange.GenerateKeyPair();

        privateKey.Should().HaveCount(32);
        publicKey.Should().HaveCount(32);
    }

    [Fact]
    public void X25519_DeriveSharedSecret_BothPartiesGetSameSecret()
    {
        var (alicePrivate, alicePublic) = X25519KeyExchange.GenerateKeyPair();
        var (bobPrivate, bobPublic) = X25519KeyExchange.GenerateKeyPair();

        var secretAlice = X25519KeyExchange.DeriveSharedSecret(alicePrivate, bobPublic);
        var secretBob = X25519KeyExchange.DeriveSharedSecret(bobPrivate, alicePublic);

        secretAlice.Should().HaveCount(32);
        secretAlice.Should().Equal(secretBob);
    }

    [Fact]
    public void ChannelEncryption_EncryptDecrypt_RoundTrip()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("hello world");

        var ciphertext = ChannelEncryption.Encrypt(key, nonce, plaintext);
        var decrypted = ChannelEncryption.Decrypt(key, nonce, ciphertext);

        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public void ChannelEncryption_TamperedCiphertext_ThrowsOnDecrypt()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)0x01);
        var nonce = ChannelEncryption.BuildNonce(0);
        var plaintext = Encoding.UTF8.GetBytes("hello world");

        var ciphertext = ChannelEncryption.Encrypt(key, nonce, plaintext);

        // Flip a bit in the ciphertext portion (before the tag)
        ciphertext[0] ^= 0xFF;

        var act = () => ChannelEncryption.Decrypt(key, nonce, ciphertext);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void ChannelEncryption_BuildNonce_DifferentSequenceNumbers_DifferentNonces()
    {
        var nonce0 = ChannelEncryption.BuildNonce(0);
        var nonce1 = ChannelEncryption.BuildNonce(1);

        nonce0.Should().HaveCount(ChannelEncryption.NonceSize);
        nonce1.Should().HaveCount(ChannelEncryption.NonceSize);
        nonce0.Should().NotEqual(nonce1);
    }

    [Fact]
    public void PrivateChannel_DeriveChannelId_Deterministic()
    {
        var (_, pubKeyA) = X25519KeyExchange.GenerateKeyPair();
        var (_, pubKeyB) = X25519KeyExchange.GenerateKeyPair();

        var id1 = PrivateChannel.DeriveChannelId(pubKeyA, pubKeyB);
        var id2 = PrivateChannel.DeriveChannelId(pubKeyA, pubKeyB);

        id1.Should().Be(id2);
    }

    [Fact]
    public void PrivateChannel_DeriveChannelId_OrderIndependent()
    {
        var (_, pubKeyA) = X25519KeyExchange.GenerateKeyPair();
        var (_, pubKeyB) = X25519KeyExchange.GenerateKeyPair();

        var idAB = PrivateChannel.DeriveChannelId(pubKeyA, pubKeyB);
        var idBA = PrivateChannel.DeriveChannelId(pubKeyB, pubKeyA);

        idAB.Should().Be(idBA);
    }

    [Fact]
    public void PrivateChannel_CreateAndVerifyMessage_RoundTrip()
    {
        // X25519 key pairs for encryption
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();

        // Ed25519 key pairs for signing
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();
        var (bobEd25519Private, bobEd25519Public) = Ed25519Signer.GenerateKeyPair();

        // Derive shared secrets from both sides
        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);

        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyA = Address.Zero,
            PartyB = Address.Zero,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var payload = Encoding.UTF8.GetBytes("secret message from Alice to Bob");

        // Alice creates a message signed with her Ed25519 private key
        var message = channel.CreateMessage(aliceSharedSecret, payload, aliceEd25519Private);

        // Bob verifies and decrypts using Alice's Ed25519 public key
        var decrypted = channel.VerifyAndDecrypt(message, bobSharedSecret, aliceEd25519Public);

        decrypted.Should().Equal(payload);
    }

    [Fact]
    public void PrivateChannel_CreateMessage_IncreasesNonce()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, _) = Ed25519Signer.GenerateKeyPair();

        var sharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var nonceBefore = channel.Nonce;
        channel.CreateMessage(sharedSecret, Encoding.UTF8.GetBytes("msg1"), aliceEd25519Private);
        var nonceAfter = channel.Nonce;

        nonceAfter.Should().Be(nonceBefore + 1);
    }

    [Fact]
    public void PrivateChannel_CreateMessage_NotActive_Throws()
    {
        var (_, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, _) = Ed25519Signer.GenerateKeyPair();

        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Open, // Not Active
        };

        var act = () => channel.CreateMessage(
            new byte[32],
            Encoding.UTF8.GetBytes("test"),
            aliceEd25519Private);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PrivateChannel_VerifyMessage_WrongSignature_Throws()
    {
        // X25519 key pairs
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();

        // Ed25519 key pairs
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();
        var (_, eveEd25519Public) = Ed25519Signer.GenerateKeyPair(); // Eve's key (wrong)

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);

        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var payload = Encoding.UTF8.GetBytes("message from Alice");
        var message = channel.CreateMessage(aliceSharedSecret, payload, aliceEd25519Private);

        // Verify with Eve's public key instead of Alice's -- should fail
        var act = () => channel.VerifyAndDecrypt(message, bobSharedSecret, eveEd25519Public);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*signature*");
    }

    // ── Bidirectional communication ─────────────────────────────────────────

    [Fact]
    public void PrivateChannel_BidirectionalCommunication_BothDirectionsWork()
    {
        // X25519 key pairs
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();

        // Ed25519 key pairs
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();
        var (bobEd25519Private, bobEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyA = Address.Zero,
            PartyB = Address.Zero,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        // Alice sends to Bob
        var alicePayload = Encoding.UTF8.GetBytes("Hello Bob, this is Alice");
        var aliceMessage = channel.CreateMessage(aliceSharedSecret, alicePayload, aliceEd25519Private);
        var decryptedByBob = channel.VerifyAndDecrypt(aliceMessage, bobSharedSecret, aliceEd25519Public);
        decryptedByBob.Should().Equal(alicePayload);

        // Bob sends to Alice
        var bobPayload = Encoding.UTF8.GetBytes("Hello Alice, this is Bob");
        var bobMessage = channel.CreateMessage(bobSharedSecret, bobPayload, bobEd25519Private);
        var decryptedByAlice = channel.VerifyAndDecrypt(bobMessage, aliceSharedSecret, bobEd25519Public);
        decryptedByAlice.Should().Equal(bobPayload);
    }

    // ── Status transitions ──────────────────────────────────────────────────

    [Fact]
    public void PrivateChannel_CreateMessage_ClosingStatus_Throws()
    {
        var (_, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, _) = Ed25519Signer.GenerateKeyPair();
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Closing,
        };

        var act = () => channel.CreateMessage(
            new byte[32],
            Encoding.UTF8.GetBytes("test"),
            aliceEd25519Private);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PrivateChannel_CreateMessage_ClosedStatus_Throws()
    {
        var (_, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, _) = Ed25519Signer.GenerateKeyPair();
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Closed,
        };

        var act = () => channel.CreateMessage(
            new byte[32],
            Encoding.UTF8.GetBytes("test"),
            aliceEd25519Private);

        act.Should().Throw<InvalidOperationException>();
    }

    // ── Nonce management ────────────────────────────────────────────────────

    [Fact]
    public void PrivateChannel_MultipleMessages_NonceIncrementsCorrectly()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, _) = Ed25519Signer.GenerateKeyPair();

        var sharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        channel.Nonce.Should().Be(0);

        var msg1 = channel.CreateMessage(sharedSecret, Encoding.UTF8.GetBytes("msg1"), aliceEd25519Private);
        channel.Nonce.Should().Be(1);
        msg1.Nonce.Should().Be(0);

        var msg2 = channel.CreateMessage(sharedSecret, Encoding.UTF8.GetBytes("msg2"), aliceEd25519Private);
        channel.Nonce.Should().Be(2);
        msg2.Nonce.Should().Be(1);

        var msg3 = channel.CreateMessage(sharedSecret, Encoding.UTF8.GetBytes("msg3"), aliceEd25519Private);
        channel.Nonce.Should().Be(3);
        msg3.Nonce.Should().Be(2);
    }

    [Fact]
    public void PrivateChannel_MessageNonces_AreUnique()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, _) = Ed25519Signer.GenerateKeyPair();

        var sharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var messages = new ChannelMessage[5];
        for (int i = 0; i < 5; i++)
        {
            messages[i] = channel.CreateMessage(sharedSecret, Encoding.UTF8.GetBytes($"msg{i}"), aliceEd25519Private);
        }

        var nonces = messages.Select(m => m.Nonce).ToArray();
        nonces.Should().OnlyHaveUniqueItems();
    }

    // ── Wrong channel ID ────────────────────────────────────────────────────

    [Fact]
    public void PrivateChannel_VerifyAndDecrypt_WrongChannelId_Throws()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var payload = Encoding.UTF8.GetBytes("test message");
        var message = channel.CreateMessage(aliceSharedSecret, payload, aliceEd25519Private);

        // Create a different channel with a different ID
        var (_, eveX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var differentChannelId = PrivateChannel.DeriveChannelId(aliceX25519Public, eveX25519Public);

        var differentChannel = new PrivateChannel
        {
            ChannelId = differentChannelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = eveX25519Public,
            Status = ChannelStatus.Active,
        };

        // Try to verify a message from the original channel on a different channel
        var act = () => differentChannel.VerifyAndDecrypt(message, bobSharedSecret, aliceEd25519Public);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*channel ID*");
    }

    // ── VerifyAndDecrypt with wrong shared secret ───────────────────────────

    [Fact]
    public void PrivateChannel_VerifyAndDecrypt_WrongSharedSecret_Throws()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var payload = Encoding.UTF8.GetBytes("secret message");
        var message = channel.CreateMessage(aliceSharedSecret, payload, aliceEd25519Private);

        // Use a completely wrong shared secret
        var wrongSecret = new byte[32];
        Array.Fill(wrongSecret, (byte)0xFF);

        var act = () => channel.VerifyAndDecrypt(message, wrongSecret, aliceEd25519Public);
        act.Should().Throw<Exception>(); // CryptographicException from AES-GCM
    }

    // ── DeriveChannelId properties ──────────────────────────────────────────

    [Fact]
    public void PrivateChannel_DeriveChannelId_DifferentKeys_DifferentIds()
    {
        var (_, pubKeyA) = X25519KeyExchange.GenerateKeyPair();
        var (_, pubKeyB) = X25519KeyExchange.GenerateKeyPair();
        var (_, pubKeyC) = X25519KeyExchange.GenerateKeyPair();

        var idAB = PrivateChannel.DeriveChannelId(pubKeyA, pubKeyB);
        var idAC = PrivateChannel.DeriveChannelId(pubKeyA, pubKeyC);
        var idBC = PrivateChannel.DeriveChannelId(pubKeyB, pubKeyC);

        idAB.Should().NotBe(idAC);
        idAB.Should().NotBe(idBC);
        idAC.Should().NotBe(idBC);
    }

    [Fact]
    public void PrivateChannel_CreateMessage_EmptyPayload_Works()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var emptyPayload = Array.Empty<byte>();
        var message = channel.CreateMessage(aliceSharedSecret, emptyPayload, aliceEd25519Private);
        var decrypted = channel.VerifyAndDecrypt(message, bobSharedSecret, aliceEd25519Public);

        decrypted.Should().BeEmpty();
    }

    [Fact]
    public void PrivateChannel_CreateMessage_LargePayload_Works()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        // 64KB payload
        var largePayload = new byte[65536];
        RandomNumberGenerator.Fill(largePayload);

        var message = channel.CreateMessage(aliceSharedSecret, largePayload, aliceEd25519Private);
        var decrypted = channel.VerifyAndDecrypt(message, bobSharedSecret, aliceEd25519Public);

        decrypted.Should().Equal(largePayload);
    }

    [Fact]
    public void PrivateChannel_StatusTransition_OpenToActive()
    {
        var (_, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Open,
        };

        channel.Status.Should().Be(ChannelStatus.Open);

        channel.Status = ChannelStatus.Active;
        channel.Status.Should().Be(ChannelStatus.Active);
    }

    [Fact]
    public void PrivateChannel_StatusTransition_ActiveToClosingToClosed()
    {
        var (_, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        channel.Status = ChannelStatus.Closing;
        channel.Status.Should().Be(ChannelStatus.Closing);

        channel.Status = ChannelStatus.Closed;
        channel.Status.Should().Be(ChannelStatus.Closed);
    }

    // ── F-01: Directional Encryption Keys ───────────────────────────────────

    [Fact]
    public void F01_DirectionalKeys_BidirectionalWithExplicitKeys_RoundTrips()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();
        var (bobEd25519Private, bobEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        // Alice's channel
        var aliceChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyA = Address.Zero,
            PartyB = Address.Zero,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        // Bob's channel (separate instance for independent nonce tracking)
        var bobChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyA = Address.Zero,
            PartyB = Address.Zero,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        // Alice sends to Bob using explicit X25519 key parameter
        var alicePayload = Encoding.UTF8.GetBytes("Hello from Alice");
        var aliceMsg = aliceChannel.CreateMessage(aliceSharedSecret, alicePayload, aliceEd25519Private, aliceX25519Public);
        var decryptedByBob = bobChannel.VerifyAndDecrypt(aliceMsg, bobSharedSecret, aliceEd25519Public, aliceX25519Public);
        decryptedByBob.Should().Equal(alicePayload);

        // Bob sends to Alice using explicit X25519 key parameter
        var bobPayload = Encoding.UTF8.GetBytes("Hello from Bob");
        var bobMsg = bobChannel.CreateMessage(bobSharedSecret, bobPayload, bobEd25519Private, bobX25519Public);
        var decryptedByAlice = aliceChannel.VerifyAndDecrypt(bobMsg, aliceSharedSecret, bobEd25519Public, bobX25519Public);
        decryptedByAlice.Should().Equal(bobPayload);
    }

    [Fact]
    public void F01_DirectionalKeys_DifferentDirectionsUseDifferentKeys()
    {
        // Verify that encrypting with direction A->B produces different ciphertext
        // than B->A for the same plaintext and nonce, proving directional key separation.
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, _) = Ed25519Signer.GenerateKeyPair();
        var (bobEd25519Private, _) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel1 = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var channel2 = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var payload = Encoding.UTF8.GetBytes("same payload");

        // Alice sends (nonce 0)
        var msgAlice = channel1.CreateMessage(aliceSharedSecret, payload, aliceEd25519Private, aliceX25519Public);
        // Bob sends (nonce 0 on his channel)
        var msgBob = channel2.CreateMessage(bobSharedSecret, payload, bobEd25519Private, bobX25519Public);

        // Both use nonce 0, but encrypted payloads should differ because directional keys differ
        msgAlice.EncryptedPayload.Should().NotEqual(msgBob.EncryptedPayload);
    }

    [Fact]
    public void F01_DirectionalKeys_WrongDirectionKeyFails()
    {
        // Decrypting with the wrong direction should fail
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var payload = Encoding.UTF8.GetBytes("test");
        var msg = channel.CreateMessage(aliceSharedSecret, payload, aliceEd25519Private, aliceX25519Public);

        // Try to decrypt claiming Bob was sender (wrong direction)
        var act = () => channel.VerifyAndDecrypt(msg, bobSharedSecret, aliceEd25519Public, bobX25519Public);
        act.Should().Throw<Exception>(); // CryptographicException from AES-GCM mismatch
    }

    // ── F-05: Nonce Replay Protection ───────────────────────────────────────

    [Fact]
    public void F05_NonceReplay_ReplayedMessage_Throws()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var senderChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var receiverChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var payload = Encoding.UTF8.GetBytes("message");
        var msg = senderChannel.CreateMessage(aliceSharedSecret, payload, aliceEd25519Private);

        // First decrypt succeeds
        receiverChannel.VerifyAndDecrypt(msg, bobSharedSecret, aliceEd25519Public);

        // Replaying the same message should fail
        var act = () => receiverChannel.VerifyAndDecrypt(msg, bobSharedSecret, aliceEd25519Public);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*F-05*replay*");
    }

    [Fact]
    public void F05_NonceReplay_OutOfOrderNonce_Throws()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var senderChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var receiverChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        // Send two messages
        var msg0 = senderChannel.CreateMessage(aliceSharedSecret, Encoding.UTF8.GetBytes("msg0"), aliceEd25519Private);
        var msg1 = senderChannel.CreateMessage(aliceSharedSecret, Encoding.UTF8.GetBytes("msg1"), aliceEd25519Private);

        // Receive msg1 first (nonce 1)
        receiverChannel.VerifyAndDecrypt(msg1, bobSharedSecret, aliceEd25519Public);

        // Now try msg0 (nonce 0) - should fail because nonce is not increasing
        var act = () => receiverChannel.VerifyAndDecrypt(msg0, bobSharedSecret, aliceEd25519Public);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*F-05*");
    }

    [Fact]
    public void F05_NonceReplay_StrictlyIncreasingNonces_Succeed()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobSharedSecret = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var senderChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var receiverChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        // Send and receive 5 messages in order
        for (int i = 0; i < 5; i++)
        {
            var msg = senderChannel.CreateMessage(aliceSharedSecret, Encoding.UTF8.GetBytes($"msg{i}"), aliceEd25519Private);
            var decrypted = receiverChannel.VerifyAndDecrypt(msg, bobSharedSecret, aliceEd25519Public);
            decrypted.Should().Equal(Encoding.UTF8.GetBytes($"msg{i}"));
        }
    }

    // ── F-06: HKDF Identity Binding ─────────────────────────────────────────

    [Fact]
    public void F06_HkdfIdentityBinding_BothPartiesDeriveSameSecret()
    {
        var (alicePrivate, alicePublic) = X25519KeyExchange.GenerateKeyPair();
        var (bobPrivate, bobPublic) = X25519KeyExchange.GenerateKeyPair();

        // With explicit public keys
        var secretAlice = X25519KeyExchange.DeriveSharedSecret(alicePrivate, bobPublic, alicePublic);
        var secretBob = X25519KeyExchange.DeriveSharedSecret(bobPrivate, alicePublic, bobPublic);

        secretAlice.Should().HaveCount(32);
        secretAlice.Should().Equal(secretBob);
    }

    [Fact]
    public void F06_HkdfIdentityBinding_ImplicitAndExplicitPubKeyProduceSameResult()
    {
        var (alicePrivate, alicePublic) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobPublic) = X25519KeyExchange.GenerateKeyPair();

        // Implicit (derives pubkey from private key internally)
        var secretImplicit = X25519KeyExchange.DeriveSharedSecret(alicePrivate, bobPublic);
        // Explicit
        var secretExplicit = X25519KeyExchange.DeriveSharedSecret(alicePrivate, bobPublic, alicePublic);

        secretImplicit.Should().Equal(secretExplicit);
    }

    // ── F-07: Signed Key Exchange ───────────────────────────────────────────

    [Fact]
    public void F07_SignedKeyExchange_ValidSignatureVerifies()
    {
        var (x25519Private, x25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (ed25519Private, ed25519Public) = Ed25519Signer.GenerateKeyPair();

        var signatureBytes = X25519KeyExchange.SignKeyExchange(x25519Public, ed25519Private);
        var signature = new Signature(signatureBytes);

        X25519KeyExchange.VerifyKeyExchange(x25519Public, ed25519Public, signature).Should().BeTrue();
    }

    [Fact]
    public void F07_SignedKeyExchange_WrongEdPublicKey_FailsVerification()
    {
        var (_, x25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (ed25519Private, _) = Ed25519Signer.GenerateKeyPair();
        var (_, eveEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var signatureBytes = X25519KeyExchange.SignKeyExchange(x25519Public, ed25519Private);
        var signature = new Signature(signatureBytes);

        X25519KeyExchange.VerifyKeyExchange(x25519Public, eveEd25519Public, signature).Should().BeFalse();
    }

    [Fact]
    public void F07_SignedKeyExchange_TamperedX25519Key_FailsVerification()
    {
        var (_, x25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, differentX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (ed25519Private, ed25519Public) = Ed25519Signer.GenerateKeyPair();

        var signatureBytes = X25519KeyExchange.SignKeyExchange(x25519Public, ed25519Private);
        var signature = new Signature(signatureBytes);

        // Verify with a different X25519 key (MITM substitution)
        X25519KeyExchange.VerifyKeyExchange(differentX25519Public, ed25519Public, signature).Should().BeFalse();
    }

    [Fact]
    public void F07_SignedKeyExchange_WrongKeySize_Throws()
    {
        var (ed25519Private, _) = Ed25519Signer.GenerateKeyPair();
        var badKey = new byte[31]; // Wrong size

        var act = () => X25519KeyExchange.SignKeyExchange(badKey, ed25519Private);
        act.Should().Throw<ArgumentException>();
    }

    // ── F-08: Key Ratcheting ────────────────────────────────────────────────

    [Fact]
    public void F08_RatchetKey_ProducesDifferentKey()
    {
        var currentKey = new byte[32];
        Array.Fill(currentKey, (byte)0x42);

        var ratcheted = PrivateChannel.RatchetKey(currentKey, 0);

        ratcheted.Should().HaveCount(32);
        ratcheted.Should().NotEqual(currentKey);
    }

    [Fact]
    public void F08_RatchetKey_DifferentNonces_DifferentKeys()
    {
        var currentKey = new byte[32];
        Array.Fill(currentKey, (byte)0x42);

        var key1 = PrivateChannel.RatchetKey(currentKey, 0);
        var key2 = PrivateChannel.RatchetKey(currentKey, 1);

        key1.Should().NotEqual(key2);
    }

    [Fact]
    public void F08_RatchetKey_Deterministic()
    {
        var currentKey = new byte[32];
        Array.Fill(currentKey, (byte)0x42);

        var ratcheted1 = PrivateChannel.RatchetKey(currentKey, 5);
        var ratcheted2 = PrivateChannel.RatchetKey(currentKey, 5);

        ratcheted1.Should().Equal(ratcheted2);
    }

    [Fact]
    public void F08_RatchetKey_ChainedRatcheting_Works()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceKey = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var bobKey = X25519KeyExchange.DeriveSharedSecret(bobX25519Private, aliceX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var senderChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var receiverChannel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        // Send 3 messages, ratcheting after each
        for (int i = 0; i < 3; i++)
        {
            var payload = Encoding.UTF8.GetBytes($"ratcheted-msg-{i}");
            var msg = senderChannel.CreateMessage(aliceKey, payload, aliceEd25519Private);
            var decrypted = receiverChannel.VerifyAndDecrypt(msg, bobKey, aliceEd25519Public);
            decrypted.Should().Equal(payload);

            // Ratchet both sides
            aliceKey = PrivateChannel.RatchetKey(aliceKey, msg.Nonce);
            bobKey = PrivateChannel.RatchetKey(bobKey, msg.Nonce);
        }
    }

    // ── F-14: Zero Shared Secret Material ───────────────────────────────────

    [Fact]
    public void F14_GetPublicKey_DerivesCorrectly()
    {
        var (privateKey, expectedPublicKey) = X25519KeyExchange.GenerateKeyPair();
        var derivedPublicKey = X25519KeyExchange.GetPublicKey(privateKey);

        derivedPublicKey.Should().Equal(expectedPublicKey);
    }

    [Fact]
    public void F14_GetPublicKey_WrongSize_Throws()
    {
        var badKey = new byte[31];
        var act = () => X25519KeyExchange.GetPublicKey(badKey);
        act.Should().Throw<ArgumentException>();
    }

    // ── F-18: Max Channel Payload Size ──────────────────────────────────────

    [Fact]
    public void F18_MaxPayloadSize_ConstantIs1MB()
    {
        PrivateChannel.MaxPayloadSize.Should().Be(1024 * 1024);
    }

    [Fact]
    public void F18_MaxPayloadSize_ExceedingLimit_Throws()
    {
        var (_, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (_, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, _) = Ed25519Signer.GenerateKeyPair();
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        var oversizedPayload = new byte[PrivateChannel.MaxPayloadSize + 1];

        var act = () => channel.CreateMessage(new byte[32], oversizedPayload, aliceEd25519Private);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*F-18*");
    }

    [Fact]
    public void F18_MaxPayloadSize_ExactLimit_Succeeds()
    {
        var (aliceX25519Private, aliceX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (bobX25519Private, bobX25519Public) = X25519KeyExchange.GenerateKeyPair();
        var (aliceEd25519Private, aliceEd25519Public) = Ed25519Signer.GenerateKeyPair();

        var aliceSharedSecret = X25519KeyExchange.DeriveSharedSecret(aliceX25519Private, bobX25519Public);
        var channelId = PrivateChannel.DeriveChannelId(aliceX25519Public, bobX25519Public);

        var channel = new PrivateChannel
        {
            ChannelId = channelId,
            PartyAPublicKey = aliceX25519Public,
            PartyBPublicKey = bobX25519Public,
            Status = ChannelStatus.Active,
        };

        // Exactly at limit should succeed
        var exactPayload = new byte[PrivateChannel.MaxPayloadSize];
        var act = () => channel.CreateMessage(aliceSharedSecret, exactPayload, aliceEd25519Private);
        act.Should().NotThrow();
    }
}
