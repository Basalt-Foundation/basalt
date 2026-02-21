using System.Buffers.Binary;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Bridge;

/// <summary>
/// BridgeETH system contract state (0x0...0010).
/// Manages locked assets, deposit tracking, and withdrawal processing.
/// </summary>
public sealed class BridgeState
{
    private readonly Dictionary<ulong, BridgeDeposit> _deposits = new();
    private readonly HashSet<ulong> _processedWithdrawals = new();
    private readonly Dictionary<string, UInt256> _lockedBalances = new(); // tokenAddr -> locked amount
    private ulong _nextNonce;
    private readonly object _lock = new();

    /// <summary>Basalt chain ID.</summary>
    public uint BasaltChainId { get; init; } = 1;

    /// <summary>Ethereum chain ID (Sepolia = 11155111).</summary>
    public uint EthereumChainId { get; init; } = 11155111;

    /// <summary>
    /// Lock tokens on Basalt for bridging to Ethereum.
    /// Returns the deposit with assigned nonce.
    /// </summary>
    public BridgeDeposit Lock(byte[] sender, byte[] recipient, UInt256 amount, byte[]? tokenAddress = null)
    {
        if (amount.IsZero)
            throw new BridgeException("Cannot bridge zero amount");

        lock (_lock)
        {
            var token = tokenAddress ?? new byte[20];
            var tokenKey = Convert.ToHexString(token);

            var deposit = new BridgeDeposit
            {
                Nonce = _nextNonce++,
                Sender = sender,
                Recipient = recipient,
                Amount = amount,
                TokenAddress = token,
                SourceChainId = BasaltChainId,
                DestinationChainId = EthereumChainId,
                BlockHeight = 0, // Set by block executor
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Direction = BridgeDirection.BasaltToEthereum,
                Status = BridgeTransferStatus.Pending,
            };

            _deposits[deposit.Nonce] = deposit;

            // Track locked balance
            _lockedBalances.TryGetValue(tokenKey, out var current);
            _lockedBalances[tokenKey] = current + amount;

            return deposit;
        }
    }

    /// <summary>
    /// Process a withdrawal (unlock) on Basalt after verifying the Merkle proof and multisig.
    /// CRIT-01: Verifies the Merkle proof before accepting the withdrawal.
    /// HIGH-02: Decrements the locked balance upon successful unlock.
    /// </summary>
    /// <param name="withdrawal">The withdrawal request with proof and signatures.</param>
    /// <param name="relayer">The multisig relayer for signature verification.</param>
    /// <param name="tokenAddress">Token address to decrement locked balance (null = native).</param>
    /// <returns>True if the withdrawal was processed successfully.</returns>
    public bool Unlock(BridgeWithdrawal withdrawal, MultisigRelayer relayer, byte[]? tokenAddress = null)
    {
        lock (_lock)
        {
            // Check if already processed (replay protection)
            if (_processedWithdrawals.Contains(withdrawal.DepositNonce))
                return false;

            // CRIT-01: Verify Merkle proof when provided
            if (withdrawal.Proof.Length > 0)
            {
                var depositLeaf = ComputeDepositLeaf(withdrawal.DepositNonce, withdrawal.Recipient, withdrawal.Amount);
                if (!BridgeProofVerifier.VerifyMerkleProof(
                        depositLeaf, withdrawal.Proof, withdrawal.DepositNonce, withdrawal.StateRoot))
                    return false;
            }

            // Verify relayer signatures (multisig threshold)
            var withdrawalHash = ComputeWithdrawalHash(withdrawal, BasaltChainId);
            if (!relayer.VerifyMessage(withdrawalHash, withdrawal.Signatures))
                return false;

            // HIGH-02: Decrement locked balance
            var token = tokenAddress ?? new byte[20];
            var tokenKey = Convert.ToHexString(token);
            if (_lockedBalances.TryGetValue(tokenKey, out var locked) && locked >= withdrawal.Amount)
            {
                _lockedBalances[tokenKey] = locked - withdrawal.Amount;
            }

            _processedWithdrawals.Add(withdrawal.DepositNonce);
            return true;
        }
    }

    /// <summary>
    /// Mark a deposit as confirmed (included in a finalized block).
    /// BRIDGE-05: Only transitions from Pending to Confirmed.
    /// </summary>
    public bool ConfirmDeposit(ulong nonce, ulong blockHeight)
    {
        lock (_lock)
        {
            if (!_deposits.TryGetValue(nonce, out var deposit))
                return false;

            // BRIDGE-05: enforce state machine transition
            if (deposit.Status != BridgeTransferStatus.Pending)
                return false;

            deposit.Status = BridgeTransferStatus.Confirmed;
            return true;
        }
    }

    /// <summary>
    /// Mark a deposit as finalized (cross-chain proof submitted).
    /// BRIDGE-05: Only transitions from Confirmed to Finalized.
    /// </summary>
    public bool FinalizeDeposit(ulong nonce)
    {
        lock (_lock)
        {
            if (!_deposits.TryGetValue(nonce, out var deposit))
                return false;

            // BRIDGE-05: enforce state machine transition
            if (deposit.Status != BridgeTransferStatus.Confirmed)
                return false;

            deposit.Status = BridgeTransferStatus.Finalized;
            return true;
        }
    }

