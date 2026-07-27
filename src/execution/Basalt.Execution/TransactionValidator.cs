using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.VM;
using Basalt.Storage;

namespace Basalt.Execution;

/// <summary>
/// 7-step transaction validation pipeline.
/// </summary>
public sealed class TransactionValidator
{
    private readonly ChainParameters _chainParams;

    public TransactionValidator(ChainParameters chainParams)
    {
        _chainParams = chainParams;
    }

    /// <summary>
    /// Validate a transaction against the current state (legacy — no base fee check).
    /// </summary>
    public BasaltResult Validate(Transaction tx, IStateDatabase stateDb) => Validate(tx, stateDb, UInt256.Zero);

    /// <summary>
    /// Validate a transaction against the current state with EIP-1559 base fee.
    /// </summary>
    public BasaltResult Validate(Transaction tx, IStateDatabase stateDb, UInt256 baseFee)
        => Validate(tx, stateDb, baseFee, skipSignature: false);

    /// <summary>
    /// Validate a transaction, optionally skipping signature verification
    /// (for transactions already verified at mempool admission).
    /// </summary>
    /// <summary>
    /// How far ahead of the account's nonce a transaction may be when entering the mempool.
    ///
    /// Block building needs the exact next nonce, but admission does not: the mempool already groups by
    /// sender, sorts by nonce and serves only the contiguous run from the on-chain nonce, stopping at
    /// the first gap. Refusing a future nonce outright meant a sender had to wait for one transaction to
    /// be mined before offering the next, so anything wanting two actions in a row paid a block of
    /// latency per action.
    ///
    /// Bounded because a queue nobody can fill is a queue anyone can flood. Beyond this the sender is
    /// asking us to hold transactions that may never become valid.
    /// </summary>
    public const ulong MaxNonceLookahead = 64;

    public BasaltResult Validate(Transaction tx, IStateDatabase stateDb, UInt256 baseFee, bool skipSignature)
        => Validate(tx, stateDb, baseFee, skipSignature, allowFutureNonce: false);

    /// <param name="allowFutureNonce">True at mempool admission, false when building a block.</param>
    public BasaltResult Validate(
        Transaction tx, IStateDatabase stateDb, UInt256 baseFee, bool skipSignature, bool allowFutureNonce)
    {
        if (!skipSignature)
        {
            // Step 1: Verify signature
            if (!tx.VerifySignature())
                return BasaltResult.Error(BasaltErrorCode.InvalidSignature, "Transaction signature verification failed.");

            // Step 2: Verify sender matches public key
            var derivedAddress = Ed25519Signer.DeriveAddress(tx.SenderPublicKey);
            if (derivedAddress != tx.Sender)
                return BasaltResult.Error(BasaltErrorCode.InvalidSignature, "Sender address does not match public key.");
        }

        // Step 3: Check chain ID
        if (tx.ChainId != _chainParams.ChainId)
            return BasaltResult.Error(BasaltErrorCode.InvalidChainId, $"Expected chain ID {_chainParams.ChainId}, got {tx.ChainId}.");

        // Step 4: Check nonce
        var account = stateDb.GetAccount(tx.Sender);
        var expectedNonce = account?.Nonce ?? 0;
        if (tx.Nonce < expectedNonce)
            return BasaltResult.Error(BasaltErrorCode.InvalidNonce,
                $"Nonce {tx.Nonce} is already used; account is at {expectedNonce}.");

        if (allowFutureNonce)
        {
            if (tx.Nonce > expectedNonce + MaxNonceLookahead)
                return BasaltResult.Error(BasaltErrorCode.InvalidNonce,
                    $"Nonce {tx.Nonce} is more than {MaxNonceLookahead} ahead of {expectedNonce}.");
        }
        else if (tx.Nonce != expectedNonce)
        {
            return BasaltResult.Error(BasaltErrorCode.InvalidNonce, $"Expected nonce {expectedNonce}, got {tx.Nonce}.");
        }

        // Step 5: Check balance (value + max gas cost)
        // MED-03 R3: Use checked arithmetic to prevent UInt256 overflow.
        // Without this, tx.Value + gasCost could wrap to a small number, passing the balance
        // check for a transaction the sender cannot actually afford.
        var gasCost = tx.EffectiveMaxFee * new UInt256(tx.GasLimit);
        if (!UInt256.TryAdd(tx.Value, gasCost, out var totalCost))
            return BasaltResult.Error(BasaltErrorCode.InsufficientBalance, "Value + gas cost overflows.");
        var balance = account?.Balance ?? UInt256.Zero;
        if (balance < totalCost)
            return BasaltResult.Error(BasaltErrorCode.InsufficientBalance, "Insufficient balance for value + gas.");

        // Step 6: Check gas limits and fee constraints
        // H-5: Minimum gas limit validation — must cover at least the base tx cost
        if (tx.GasLimit < GasTable.TxBase)
            return BasaltResult.Error(BasaltErrorCode.InsufficientGas, $"Transaction gas limit {tx.GasLimit} is below minimum ({GasTable.TxBase}).");

        if (tx.GasLimit > _chainParams.BlockGasLimit)
            return BasaltResult.Error(BasaltErrorCode.GasLimitExceeded, "Transaction gas limit exceeds block gas limit.");

        if (tx.IsEip1559)
        {
            // H-2: MaxPriorityFeePerGas must not exceed MaxFeePerGas (EIP-1559 invariant)
            if (tx.MaxPriorityFeePerGas > tx.MaxFeePerGas)
                return BasaltResult.Error(BasaltErrorCode.InsufficientGas, "MaxPriorityFeePerGas exceeds MaxFeePerGas.");

            // EIP-1559: MaxFeePerGas must cover the current base fee
            if (!baseFee.IsZero && tx.MaxFeePerGas < baseFee)
                return BasaltResult.Error(BasaltErrorCode.InsufficientGas, "MaxFeePerGas below current base fee.");
        }
        else
        {
            // Legacy: GasPrice must meet minimum and cover base fee
            if (tx.GasPrice < _chainParams.MinGasPrice)
                return BasaltResult.Error(BasaltErrorCode.InsufficientGas, "Gas price below minimum.");
            if (!baseFee.IsZero && tx.GasPrice < baseFee)
                return BasaltResult.Error(BasaltErrorCode.InsufficientGas, "Gas price below current base fee.");
        }

        // Step 7: Check data size
        if ((uint)tx.Data.Length > _chainParams.MaxTransactionDataBytes)
            return BasaltResult.Error(BasaltErrorCode.DataTooLarge, $"Transaction data exceeds maximum size of {_chainParams.MaxTransactionDataBytes} bytes.");

        return BasaltResult.Ok;
    }
}
