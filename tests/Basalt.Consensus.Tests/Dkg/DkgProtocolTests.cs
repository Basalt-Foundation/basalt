using System.Numerics;
using System.Security.Cryptography;
using Basalt.Consensus.Dkg;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Network;
using FluentAssertions;
using Xunit;

namespace Basalt.Consensus.Tests.Dkg;

public class DkgProtocolTests
{
    private static (byte[] PrivateKey, BlsPublicKey BlsPubKey, PeerId PeerId)[] GenerateValidators(int count)
    {
        var validators = new (byte[], BlsPublicKey, PeerId)[count];
        for (int i = 0; i < count; i++)
        {
            var privKey = new byte[32];
            RandomNumberGenerator.Fill(privKey);
            privKey[0] &= 0x3F;
            if (privKey[0] == 0) privKey[0] = 1;

            var blsPub = new BlsPublicKey(BlsSigner.GetPublicKeyStatic(privKey));
            var edPub = Ed25519Signer.GetPublicKey(privKey);
            var peerId = PeerId.FromPublicKey(edPub);

            validators[i] = (privKey, blsPub, peerId);
        }
        return validators;
    }

    [Fact]
    public void Constructor_ValidParameters_CreatesProtocol()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        var protocol = new DkgProtocol(0, 4, 1, blsKeys);

