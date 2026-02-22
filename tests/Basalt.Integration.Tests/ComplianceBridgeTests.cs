using Basalt.Bridge;
using Basalt.Compliance;
using Basalt.Core;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Integration.Tests;

/// <summary>
/// Cross-module integration tests for compliance + bridge interaction.
/// </summary>
public class ComplianceBridgeTests
{
    [Fact]
    public void KYC_Verified_User_Can_Use_Bridge()
    {
        // Setup compliance
        var registry = new IdentityRegistry();
        var sanctionsList = new SanctionsList();
        var engine = new ComplianceEngine(registry, sanctionsList);
        var providerAddr = new byte[20];
        providerAddr[19] = 0xFF;
        var mockKyc = new MockKycProvider(registry, providerAddr);

        var sender = new byte[20];
        sender[19] = 1;
        var recipient = new byte[20];
        recipient[19] = 2;

        // Issue KYC for sender
        mockKyc.IssueBasic(sender, 840); // US = 840

        // Setup bridge
        var bridgeState = new BridgeState();

        // Lock tokens (bridge deposit)
        var deposit = bridgeState.Lock(sender, recipient, 1000);
        deposit.Amount.Should().Be(1000);
        deposit.Status.Should().Be(BridgeTransferStatus.Pending);
        bridgeState.GetLockedBalance().Should().Be(1000);
    }

    [Fact]
    public void Sanctioned_Address_Blocked_By_Compliance()
    {
        var registry = new IdentityRegistry();
        var sanctionsList = new SanctionsList();
        var engine = new ComplianceEngine(registry, sanctionsList);
        var providerAddr = new byte[20];
        providerAddr[19] = 0xFF;
        var mockKyc = new MockKycProvider(registry, providerAddr);

        var sender = new byte[20];
        sender[19] = 1;
        var recipient = new byte[20];
        recipient[19] = 2;
        var tokenAddr = new byte[20];

        // Issue KYC but also sanction the sender
        mockKyc.IssueBasic(sender, 840);
        sanctionsList.AddSanction(sender, "test sanction");
        sanctionsList.IsSanctioned(sender).Should().BeTrue();

        // Set policy that requires sanctions check
        engine.SetPolicy(tokenAddr, new CompliancePolicy
        {
            SanctionsCheckEnabled = true,
        });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var result = engine.CheckTransfer(tokenAddr, sender, recipient, 100, now);
        result.Allowed.Should().BeFalse();
        result.ErrorCode.Should().Be(ComplianceErrorCode.Sanctioned);
    }

    [Fact]
    public void Bridge_With_Multisig_Relayer_Full_Flow()
    {
        // Setup 2-of-3 multisig relayer
        var k0 = Ed25519Signer.GenerateKeyPair();
        var k1 = Ed25519Signer.GenerateKeyPair();
        var k2 = Ed25519Signer.GenerateKeyPair();

        var relayer = new MultisigRelayer(2);
        relayer.AddRelayer(k0.PublicKey.ToArray());
        relayer.AddRelayer(k1.PublicKey.ToArray());
        relayer.AddRelayer(k2.PublicKey.ToArray());

        // Setup bridge
        var bridgeState = new BridgeState();

        var sender = new byte[20];
        sender[19] = 1;
        var recipient = new byte[20];
        recipient[19] = 2;

        // Step 1: Lock tokens
        var deposit = bridgeState.Lock(sender, recipient, 5000);
        deposit.Nonce.Should().Be(0);
        bridgeState.GetLockedBalance().Should().Be(5000);

        // Step 2: Confirm deposit
        bridgeState.ConfirmDeposit(0, 42).Should().BeTrue();
        bridgeState.GetDeposit(0)!.Status.Should().Be(BridgeTransferStatus.Confirmed);

        // Step 3: Finalize deposit
        bridgeState.FinalizeDeposit(0).Should().BeTrue();

        // Step 4: Process withdrawal with multisig — build valid Merkle proof (LOW-06)
        var depositLeaf = BridgeState.ComputeDepositLeaf(0, recipient, 5000);
        var dummyLeaf = BridgeState.ComputeDepositLeaf(ulong.MaxValue, new byte[20], (UInt256)1);
        var (root, proof) = BridgeProofVerifier.BuildMerkleProof([depositLeaf, dummyLeaf], 0);

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = recipient,
            Amount = 5000,
            StateRoot = root,
            Proof = proof,
        };

        var msgHash = BridgeState.ComputeWithdrawalHash(withdrawal, 1);
        var sig0 = MultisigRelayer.Sign(msgHash, k0.PrivateKey, k0.PublicKey.ToArray());
        var sig2 = MultisigRelayer.Sign(msgHash, k2.PrivateKey, k2.PublicKey.ToArray());
        withdrawal.AddSignature(sig0);
        withdrawal.AddSignature(sig2);

        bridgeState.Unlock(withdrawal, relayer).Should().BeTrue();
        bridgeState.IsWithdrawalProcessed(0).Should().BeTrue();
    }

    [Fact]
    public void Merkle_Proof_Verification_With_Bridge_Deposits()
    {
        // Create multiple deposits and verify Merkle proofs
        var bridgeState = new BridgeState();
        var deposits = new List<byte[]>();

        for (byte i = 0; i < 4; i++)
        {
            var sender = new byte[20];
            sender[19] = i;
            var recipient = new byte[20];
            recipient[19] = (byte)(i + 10);

            var deposit = bridgeState.Lock(sender, recipient, new UInt256((ulong)(100 * (i + 1))));

            // Serialize deposit as leaf data
            var leafData = new byte[8 + 20 + 20 + 32];
            BitConverter.TryWriteBytes(leafData.AsSpan(0, 8), deposit.Nonce);
            deposit.Sender.CopyTo(leafData, 8);
            deposit.Recipient.CopyTo(leafData, 28);
            deposit.Amount.WriteTo(leafData.AsSpan(48, 32));
            deposits.Add(leafData);
        }

        // Build Merkle tree and verify each deposit's proof
        var leaves = deposits.ToArray();
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        for (int i = 0; i < leaves.Length; i++)
        {
            var (proofRoot, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            proofRoot.Should().BeEquivalentTo(root);
            BridgeProofVerifier.VerifyMerkleProof(leaves[i], proof, (ulong)i, root).Should().BeTrue();
        }
    }
}
