using Basalt.Core;
using Basalt.Execution;

namespace Basalt.Sdk.Wallet.Transactions;

/// <summary>
/// Convenience builder for native token transfer transactions.
/// Wraps <see cref="TransactionBuilder"/> with a simplified API focused on transfers.
/// </summary>
public sealed class TransferBuilder
{
    private readonly TransactionBuilder _inner;

    /// <summary>
    /// Creates a new transfer builder targeting the specified recipient with the given amount.
    /// </summary>
    /// <param name="to">The recipient address.</param>
    /// <param name="value">The amount of native tokens to transfer.</param>
    public TransferBuilder(Address to, UInt256 value)
    {
        _inner = TransactionBuilder.Transfer()
            .WithTo(to)
            .WithValue(value);
    }

    /// <summary>
    /// Sets the gas limit for the transfer. Defaults to 21,000.
    /// </summary>
    /// <param name="gasLimit">The maximum gas units this transaction may consume.</param>
    /// <returns>This builder for chaining.</returns>
    public TransferBuilder WithGasLimit(ulong gasLimit)
    {
        _inner.WithGasLimit(gasLimit);
        return this;
    }

    /// <summary>
    /// Sets the gas price (fee per gas unit). Defaults to 1.
    /// </summary>
    /// <param name="gasPrice">The price per gas unit.</param>
    /// <returns>This builder for chaining.</returns>
    public TransferBuilder WithGasPrice(UInt256 gasPrice)
    {
        _inner.WithGasPrice(gasPrice);
        return this;
    }

    /// <summary>
    /// Sets the maximum fee per gas (EIP-1559).
    /// </summary>
    public TransferBuilder WithMaxFeePerGas(UInt256 maxFeePerGas)
    {
        _inner.WithMaxFeePerGas(maxFeePerGas);
        return this;
    }

    /// <summary>
    /// Sets the maximum priority fee (tip) per gas (EIP-1559).
    /// </summary>
    public TransferBuilder WithMaxPriorityFeePerGas(UInt256 maxPriorityFeePerGas)
    {
        _inner.WithMaxPriorityFeePerGas(maxPriorityFeePerGas);
        return this;
    }

    /// <summary>
    /// Sets the chain ID for replay protection. Defaults to 1.
    /// </summary>
    /// <param name="chainId">The chain identifier.</param>
    /// <returns>This builder for chaining.</returns>
    public TransferBuilder WithChainId(uint chainId)
    {
        _inner.WithChainId(chainId);
        return this;
    }

    /// <summary>
    /// Sets the transaction nonce (sender's sequence number).
    /// </summary>
    /// <param name="nonce">The account nonce.</param>
    /// <returns>This builder for chaining.</returns>
    public TransferBuilder WithNonce(ulong nonce)
    {
        _inner.WithNonce(nonce);
        return this;
    }

    /// <summary>
    /// Sets the sender address.
    /// </summary>
    /// <param name="sender">The address of the transaction sender.</param>
    /// <returns>This builder for chaining.</returns>
    public TransferBuilder WithSender(Address sender)
    {
        _inner.WithSender(sender);
        return this;
    }

    /// <summary>
    /// Builds an unsigned transfer <see cref="Transaction"/>.
    /// The returned transaction must be signed before submission.
    /// </summary>
    /// <returns>An unsigned transfer transaction.</returns>
    public Transaction Build() => _inner.Build();
}
