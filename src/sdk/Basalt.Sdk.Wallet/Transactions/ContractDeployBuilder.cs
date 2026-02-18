using Basalt.Core;
using Basalt.Execution;

namespace Basalt.Sdk.Wallet.Transactions;

/// <summary>
/// Convenience builder for contract deployment transactions.
/// Sets the recipient to <see cref="Address.Zero"/> and encodes the bytecode
/// (with optional constructor arguments) into the transaction data field.
/// </summary>
public sealed class ContractDeployBuilder
{
    private readonly TransactionBuilder _inner;

    /// <summary>
    /// Creates a new contract deploy builder with the given bytecode and optional constructor arguments.
    /// The bytecode and constructor arguments are concatenated into the transaction data field.
    /// </summary>
    /// <param name="bytecode">The compiled contract bytecode.</param>
    /// <param name="constructorArgs">Optional ABI-encoded constructor arguments appended after the bytecode.</param>
    public ContractDeployBuilder(byte[] bytecode, byte[]? constructorArgs = null)
    {
        var data = ConcatData(bytecode, constructorArgs);

        _inner = TransactionBuilder.ContractDeploy()
            .WithTo(Address.Zero)
            .WithData(data);
    }

    /// <summary>
    /// Sets the gas limit for the deployment. Defaults to 21,000.
    /// </summary>
    /// <param name="gasLimit">The maximum gas units this transaction may consume.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractDeployBuilder WithGasLimit(ulong gasLimit)
    {
        _inner.WithGasLimit(gasLimit);
        return this;
    }

    /// <summary>
    /// Sets the gas price (fee per gas unit). Defaults to 1.
    /// </summary>
    /// <param name="gasPrice">The price per gas unit.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractDeployBuilder WithGasPrice(UInt256 gasPrice)
    {
        _inner.WithGasPrice(gasPrice);
        return this;
    }

    /// <summary>
    /// Sets the maximum fee per gas (EIP-1559).
    /// </summary>
    public ContractDeployBuilder WithMaxFeePerGas(UInt256 maxFeePerGas)
    {
        _inner.WithMaxFeePerGas(maxFeePerGas);
        return this;
    }

    /// <summary>
    /// Sets the maximum priority fee (tip) per gas (EIP-1559).
    /// </summary>
    public ContractDeployBuilder WithMaxPriorityFeePerGas(UInt256 maxPriorityFeePerGas)
    {
        _inner.WithMaxPriorityFeePerGas(maxPriorityFeePerGas);
        return this;
    }

    /// <summary>
    /// Sets the chain ID for replay protection. Defaults to 1.
    /// </summary>
    /// <param name="chainId">The chain identifier.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractDeployBuilder WithChainId(uint chainId)
    {
        _inner.WithChainId(chainId);
        return this;
    }

    /// <summary>
    /// Sets the transaction nonce (sender's sequence number).
    /// </summary>
    /// <param name="nonce">The account nonce.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractDeployBuilder WithNonce(ulong nonce)
    {
        _inner.WithNonce(nonce);
        return this;
    }

    /// <summary>
    /// Sets the sender address.
    /// </summary>
    /// <param name="sender">The address of the transaction sender.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractDeployBuilder WithSender(Address sender)
    {
        _inner.WithSender(sender);
        return this;
    }

    /// <summary>
    /// Builds an unsigned contract deployment <see cref="Transaction"/>.
    /// The returned transaction must be signed before submission.
    /// </summary>
    /// <returns>An unsigned contract deployment transaction.</returns>
    public Transaction Build() => _inner.Build();

    private static byte[] ConcatData(byte[] bytecode, byte[]? constructorArgs)
    {
        if (constructorArgs is null || constructorArgs.Length == 0)
            return bytecode;

        var data = new byte[bytecode.Length + constructorArgs.Length];
        bytecode.CopyTo(data, 0);
        constructorArgs.CopyTo(data, bytecode.Length);
        return data;
    }
}
