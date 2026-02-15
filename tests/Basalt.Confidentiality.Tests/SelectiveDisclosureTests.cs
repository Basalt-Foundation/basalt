using System.Security.Cryptography;
using Basalt.Confidentiality.Crypto;
using Basalt.Confidentiality.Disclosure;
using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Confidentiality.Tests;

public class SelectiveDisclosureTests
{
    [Fact]
    public void ViewingKey_EncryptDecrypt_RoundTrip()
    {
        var (privateKey, publicKey) = ViewingKey.GenerateViewingKeyPair();

        UInt256 value = new UInt256(42);
        var blindingFactor = new byte[32];
        blindingFactor[31] = 0x42;

        var encrypted = ViewingKey.EncryptForViewer(publicKey, value, blindingFactor);
        var (decryptedValue, decryptedBlinding) = ViewingKey.DecryptWithViewingKey(privateKey, encrypted);

        decryptedValue.Should().Be(value);
        decryptedBlinding.Should().Equal(blindingFactor);
    }

    [Fact]
    public void ViewingKey_EncryptForViewer_Returns124Bytes()
    {
        var (_, publicKey) = ViewingKey.GenerateViewingKeyPair();

        UInt256 value = new UInt256(100);
        var blindingFactor = new byte[32];
        blindingFactor[31] = 0x01;

        var encrypted = ViewingKey.EncryptForViewer(publicKey, value, blindingFactor);

        encrypted.Should().HaveCount(ViewingKey.EnclosedSize);
        encrypted.Should().HaveCount(124);
    }