        protocol.Phase.Should().Be(DkgPhase.Idle);
        protocol.Result.Should().BeNull();
        protocol.Threshold.Should().Be(1); // (4-1)/3 = 1
    }

    [Fact]
    public void Constructor_InvalidIndex_Throws()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => new DkgProtocol(-1, 4, 1, blsKeys));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DkgProtocol(4, 4, 1, blsKeys));
    }

    [Fact]
    public void Constructor_MismatchedKeyCount_Throws()
    {
        var validators = GenerateValidators(3);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        Assert.Throws<ArgumentException>(() => new DkgProtocol(0, 4, 1, blsKeys));
    }

    [Fact]
    public void Threshold_CorrectForVariousValidatorCounts()
    {
        // threshold = floor((n-1)/3)
        var v4 = GenerateValidators(4);
        new DkgProtocol(0, 4, 1, v4.Select(v => v.BlsPubKey).ToArray()).Threshold.Should().Be(1);

        var v7 = GenerateValidators(7);
        new DkgProtocol(0, 7, 1, v7.Select(v => v.BlsPubKey).ToArray()).Threshold.Should().Be(2);

        var v10 = GenerateValidators(10);
        new DkgProtocol(0, 10, 1, v10.Select(v => v.BlsPubKey).ToArray()).Threshold.Should().Be(3);

        // Edge case: 1 validator
        var v1 = GenerateValidators(1);
        new DkgProtocol(0, 1, 1, v1.Select(v => v.BlsPubKey).ToArray()).Threshold.Should().Be(1); // max(1, 0) = 1
    }

    [Fact]
    public void StartDealPhase_TransitionsToDealing()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var protocol = new DkgProtocol(0, 4, 1, blsKeys);

        var broadcastMessages = new List<NetworkMessage>();
        protocol.OnBroadcast += msg => broadcastMessages.Add(msg);

        protocol.StartDealPhase(validators[0].PeerId);

        protocol.Phase.Should().Be(DkgPhase.Deal);
        protocol.ReceivedDealCount.Should().Be(1); // Stored own deal
        broadcastMessages.Should().HaveCount(1);

        var deal = broadcastMessages[0].Should().BeOfType<DkgDealMessage>().Subject;
        deal.EpochNumber.Should().Be(1);
        deal.DealerIndex.Should().Be(0);
        deal.Commitments.Should().HaveCount(2); // threshold(1) + 1 = 2
        deal.EncryptedShares.Should().HaveCount(4);
    }

    [Fact]
    public void StartDealPhase_Idempotent_OnlyRunsOnce()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var protocol = new DkgProtocol(0, 4, 1, blsKeys);

        var count = 0;
        protocol.OnBroadcast += _ => count++;

        protocol.StartDealPhase(validators[0].PeerId);
        protocol.StartDealPhase(validators[0].PeerId); // second call should be ignored

        count.Should().Be(1);
    }

    [Fact]
    public void ProcessDeal_ValidDeal_RecordsIt()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        // Validator 0 starts
        var proto0 = new DkgProtocol(0, 4, 1, blsKeys);
        DkgDealMessage? dealMsg = null;
        proto0.OnBroadcast += msg => dealMsg = msg as DkgDealMessage;
        proto0.StartDealPhase(validators[0].PeerId);

        // Validator 1 processes the deal
        var proto1 = new DkgProtocol(1, 4, 1, blsKeys);
        proto1.StartDealPhase(validators[1].PeerId);
        proto1.ProcessDeal(dealMsg!);

        proto1.ReceivedDealCount.Should().Be(2); // own deal + dealer 0's deal
    }

    [Fact]
    public void ProcessDeal_DuplicateDeal_Ignored()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        var proto0 = new DkgProtocol(0, 4, 1, blsKeys);
        DkgDealMessage? dealMsg = null;
        proto0.OnBroadcast += msg => dealMsg = msg as DkgDealMessage;
        proto0.StartDealPhase(validators[0].PeerId);

        var proto1 = new DkgProtocol(1, 4, 1, blsKeys);
        proto1.StartDealPhase(validators[1].PeerId);
        proto1.ProcessDeal(dealMsg!);
        proto1.ProcessDeal(dealMsg!); // duplicate

        proto1.ReceivedDealCount.Should().Be(2); // still just 2
    }

    [Fact]
    public void ProcessDeal_WrongEpoch_Ignored()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        // Protocol for epoch 1
        var proto0 = new DkgProtocol(0, 4, 1, blsKeys);
        DkgDealMessage? dealMsg = null;
        proto0.OnBroadcast += msg => dealMsg = msg as DkgDealMessage;
        proto0.StartDealPhase(validators[0].PeerId);

        // Protocol for epoch 2 — should ignore epoch 1 deal
        var proto1 = new DkgProtocol(1, 4, 2, blsKeys);
        proto1.StartDealPhase(validators[1].PeerId);
        proto1.ProcessDeal(dealMsg!);

        proto1.ReceivedDealCount.Should().Be(1); // only own deal
    }

    [Fact]
    public void ComplaintPhase_NoComplaints_WhenSharesValid()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        // All validators start deal phase and exchange deals
        var protocols = new DkgProtocol[4];
        var deals = new DkgDealMessage[4];

        for (int i = 0; i < 4; i++)
        {
            protocols[i] = new DkgProtocol(i, 4, 1, blsKeys);
            DkgDealMessage? msg = null;
            protocols[i].OnBroadcast += m => msg = m as DkgDealMessage;
            protocols[i].StartDealPhase(validators[i].PeerId);
            deals[i] = msg!;
        }

        // Each validator processes all other deals
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                if (i != j)
                    protocols[i].ProcessDeal(deals[j]);
            }
            protocols[i].ReceivedDealCount.Should().Be(4);
        }

        // Start complaint phase — should be no complaints since all shares are valid
        for (int i = 0; i < 4; i++)
        {
            var complaints = new List<NetworkMessage>();
            protocols[i].OnBroadcast += msg => complaints.Add(msg);
            protocols[i].StartComplaintPhase(validators[i].PeerId);
            // Complaints will be 0 since VerifyShare only checks range and pk derivation
            protocols[i].ComplaintCount.Should().Be(0);
        }
    }

    [Fact]
    public void FullDkgLifecycle_4Validators_Succeeds()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var n = 4;

        // Create protocols
        var protocols = new DkgProtocol[n];
        var dealMessages = new DkgDealMessage[n];

        for (int i = 0; i < n; i++)
        {
            protocols[i] = new DkgProtocol(i, n, 1, blsKeys);
        }

        // Phase 1: Deal
        for (int i = 0; i < n; i++)
        {
            DkgDealMessage? msg = null;
            protocols[i].OnBroadcast += m => { if (m is DkgDealMessage d) msg = d; };
            protocols[i].StartDealPhase(validators[i].PeerId);
            dealMessages[i] = msg!;
        }

        // Distribute deals
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i != j)
                    protocols[i].ProcessDeal(dealMessages[j]);
            }
        }

        // Phase 2: Complaint
        for (int i = 0; i < n; i++)
            protocols[i].StartComplaintPhase(validators[i].PeerId);

        // Phase 3: Justification (no complaints expected)
        for (int i = 0; i < n; i++)
            protocols[i].StartJustificationPhase(validators[i].PeerId);

        // Phase 4: Finalize
        for (int i = 0; i < n; i++)
            protocols[i].Finalize(validators[i].PeerId);

        // All should complete
        for (int i = 0; i < n; i++)
        {
            protocols[i].Phase.Should().Be(DkgPhase.Completed);
            protocols[i].Result.Should().NotBeNull();
            protocols[i].Result!.EpochNumber.Should().Be(1);
            protocols[i].Result!.Threshold.Should().Be(1);
            protocols[i].Result!.QualifiedDealers.Should().HaveCount(4);
            protocols[i].Result!.SecretShare.Should().BeGreaterThan(BigInteger.Zero);
        }
    }

    [Fact]
    public void FullDkgLifecycle_SecretSharesReconstructable()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var n = 4;

        var protocols = new DkgProtocol[n];
        var dealMessages = new DkgDealMessage[n];

        for (int i = 0; i < n; i++)
            protocols[i] = new DkgProtocol(i, n, 1, blsKeys);

        // Deal
        for (int i = 0; i < n; i++)
        {
            DkgDealMessage? msg = null;
            protocols[i].OnBroadcast += m => { if (m is DkgDealMessage d) msg = d; };
            protocols[i].StartDealPhase(validators[i].PeerId);
            dealMessages[i] = msg!;
        }

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j) protocols[i].ProcessDeal(dealMessages[j]);

        // Complaint + Justification + Finalize
        for (int i = 0; i < n; i++) protocols[i].StartComplaintPhase(validators[i].PeerId);
        for (int i = 0; i < n; i++) protocols[i].StartJustificationPhase(validators[i].PeerId);
        for (int i = 0; i < n; i++) protocols[i].Finalize(validators[i].PeerId);

        // All should have completed successfully
        for (int i = 0; i < n; i++)
            protocols[i].Phase.Should().Be(DkgPhase.Completed);

        // Collect combined shares (1-based indices)
        var shares = new List<(int Index, BigInteger Share)>();
        for (int i = 0; i < n; i++)
            shares.Add((i + 1, protocols[i].Result!.SecretShare));

        // Reconstruct from threshold+1 (2) shares — different subsets should give same result
        var subset1 = new List<(int, BigInteger)> { shares[0], shares[1] };
        var subset2 = new List<(int, BigInteger)> { shares[0], shares[2] };
        var subset3 = new List<(int, BigInteger)> { shares[1], shares[3] };

        var secret1 = ThresholdCrypto.ReconstructSecret(subset1);
        var secret2 = ThresholdCrypto.ReconstructSecret(subset2);
        var secret3 = ThresholdCrypto.ReconstructSecret(subset3);

        secret1.Should().Be(secret2);
        secret1.Should().Be(secret3);
    }

    [Fact]
    public void FullDkgLifecycle_7Validators_Threshold2()
    {
        var n = 7;
        var validators = GenerateValidators(n);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        var protocols = new DkgProtocol[n];
        var dealMessages = new DkgDealMessage[n];

        for (int i = 0; i < n; i++)
            protocols[i] = new DkgProtocol(i, n, 1, blsKeys);

        // Expected threshold: (7-1)/3 = 2
        protocols[0].Threshold.Should().Be(2);

        // Deal
        for (int i = 0; i < n; i++)
        {
            DkgDealMessage? msg = null;
            protocols[i].OnBroadcast += m => { if (m is DkgDealMessage d) msg = d; };
            protocols[i].StartDealPhase(validators[i].PeerId);
            dealMessages[i] = msg!;
        }

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j) protocols[i].ProcessDeal(dealMessages[j]);

        // Complaint + Justification + Finalize
        for (int i = 0; i < n; i++) protocols[i].StartComplaintPhase(validators[i].PeerId);
        for (int i = 0; i < n; i++) protocols[i].StartJustificationPhase(validators[i].PeerId);
        for (int i = 0; i < n; i++) protocols[i].Finalize(validators[i].PeerId);

        // All complete
        for (int i = 0; i < n; i++)
        {
            protocols[i].Phase.Should().Be(DkgPhase.Completed);
            protocols[i].Result!.QualifiedDealers.Should().HaveCount(7);
        }

        // Reconstruct from exactly 3 shares (threshold+1)
        var shares = new List<(int, BigInteger)>();
        for (int i = 0; i < n; i++)
            shares.Add((i + 1, protocols[i].Result!.SecretShare));

        var sub1 = shares.Take(3).ToList();
        var sub2 = new List<(int, BigInteger)> { shares[0], shares[3], shares[6] };

        var secret1 = ThresholdCrypto.ReconstructSecret(sub1);
        var secret2 = ThresholdCrypto.ReconstructSecret(sub2);

        secret1.Should().Be(secret2);
    }

    [Fact]
    public void DkgWithMissingDealer_StillSucceeds()
    {
        var n = 4;
        var validators = GenerateValidators(n);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        var protocols = new DkgProtocol[n];
        var dealMessages = new DkgDealMessage?[n];

        for (int i = 0; i < n; i++)
            protocols[i] = new DkgProtocol(i, n, 1, blsKeys);

        // Only validators 0, 1, 2 broadcast deals (validator 3 is offline)
        for (int i = 0; i < 3; i++)
        {
            DkgDealMessage? msg = null;
            protocols[i].OnBroadcast += m => { if (m is DkgDealMessage d) msg = d; };
            protocols[i].StartDealPhase(validators[i].PeerId);
            dealMessages[i] = msg!;
        }

        // Validator 3 also starts (so it transitions from Idle) but doesn't receive enough deals
        protocols[3].StartDealPhase(validators[3].PeerId);
        // It broadcasts its own deal but we don't distribute it

        // Distribute available deals (only from 0, 1, 2)
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                if (i != j) protocols[i].ProcessDeal(dealMessages[j]!);
            }
        }

        // Complete DKG for validators 0-2
        for (int i = 0; i < 3; i++) protocols[i].StartComplaintPhase(validators[i].PeerId);
        for (int i = 0; i < 3; i++) protocols[i].StartJustificationPhase(validators[i].PeerId);
        for (int i = 0; i < 3; i++) protocols[i].Finalize(validators[i].PeerId);

        // Validators 0-2 should succeed (3 qualified dealers >= threshold+1=2)
        for (int i = 0; i < 3; i++)
        {
            protocols[i].Phase.Should().Be(DkgPhase.Completed);
            protocols[i].Result!.QualifiedDealers.Should().HaveCount(3);
        }
    }

    [Fact]
    public void DkgTooFewDealers_Fails()
    {
        var n = 4;
        var validators = GenerateValidators(n);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();

        // Only 1 validator runs (threshold is 1, needs 2 qualified dealers)
        var protocol = new DkgProtocol(0, n, 1, blsKeys);
        protocol.StartDealPhase(validators[0].PeerId);
        protocol.StartComplaintPhase(validators[0].PeerId);
        protocol.StartJustificationPhase(validators[0].PeerId);
        protocol.Finalize(validators[0].PeerId);

        // Only 1 qualified dealer, but need threshold+1 = 2
        protocol.Phase.Should().Be(DkgPhase.Failed);
        protocol.Result.Should().BeNull();
    }

    [Fact]
    public void ProcessDeal_InvalidDealerIndex_Ignored()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var protocol = new DkgProtocol(0, 4, 1, blsKeys);
        protocol.StartDealPhase(validators[0].PeerId);

        var badMsg = new DkgDealMessage
        {
            SenderId = validators[0].PeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EpochNumber = 1,
            DealerIndex = 99, // out of range
            Commitments = new BlsPublicKey[2],
            EncryptedShares = new byte[4][],
        };

        protocol.ProcessDeal(badMsg);
        protocol.ReceivedDealCount.Should().Be(1); // only own deal
    }

    [Fact]
    public void ProcessDeal_WrongCommitmentCount_Ignored()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var protocol = new DkgProtocol(0, 4, 1, blsKeys);
        protocol.StartDealPhase(validators[0].PeerId);

        var badMsg = new DkgDealMessage
        {
            SenderId = validators[1].PeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EpochNumber = 1,
            DealerIndex = 1,
            Commitments = new BlsPublicKey[5], // wrong count (should be threshold+1 = 2)
            EncryptedShares = new byte[4][],
        };

        protocol.ProcessDeal(badMsg);
        protocol.ReceivedDealCount.Should().Be(1);
    }

    [Fact]
    public void PhaseTransitions_CannotSkipPhases()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var protocol = new DkgProtocol(0, 4, 1, blsKeys);

        // Can't go to complaint from Idle
        protocol.StartComplaintPhase(validators[0].PeerId);
        protocol.Phase.Should().Be(DkgPhase.Idle);

        // Can't finalize from Idle
        protocol.Finalize(validators[0].PeerId);
        protocol.Phase.Should().Be(DkgPhase.Idle);

        // Start deal, then can go to complaint
        protocol.StartDealPhase(validators[0].PeerId);
        protocol.Phase.Should().Be(DkgPhase.Deal);

        protocol.StartComplaintPhase(validators[0].PeerId);
        protocol.Phase.Should().Be(DkgPhase.Complaint);
    }

    // ────────── Test 7: Malicious Dealer Detection ──────────

    [Fact]
    public void MaliciousDealer_InvalidShares_TriggersComplaint()
    {
        // Test 7: A malicious dealer sends shares inconsistent with commitments.
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var n = 4;

        var protocols = new DkgProtocol[n];
        var dealMessages = new DkgDealMessage[n];

        for (int i = 0; i < n; i++)
            protocols[i] = new DkgProtocol(i, n, 1, blsKeys);

        // Phase 1: Deal — all honest
        for (int i = 0; i < n; i++)
        {
            DkgDealMessage? msg = null;
            protocols[i].OnBroadcast += m => { if (m is DkgDealMessage d) msg = d; };
            protocols[i].StartDealPhase(validators[i].PeerId);
            dealMessages[i] = msg!;
        }

        // Tamper with dealer 0's encrypted shares — corrupt the share for validator 1
        var tamperedDeal = dealMessages[0];
        if (tamperedDeal.EncryptedShares.Length > 1 && tamperedDeal.EncryptedShares[1].Length > 0)
        {
            tamperedDeal.EncryptedShares[1][0] ^= 0xFF; // Flip bits
        }

        // Distribute deals — validator 1 should detect the tampered share
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                if (i != j) protocols[i].ProcessDeal(dealMessages[j]);
            }
        }

        // Move to complaint phase
        for (int i = 0; i < n; i++)
            protocols[i].StartComplaintPhase(validators[i].PeerId);

        // At least one validator should have filed a complaint about dealer 0
        var totalComplaints = protocols.Sum(p => p.ComplaintCount);
        totalComplaints.Should().BeGreaterThan(0,
            "tampered shares should trigger at least one complaint");
    }

    // ────────── Test 8: All Validators Derive Same GPK ──────────

    [Fact]
    public void AllValidators_DeriveSameGroupPublicKey()
    {
        var validators = GenerateValidators(4);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var n = 4;

        var protocols = new DkgProtocol[n];
        var dealMessages = new DkgDealMessage[n];

        for (int i = 0; i < n; i++)
            protocols[i] = new DkgProtocol(i, n, 1, blsKeys);

        for (int i = 0; i < n; i++)
        {
            DkgDealMessage? msg = null;
            protocols[i].OnBroadcast += m => { if (m is DkgDealMessage d) msg = d; };
            protocols[i].StartDealPhase(validators[i].PeerId);
            dealMessages[i] = msg!;
        }

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j) protocols[i].ProcessDeal(dealMessages[j]);

        for (int i = 0; i < n; i++) protocols[i].StartComplaintPhase(validators[i].PeerId);
        for (int i = 0; i < n; i++) protocols[i].StartJustificationPhase(validators[i].PeerId);
        for (int i = 0; i < n; i++) protocols[i].Finalize(validators[i].PeerId);

        // All should complete
        for (int i = 0; i < n; i++)
            protocols[i].Phase.Should().Be(DkgPhase.Completed);

        // All should derive the same group public key
        var gpk0 = protocols[0].Result!.GroupPublicKey;
        gpk0.Should().NotBeNull("group public key should be computed");
        for (int i = 1; i < n; i++)
        {
            protocols[i].Result!.GroupPublicKey.ToArray().Should().BeEquivalentTo(gpk0.ToArray(),
                $"validator {i} should derive the same GPK as validator 0");
        }
    }

    // ────────── Test 9: Reconstruction Threshold Boundary ──────────

    [Fact]
    public void Reconstruct_ExactlyThreshold_Fails_ThresholdPlusOne_Succeeds()
    {
        // DKG with 7 validators, threshold=2. Need 3 shares to reconstruct (threshold+1).
        var validators = GenerateValidators(7);
        var blsKeys = validators.Select(v => v.BlsPubKey).ToArray();
        var n = 7;

        var protocols = new DkgProtocol[n];
        var dealMessages = new DkgDealMessage[n];

        for (int i = 0; i < n; i++)
            protocols[i] = new DkgProtocol(i, n, 1, blsKeys);

        for (int i = 0; i < n; i++)
        {
            DkgDealMessage? msg = null;
            protocols[i].OnBroadcast += m => { if (m is DkgDealMessage d) msg = d; };
            protocols[i].StartDealPhase(validators[i].PeerId);
            dealMessages[i] = msg!;
        }

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                if (i != j) protocols[i].ProcessDeal(dealMessages[j]);

        for (int i = 0; i < n; i++) protocols[i].StartComplaintPhase(validators[i].PeerId);
        for (int i = 0; i < n; i++) protocols[i].StartJustificationPhase(validators[i].PeerId);
        for (int i = 0; i < n; i++) protocols[i].Finalize(validators[i].PeerId);

        for (int i = 0; i < n; i++)
            protocols[i].Phase.Should().Be(DkgPhase.Completed);

        var threshold = protocols[0].Result!.Threshold;
        threshold.Should().Be(2, "threshold for 7 validators should be 2");

        // Collect all shares
        var allShares = new List<(int Index, BigInteger Share)>();
        for (int i = 0; i < n; i++)
            allShares.Add((i + 1, protocols[i].Result!.SecretShare));

        // Reconstruct with threshold+1 (3) shares — should succeed
        var threshPlusOne = allShares.Take(threshold + 1).ToList();
        var secretA = ThresholdCrypto.ReconstructSecret(threshPlusOne);

        // Reconstruct with a different set of threshold+1 shares — same result
        var differentSet = allShares.Skip(2).Take(threshold + 1).ToList();
        var secretB = ThresholdCrypto.ReconstructSecret(differentSet);
        secretA.Should().Be(secretB, "any threshold+1 shares should reconstruct the same secret");

        // Reconstruct with exactly threshold (2) shares — should NOT match
        var exactThreshold = allShares.Take(threshold).ToList();
        var wrongSecret = ThresholdCrypto.ReconstructSecret(exactThreshold);
        wrongSecret.Should().NotBe(secretA,
            "exactly threshold shares (without +1) should NOT reconstruct the correct secret");
    }
}