    /// <summary>Get a deposit by nonce.</summary>
    public BridgeDeposit? GetDeposit(ulong nonce)
    {
        lock (_lock)
        {
            _deposits.TryGetValue(nonce, out var deposit);
            return deposit;
        }
    }

    /// <summary>Get all pending deposits awaiting relay.</summary>
    public IReadOnlyList<BridgeDeposit> GetPendingDeposits()
    {
        lock (_lock)
            return _deposits.Values
                .Where(d => d.Status == BridgeTransferStatus.Pending || d.Status == BridgeTransferStatus.Confirmed)
                .OrderBy(d => d.Nonce)
                .ToList();
    }

    /// <summary>Check if a withdrawal has already been processed.</summary>
    public bool IsWithdrawalProcessed(ulong nonce)
    {
        lock (_lock)
            return _processedWithdrawals.Contains(nonce);
    }

    /// <summary>Get the total locked balance for a token.</summary>
    public UInt256 GetLockedBalance(byte[]? tokenAddress = null)
    {
        lock (_lock)
        {
            var key = Convert.ToHexString(tokenAddress ?? new byte[20]);
            _lockedBalances.TryGetValue(key, out var balance);
            return balance;
        }
    }

    /// <summary>Get the current nonce (next deposit ID).</summary>
    public ulong CurrentNonce
    {
        get { lock (_lock) return _nextNonce; }
    }

    /// <summary>
    /// CRIT-01: Compute a deposit leaf for Merkle proof verification.
    /// Format: nonce (8 bytes LE) || recipient (20 bytes) || amount (32 bytes LE).
    /// This is used as the leaf data in the Merkle tree of deposits.
    /// </summary>
    public static byte[] ComputeDepositLeaf(ulong nonce, byte[] recipient, UInt256 amount)
    {
        var data = new byte[8 + 20 + 32];
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0, 8), nonce);
        recipient.AsSpan(0, Math.Min(recipient.Length, 20)).CopyTo(data.AsSpan(8));
        amount.WriteTo(data.AsSpan(28, 32));
        return data;
    }

    /// <summary>
    /// Compute the hash of a withdrawal message for signature verification.
    /// Format: BLAKE3(version || LE_u32(chainId) || contractAddress || LE_u64(nonce) || recipient || LE_u256(amount) || stateRoot)
    /// BRIDGE-01: includes chain ID and contract address for cross-chain replay protection.
    /// BRIDGE-03: enforces fixed-length recipient (20 bytes) and stateRoot (32 bytes).
    /// BRIDGE-12: prepends version byte (0x02).
    /// </summary>
    public static byte[] ComputeWithdrawalHash(BridgeWithdrawal withdrawal, uint chainId = 1, byte[]? contractAddress = null)
    {
        var contractAddr = contractAddress ?? new byte[20];

        // BRIDGE-03: Strict length validation — reject ambiguous inputs instead of silent truncation/padding
        if (contractAddr.Length != 20)
            throw new BridgeException($"Contract address must be exactly 20 bytes, got {contractAddr.Length}.");
        if (withdrawal.Recipient.Length != 20)
            throw new BridgeException($"Recipient must be exactly 20 bytes, got {withdrawal.Recipient.Length}.");
        if (withdrawal.StateRoot.Length != 32)
            throw new BridgeException($"State root must be exactly 32 bytes, got {withdrawal.StateRoot.Length}.");

        // version(1) + chainId(4) + contractAddress(20) + nonce(8) + recipient(20) + amount(32) + stateRoot(32) = 117
        var data = new byte[1 + 4 + 20 + 8 + 20 + 32 + 32];
        var offset = 0;

        // Version byte (BRIDGE-12)
        data[offset] = 0x02;
        offset += 1;

        // Chain ID (BRIDGE-01)
        BitConverter.TryWriteBytes(data.AsSpan(offset, 4), chainId);
        offset += 4;

        // Contract address (BRIDGE-01)
        contractAddr.CopyTo(data.AsSpan(offset, 20));
        offset += 20;

        // Nonce
        BitConverter.TryWriteBytes(data.AsSpan(offset, 8), withdrawal.DepositNonce);
        offset += 8;

        // Recipient (fixed 20 bytes)
        withdrawal.Recipient.CopyTo(data.AsSpan(offset, 20));
        offset += 20;

        // Amount (UInt256 LE, 32 bytes)
        withdrawal.Amount.WriteTo(data.AsSpan(offset, 32));
        offset += 32;

        // State root (fixed 32 bytes)
        withdrawal.StateRoot.CopyTo(data.AsSpan(offset, 32));

        return Blake3Hasher.Hash(data).ToArray();
    }
}

/// <summary>
/// Exception for bridge-specific errors.
/// </summary>
public sealed class BridgeException : Exception
{
    public BridgeException(string message) : base(message) { }
}