    [Fact]
    public void ViewingKey_WrongPrivateKey_ThrowsOnDecrypt()
    {
        var (_, viewerPublicKey) = ViewingKey.GenerateViewingKeyPair();
        var (wrongPrivateKey, _) = ViewingKey.GenerateViewingKeyPair();

        UInt256 value = new UInt256(99);
        var blindingFactor = new byte[32];
        blindingFactor[31] = 0x10;

        var encrypted = ViewingKey.EncryptForViewer(viewerPublicKey, value, blindingFactor);

        var act = () => ViewingKey.DecryptWithViewingKey(wrongPrivateKey, encrypted);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void ViewingKey_DifferentValuesProduceDifferentEncryptions()
    {
        var (_, publicKey) = ViewingKey.GenerateViewingKeyPair();

        var blindingFactor = new byte[32];
        blindingFactor[31] = 0x05;

        UInt256 value1 = new UInt256(10);
        UInt256 value2 = new UInt256(20);

        var encrypted1 = ViewingKey.EncryptForViewer(publicKey, value1, blindingFactor);
        var encrypted2 = ViewingKey.EncryptForViewer(publicKey, value2, blindingFactor);

        encrypted1.Should().NotEqual(encrypted2);
    }

    [Fact]
    public void DisclosureProof_CreateAndVerify_RoundTrip()
    {
        UInt256 value = new UInt256(500);
        var blindingFactor = new byte[32];
        blindingFactor[31] = 0x07;

        var commitment = PedersenCommitment.Commit(value, blindingFactor);
        var proof = DisclosureProof.Create(value, blindingFactor);

        DisclosureProof.Verify(commitment, proof).Should().BeTrue();
    }

    [Fact]
    public void DisclosureProof_WrongValue_FailsVerification()
    {
        UInt256 correctValue = new UInt256(100);
        var blindingFactor = new byte[32];
        blindingFactor[31] = 0x0A;

        var commitment = PedersenCommitment.Commit(correctValue, blindingFactor);

        // Correct proof should pass
        var correctProof = DisclosureProof.Create(correctValue, blindingFactor);
        DisclosureProof.Verify(commitment, correctProof).Should().BeTrue();

        // Wrong value should fail
        UInt256 wrongValue = new UInt256(200);
        var wrongProof = DisclosureProof.Create(wrongValue, blindingFactor);
        DisclosureProof.Verify(commitment, wrongProof).Should().BeFalse();
    }

    [Fact]
    public void DisclosureProof_WrongBlinding_FailsVerification()
    {
        UInt256 value = new UInt256(50);
        var correctBlinding = new byte[32];
        correctBlinding[31] = 0x0B;

        var wrongBlinding = new byte[32];
        wrongBlinding[31] = 0x0C;

        var commitment = PedersenCommitment.Commit(value, correctBlinding);
        var proof = DisclosureProof.Create(value, wrongBlinding);

        DisclosureProof.Verify(commitment, proof).Should().BeFalse();
    }

    [Fact]
    public void DisclosureProof_NullProof_ReturnsFalse()
    {
        UInt256 value = new UInt256(10);
        var blindingFactor = new byte[32];
        blindingFactor[31] = 0x01;

        var commitment = PedersenCommitment.Commit(value, blindingFactor);

        DisclosureProof.Verify(commitment, null!).Should().BeFalse();
    }

    [Fact]
    public void ConfidentialityModule_IsOperational_ReturnsTrue()
    {
        ConfidentialityModule.IsOperational().Should().BeTrue();
    }

    [Fact]
    public void ConfidentialityModule_HasCorrectNameAndVersion()
    {
        ConfidentialityModule.Name.Should().Be("Basalt.Confidentiality");
        ConfidentialityModule.Version.Should().Be("1.0.0");
    }

    [Fact]
    public void ViewingKey_AuditorCanVerifyTransaction()
    {
        // 1. Create a Pedersen commitment to a transaction value
        UInt256 transactionValue = new UInt256(1000);
        var blindingFactor = new byte[32];
        blindingFactor[31] = 0x42;

        var commitment = PedersenCommitment.Commit(transactionValue, blindingFactor);

        // 2. Auditor generates a viewing key pair
        var (auditorPrivateKey, auditorPublicKey) = ViewingKey.GenerateViewingKeyPair();

        // 3. Sender encrypts the opening (value + blinding) for the auditor
        var encrypted = ViewingKey.EncryptForViewer(auditorPublicKey, transactionValue, blindingFactor);

        // 4. Auditor decrypts the disclosure
        var (disclosedValue, disclosedBlinding) = ViewingKey.DecryptWithViewingKey(auditorPrivateKey, encrypted);

        // 5. Auditor verifies the commitment opens correctly
        PedersenCommitment.Open(commitment, disclosedValue, disclosedBlinding).Should().BeTrue();

        // Also verify the disclosed values match the originals
        disclosedValue.Should().Be(transactionValue);
        disclosedBlinding.Should().Equal(blindingFactor);
    }

    // ── DisclosureProof argument validation ─────────────────────────────────

    [Fact]
    public void DisclosureProof_Create_NullBlinding_Throws()
    {
        UInt256 value = new UInt256(10);

        var act = () => DisclosureProof.Create(value, null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DisclosureProof_Create_WrongSizeBlinding_Throws()
    {
        UInt256 value = new UInt256(10);
        var wrongBlinding = new byte[31]; // should be 32

        var act = () => DisclosureProof.Create(value, wrongBlinding);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DisclosureProof_Create_ClonesBlindingFactor()
    {
        UInt256 value = new UInt256(10);
        var blinding = new byte[32];
        blinding[31] = 0x05;

        var proof = DisclosureProof.Create(value, blinding);

        // Mutating the original should not affect the proof
        blinding[31] = 0xFF;
        proof.BlindingFactor[31].Should().Be(0x05);
    }

    [Fact]
    public void DisclosureProof_Verify_WrongSizeCommitment_ReturnsFalse()
    {
        UInt256 value = new UInt256(10);
        var blinding = new byte[32]; blinding[31] = 0x05;

        var proof = DisclosureProof.Create(value, blinding);

        var shortCommitment = new byte[47]; // wrong size
        DisclosureProof.Verify(shortCommitment, proof).Should().BeFalse();
    }

    [Fact]
    public void DisclosureProof_Verify_ZeroValue_Succeeds()
    {
        UInt256 value = UInt256.Zero;
        var blinding = new byte[32]; blinding[31] = 0x0A;

        var commitment = PedersenCommitment.Commit(value, blinding);
        var proof = DisclosureProof.Create(value, blinding);

        DisclosureProof.Verify(commitment, proof).Should().BeTrue();
    }

    // ── ViewingKey argument validation ──────────────────────────────────────

    [Fact]
    public void ViewingKey_EncryptForViewer_WrongSizePublicKey_Throws()
    {
        var wrongKey = new byte[31]; // should be 32
        UInt256 value = new UInt256(10);
        var blinding = new byte[32]; blinding[31] = 0x01;

        var act = () => ViewingKey.EncryptForViewer(wrongKey, value, blinding);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ViewingKey_EncryptForViewer_WrongSizeBlinding_Throws()
    {
        var (_, publicKey) = ViewingKey.GenerateViewingKeyPair();
        UInt256 value = new UInt256(10);
        var wrongBlinding = new byte[31]; // should be 32

        var act = () => ViewingKey.EncryptForViewer(publicKey, value, wrongBlinding);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ViewingKey_DecryptWithViewingKey_WrongSizePrivateKey_Throws()
    {
        var (_, publicKey) = ViewingKey.GenerateViewingKeyPair();
        UInt256 value = new UInt256(10);
        var blinding = new byte[32]; blinding[31] = 0x01;

        var encrypted = ViewingKey.EncryptForViewer(publicKey, value, blinding);

        var wrongKey = new byte[31]; // should be 32
        var act = () => ViewingKey.DecryptWithViewingKey(wrongKey, encrypted);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ViewingKey_DecryptWithViewingKey_WrongSizeEncrypted_Throws()
    {
        var (privateKey, _) = ViewingKey.GenerateViewingKeyPair();
        var truncated = new byte[123]; // should be 124

        var act = () => ViewingKey.DecryptWithViewingKey(privateKey, truncated);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ViewingKey_DecryptWithViewingKey_TooLongEncrypted_Throws()
    {
        var (privateKey, _) = ViewingKey.GenerateViewingKeyPair();
        var tooLong = new byte[125]; // should be 124

        var act = () => ViewingKey.DecryptWithViewingKey(privateKey, tooLong);
        act.Should().Throw<ArgumentException>();
    }

    // ── ViewingKey round-trip edge cases ─────────────────────────────────────

    [Fact]
    public void ViewingKey_RoundTrip_ZeroValue()
    {
        var (privateKey, publicKey) = ViewingKey.GenerateViewingKeyPair();

        UInt256 value = UInt256.Zero;
        var blinding = new byte[32]; blinding[31] = 0x99;

        var encrypted = ViewingKey.EncryptForViewer(publicKey, value, blinding);
        var (decryptedValue, decryptedBlinding) = ViewingKey.DecryptWithViewingKey(privateKey, encrypted);

        decryptedValue.Should().Be(UInt256.Zero);
        decryptedBlinding.Should().Equal(blinding);
    }

    [Fact]
    public void ViewingKey_RoundTrip_LargeValue()
    {
        var (privateKey, publicKey) = ViewingKey.GenerateViewingKeyPair();

        // Value with bytes set across the full 32-byte range
        var valueBytes = new byte[32];
        for (int i = 0; i < 32; i++) valueBytes[i] = (byte)(i + 1);
        UInt256 value = new UInt256(valueBytes, isBigEndian: true);

        var blinding = new byte[32];
        for (int i = 0; i < 32; i++) blinding[i] = (byte)(32 - i);

        var encrypted = ViewingKey.EncryptForViewer(publicKey, value, blinding);
        var (decryptedValue, decryptedBlinding) = ViewingKey.DecryptWithViewingKey(privateKey, encrypted);

        decryptedValue.Should().Be(value);
        decryptedBlinding.Should().Equal(blinding);
    }

    [Fact]
    public void ViewingKey_EncryptForViewer_NonDeterministic()
    {
        // Each encryption should use a different ephemeral key, so
        // encrypting the same data twice should yield different ciphertexts.
        var (_, publicKey) = ViewingKey.GenerateViewingKeyPair();

        UInt256 value = new UInt256(42);
        var blinding = new byte[32]; blinding[31] = 0x42;

        var encrypted1 = ViewingKey.EncryptForViewer(publicKey, value, blinding);
        var encrypted2 = ViewingKey.EncryptForViewer(publicKey, value, blinding);

        // Ephemeral public key is the first 32 bytes - those should differ
        encrypted1.AsSpan(0, 32).ToArray().Should().NotEqual(encrypted2.AsSpan(0, 32).ToArray());
    }

    [Fact]
    public void ViewingKey_MultipleViewers_EachCanDecrypt()
    {
        UInt256 value = new UInt256(500);
        var blinding = new byte[32]; blinding[31] = 0x07;

        var (privateKey1, publicKey1) = ViewingKey.GenerateViewingKeyPair();
        var (privateKey2, publicKey2) = ViewingKey.GenerateViewingKeyPair();

        var encrypted1 = ViewingKey.EncryptForViewer(publicKey1, value, blinding);
        var encrypted2 = ViewingKey.EncryptForViewer(publicKey2, value, blinding);

        var (v1, b1) = ViewingKey.DecryptWithViewingKey(privateKey1, encrypted1);
        var (v2, b2) = ViewingKey.DecryptWithViewingKey(privateKey2, encrypted2);

        v1.Should().Be(value);
        v2.Should().Be(value);
        b1.Should().Equal(blinding);
        b2.Should().Equal(blinding);
    }

    [Fact]
    public void ViewingKey_GenerateViewingKeyPair_KeysAre32Bytes()
    {
        var (privateKey, publicKey) = ViewingKey.GenerateViewingKeyPair();

        privateKey.Should().HaveCount(32);
        publicKey.Should().HaveCount(32);
    }

    [Fact]
    public void ViewingKey_GenerateViewingKeyPair_KeysAreUnique()
    {
        var (priv1, pub1) = ViewingKey.GenerateViewingKeyPair();
        var (priv2, pub2) = ViewingKey.GenerateViewingKeyPair();

        priv1.Should().NotEqual(priv2);
        pub1.Should().NotEqual(pub2);
    }
}
