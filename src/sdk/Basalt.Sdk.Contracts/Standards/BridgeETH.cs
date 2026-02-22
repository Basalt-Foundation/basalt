using System.Buffers.Binary;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// EVM Bridge system contract — lock/unlock native BST tokens for cross-chain transfers.
/// Testnet MVP uses M-of-N Ed25519 multisig relayer verification.
/// Type ID: 0x0107
/// </summary>
[BasaltContract]
public partial class BridgeETH
{
    private const int PubKeySize = 32;
    private const int SigSize = 64;
    private const int SignatureEntrySize = PubKeySize + SigSize; // 96

    /// <summary>
    /// Number of blocks after which a pending deposit can be cancelled (~7 days at 12s blocks).
    /// </summary>
    private const ulong DepositExpiryBlocks = 50400;

    // Admin & config
    private readonly StorageMap<string, string> _admin;
    private readonly StorageValue<uint> _threshold;
    private readonly StorageValue<bool> _paused;

    // Relayer management
    private readonly StorageMap<string, bool> _relayers;
    private readonly StorageValue<uint> _relayerCount;

    // Deposit tracking (Basalt → Ethereum)
    private readonly StorageValue<ulong> _nextNonce;
    private readonly StorageMap<string, string> _depositSenders;
    private readonly StorageMap<string, string> _depositRecipients;
    private readonly StorageMap<string, UInt256> _depositAmounts;
    private readonly StorageMap<string, string> _depositStatus;
    private readonly StorageMap<string, ulong> _depositBlockHeights;

    // Withdrawal tracking (Ethereum → Basalt)
    private readonly StorageMap<string, bool> _processedWithdrawals;

    // Balance
    private readonly StorageValue<UInt256> _totalLocked;

    public BridgeETH(uint threshold = 2)
    {
        _admin = new StorageMap<string, string>("bch_admin");
        _threshold = new StorageValue<uint>("bch_thresh");
        _paused = new StorageValue<bool>("bch_paused");
        _relayers = new StorageMap<string, bool>("bch_rel");
        _relayerCount = new StorageValue<uint>("bch_relcnt");
        _nextNonce = new StorageValue<ulong>("bch_nonce");
        _depositSenders = new StorageMap<string, string>("bch_dsnd");
        _depositRecipients = new StorageMap<string, string>("bch_drec");
        _depositAmounts = new StorageMap<string, UInt256>("bch_damt");
        _depositStatus = new StorageMap<string, string>("bch_dsts");
        _depositBlockHeights = new StorageMap<string, ulong>("bch_dblk");
        _processedWithdrawals = new StorageMap<string, bool>("bch_proc");
        _totalLocked = new StorageValue<UInt256>("bch_locked");

        // BRIDGE-04: Validate threshold
        Context.Require(threshold >= 2, "BRIDGE: threshold must be >= 2");

        _admin.Set("admin", Convert.ToHexString(Context.Caller));
        _threshold.Set(threshold);
    }

