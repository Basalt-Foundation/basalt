using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using FluentAssertions;
using Xunit;

namespace Basalt.Integration.Tests;

/// <summary>
/// Integration tests verifying cryptographic operations across the stack:
/// key generation, signing, verification, address derivation, hashing.
/// </summary>
public class CryptoIntegrationTests
{
    [Fact]
    public void KeyPair_Address_Derivation_Is_Deterministic()
    {
        var (privKey1, pubKey1) = Ed25519Signer.GenerateKeyPair();
        var addr1 = Ed25519Signer.DeriveAddress(pubKey1);

        // Derive public key again from private key
        var pubKey2 = Ed25519Signer.GetPublicKey(privKey1);
        var addr2 = Ed25519Signer.DeriveAddress(pubKey2);

        addr1.Should().Be(addr2, "same private key should yield same address");
    }

    [Fact]
    public void Transaction_Signature_Roundtrip()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 42,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(1000),
            GasLimit = 21_000,
            GasPrice = new UInt256(5),
            ChainId = 31337,
        }, privKey);

        tx.VerifySignature().Should().BeTrue();
        tx.Signature.IsEmpty.Should().BeFalse();
        tx.SenderPublicKey.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Tampered_Transaction_Fails_Verification()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(1000),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = 31337,
        }, privKey);

        // Create tampered transaction (different value, same signature)
        var tampered = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(999999), // Different value!
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = 31337,
            Signature = tx.Signature, // Original signature
            SenderPublicKey = tx.SenderPublicKey,
        };

        tampered.VerifySignature().Should().BeFalse();
    }

    [Fact]
    public void Blake3_Hash_Is_Deterministic()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var hash1 = Blake3Hasher.Hash(data);
        var hash2 = Blake3Hasher.Hash(data);

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Different_Data_Produces_Different_Hashes()
    {
        var hash1 = Blake3Hasher.Hash(new byte[] { 1, 2, 3 });
        var hash2 = Blake3Hasher.Hash(new byte[] { 4, 5, 6 });

        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void Transaction_Hash_Is_Deterministic()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);

        var tx1 = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = 31337,
        }, privKey);

        // Same key, same params -> same hash
        var tx2 = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = 31337,
        }, privKey);

        tx1.Hash.Should().Be(tx2.Hash, "identical transactions should have identical hashes");
    }
}
