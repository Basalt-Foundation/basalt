using Basalt.Bridge;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Bridge.Tests;

public class BridgeStateTests
{
    private static byte[] Addr(byte seed) { var a = new byte[20]; a[19] = seed; return a; }

    private readonly BridgeState _bridge = new() { BasaltChainId = 1, EthereumChainId = 11155111 };

    // ── Lock ─────────────────────────────────────────────────────────────

    [Fact]
    public void Lock_Creates_Deposit_With_Incrementing_Nonce()
    {
        var sender = Addr(1);
        var recipient = Addr(2);

        var d0 = _bridge.Lock(sender, recipient, 1000);
        var d1 = _bridge.Lock(sender, recipient, 2000);

        d0.Nonce.Should().Be(0);
        d1.Nonce.Should().Be(1);
        d0.Amount.Should().Be(1000);
        d1.Amount.Should().Be(2000);
    }

    [Fact]
    public void Lock_Sets_Correct_Fields()
    {
        var sender = Addr(1);
        var recipient = Addr(2);

        var deposit = _bridge.Lock(sender, recipient, 500);

        deposit.Sender.Should().BeEquivalentTo(sender);
        deposit.Recipient.Should().BeEquivalentTo(recipient);
        deposit.SourceChainId.Should().Be(1);
        deposit.DestinationChainId.Should().Be(11155111);
        deposit.Direction.Should().Be(BridgeDirection.BasaltToEthereum);
        deposit.Status.Should().Be(BridgeTransferStatus.Pending);
    }

    [Fact]
    public void Lock_Tracks_Locked_Balance()
    {
        var sender = Addr(1);
        var recipient = Addr(2);

        _bridge.Lock(sender, recipient, 1000);
        _bridge.Lock(sender, recipient, 2000);

        _bridge.GetLockedBalance().Should().Be(3000);
    }

    [Fact]
    public void Lock_Zero_Amount_Throws()
    {
        var act = () => _bridge.Lock(Addr(1), Addr(2), 0);
        act.Should().Throw<BridgeException>().WithMessage("*zero*");
    }

    [Fact]
    public void Lock_With_Custom_Token_Address()
    {
        var token = new byte[20];
        token[0] = 0xAA;
        token[19] = 0xBB;

        var deposit = _bridge.Lock(Addr(1), Addr(2), 5000, token);

        deposit.TokenAddress.Should().BeEquivalentTo(token);
        deposit.Amount.Should().Be(5000);
    }

    [Fact]
    public void Lock_Null_Token_Uses_Native_Token()
    {
        var deposit = _bridge.Lock(Addr(1), Addr(2), 100, tokenAddress: null);
        deposit.TokenAddress.Should().BeEquivalentTo(new byte[20]);
    }

    [Fact]
    public void Lock_Tracks_Per_Token_Balance()
    {
        var tokenA = new byte[20]; tokenA[0] = 0xAA;
        var tokenB = new byte[20]; tokenB[0] = 0xBB;

        _bridge.Lock(Addr(1), Addr(2), 1000, tokenA);
        _bridge.Lock(Addr(1), Addr(2), 2000, tokenA);
        _bridge.Lock(Addr(1), Addr(2), 500, tokenB);

        _bridge.GetLockedBalance(tokenA).Should().Be(3000);
        _bridge.GetLockedBalance(tokenB).Should().Be(500);
        _bridge.GetLockedBalance().Should().Be(0); // native token untouched
    }