    // --- Admin ---

    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        RequireAdmin();
        // L-7: Validate 20-byte address length
        Context.Require(newAdmin.Length == 20, "BRIDGE: invalid admin address length");
        _admin.Set("admin", Convert.ToHexString(newAdmin));
    }

    [BasaltEntrypoint]
    public void Pause()
    {
        RequireAdmin();
        _paused.Set(true);
        Context.Emit(new BridgePausedEvent { Admin = Context.Caller });
    }

    [BasaltEntrypoint]
    public void Unpause()
    {
        RequireAdmin();
        _paused.Set(false);
        Context.Emit(new BridgeUnpausedEvent { Admin = Context.Caller });
    }

    // --- Relayer Management ---

    [BasaltEntrypoint]
    public void AddRelayer(byte[] relayerPublicKey)
    {
        RequireAdmin();
        Context.Require(relayerPublicKey.Length == PubKeySize, "BRIDGE: invalid public key");

        var hex = Convert.ToHexString(relayerPublicKey);
        if (!_relayers.Get(hex))
        {
            _relayers.Set(hex, true);
            _relayerCount.Set(_relayerCount.Get() + 1);

            Context.Emit(new RelayerAddedEvent { RelayerPublicKey = relayerPublicKey });
        }
    }

    [BasaltEntrypoint]
    public void RemoveRelayer(byte[] relayerPublicKey)
    {
        RequireAdmin();
        var hex = Convert.ToHexString(relayerPublicKey);
        Context.Require(_relayers.Get(hex), "BRIDGE: not a relayer");

        var newCount = _relayerCount.Get() - 1;
        Context.Require(newCount >= _threshold.Get(), "BRIDGE: would go below threshold");

        _relayers.Set(hex, false);
        _relayerCount.Set(newCount);

        Context.Emit(new RelayerRemovedEvent { RelayerPublicKey = relayerPublicKey });
    }

    [BasaltEntrypoint]
    public void SetThreshold(uint newThreshold)
    {
        RequireAdmin();
        Context.Require(newThreshold >= 2, "BRIDGE: threshold must be >= 2");
        Context.Require(newThreshold <= _relayerCount.Get(), "BRIDGE: threshold exceeds relayer count");

        var old = _threshold.Get();
        _threshold.Set(newThreshold);

        Context.Emit(new ThresholdUpdatedEvent { OldThreshold = old, NewThreshold = newThreshold });
    }

    // --- Lock (Basalt → Ethereum) ---

    [BasaltEntrypoint]
    public ulong Lock(byte[] ethRecipient)
    {
        RequireNotPaused();
        Context.Require(!Context.TxValue.IsZero, "BRIDGE: must send value");
        Context.Require(ethRecipient.Length > 0, "BRIDGE: invalid recipient");

        var nonce = _nextNonce.Get();
        _nextNonce.Set(nonce + 1);

        var key = nonce.ToString();
        _depositSenders.Set(key, Convert.ToHexString(Context.Caller));
        _depositRecipients.Set(key, Convert.ToHexString(ethRecipient));
        _depositAmounts.Set(key, Context.TxValue);
        _depositStatus.Set(key, "pending");
        _depositBlockHeights.Set(key, Context.BlockHeight);

        _totalLocked.Set(_totalLocked.Get() + Context.TxValue);

        Context.Emit(new DepositLockedEvent
        {
            Nonce = nonce,
            Sender = Context.Caller,
            EthRecipient = ethRecipient,
            Amount = Context.TxValue,
            BlockHeight = Context.BlockHeight,
        });

        return nonce;
    }

    [BasaltEntrypoint]
    public void ConfirmDeposit(ulong nonce)
    {
        RequireAdmin();
        var key = nonce.ToString();
        Context.Require(_depositStatus.Get(key) == "pending", "BRIDGE: not pending");
        _depositStatus.Set(key, "confirmed");

        Context.Emit(new DepositConfirmedEvent { Nonce = nonce });
    }

    [BasaltEntrypoint]
    public void FinalizeDeposit(ulong nonce)
    {
        RequireAdmin();
        var key = nonce.ToString();
        Context.Require(_depositStatus.Get(key) == "confirmed", "BRIDGE: not confirmed");
        _depositStatus.Set(key, "finalized");

        Context.Emit(new DepositFinalizedEvent { Nonce = nonce });
    }

    // --- Cancel Deposit (BRIDGE-09) ---

    /// <summary>
    /// Cancel an expired pending deposit and refund the locked amount to the sender.
    /// Only the original sender can cancel, and only after DepositExpiryBlocks have passed.
    /// </summary>
    [BasaltEntrypoint]
    public void CancelDeposit(ulong nonce)
    {
        RequireNotPaused();
        var key = nonce.ToString();

        // Check deposit exists and is still pending
        var status = _depositStatus.Get(key);
        Context.Require(status == "pending", "BRIDGE: deposit not pending");

        // Check caller is the original sender
        var senderHex = _depositSenders.Get(key);
        Context.Require(senderHex == Convert.ToHexString(Context.Caller), "BRIDGE: not deposit sender");

        // Check enough blocks have passed
        var depositBlock = _depositBlockHeights.Get(key);
        Context.Require(Context.BlockHeight >= depositBlock + DepositExpiryBlocks,
            "BRIDGE: deposit not expired");

        // Update status
        _depositStatus.Set(key, "cancelled");

        // Refund the locked amount
        var amount = _depositAmounts.Get(key);
        Context.TransferNative(Context.Caller, amount);

        // Decrement total locked
        _totalLocked.Set(_totalLocked.Get() - amount);

        Context.Emit(new DepositCancelledEvent
        {
            Nonce = nonce,
            Sender = Context.Caller,
            Amount = amount,
        });
    }

    // --- Expire Confirmed Deposit (N-3) ---

    /// <summary>
    /// N-3: Expire a confirmed deposit that has not been finalized.
    /// Only the original sender can expire, and only after 2x DepositExpiryBlocks.
    /// Refunds the locked amount back to the sender.
    /// </summary>
    [BasaltEntrypoint]
    public void ExpireDeposit(ulong nonce)
    {
        RequireNotPaused();
        var key = nonce.ToString();

        var status = _depositStatus.Get(key);
        Context.Require(status == "confirmed", "BRIDGE: deposit not confirmed");

        var senderHex = _depositSenders.Get(key);
        Context.Require(senderHex == Convert.ToHexString(Context.Caller), "BRIDGE: not deposit sender");

        var depositBlock = _depositBlockHeights.Get(key);
        Context.Require(Context.BlockHeight >= depositBlock + (DepositExpiryBlocks * 2),
            "BRIDGE: confirmed deposit not expired");

        _depositStatus.Set(key, "expired");

        var amount = _depositAmounts.Get(key);
        Context.TransferNative(Context.Caller, amount);
        _totalLocked.Set(_totalLocked.Get() - amount);

        Context.Emit(new DepositCancelledEvent
        {
            Nonce = nonce,
            Sender = Context.Caller,
            Amount = amount,
        });
    }

    // --- Unlock (Ethereum → Basalt) ---

    /// <summary>
    /// Verify M-of-N Ed25519 relayer signatures and release locked tokens.
    /// Signatures are packed as N × 96 bytes: [32B pubkey][64B sig][32B pubkey][64B sig]...
    /// Withdrawal hash = BLAKE3(LE_u64(nonce) || recipient || UInt256_LE(amount) || stateRoot)
    /// </summary>
    [BasaltEntrypoint]
    public void Unlock(ulong depositNonce, byte[] recipient, UInt256 amount, byte[] stateRoot, byte[] signatures)
    {
        RequireNotPaused();
        Context.Require(recipient.Length == 20, "BRIDGE: recipient must be 20 bytes");
        Context.Require(!amount.IsZero, "BRIDGE: zero amount");
        Context.Require(signatures.Length > 0 && signatures.Length % SignatureEntrySize == 0,
            "BRIDGE: invalid signatures format");

        var nonceKey = depositNonce.ToString();
        Context.Require(!_processedWithdrawals.Get(nonceKey), "BRIDGE: already processed");

        // Compute withdrawal hash (must match BridgeState.ComputeWithdrawalHash)
        var withdrawalHash = ComputeWithdrawalHash(depositNonce, recipient, amount, stateRoot);

        // Verify M-of-N signatures
        var threshold = _threshold.Get();
        var sigCount = signatures.Length / SignatureEntrySize;
        uint validCount = 0;
        var seenRelayers = new HashSet<string>();

        for (var i = 0; i < sigCount; i++)
        {
            var offset = i * SignatureEntrySize;
            var pubKeyBytes = signatures[offset..(offset + PubKeySize)];
            var sigBytes = signatures[(offset + PubKeySize)..(offset + SignatureEntrySize)];

            var pubKeyHex = Convert.ToHexString(pubKeyBytes);
            if (!_relayers.Get(pubKeyHex)) continue;
            if (!seenRelayers.Add(pubKeyHex)) continue;

            var pubKey = new PublicKey(pubKeyBytes);
            var sig = new Signature(sigBytes);
            if (Ed25519Signer.Verify(pubKey, withdrawalHash, sig))
                validCount++;

            if (validCount >= threshold) break;
        }

        Context.Require(validCount >= threshold, "BRIDGE: insufficient valid signatures");

        // C-9: Verify unlock amount does not exceed locked balance
        var locked = _totalLocked.Get();
        Context.Require(amount <= locked, "BRIDGE: amount exceeds locked balance");

        _processedWithdrawals.Set(nonceKey, true);
        Context.TransferNative(recipient, amount);

        // BRIDGE-02: Decrement total locked on unlock
        _totalLocked.Set(locked - amount);

        Context.Emit(new WithdrawalUnlockedEvent
        {
            Nonce = depositNonce,
            Recipient = recipient,
            Amount = amount,
        });
    }

    // --- Views ---

    [BasaltView]
    public string GetAdmin() => _admin.Get("admin") ?? "";

    [BasaltView]
    public UInt256 GetDepositAmount(ulong nonce) => _depositAmounts.Get(nonce.ToString());

    [BasaltView]
    public string GetDepositStatus(ulong nonce) => _depositStatus.Get(nonce.ToString()) ?? "";

    [BasaltView]
    public string GetDepositRecipient(ulong nonce) => _depositRecipients.Get(nonce.ToString()) ?? "";

    [BasaltView]
    public ulong GetCurrentNonce() => _nextNonce.Get();

    [BasaltView]
    public UInt256 GetTotalLocked() => _totalLocked.Get();

    [BasaltView]
    public bool IsRelayer(byte[] publicKey) => _relayers.Get(Convert.ToHexString(publicKey));

    [BasaltView]
    public uint GetThreshold() => _threshold.Get();

    [BasaltView]
    public uint GetRelayerCount() => _relayerCount.Get();

    [BasaltView]
    public bool IsWithdrawalProcessed(ulong nonce) => _processedWithdrawals.Get(nonce.ToString());

    [BasaltView]
    public bool IsPaused() => _paused.Get();

    // --- Internal helpers ---

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "BRIDGE: not admin");
    }

    private void RequireNotPaused()
    {
        Context.Require(!_paused.Get(), "BRIDGE: paused");
    }

    /// <summary>
    /// Compute withdrawal hash — byte-for-byte compatible with BridgeState.ComputeWithdrawalHash().
    /// Format: BLAKE3(version || LE_u32(chainId) || contractAddress || LE_u64(nonce) || recipient || LE_u256(amount) || stateRoot)
    /// BRIDGE-01: includes chain ID and contract address to prevent cross-chain replay.
    /// BRIDGE-03: enforces fixed-length recipient (20 bytes) and stateRoot (32 bytes).
    /// BRIDGE-12: prepends version byte (0x02).
    /// </summary>
    private static byte[] ComputeWithdrawalHash(ulong nonce, byte[] recipient, UInt256 amount, byte[] stateRoot)
    {
        // BRIDGE-03: enforce fixed lengths
        Context.Require(recipient.Length == 20, "BRIDGE: recipient must be 20 bytes");
        Context.Require(stateRoot.Length == 32, "BRIDGE: stateRoot must be 32 bytes");

        // BRIDGE-01 + BRIDGE-12: version(1) + chainId(4) + contractAddress(20) + nonce(8) + recipient(20) + amount(32) + stateRoot(32) = 117
        var data = new byte[1 + 4 + 20 + 8 + 20 + 32 + 32];
        var offset = 0;

        // Version byte (BRIDGE-12)
        data[offset] = 0x02;
        offset += 1;

        // Chain ID (BRIDGE-01) — MED-02: explicit little-endian
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), Context.ChainId);
        offset += 4;

        // Contract address (BRIDGE-01)
        Context.Self.CopyTo(data.AsSpan(offset, 20));
        offset += 20;

        // Nonce — MED-02: explicit little-endian
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset, 8), nonce);
        offset += 8;

        // Recipient (fixed 20 bytes)
        recipient.CopyTo(data.AsSpan(offset, 20));
        offset += 20;

        // Amount (UInt256 LE, 32 bytes)
        amount.WriteTo(data.AsSpan(offset, 32));
        offset += 32;

        // State root (fixed 32 bytes)
        stateRoot.CopyTo(data.AsSpan(offset, 32));

        return Blake3Hasher.Hash(data).ToArray();
    }
}

