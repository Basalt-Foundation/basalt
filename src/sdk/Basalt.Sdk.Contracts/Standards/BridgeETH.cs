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
    private readonly StorageMap<string, ulong> _depositAmounts;
    private readonly StorageMap<string, string> _depositStatus;
    private readonly StorageMap<string, ulong> _depositBlockHeights;

    // Withdrawal tracking (Ethereum → Basalt)
    private readonly StorageMap<string, bool> _processedWithdrawals;

    // Balance
    private readonly StorageValue<ulong> _totalLocked;

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
        _depositAmounts = new StorageMap<string, ulong>("bch_damt");
        _depositStatus = new StorageMap<string, string>("bch_dsts");
        _depositBlockHeights = new StorageMap<string, ulong>("bch_dblk");
        _processedWithdrawals = new StorageMap<string, bool>("bch_proc");
        _totalLocked = new StorageValue<ulong>("bch_locked");

        _admin.Set("admin", Convert.ToHexString(Context.Caller));
        _threshold.Set(threshold);
    }

    // --- Admin ---

    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        RequireAdmin();
        Context.Require(newAdmin.Length > 0, "BRIDGE: invalid admin");
        _admin.Set("admin", Convert.ToHexString(newAdmin));
    }

    [BasaltEntrypoint]
    public void Pause()
    {
        RequireAdmin();
        _paused.Set(true);
    }

    [BasaltEntrypoint]
    public void Unpause()
    {
        RequireAdmin();
        _paused.Set(false);
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
        Context.Require(newThreshold >= 1, "BRIDGE: threshold must be >= 1");
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
        Context.Require(Context.TxValue > 0, "BRIDGE: must send value");
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

    // --- Unlock (Ethereum → Basalt) ---

    /// <summary>
    /// Verify M-of-N Ed25519 relayer signatures and release locked tokens.
    /// Signatures are packed as N × 96 bytes: [32B pubkey][64B sig][32B pubkey][64B sig]...
    /// Withdrawal hash = BLAKE3(LE_u64(nonce) || recipient || LE_u64(amount) || stateRoot)
    /// </summary>
    [BasaltEntrypoint]
    public void Unlock(ulong depositNonce, byte[] recipient, ulong amount, byte[] stateRoot, byte[] signatures)
    {
        RequireNotPaused();
        Context.Require(recipient.Length > 0, "BRIDGE: invalid recipient");
        Context.Require(amount > 0, "BRIDGE: zero amount");
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

        _processedWithdrawals.Set(nonceKey, true);
        Context.TransferNative(recipient, amount);

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
    public ulong GetDepositAmount(ulong nonce) => _depositAmounts.Get(nonce.ToString());

    [BasaltView]
    public string GetDepositStatus(ulong nonce) => _depositStatus.Get(nonce.ToString()) ?? "";

    [BasaltView]
    public string GetDepositRecipient(ulong nonce) => _depositRecipients.Get(nonce.ToString()) ?? "";

    [BasaltView]
    public ulong GetCurrentNonce() => _nextNonce.Get();

    [BasaltView]
    public ulong GetTotalLocked() => _totalLocked.Get();

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
    /// Format: BLAKE3(LE_u64(nonce) || recipient || LE_u64(amount) || stateRoot)
    /// </summary>
    private static byte[] ComputeWithdrawalHash(ulong nonce, byte[] recipient, ulong amount, byte[] stateRoot)
    {
        var data = new byte[8 + recipient.Length + 8 + stateRoot.Length];
        BitConverter.TryWriteBytes(data.AsSpan(0, 8), nonce);
        recipient.CopyTo(data.AsSpan(8));
        BitConverter.TryWriteBytes(data.AsSpan(8 + recipient.Length, 8), amount);
        stateRoot.CopyTo(data.AsSpan(8 + recipient.Length + 8));

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
    public ulong Amount { get; set; }
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
    public ulong Amount { get; set; }
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
