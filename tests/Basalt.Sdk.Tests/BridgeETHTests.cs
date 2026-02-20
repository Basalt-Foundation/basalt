using Basalt.Core;
using Basalt.Crypto;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class BridgeETHTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BridgeETH _bridge;
    private readonly byte[] _admin;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    // Ed25519 relayer keys (3 relayers for M-of-N testing)
    private readonly byte[] _relayer1Priv;
    private readonly byte[] _relayer1Pub;
    private readonly byte[] _relayer2Priv;
    private readonly byte[] _relayer2Pub;
    private readonly byte[] _relayer3Priv;
    private readonly byte[] _relayer3Pub;

    public BridgeETHTests()
    {
        _admin = BasaltTestHost.CreateAddress(1);
        _alice = BasaltTestHost.CreateAddress(2);
        _bob = BasaltTestHost.CreateAddress(3);

        // Generate 3 Ed25519 keypairs for relayer testing
        var (priv1, pub1) = Ed25519Signer.GenerateKeyPair();
        _relayer1Priv = priv1;
        _relayer1Pub = pub1.ToArray();

        var (priv2, pub2) = Ed25519Signer.GenerateKeyPair();
        _relayer2Priv = priv2;
        _relayer2Pub = pub2.ToArray();

        var (priv3, pub3) = Ed25519Signer.GenerateKeyPair();
        _relayer3Priv = priv3;
        _relayer3Pub = pub3.ToArray();

        // Admin deploys the BridgeETH -- constructor writes Context.Caller as admin
        _host.SetCaller(_admin);
        _bridge = new BridgeETH(threshold: 2);
    }

    // ---- Helper Methods ----

    /// <summary>
    /// Compute withdrawal hash using the same algorithm as BridgeETH.ComputeWithdrawalHash.
    /// Format: BLAKE3(version || LE_u32(chainId) || contractAddress || LE_u64(nonce) || recipient || LE_u256(amount) || stateRoot)
    /// </summary>
    private static byte[] ComputeWithdrawalHash(ulong nonce, byte[] recipient, UInt256 amount, byte[] stateRoot)
    {
        // version(1) + chainId(4) + contractAddress(20) + nonce(8) + recipient(20) + amount(32) + stateRoot(32) = 117
        var data = new byte[1 + 4 + 20 + 8 + 20 + 32 + 32];
        var offset = 0;

        // Version byte
        data[offset] = 0x02;
        offset += 1;

        // Chain ID (Context.ChainId)
        BitConverter.TryWriteBytes(data.AsSpan(offset, 4), Context.ChainId);
        offset += 4;

        // Contract address (Context.Self)
        Context.Self.CopyTo(data.AsSpan(offset, 20));
        offset += 20;

        // Nonce
        BitConverter.TryWriteBytes(data.AsSpan(offset, 8), nonce);
        offset += 8;

        // Recipient (fixed 20 bytes)
        recipient.AsSpan(0, Math.Min(recipient.Length, 20)).CopyTo(data.AsSpan(offset, 20));
        offset += 20;

        // Amount (UInt256 LE, 32 bytes)
        amount.WriteTo(data.AsSpan(offset, 32));
        offset += 32;

        // State root (fixed 32 bytes)
        stateRoot.AsSpan(0, Math.Min(stateRoot.Length, 32)).CopyTo(data.AsSpan(offset, 32));

        return Blake3Hasher.Hash(data).ToArray();
    }

    /// <summary>
    /// Pack N Ed25519 signatures as N x 96 bytes: [32B pubkey][64B sig] per entry.
    /// </summary>
    private static byte[] PackSignatures(byte[] messageHash, params (byte[] pubKey, byte[] privKey)[] relayers)
    {
        var packed = new byte[relayers.Length * 96];
        for (var i = 0; i < relayers.Length; i++)
        {
            var (pubKey, privKey) = relayers[i];
            var sig = Ed25519Signer.Sign(privKey, messageHash);
            var sigBytes = sig.ToArray();

            var offset = i * 96;
            pubKey.CopyTo(packed, offset);
            sigBytes.CopyTo(packed, offset + 32);
        }
        return packed;
    }

    /// <summary>
    /// Register all 3 relayers as admin.
    /// </summary>
    private void RegisterAllRelayers()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.AddRelayer(_relayer1Pub));
        _host.Call(() => _bridge.AddRelayer(_relayer2Pub));
        _host.Call(() => _bridge.AddRelayer(_relayer3Pub));
    }

    /// <summary>
    /// Set up native transfer tracking.
    /// </summary>
    private List<(byte[] to, UInt256 amount)> SetupNativeTransferTracking()
    {
        var transfers = new List<(byte[] to, UInt256 amount)>();
        Context.NativeTransferHandler = (to, amount) => transfers.Add((to, amount));
        return transfers;
    }

    /// <summary>
    /// Create a 32-byte state root for testing.
    /// </summary>
    private static byte[] MakeStateRoot(byte seed = 0xAA)
    {
        var root = new byte[32];
        root[0] = seed;
        return root;
    }

    // ==========================================================================
    // 1. Admin Tests (5 tests)
    // ==========================================================================

    [Fact]
    public void Constructor_SetsCallerAsAdmin()
    {
        _host.Call(() => _bridge.GetAdmin())
            .Should().Be(Convert.ToHexString(_admin));
    }

    [Fact]
    public void Constructor_SetsThreshold()
    {
        _host.Call(() => _bridge.GetThreshold()).Should().Be(2u);
    }

    [Fact]
    public void Constructor_ThresholdBelowTwo_Reverts()
    {
        // BRIDGE-04: threshold must be >= 2
        _host.SetCaller(_admin);
        var msg = _host.ExpectRevert(() => new BridgeETH(threshold: 1));
        msg.Should().Contain("threshold must be >= 2");
    }

    [Fact]
    public void TransferAdmin_UpdatesAdmin()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.TransferAdmin(_alice));

        _host.Call(() => _bridge.GetAdmin())
            .Should().Be(Convert.ToHexString(_alice));
    }

    [Fact]
    public void TransferAdmin_NonAdmin_Reverts()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _bridge.TransferAdmin(_bob));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void TransferAdmin_NewAdminCanPerformAdminActions()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.TransferAdmin(_alice));

        // New admin (alice) can add relayers
        _host.SetCaller(_alice);
        _host.Call(() => _bridge.AddRelayer(_relayer1Pub));

        _host.Call(() => _bridge.IsRelayer(_relayer1Pub)).Should().BeTrue();
    }

    // ==========================================================================
    // 2. Relayer Management Tests (7 tests)
    // ==========================================================================

    [Fact]
    public void AddRelayer_RegistersRelayer()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.AddRelayer(_relayer1Pub));

        _host.Call(() => _bridge.IsRelayer(_relayer1Pub)).Should().BeTrue();
        _host.Call(() => _bridge.GetRelayerCount()).Should().Be(1u);
    }

    [Fact]
    public void AddRelayer_EmitsRelayerAddedEvent()
    {
        _host.SetCaller(_admin);
        _host.ClearEvents();
        _host.Call(() => _bridge.AddRelayer(_relayer1Pub));

        var events = _host.GetEvents<RelayerAddedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].RelayerPublicKey.Should().BeEquivalentTo(_relayer1Pub);
    }

    [Fact]
    public void AddRelayer_NonAdmin_Reverts()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _bridge.AddRelayer(_relayer1Pub));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void AddRelayer_InvalidKeyLength_Reverts()
    {
        _host.SetCaller(_admin);
        var msg = _host.ExpectRevert(() => _bridge.AddRelayer(new byte[] { 0x01, 0x02 }));
        msg.Should().Contain("invalid public key");
    }

    [Fact]
    public void AddRelayer_DuplicateDoesNotIncrementCount()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.AddRelayer(_relayer1Pub));
        _host.Call(() => _bridge.AddRelayer(_relayer1Pub)); // duplicate

        _host.Call(() => _bridge.GetRelayerCount()).Should().Be(1u);
    }

    [Fact]
    public void RemoveRelayer_RemovesRelayer()
    {
        RegisterAllRelayers();

        _host.SetCaller(_admin);
        _host.ClearEvents();
        _host.Call(() => _bridge.RemoveRelayer(_relayer3Pub));

        _host.Call(() => _bridge.IsRelayer(_relayer3Pub)).Should().BeFalse();
        _host.Call(() => _bridge.GetRelayerCount()).Should().Be(2u);

        var events = _host.GetEvents<RelayerRemovedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].RelayerPublicKey.Should().BeEquivalentTo(_relayer3Pub);
    }

    [Fact]
    public void RemoveRelayer_CannotGoBelowThreshold()
    {
        RegisterAllRelayers(); // 3 relayers, threshold = 2

        // Remove one -> 2 relayers, threshold = 2: OK
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.RemoveRelayer(_relayer3Pub));

        // Remove another -> would be 1 relayer, threshold = 2: FAIL
        var msg = _host.ExpectRevert(() => _bridge.RemoveRelayer(_relayer2Pub));
        msg.Should().Contain("would go below threshold");
    }

    // ==========================================================================
    // 2b. SetThreshold Tests (3 tests)
    // ==========================================================================

    [Fact]
    public void SetThreshold_UpdatesThreshold()
    {
        RegisterAllRelayers(); // 3 relayers

        _host.SetCaller(_admin);
        _host.ClearEvents();
        _host.Call(() => _bridge.SetThreshold(3));

        _host.Call(() => _bridge.GetThreshold()).Should().Be(3u);

        var events = _host.GetEvents<ThresholdUpdatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].OldThreshold.Should().Be(2u);
        events[0].NewThreshold.Should().Be(3u);
    }

    [Fact]
    public void SetThreshold_ExceedsRelayerCount_Reverts()
    {
        RegisterAllRelayers(); // 3 relayers

        _host.SetCaller(_admin);
        var msg = _host.ExpectRevert(() => _bridge.SetThreshold(4));
        msg.Should().Contain("threshold exceeds relayer count");
    }

    [Fact]
    public void SetThreshold_ZeroThreshold_Reverts()
    {
        RegisterAllRelayers();

        _host.SetCaller(_admin);
        var msg = _host.ExpectRevert(() => _bridge.SetThreshold(0));
        msg.Should().Contain("threshold must be >= 1");
    }

    // ==========================================================================
    // 3. Lock / Deposit Tests (8 tests)
    // ==========================================================================

    [Fact]
    public void Lock_CreatesDepositWithNonce()
    {
        var ethRecipient = new byte[] { 0xAA, 0xBB, 0xCC };

        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)1000;
        var nonce = _host.Call(() => _bridge.Lock(ethRecipient));

        nonce.Should().Be(0UL);
        _host.Call(() => _bridge.GetDepositStatus(0)).Should().Be("pending");
        _host.Call(() => _bridge.GetDepositAmount(0)).Should().Be((UInt256)1000);
        _host.Call(() => _bridge.GetDepositRecipient(0))
            .Should().Be(Convert.ToHexString(ethRecipient));
    }

    [Fact]
    public void Lock_IncrementsNonce()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)500;
        var nonce1 = _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        var nonce2 = _host.Call(() => _bridge.Lock(new byte[] { 0x02 }));
        var nonce3 = _host.Call(() => _bridge.Lock(new byte[] { 0x03 }));

        nonce1.Should().Be(0UL);
        nonce2.Should().Be(1UL);
        nonce3.Should().Be(2UL);

        _host.Call(() => _bridge.GetCurrentNonce()).Should().Be(3UL);
    }

    [Fact]
    public void Lock_EmitsDepositLockedEvent()
    {
        var ethRecipient = new byte[] { 0xDE, 0xAD };

        _host.SetCaller(_alice);
        _host.ClearEvents();
        Context.TxValue = (UInt256)2000;
        _host.Call(() => _bridge.Lock(ethRecipient));

        var events = _host.GetEvents<DepositLockedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Nonce.Should().Be(0UL);
        events[0].Sender.Should().BeEquivalentTo(_alice);
        events[0].EthRecipient.Should().BeEquivalentTo(ethRecipient);
        events[0].Amount.Should().Be((UInt256)2000);
    }

    [Fact]
    public void Lock_IncrementsTotalLocked()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)1000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = (UInt256)2000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x02 }));

        _host.Call(() => _bridge.GetTotalLocked()).Should().Be((UInt256)3000);
    }

    [Fact]
    public void Lock_ZeroValue_Reverts()
    {
        _host.SetCaller(_alice);
        Context.TxValue = UInt256.Zero;
        var msg = _host.ExpectRevert(() => _bridge.Lock(new byte[] { 0x01 }));
        msg.Should().Contain("must send value");
    }

    [Fact]
    public void Lock_EmptyRecipient_Reverts()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)1000;
        var msg = _host.ExpectRevert(() => _bridge.Lock(Array.Empty<byte>()));
        msg.Should().Contain("invalid recipient");
    }

    [Fact]
    public void ConfirmDeposit_TransitionsStatus()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)1000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.SetCaller(_admin);
        _host.ClearEvents();
        _host.Call(() => _bridge.ConfirmDeposit(0));

        _host.Call(() => _bridge.GetDepositStatus(0)).Should().Be("confirmed");

        var events = _host.GetEvents<DepositConfirmedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Nonce.Should().Be(0UL);
    }

    [Fact]
    public void FinalizeDeposit_TransitionsStatus()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)1000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.SetCaller(_admin);
        _host.Call(() => _bridge.ConfirmDeposit(0));
        _host.ClearEvents();
        _host.Call(() => _bridge.FinalizeDeposit(0));

        _host.Call(() => _bridge.GetDepositStatus(0)).Should().Be("finalized");

        var events = _host.GetEvents<DepositFinalizedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Nonce.Should().Be(0UL);
    }

    // ==========================================================================
    // 4. Unlock / Withdrawal Tests (10 tests)
    // ==========================================================================

    [Fact]
    public void Unlock_ValidTwoOfThreeSignatures_Succeeds()
    {
        RegisterAllRelayers();
        var transfers = SetupNativeTransferTracking();

        var recipient = _alice;
        UInt256 amount = 5000;
        ulong depositNonce = 42;
        var stateRoot = MakeStateRoot(0xAA);

        var hash = ComputeWithdrawalHash(depositNonce, recipient, amount, stateRoot);
        var sigs = PackSignatures(hash,
            (_relayer1Pub, _relayer1Priv),
            (_relayer2Pub, _relayer2Priv));

        _host.SetCaller(_bob);
        _host.ClearEvents();
        _host.Call(() => _bridge.Unlock(depositNonce, recipient, amount, stateRoot, sigs));

        // Verify native transfer was made
        transfers.Should().HaveCount(1);
        transfers[0].to.Should().BeEquivalentTo(recipient);
        transfers[0].amount.Should().Be(amount);

        // Verify event
        var events = _host.GetEvents<WithdrawalUnlockedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Nonce.Should().Be(depositNonce);
        events[0].Recipient.Should().BeEquivalentTo(recipient);
        events[0].Amount.Should().Be(amount);

        // Verify processed
        _host.Call(() => _bridge.IsWithdrawalProcessed(depositNonce)).Should().BeTrue();
    }

    [Fact]
    public void Unlock_AllThreeSignatures_Succeeds()
    {
        RegisterAllRelayers();
        var transfers = SetupNativeTransferTracking();

        var recipient = _alice;
        UInt256 amount = 3000;
        ulong depositNonce = 10;
        var stateRoot = MakeStateRoot(0x11);

        var hash = ComputeWithdrawalHash(depositNonce, recipient, amount, stateRoot);
        var sigs = PackSignatures(hash,
            (_relayer1Pub, _relayer1Priv),
            (_relayer2Pub, _relayer2Priv),
            (_relayer3Pub, _relayer3Priv));

        _host.SetCaller(_bob);
        _host.Call(() => _bridge.Unlock(depositNonce, recipient, amount, stateRoot, sigs));

        transfers.Should().HaveCount(1);
        transfers[0].amount.Should().Be(amount);
    }

    [Fact]
    public void Unlock_InsufficientSignatures_Reverts()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        var recipient = _alice;
        UInt256 amount = 1000;
        ulong depositNonce = 0;
        var stateRoot = MakeStateRoot(0xFF);

        var hash = ComputeWithdrawalHash(depositNonce, recipient, amount, stateRoot);
        // Only 1 signature, threshold is 2
        var sigs = PackSignatures(hash,
            (_relayer1Pub, _relayer1Priv));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _bridge.Unlock(depositNonce, recipient, amount, stateRoot, sigs));
        msg.Should().Contain("insufficient valid signatures");
    }

    [Fact]
    public void Unlock_InvalidSignatureBytes_Reverts()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        // Signatures not a multiple of 96
        var badSigs = new byte[50];

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(0, _alice, (UInt256)1000, MakeStateRoot(), badSigs));
        msg.Should().Contain("invalid signatures format");
    }

    [Fact]
    public void Unlock_ReplayPrevention_Reverts()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        var recipient = _alice;
        UInt256 amount = 5000;
        ulong depositNonce = 7;
        var stateRoot = MakeStateRoot(0xAA);

        var hash = ComputeWithdrawalHash(depositNonce, recipient, amount, stateRoot);
        var sigs = PackSignatures(hash,
            (_relayer1Pub, _relayer1Priv),
            (_relayer2Pub, _relayer2Priv));

        _host.SetCaller(_bob);
        _host.Call(() => _bridge.Unlock(depositNonce, recipient, amount, stateRoot, sigs));

        // Attempt replay
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(depositNonce, recipient, amount, stateRoot, sigs));
        msg.Should().Contain("already processed");
    }

    [Fact]
    public void Unlock_UnregisteredRelayerSignature_Ignored()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        // Remove relayer3 so only relayer1 and relayer2 are valid
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.RemoveRelayer(_relayer3Pub));

        var recipient = _alice;
        UInt256 amount = 1000;
        ulong depositNonce = 0;
        var stateRoot = MakeStateRoot(0x01);

        var hash = ComputeWithdrawalHash(depositNonce, recipient, amount, stateRoot);

        // Use an unregistered key (relayer3) and only one registered key (relayer1)
        // Should fail because only 1 valid signature (relayer1), threshold=2
        var sigs = PackSignatures(hash,
            (_relayer3Pub, _relayer3Priv),
            (_relayer1Pub, _relayer1Priv));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(depositNonce, recipient, amount, stateRoot, sigs));
        msg.Should().Contain("insufficient valid signatures");
    }

    [Fact]
    public void Unlock_DuplicateRelayerCountedOnce()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        var recipient = _alice;
        UInt256 amount = 1000;
        ulong depositNonce = 0;
        var stateRoot = MakeStateRoot(0x01);

        var hash = ComputeWithdrawalHash(depositNonce, recipient, amount, stateRoot);

        // Same relayer signs twice - should only count once
        var sigs = PackSignatures(hash,
            (_relayer1Pub, _relayer1Priv),
            (_relayer1Pub, _relayer1Priv));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(depositNonce, recipient, amount, stateRoot, sigs));
        msg.Should().Contain("insufficient valid signatures");
    }

    [Fact]
    public void Unlock_EmitsWithdrawalUnlockedEvent()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        var recipient = _bob;
        UInt256 amount = 9999;
        ulong depositNonce = 100;
        var stateRoot = MakeStateRoot(0xFE);

        var hash = ComputeWithdrawalHash(depositNonce, recipient, amount, stateRoot);
        var sigs = PackSignatures(hash,
            (_relayer2Pub, _relayer2Priv),
            (_relayer3Pub, _relayer3Priv));

        _host.SetCaller(_alice);
        _host.ClearEvents();
        _host.Call(() => _bridge.Unlock(depositNonce, recipient, amount, stateRoot, sigs));

        var events = _host.GetEvents<WithdrawalUnlockedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Nonce.Should().Be(100UL);
        events[0].Recipient.Should().BeEquivalentTo(_bob);
        events[0].Amount.Should().Be((UInt256)9999);
    }

    [Fact]
    public void Unlock_WithdrawalHashDeterminism()
    {
        // Verify the hash is deterministic: same inputs -> same hash
        var recipient = _alice;
        UInt256 amount = 5000;
        ulong nonce = 42;
        var stateRoot = MakeStateRoot(0xAA);

        var hash1 = ComputeWithdrawalHash(nonce, recipient, amount, stateRoot);
        var hash2 = ComputeWithdrawalHash(nonce, recipient, amount, stateRoot);

        hash1.Should().BeEquivalentTo(hash2);

        // Different inputs -> different hash
        var hash3 = ComputeWithdrawalHash(nonce + 1, recipient, amount, stateRoot);
        hash3.Should().NotBeEquivalentTo(hash1);
    }

    [Fact]
    public void Unlock_ZeroAmount_Reverts()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(0, _alice, UInt256.Zero, MakeStateRoot(), new byte[96]));
        msg.Should().Contain("zero amount");
    }

    [Fact]
    public void Unlock_DecrementsTotalLocked()
    {
        // BRIDGE-02: verify _totalLocked is decremented on Unlock
        RegisterAllRelayers();
        var transfers = SetupNativeTransferTracking();

        // First lock some tokens
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)5000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.Call(() => _bridge.GetTotalLocked()).Should().Be((UInt256)5000);

        // Now unlock some
        var recipient = _alice;
        UInt256 unlockAmount = 3000;
        var stateRoot = MakeStateRoot();

        var hash = ComputeWithdrawalHash(0, recipient, unlockAmount, stateRoot);
        var sigs = PackSignatures(hash,
            (_relayer1Pub, _relayer1Priv),
            (_relayer2Pub, _relayer2Priv));

        _host.SetCaller(_bob);
        _host.Call(() => _bridge.Unlock(0, recipient, unlockAmount, stateRoot, sigs));

        // Total locked should be decremented
        _host.Call(() => _bridge.GetTotalLocked()).Should().Be((UInt256)2000);
    }

    // ==========================================================================
    // 5. Pause Tests (4 tests + event tests)
    // ==========================================================================

    [Fact]
    public void Pause_BlocksLock()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.Pause());

        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)1000;
        var msg = _host.ExpectRevert(() => _bridge.Lock(new byte[] { 0x01 }));
        msg.Should().Contain("paused");
    }

    [Fact]
    public void Pause_BlocksUnlock()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        _host.SetCaller(_admin);
        _host.Call(() => _bridge.Pause());

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(0, _alice, (UInt256)1000, MakeStateRoot(), new byte[96]));
        msg.Should().Contain("paused");
    }

    [Fact]
    public void Unpause_RestoresOperations()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.Pause());

        _host.Call(() => _bridge.IsPaused()).Should().BeTrue();

        _host.Call(() => _bridge.Unpause());

        _host.Call(() => _bridge.IsPaused()).Should().BeFalse();

        // Lock should work again
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)1000;
        var nonce = _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        nonce.Should().Be(0UL);
    }

    [Fact]
    public void Pause_NonAdmin_Reverts()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _bridge.Pause());
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void Pause_EmitsBridgePausedEvent()
    {
        // BRIDGE-07: verify pause emits event
        _host.SetCaller(_admin);
        _host.ClearEvents();
        _host.Call(() => _bridge.Pause());

        var events = _host.GetEvents<BridgePausedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Admin.Should().BeEquivalentTo(_admin);
    }

    [Fact]
    public void Unpause_EmitsBridgeUnpausedEvent()
    {
        // BRIDGE-07: verify unpause emits event
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.Pause());
        _host.ClearEvents();
        _host.Call(() => _bridge.Unpause());

        var events = _host.GetEvents<BridgeUnpausedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Admin.Should().BeEquivalentTo(_admin);
    }

    // ==========================================================================
    // 6. View Tests (3 tests)
    // ==========================================================================

    [Fact]
    public void GetDepositAmount_ReturnsCorrectAmount()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)7777;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.Call(() => _bridge.GetDepositAmount(0)).Should().Be((UInt256)7777);
    }

    [Fact]
    public void GetDepositStatus_ReturnsCorrectStatus()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)100;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.Call(() => _bridge.GetDepositStatus(0)).Should().Be("pending");

        _host.SetCaller(_admin);
        _host.Call(() => _bridge.ConfirmDeposit(0));
        _host.Call(() => _bridge.GetDepositStatus(0)).Should().Be("confirmed");

        _host.Call(() => _bridge.FinalizeDeposit(0));
        _host.Call(() => _bridge.GetDepositStatus(0)).Should().Be("finalized");
    }

    [Fact]
    public void GetDepositRecipient_ReturnsCorrectRecipient()
    {
        var ethRecipient = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)100;
        _host.Call(() => _bridge.Lock(ethRecipient));
        Context.TxValue = UInt256.Zero;

        _host.Call(() => _bridge.GetDepositRecipient(0))
            .Should().Be(Convert.ToHexString(ethRecipient));
    }

    // ==========================================================================
    // 7. Edge Cases (3 tests)
    // ==========================================================================

    [Fact]
    public void Unlock_MalformedSignatures_NotMultipleOf96_Reverts()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        // 97 bytes (not a multiple of 96)
        var badSigs = new byte[97];

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(0, _alice, (UInt256)1000, MakeStateRoot(), badSigs));
        msg.Should().Contain("invalid signatures format");
    }

    [Fact]
    public void Unlock_EmptySignatures_Reverts()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(0, _alice, (UInt256)1000, MakeStateRoot(), Array.Empty<byte>()));
        msg.Should().Contain("invalid signatures format");
    }

    [Fact]
    public void Unlock_RecipientNot20Bytes_Reverts()
    {
        // BRIDGE-03: recipient must be exactly 20 bytes
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        var shortRecipient = new byte[] { 0x01, 0x02, 0x03 };

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(0, shortRecipient, (UInt256)1000, MakeStateRoot(), new byte[96]));
        msg.Should().Contain("recipient must be 20 bytes");
    }

    [Fact]
    public void Unlock_StateRootNot32Bytes_Reverts()
    {
        // BRIDGE-03: stateRoot must be exactly 32 bytes
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        var shortRoot = new byte[] { 0x01 };

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(0, _alice, (UInt256)1000, shortRoot, new byte[96]));
        msg.Should().Contain("stateRoot must be 32 bytes");
    }

    // ==========================================================================
    // 8. CancelDeposit Tests (BRIDGE-09)
    // ==========================================================================

    [Fact]
    public void CancelDeposit_RefundsExpiredDeposit()
    {
        var transfers = SetupNativeTransferTracking();

        _host.SetCaller(_alice);
        _host.SetBlockHeight(100);
        Context.TxValue = (UInt256)5000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.Call(() => _bridge.GetTotalLocked()).Should().Be((UInt256)5000);

        // Advance past expiry
        _host.SetBlockHeight(100 + 50400 + 1);

        _host.ClearEvents();
        _host.Call(() => _bridge.CancelDeposit(0));

        // Verify refund
        transfers.Should().HaveCount(1);
        transfers[0].to.Should().BeEquivalentTo(_alice);
        transfers[0].amount.Should().Be((UInt256)5000);

        // Verify status
        _host.Call(() => _bridge.GetDepositStatus(0)).Should().Be("cancelled");

        // Verify total locked decremented
        _host.Call(() => _bridge.GetTotalLocked()).Should().Be(UInt256.Zero);

        // Verify event
        var events = _host.GetEvents<DepositCancelledEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Nonce.Should().Be(0UL);
        events[0].Sender.Should().BeEquivalentTo(_alice);
        events[0].Amount.Should().Be((UInt256)5000);
    }

    [Fact]
    public void CancelDeposit_NotExpired_Reverts()
    {
        SetupNativeTransferTracking();

        _host.SetCaller(_alice);
        _host.SetBlockHeight(100);
        Context.TxValue = (UInt256)5000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        // Not enough blocks have passed
        _host.SetBlockHeight(100 + 50000);

        var msg = _host.ExpectRevert(() => _bridge.CancelDeposit(0));
        msg.Should().Contain("deposit not expired");
    }

    [Fact]
    public void CancelDeposit_NotSender_Reverts()
    {
        SetupNativeTransferTracking();

        _host.SetCaller(_alice);
        _host.SetBlockHeight(100);
        Context.TxValue = (UInt256)5000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        // Bob tries to cancel Alice's deposit
        _host.SetBlockHeight(100 + 50400 + 1);
        _host.SetCaller(_bob);

        var msg = _host.ExpectRevert(() => _bridge.CancelDeposit(0));
        msg.Should().Contain("not deposit sender");
    }

    [Fact]
    public void CancelDeposit_AlreadyConfirmed_Reverts()
    {
        SetupNativeTransferTracking();

        _host.SetCaller(_alice);
        _host.SetBlockHeight(100);
        Context.TxValue = (UInt256)5000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.SetCaller(_admin);
        _host.Call(() => _bridge.ConfirmDeposit(0));

        _host.SetBlockHeight(100 + 50400 + 1);
        _host.SetCaller(_alice);

        var msg = _host.ExpectRevert(() => _bridge.CancelDeposit(0));
        msg.Should().Contain("deposit not pending");
    }

    [Fact]
    public void CancelDeposit_WhenPaused_Reverts()
    {
        SetupNativeTransferTracking();

        _host.SetCaller(_alice);
        _host.SetBlockHeight(100);
        Context.TxValue = (UInt256)5000;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.SetCaller(_admin);
        _host.Call(() => _bridge.Pause());

        _host.SetBlockHeight(100 + 50400 + 1);
        _host.SetCaller(_alice);

        var msg = _host.ExpectRevert(() => _bridge.CancelDeposit(0));
        msg.Should().Contain("paused");
    }

    // ==========================================================================
    // Additional Edge Cases & Coverage
    // ==========================================================================

    [Fact]
    public void ConfirmDeposit_NonPending_Reverts()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)100;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.SetCaller(_admin);
        _host.Call(() => _bridge.ConfirmDeposit(0));

        // Trying to confirm again when status is "confirmed" should fail
        var msg = _host.ExpectRevert(() => _bridge.ConfirmDeposit(0));
        msg.Should().Contain("not pending");
    }

    [Fact]
    public void FinalizeDeposit_NonConfirmed_Reverts()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)100;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.SetCaller(_admin);
        // Try to finalize a "pending" deposit (not yet confirmed)
        var msg = _host.ExpectRevert(() => _bridge.FinalizeDeposit(0));
        msg.Should().Contain("not confirmed");
    }

    [Fact]
    public void RemoveRelayer_NonAdmin_Reverts()
    {
        RegisterAllRelayers();

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _bridge.RemoveRelayer(_relayer1Pub));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void RemoveRelayer_NotARelayer_Reverts()
    {
        _host.SetCaller(_admin);
        var fakeKey = new byte[32];
        fakeKey[0] = 0xFF;
        var msg = _host.ExpectRevert(() => _bridge.RemoveRelayer(fakeKey));
        msg.Should().Contain("not a relayer");
    }

    [Fact]
    public void Unpause_NonAdmin_Reverts()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.Pause());

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _bridge.Unpause());
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void SetThreshold_NonAdmin_Reverts()
    {
        RegisterAllRelayers();

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _bridge.SetThreshold(1));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void ConfirmDeposit_NonAdmin_Reverts()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)100;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _bridge.ConfirmDeposit(0));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void FinalizeDeposit_NonAdmin_Reverts()
    {
        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)100;
        _host.Call(() => _bridge.Lock(new byte[] { 0x01 }));
        Context.TxValue = UInt256.Zero;

        _host.SetCaller(_admin);
        _host.Call(() => _bridge.ConfirmDeposit(0));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _bridge.FinalizeDeposit(0));
        msg.Should().Contain("not admin");
    }

    [Fact]
    public void Unlock_EmptyRecipient_Reverts()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(0, Array.Empty<byte>(), (UInt256)1000, MakeStateRoot(), new byte[96]));
        msg.Should().Contain("recipient must be 20 bytes");
    }

    [Fact]
    public void Unlock_WrongSignatureForMessage_Reverts()
    {
        RegisterAllRelayers();
        SetupNativeTransferTracking();

        var recipient = _alice;
        UInt256 amount = 1000;
        ulong depositNonce = 0;
        var stateRoot = MakeStateRoot(0x01);

        // Sign a DIFFERENT hash than what the contract will compute
        var wrongHash = new byte[32];
        wrongHash[0] = 0xFF;
        var sigs = PackSignatures(wrongHash,
            (_relayer1Pub, _relayer1Priv),
            (_relayer2Pub, _relayer2Priv));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() =>
            _bridge.Unlock(depositNonce, recipient, amount, stateRoot, sigs));
        msg.Should().Contain("insufficient valid signatures");
    }

    [Fact]
    public void TransferAdmin_EmptyAddress_Reverts()
    {
        _host.SetCaller(_admin);
        var msg = _host.ExpectRevert(() => _bridge.TransferAdmin(Array.Empty<byte>()));
        msg.Should().Contain("invalid admin");
    }

    [Fact]
    public void IsRelayer_UnregisteredKey_ReturnsFalse()
    {
        var fakeKey = new byte[32];
        fakeKey[0] = 0xAB;
        _host.Call(() => _bridge.IsRelayer(fakeKey)).Should().BeFalse();
    }

    [Fact]
    public void GetDepositStatus_NonexistentNonce_ReturnsEmpty()
    {
        _host.Call(() => _bridge.GetDepositStatus(9999)).Should().Be("");
    }

    [Fact]
    public void IsWithdrawalProcessed_UnprocessedNonce_ReturnsFalse()
    {
        _host.Call(() => _bridge.IsWithdrawalProcessed(0)).Should().BeFalse();
    }

    [Fact]
    public void Lock_PausedThenUnpaused_Succeeds()
    {
        _host.SetCaller(_admin);
        _host.Call(() => _bridge.Pause());
        _host.Call(() => _bridge.Unpause());

        _host.SetCaller(_alice);
        Context.TxValue = (UInt256)500;
        var nonce = _host.Call(() => _bridge.Lock(new byte[] { 0xAA }));
        nonce.Should().Be(0UL);
    }

    public void Dispose() => _host.Dispose();
}