    [Fact]
    public void Lock_Sets_Timestamp()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var deposit = _bridge.Lock(Addr(1), Addr(2), 100);
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        deposit.Timestamp.Should().BeInRange(before, after);
    }

    [Fact]
    public void Lock_Multiple_Senders_Track_Same_Balance()
    {
        _bridge.Lock(Addr(1), Addr(10), 100);
        _bridge.Lock(Addr(2), Addr(20), 200);
        _bridge.Lock(Addr(3), Addr(30), 300);

        // All use native token, so locked balance accumulates
        _bridge.GetLockedBalance().Should().Be(600);
        _bridge.CurrentNonce.Should().Be(3);
    }

    // ── CurrentNonce ─────────────────────────────────────────────────────

    [Fact]
    public void CurrentNonce_Starts_At_Zero()
    {
        var bridge = new BridgeState();
        bridge.CurrentNonce.Should().Be(0);
    }

    [Fact]
    public void CurrentNonce_Increments_After_Each_Lock()
    {
        _bridge.CurrentNonce.Should().Be(0);
        _bridge.Lock(Addr(1), Addr(2), 100);
        _bridge.CurrentNonce.Should().Be(1);
        _bridge.Lock(Addr(1), Addr(2), 200);
        _bridge.CurrentNonce.Should().Be(2);
        _bridge.Lock(Addr(1), Addr(2), 300);
        _bridge.CurrentNonce.Should().Be(3);
    }

    // ── GetDeposit ───────────────────────────────────────────────────────

    [Fact]
    public void GetDeposit_Returns_Deposit_By_Nonce()
    {
        _bridge.Lock(Addr(1), Addr(2), 100);
        var deposit = _bridge.GetDeposit(0);
        deposit.Should().NotBeNull();
        deposit!.Amount.Should().Be(100);
    }

    [Fact]
    public void GetDeposit_Returns_Null_For_Unknown_Nonce()
    {
        _bridge.GetDeposit(999).Should().BeNull();
    }

    [Fact]
    public void GetDeposit_Returns_Correct_Deposit_Among_Multiple()
    {
        _bridge.Lock(Addr(1), Addr(2), 100);
        _bridge.Lock(Addr(3), Addr(4), 200);
        _bridge.Lock(Addr(5), Addr(6), 300);

        var d1 = _bridge.GetDeposit(1);
        d1.Should().NotBeNull();
        d1!.Amount.Should().Be(200);
        d1.Sender.Should().BeEquivalentTo(Addr(3));
    }

    // ── ConfirmDeposit ───────────────────────────────────────────────────

    [Fact]
    public void ConfirmDeposit_Updates_Status()
    {
        _bridge.Lock(Addr(1), Addr(2), 100);
        _bridge.ConfirmDeposit(0, 42).Should().BeTrue();
        _bridge.GetDeposit(0)!.Status.Should().Be(BridgeTransferStatus.Confirmed);
    }

    [Fact]
    public void ConfirmDeposit_Unknown_Nonce_Returns_False()
    {
        _bridge.ConfirmDeposit(999, 42).Should().BeFalse();
    }

    [Fact]
    public void ConfirmDeposit_AlreadyConfirmed_Returns_False()
    {
        // BRIDGE-05: re-confirm is no longer allowed
        _bridge.Lock(Addr(1), Addr(2), 100);
        _bridge.ConfirmDeposit(0, 42).Should().BeTrue();
        _bridge.ConfirmDeposit(0, 43).Should().BeFalse(); // already confirmed
        _bridge.GetDeposit(0)!.Status.Should().Be(BridgeTransferStatus.Confirmed);
    }

    // ── FinalizeDeposit ──────────────────────────────────────────────────

    [Fact]
    public void FinalizeDeposit_Updates_Status()
    {
        _bridge.Lock(Addr(1), Addr(2), 100);
        _bridge.ConfirmDeposit(0, 42);
        _bridge.FinalizeDeposit(0).Should().BeTrue();
        _bridge.GetDeposit(0)!.Status.Should().Be(BridgeTransferStatus.Finalized);
    }

    [Fact]
    public void FinalizeDeposit_Unknown_Nonce_Returns_False()
    {
        _bridge.FinalizeDeposit(999).Should().BeFalse();
    }

    [Fact]
    public void FinalizeDeposit_Without_Prior_Confirm_Returns_False()
    {
        // BRIDGE-05: FinalizeDeposit enforces the Pending->Confirmed->Finalized order
        _bridge.Lock(Addr(1), Addr(2), 100);
        _bridge.FinalizeDeposit(0).Should().BeFalse(); // must be Confirmed first
        _bridge.GetDeposit(0)!.Status.Should().Be(BridgeTransferStatus.Pending);
    }

    // ── GetPendingDeposits ───────────────────────────────────────────────

    [Fact]
    public void GetPendingDeposits_Returns_NonFinalized()
    {
        _bridge.Lock(Addr(1), Addr(2), 100);
        _bridge.Lock(Addr(1), Addr(2), 200);
        _bridge.ConfirmDeposit(0, 10); // BRIDGE-05: must confirm before finalize
        _bridge.FinalizeDeposit(0);

        var pending = _bridge.GetPendingDeposits();
        pending.Should().HaveCount(1);
        pending[0].Nonce.Should().Be(1);
    }

    [Fact]
    public void GetPendingDeposits_Includes_Confirmed_But_Not_Finalized()
    {
        _bridge.Lock(Addr(1), Addr(2), 100); // Pending
        _bridge.Lock(Addr(1), Addr(2), 200); // Pending -> Confirmed
        _bridge.Lock(Addr(1), Addr(2), 300); // Pending -> Confirmed -> Finalized
        _bridge.ConfirmDeposit(1, 10);
        _bridge.ConfirmDeposit(2, 11); // BRIDGE-05: must confirm before finalize
        _bridge.FinalizeDeposit(2);

        var pending = _bridge.GetPendingDeposits();
        pending.Should().HaveCount(2);
        pending[0].Nonce.Should().Be(0); // Still Pending
        pending[1].Nonce.Should().Be(1); // Confirmed (included in pending)
    }

    [Fact]
    public void GetPendingDeposits_Returns_In_Nonce_Order()
    {
        _bridge.Lock(Addr(1), Addr(2), 300);
        _bridge.Lock(Addr(1), Addr(2), 100);
        _bridge.Lock(Addr(1), Addr(2), 200);

        var pending = _bridge.GetPendingDeposits();
        pending.Should().HaveCount(3);
        pending[0].Nonce.Should().Be(0);
        pending[1].Nonce.Should().Be(1);
        pending[2].Nonce.Should().Be(2);
    }

    [Fact]
    public void GetPendingDeposits_Empty_When_All_Finalized()
    {
        _bridge.Lock(Addr(1), Addr(2), 100);
        _bridge.Lock(Addr(1), Addr(2), 200);
        _bridge.ConfirmDeposit(0, 10); // BRIDGE-05: must confirm before finalize
        _bridge.ConfirmDeposit(1, 11);
        _bridge.FinalizeDeposit(0);
        _bridge.FinalizeDeposit(1);

        _bridge.GetPendingDeposits().Should().BeEmpty();
    }

    [Fact]
    public void GetPendingDeposits_Empty_When_No_Deposits()
    {
        _bridge.GetPendingDeposits().Should().BeEmpty();
    }

    // ── GetLockedBalance ─────────────────────────────────────────────────

    [Fact]
    public void GetLockedBalance_Zero_When_No_Deposits()
    {
        _bridge.GetLockedBalance().Should().Be(0);
    }

    [Fact]
    public void GetLockedBalance_Untracked_Token_Returns_Zero()
    {
        var unknownToken = new byte[20]; unknownToken[0] = 0xFF;
        _bridge.GetLockedBalance(unknownToken).Should().Be(0);
    }

    // ── Unlock ───────────────────────────────────────────────────────────

    [Fact]
    public void Unlock_With_Valid_Multisig_Succeeds()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };

        var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal);
        var sig = MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray());
        withdrawal.Signatures.Add(sig);

        _bridge.Unlock(withdrawal, relayer).Should().BeTrue();
    }

    [Fact]
    public void Unlock_Prevents_Replay()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };

        var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal);
        withdrawal.Signatures.Add(MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray()));

        _bridge.Unlock(withdrawal, relayer).Should().BeTrue();
        _bridge.Unlock(withdrawal, relayer).Should().BeFalse(); // Replay blocked
    }

    [Fact]
    public void Unlock_With_Insufficient_Signatures_Fails()
    {
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(2);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };

        var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal);
        withdrawal.Signatures.Add(MultisigRelayer.Sign(msgHash, k0.PrivateKey, k0.PublicKey.ToArray()));

        // Only 1 of 2 required signatures
        _bridge.Unlock(withdrawal, relayer).Should().BeFalse();
    }

    [Fact]
    public void Unlock_With_Invalid_Signatures_Fails()
    {
        var (_, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };

        withdrawal.Signatures.Add(new RelayerSignature
        {
            PublicKey = pubKey.ToArray(),
            Signature = new byte[64], // invalid zeros
        });

        _bridge.Unlock(withdrawal, relayer).Should().BeFalse();
    }

    [Fact]
    public void Unlock_Multiple_Different_Nonces_Succeeds()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        for (ulong i = 0; i < 5; i++)
        {
            var withdrawal = new BridgeWithdrawal
            {
                DepositNonce = i,
                Recipient = Addr(2),
                Amount = 100 * (i + 1),
                StateRoot = new byte[32],
            };

            var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal);
            withdrawal.Signatures.Add(MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray()));

            _bridge.Unlock(withdrawal, relayer).Should().BeTrue($"withdrawal nonce {i} should succeed");
        }
    }

    [Fact]
    public void Unlock_With_Valid_MerkleProof_Succeeds()
    {
        // CRIT-01: Merkle proof must be verified during unlock
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        // Lock tokens first
        _bridge.Lock(Addr(1), Addr(2), 1000);

        // Build a valid Merkle tree from deposit leaves
        var depositLeaf = BridgeState.ComputeDepositLeaf(0, Addr(2), 1000);
        var otherLeaf1 = BridgeState.ComputeDepositLeaf(1, Addr(3), 2000);
        var otherLeaf2 = BridgeState.ComputeDepositLeaf(2, Addr(4), 3000);
        var otherLeaf3 = BridgeState.ComputeDepositLeaf(3, Addr(5), 4000);
        var leaves = new[] { depositLeaf, otherLeaf1, otherLeaf2, otherLeaf3 };
        var (root, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 1000,
            StateRoot = root,
            Proof = proof,
        };

        var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal);
        withdrawal.Signatures.Add(MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray()));

        _bridge.Unlock(withdrawal, relayer).Should().BeTrue();
    }

    [Fact]
    public void Unlock_With_Invalid_MerkleProof_Fails()
    {
        // CRIT-01: Invalid Merkle proof must cause unlock to fail
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var fakeRoot = new byte[32];
        fakeRoot[0] = 0xFF;

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = fakeRoot,
            Proof = [new byte[32]], // Invalid proof
        };

        var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal, 1);
        withdrawal.Signatures.Add(MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray()));

        _bridge.Unlock(withdrawal, relayer).Should().BeFalse();
    }

    [Fact]
    public void Unlock_Decrements_Locked_Balance()
    {
        // HIGH-02: Unlock must decrement locked balance
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        _bridge.Lock(Addr(1), Addr(2), 5000);
        _bridge.GetLockedBalance().Should().Be(5000);

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 3000,
            StateRoot = new byte[32],
        };

        var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal, 1);
        withdrawal.Signatures.Add(MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray()));

        _bridge.Unlock(withdrawal, relayer).Should().BeTrue();
        _bridge.GetLockedBalance().Should().Be(2000); // 5000 - 3000
    }

    [Fact]
    public void Unlock_Per_Token_Balance_Decremented()
    {
        // HIGH-02: Per-token locked balance decremented correctly
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var token = new byte[20]; token[0] = 0xAA;
        _bridge.Lock(Addr(1), Addr(2), 10000, token);
        _bridge.GetLockedBalance(token).Should().Be(10000);

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 4000,
            StateRoot = new byte[32],
        };

        var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal, 1);
        withdrawal.Signatures.Add(MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray()));

        _bridge.Unlock(withdrawal, relayer, token).Should().BeTrue();
        _bridge.GetLockedBalance(token).Should().Be(6000); // 10000 - 4000
    }

    // ── IsWithdrawalProcessed ────────────────────────────────────────────

    [Fact]
    public void IsWithdrawalProcessed_Tracks_Correctly()
    {
        _bridge.IsWithdrawalProcessed(0).Should().BeFalse();

        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var relayer = new MultisigRelayer(1);
        relayer.AddRelayer(pubKey.ToArray());

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };
        var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal);
        withdrawal.Signatures.Add(MultisigRelayer.Sign(msgHash, privKey, pubKey.ToArray()));

        _bridge.Unlock(withdrawal, relayer);
        _bridge.IsWithdrawalProcessed(0).Should().BeTrue();
    }

    [Fact]
    public void IsWithdrawalProcessed_Unprocessed_Nonce_Returns_False()
    {
        _bridge.IsWithdrawalProcessed(42).Should().BeFalse();
        _bridge.IsWithdrawalProcessed(0).Should().BeFalse();
        _bridge.IsWithdrawalProcessed(ulong.MaxValue).Should().BeFalse();
    }

    // ── ComputeWithdrawalHash ────────────────────────────────────────────

    [Fact]
    public void ComputeWithdrawalHash_Deterministic()
    {
        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 42,
            Recipient = Addr(5),
            Amount = 9999,
            StateRoot = new byte[32],
        };

        var hash1 = BridgeState.ComputeWithdrawalHash(withdrawal);
        var hash2 = BridgeState.ComputeWithdrawalHash(withdrawal);

        hash1.Should().BeEquivalentTo(hash2);
    }

    [Fact]
    public void ComputeWithdrawalHash_Different_Nonces_Produce_Different_Hashes()
    {
        var w1 = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };
        var w2 = new BridgeWithdrawal
        {
            DepositNonce = 1,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };

        var h1 = BridgeState.ComputeWithdrawalHash(w1);
        var h2 = BridgeState.ComputeWithdrawalHash(w2);

        h1.Should().NotBeEquivalentTo(h2);
    }

    [Fact]
    public void ComputeWithdrawalHash_Different_Amounts_Produce_Different_Hashes()
    {
        var w1 = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };
        var w2 = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 999,
            StateRoot = new byte[32],
        };

        BridgeState.ComputeWithdrawalHash(w1).Should().NotBeEquivalentTo(
            BridgeState.ComputeWithdrawalHash(w2));
    }

    [Fact]
    public void ComputeWithdrawalHash_Different_Recipients_Produce_Different_Hashes()
    {
        var w1 = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(1),
            Amount = 100,
            StateRoot = new byte[32],
        };
        var w2 = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };

        BridgeState.ComputeWithdrawalHash(w1).Should().NotBeEquivalentTo(
            BridgeState.ComputeWithdrawalHash(w2));
    }

    [Fact]
    public void ComputeWithdrawalHash_Different_StateRoots_Produce_Different_Hashes()
    {
        var root1 = new byte[32]; root1[0] = 0xAA;
        var root2 = new byte[32]; root2[0] = 0xBB;

        var w1 = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = root1,
        };
        var w2 = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = root2,
        };

        BridgeState.ComputeWithdrawalHash(w1).Should().NotBeEquivalentTo(
            BridgeState.ComputeWithdrawalHash(w2));
    }

    [Fact]
    public void ComputeWithdrawalHash_Returns_32_Bytes()
    {
        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(2),
            Amount = 100,
            StateRoot = new byte[32],
        };

        BridgeState.ComputeWithdrawalHash(withdrawal).Should().HaveCount(32);
    }

    // ── Chain ID configuration ───────────────────────────────────────────

    [Fact]
    public void Custom_ChainIds_Reflected_In_Deposits()
    {
        var bridge = new BridgeState { BasaltChainId = 42, EthereumChainId = 5 };
        var deposit = bridge.Lock(Addr(1), Addr(2), 100);

        deposit.SourceChainId.Should().Be(42);
        deposit.DestinationChainId.Should().Be(5);
    }

    // ── Thread safety ────────────────────────────────────────────────────

    [Fact]
    public void Concurrent_Locks_Produce_Unique_Nonces()
    {
        var nonces = new System.Collections.Concurrent.ConcurrentBag<ulong>();

        Parallel.For(0, 50, i =>
        {
            var deposit = _bridge.Lock(Addr((byte)(i % 255 + 1)), Addr(2), (ulong)(i + 1));
            nonces.Add(deposit.Nonce);
        });

        var nonceList = nonces.ToList();
        nonceList.Should().HaveCount(50);
        nonceList.Distinct().Should().HaveCount(50); // All unique
    }
}
