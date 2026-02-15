using Basalt.Bridge;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Bridge.Tests;

public class MultisigRelayerTests
{
    // ── Constructor ──────────────────────────────────────────────────────

    [Fact]
    public void Constructor_Rejects_Zero_Threshold()
    {
        var act = () => new MultisigRelayer(0);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_Rejects_Negative_Threshold()
    {
        var act = () => new MultisigRelayer(-1);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_Accepts_Threshold_Of_One()
    {
        var relayer = new MultisigRelayer(1);
        relayer.Threshold.Should().Be(1);
    }

    [Fact]
    public void Constructor_Accepts_Large_Threshold()
    {
        var relayer = new MultisigRelayer(100);
        relayer.Threshold.Should().Be(100);
    }

    // ── AddRelayer ───────────────────────────────────────────────────────

    [Fact]
    public void AddRelayer_Registers_Relayer()
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        relayer.IsRelayer(pubKey.ToArray()).Should().BeTrue();
        relayer.RelayerCount.Should().Be(1);
    }

    [Fact]
    public void AddRelayer_Multiple_Relayers()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();
        var k2 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(2);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());
        relayer.AddRelayer(k2.PublicKey.ToArray());

        relayer.RelayerCount.Should().Be(3);
        relayer.IsRelayer(k0.PublicKey.ToArray()).Should().BeTrue();
        relayer.IsRelayer(k1.PublicKey.ToArray()).Should().BeTrue();
        relayer.IsRelayer(k2.PublicKey.ToArray()).Should().BeTrue();
    }

    [Fact]
    public void AddRelayer_Same_Key_Twice_Is_Idempotent()
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());
        relayer.AddRelayer(pubKey.ToArray()); // duplicate add

        relayer.RelayerCount.Should().Be(1);
        relayer.IsRelayer(pubKey.ToArray()).Should().BeTrue();
    }

    // ── RemoveRelayer ────────────────────────────────────────────────────

    [Fact]
    public void RemoveRelayer_Deregisters()
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());
        relayer.RemoveRelayer(pubKey.ToArray());

        relayer.IsRelayer(pubKey.ToArray()).Should().BeFalse();
        relayer.RelayerCount.Should().Be(0);
    }

    [Fact]
    public void RemoveRelayer_Nonexistent_Does_Not_Throw()
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);

        // Should not throw when removing a key that was never added
        var act = () => relayer.RemoveRelayer(pubKey.ToArray());
        act.Should().NotThrow();
        relayer.RelayerCount.Should().Be(0);
    }

    [Fact]
    public void RemoveRelayer_Only_Removes_Target()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());
        relayer.RemoveRelayer(k0.PublicKey.ToArray());

        relayer.RelayerCount.Should().Be(1);
        relayer.IsRelayer(k0.PublicKey.ToArray()).Should().BeFalse();
        relayer.IsRelayer(k1.PublicKey.ToArray()).Should().BeTrue();
    }

    [Fact]
    public void RemoveRelayer_Then_Readd_Works()
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);

        relayer.AddRelayer(pubKey.ToArray());
        relayer.RemoveRelayer(pubKey.ToArray());
        relayer.IsRelayer(pubKey.ToArray()).Should().BeFalse();

        relayer.AddRelayer(pubKey.ToArray());
        relayer.IsRelayer(pubKey.ToArray()).Should().BeTrue();
        relayer.RelayerCount.Should().Be(1);
    }

    // ── IsRelayer ────────────────────────────────────────────────────────

    [Fact]
    public void IsRelayer_Returns_False_For_Unregistered_Key()
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);

        relayer.IsRelayer(pubKey.ToArray()).Should().BeFalse();
    }

    // ── GetRelayers ──────────────────────────────────────────────────────

    [Fact]
    public void GetRelayers_Returns_All_Registered()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());

        relayer.GetRelayers().Should().HaveCount(2);
    }

    [Fact]
    public void GetRelayers_Empty_When_No_Relayers_Added()
    {
        var relayer = new MultisigRelayer(1);
        relayer.GetRelayers().Should().BeEmpty();
    }

    [Fact]
    public void GetRelayers_Reflects_Removals()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());
        relayer.RemoveRelayer(k0.PublicKey.ToArray());

        var list = relayer.GetRelayers();
        list.Should().HaveCount(1);
        list[0].Should().BeEquivalentTo(k1.PublicKey.ToArray());
    }

    // ── Sign ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sign_Returns_Valid_Signature_Structure()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var msgHash = Blake3Hasher.Hash([1, 2, 3]).ToArray();

        var sig = MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray());

        sig.PublicKey.Should().BeEquivalentTo(pubKey.ToArray());
        sig.Signature.Should().HaveCount(64);
        sig.Signature.Should().NotBeEquivalentTo(new byte[64]); // should not be all zeros
    }

    [Fact]
    public void Sign_Different_Messages_Produce_Different_Signatures()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var hash1 = Blake3Hasher.Hash([1]).ToArray();
        var hash2 = Blake3Hasher.Hash([2]).ToArray();

        var sig1 = MultisigRelayer.Sign(hash1, privKey, pubKey.ToArray());
        var sig2 = MultisigRelayer.Sign(hash2, privKey, pubKey.ToArray());

        sig1.Signature.Should().NotBeEquivalentTo(sig2.Signature);
    }

    [Fact]
    public void Sign_Same_Message_Same_Key_Produces_Same_Signature()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var msgHash = Blake3Hasher.Hash([42]).ToArray();

        var sig1 = MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray());
        var sig2 = MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray());

        sig1.Signature.Should().BeEquivalentTo(sig2.Signature);
    }

    // ── VerifyMessage ────────────────────────────────────────────────────

    [Fact]
    public void VerifyMessage_With_1of1_Succeeds()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var msg = new byte[] { 1, 2, 3 };
        var msgHash = Blake3Hasher.Hash(msg).ToArray();
        var sig = MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray());

        relayer.VerifyMessage(msgHash, [sig]).Should().BeTrue();
    }

    [Fact]
    public void VerifyMessage_With_2of3_Succeeds()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();
        var k2 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(2);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());
        relayer.AddRelayer(k2.PublicKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig0 = MultisigRelayer.Sign(msgHash, k0.PrivateKey, k0.PublicKey.ToArray());
        var sig2 = MultisigRelayer.Sign(msgHash, k2.PrivateKey, k2.PublicKey.ToArray());

        relayer.VerifyMessage(msgHash, [sig0, sig2]).Should().BeTrue();
    }

    [Fact]
    public void VerifyMessage_With_3of3_Full_Quorum_Succeeds()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();
        var k2 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(3);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());
        relayer.AddRelayer(k2.PublicKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig0 = MultisigRelayer.Sign(msgHash, k0.PrivateKey, k0.PublicKey.ToArray());
        var sig1 = MultisigRelayer.Sign(msgHash, k1.PrivateKey, k1.PublicKey.ToArray());
        var sig2 = MultisigRelayer.Sign(msgHash, k2.PrivateKey, k2.PublicKey.ToArray());

        relayer.VerifyMessage(msgHash, [sig0, sig1, sig2]).Should().BeTrue();
    }

    [Fact]
    public void VerifyMessage_Below_Threshold_Fails()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(2);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig0 = MultisigRelayer.Sign(msgHash, k0.PrivateKey, k0.PublicKey.ToArray());

        relayer.VerifyMessage(msgHash, [sig0]).Should().BeFalse();
    }

    [Fact]
    public void VerifyMessage_Empty_Signatures_Fails()
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        relayer.VerifyMessage(msgHash, []).Should().BeFalse();
    }

    [Fact]
    public void VerifyMessage_Rejects_Duplicate_Signatures()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(2);
        relayer.AddRelayer(pubKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig = MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray());

        // Same signature twice -- should not count as 2
        relayer.VerifyMessage(msgHash, [sig, sig]).Should().BeFalse();
    }

    [Fact]
    public void VerifyMessage_Rejects_NonRelayer_Signature()
    {
        var (privKey1, pubKey1) = Ed25519Signer.GenerateKeyPair();
        var (privKey2, pubKey2) = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey1.ToArray());
        // pubKey2 is NOT a relayer

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig2 = MultisigRelayer.Sign(msgHash, privKey2, pubKey2.ToArray());

        relayer.VerifyMessage(msgHash, [sig2]).Should().BeFalse();
    }

    [Fact]
    public void VerifyMessage_Rejects_Invalid_Signature()
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var badSig = new RelayerSignature
        {
            PublicKey = pubKey.ToArray(),
            Signature = new byte[64], // zeros = invalid
        };

        relayer.VerifyMessage(msgHash, [badSig]).Should().BeFalse();
    }

    [Fact]
    public void VerifyMessage_Wrong_MessageHash_Fails()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var realHash = Blake3Hasher.Hash([1]).ToArray();
        var wrongHash = Blake3Hasher.Hash([2]).ToArray();
        var sig = MultisigRelayer.Sign(realHash, privKey, pubKey.ToArray());

        // Signature was over realHash, verifying against wrongHash should fail
        relayer.VerifyMessage(wrongHash, [sig]).Should().BeFalse();
    }

    [Fact]
    public void VerifyMessage_Mixed_Valid_And_Invalid_Meets_Threshold()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();
        var kNonRelayer = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(2);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig0 = MultisigRelayer.Sign(msgHash, k0.PrivateKey, k0.PublicKey.ToArray());
        var sig1 = MultisigRelayer.Sign(msgHash, k1.PrivateKey, k1.PublicKey.ToArray());
        var sigBad = MultisigRelayer.Sign(msgHash, kNonRelayer.PrivateKey, kNonRelayer.PublicKey.ToArray());

        // 2 valid + 1 non-relayer; threshold=2, should pass
        relayer.VerifyMessage(msgHash, [sigBad, sig0, sig1]).Should().BeTrue();
    }

    [Fact]
    public void VerifyMessage_Mixed_Valid_And_Invalid_Below_Threshold()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();
        var kNonRelayer = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(2);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig0 = MultisigRelayer.Sign(msgHash, k0.PrivateKey, k0.PublicKey.ToArray());
        var sigBad = MultisigRelayer.Sign(msgHash, kNonRelayer.PrivateKey, kNonRelayer.PublicKey.ToArray());

        // 1 valid + 1 non-relayer; threshold=2, should fail
        relayer.VerifyMessage(msgHash, [sigBad, sig0]).Should().BeFalse();
    }

    [Fact]
    public void VerifyMessage_After_Relayer_Removed_Fails()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig = MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray());

        // Valid while relayer is registered
        relayer.VerifyMessage(msgHash, [sig]).Should().BeTrue();

        // Remove the relayer, same signature should now fail
        relayer.RemoveRelayer(pubKey.ToArray());
        relayer.VerifyMessage(msgHash, [sig]).Should().BeFalse();
    }

    [Fact]
    public void VerifyMessage_Exceeds_Threshold_With_Extra_Signatures()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();
        var k2 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());
        relayer.AddRelayer(k2.PublicKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig0 = MultisigRelayer.Sign(msgHash, k0.PrivateKey, k0.PublicKey.ToArray());
        var sig1 = MultisigRelayer.Sign(msgHash, k1.PrivateKey, k1.PublicKey.ToArray());
        var sig2 = MultisigRelayer.Sign(msgHash, k2.PrivateKey, k2.PublicKey.ToArray());

        // Threshold=1 but 3 valid sigs; should succeed
        relayer.VerifyMessage(msgHash, [sig0, sig1, sig2]).Should().BeTrue();
    }

    [Fact]
    public void VerifyMessage_With_No_Relayers_Registered_Fails()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        // No relayers added

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig = MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray());

        relayer.VerifyMessage(msgHash, [sig]).Should().BeFalse();
    }

    // ── Thread safety ────────────────────────────────────────────────────

    [Fact]
    public void Concurrent_AddRelayer_Is_ThreadSafe()
    {
        var relayer = new MultisigRelayer(1);
        var keys = Enumerable.Range(0, 50)
            .Select(_ => Ed25519Signer.GenerateKeyPair())
            .ToArray();

        Parallel.ForEach(keys, kp =>
        {
            relayer.AddRelayer(kp.PublicKey.ToArray());
        });

        relayer.RelayerCount.Should().Be(50);
    }

    [Fact]
    public void Concurrent_VerifyMessage_Is_ThreadSafe()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(2);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());

        var msgHash = Blake3Hasher.Hash([42]).ToArray();
        var sig0 = MultisigRelayer.Sign(msgHash, k0.PrivateKey, k0.PublicKey.ToArray());
        var sig1 = MultisigRelayer.Sign(msgHash, k1.PrivateKey, k1.PublicKey.ToArray());

        var results = new bool[100];
        Parallel.For(0, 100, i =>
        {
            results[i] = relayer.VerifyMessage(msgHash, [sig0, sig1]);
        });

        results.Should().AllSatisfy(r => r.Should().BeTrue());
    }
}