// --- Events ---

[BasaltEvent]
public class DepositLockedEvent
{
    [Indexed] public ulong Nonce { get; set; }
    [Indexed] public byte[] Sender { get; set; } = null!;
    public byte[] EthRecipient { get; set; } = null!;
    public UInt256 Amount { get; set; }
    public ulong BlockHeight { get; set; }
}

[BasaltEvent]
public class DepositConfirmedEvent
{
    [Indexed] public ulong Nonce { get; set; }
}

[BasaltEvent]
public class DepositFinalizedEvent
{
    [Indexed] public ulong Nonce { get; set; }
}

[BasaltEvent]
public class WithdrawalUnlockedEvent
{
    [Indexed] public ulong Nonce { get; set; }
    [Indexed] public byte[] Recipient { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class RelayerAddedEvent
{
    [Indexed] public byte[] RelayerPublicKey { get; set; } = null!;
}

[BasaltEvent]
public class RelayerRemovedEvent
{
    [Indexed] public byte[] RelayerPublicKey { get; set; } = null!;
}

[BasaltEvent]
public class ThresholdUpdatedEvent
{
    public uint OldThreshold { get; set; }
    public uint NewThreshold { get; set; }
}

[BasaltEvent]
public class BridgePausedEvent
{
    [Indexed] public byte[] Admin { get; set; } = null!;
}

[BasaltEvent]
public class BridgeUnpausedEvent
{
    [Indexed] public byte[] Admin { get; set; } = null!;
}

[BasaltEvent]
public class DepositCancelledEvent
{
    [Indexed] public ulong Nonce { get; set; }
    [Indexed] public byte[] Sender { get; set; } = null!;
    public UInt256 Amount { get; set; }
}
