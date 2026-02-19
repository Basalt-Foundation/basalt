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
    /// Process a withdrawal (unlock) on Basalt after verifying the cross-chain proof.
    /// Returns true if the withdrawal was processed successfully.
    /// </summary>
    public bool Unlock(BridgeWithdrawal withdrawal, MultisigRelayer relayer)
    {
        lock (_lock)
        {
            // Check if already processed (replay protection)
            if (_processedWithdrawals.Contains(withdrawal.DepositNonce))
                return false;

            // Verify relayer signatures (multisig threshold)
            if (!relayer.VerifyMessage(
                    ComputeWithdrawalHash(withdrawal),
                    withdrawal.Signatures))
                return false;

            _processedWithdrawals.Add(withdrawal.DepositNonce);
            return true;
        }
    }

    /// <summary>
    /// Mark a deposit as confirmed (included in a finalized block).
    /// </summary>
    public bool ConfirmDeposit(ulong nonce, ulong blockHeight)
    {
        lock (_lock)
        {
            if (!_deposits.TryGetValue(nonce, out var deposit))
                return false;

            deposit.Status = BridgeTransferStatus.Confirmed;
            return true;
        }
    }

    /// <summary>
    /// Mark a deposit as finalized (cross-chain proof submitted).
    /// </summary>
    public bool FinalizeDeposit(ulong nonce)
    {
        lock (_lock)
        {
            if (!_deposits.TryGetValue(nonce, out var deposit))
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
    /// Compute the hash of a withdrawal message for signature verification.
    /// </summary>
    public static byte[] ComputeWithdrawalHash(BridgeWithdrawal withdrawal)
    {
        // Deterministic encoding: nonce || recipient || amount || stateRoot
        var data = new byte[8 + withdrawal.Recipient.Length + 32 + withdrawal.StateRoot.Length];
        BitConverter.TryWriteBytes(data.AsSpan(0, 8), withdrawal.DepositNonce);
        withdrawal.Recipient.CopyTo(data, 8);
        withdrawal.Amount.WriteTo(data.AsSpan(8 + withdrawal.Recipient.Length, 32));
        withdrawal.StateRoot.CopyTo(data, 8 + withdrawal.Recipient.Length + 32);
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
