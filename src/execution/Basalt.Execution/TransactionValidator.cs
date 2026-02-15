using Basalt.Core;
using Basalt.Crypto;
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
    /// Validate a transaction against the current state.
    /// </summary>
    public BasaltResult Validate(Transaction tx, IStateDatabase stateDb)
    {
        // Step 1: Verify signature
        if (!tx.VerifySignature())
            return BasaltResult.Error(BasaltErrorCode.InvalidSignature, "Transaction signature verification failed.");

        // Step 2: Verify sender matches public key
        var derivedAddress = Ed25519Signer.DeriveAddress(tx.SenderPublicKey);
        if (derivedAddress != tx.Sender)
            return BasaltResult.Error(BasaltErrorCode.InvalidSignature, "Sender address does not match public key.");

        // Step 3: Check chain ID
        if (tx.ChainId != _chainParams.ChainId)
            return BasaltResult.Error(BasaltErrorCode.InvalidChainId, $"Expected chain ID {_chainParams.ChainId}, got {tx.ChainId}.");

        // Step 4: Check nonce
        var account = stateDb.GetAccount(tx.Sender);
        var expectedNonce = account?.Nonce ?? 0;
        if (tx.Nonce != expectedNonce)
            return BasaltResult.Error(BasaltErrorCode.InvalidNonce, $"Expected nonce {expectedNonce}, got {tx.Nonce}.");

        // Step 5: Check balance (value + gas cost)
        var gasCost = tx.GasPrice * new UInt256(tx.GasLimit);
        var totalCost = tx.Value + gasCost;
        var balance = account?.Balance ?? UInt256.Zero;
        if (balance < totalCost)
            return BasaltResult.Error(BasaltErrorCode.InsufficientBalance, "Insufficient balance for value + gas.");

        // Step 6: Check gas limits
        if (tx.GasLimit > _chainParams.BlockGasLimit)
            return BasaltResult.Error(BasaltErrorCode.GasLimitExceeded, "Transaction gas limit exceeds block gas limit.");

        if (tx.GasPrice < _chainParams.MinGasPrice)
            return BasaltResult.Error(BasaltErrorCode.InsufficientGas, "Gas price below minimum.");

        // Step 7: Check data size
        if ((uint)tx.Data.Length > _chainParams.MaxTransactionDataBytes)
            return BasaltResult.Error(BasaltErrorCode.DataTooLarge, $"Transaction data exceeds maximum size of {_chainParams.MaxTransactionDataBytes} bytes.");

        return BasaltResult.Ok;
    }
}
