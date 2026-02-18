using System.Text;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;

namespace Basalt.Sdk.Wallet.Transactions;

/// <summary>
/// Convenience builder for contract call transactions.
/// Encodes the method selector (first 4 bytes of the BLAKE3 hash of the method name)
/// and any appended arguments into the transaction data field.
/// </summary>
public sealed class ContractCallBuilder
{
    private readonly TransactionBuilder _inner;
    private readonly byte[] _selector;
    private byte[] _args = [];

    /// <summary>
    /// Creates a new contract call builder targeting the specified contract and method.
    /// The method name is hashed via BLAKE3 to produce a 4-byte selector, matching
    /// the dispatch convention used by the Basalt VM for built-in methods.
    /// </summary>
    /// <param name="contractAddress">The address of the contract to call.</param>
    /// <param name="methodName">The method name (e.g. <c>"transfer"</c>). Hashed to a 4-byte selector.</param>
    public ContractCallBuilder(Address contractAddress, string methodName)
    {
        _selector = ComputeSelector(methodName);

        _inner = TransactionBuilder.ContractCall()
            .WithTo(contractAddress);
    }

    /// <summary>
    /// Creates a new contract call builder with a pre-computed selector.
    /// Use <see cref="Basalt.Sdk.Wallet.Contracts.SdkContractEncoder.ComputeFnvSelector"/> for SDK contracts.
    /// </summary>
    /// <param name="contractAddress">The address of the contract to call.</param>
    /// <param name="selector">A pre-computed 4-byte method selector.</param>
    public ContractCallBuilder(Address contractAddress, byte[] selector)
    {
        _selector = selector;

        _inner = TransactionBuilder.ContractCall()
            .WithTo(contractAddress);
    }

    /// <summary>
    /// Appends argument byte arrays to the call data after the 4-byte method selector.
    /// Arguments are concatenated in order with no padding or length prefixes.
    /// </summary>
    /// <param name="args">One or more byte arrays to append as arguments.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractCallBuilder WithArgs(params byte[][] args)
    {
        if (args.Length == 0)
            return this;

        var totalLength = 0;
        for (var i = 0; i < args.Length; i++)
            totalLength += args[i].Length;

        var combined = new byte[_args.Length + totalLength];
        _args.CopyTo(combined, 0);

        var offset = _args.Length;
        for (var i = 0; i < args.Length; i++)
        {
            args[i].CopyTo(combined, offset);
            offset += args[i].Length;
        }

        _args = combined;
        return this;
    }

    /// <summary>
    /// Sets the value (amount of native tokens) to send with the call.
    /// </summary>
    /// <param name="value">The transfer amount.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractCallBuilder WithValue(UInt256 value)
    {
        _inner.WithValue(value);
        return this;
    }

    /// <summary>
    /// Sets the gas limit for the call. Defaults to 21,000.
    /// </summary>
    /// <param name="gasLimit">The maximum gas units this transaction may consume.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractCallBuilder WithGasLimit(ulong gasLimit)
    {
        _inner.WithGasLimit(gasLimit);
        return this;
    }

    /// <summary>
    /// Sets the gas price (fee per gas unit). Defaults to 1.
    /// </summary>
    /// <param name="gasPrice">The price per gas unit.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractCallBuilder WithGasPrice(UInt256 gasPrice)
    {
        _inner.WithGasPrice(gasPrice);
        return this;
    }

    /// <summary>
    /// Sets the maximum fee per gas (EIP-1559).
    /// </summary>
    public ContractCallBuilder WithMaxFeePerGas(UInt256 maxFeePerGas)
    {
        _inner.WithMaxFeePerGas(maxFeePerGas);
        return this;
    }

    /// <summary>
    /// Sets the maximum priority fee (tip) per gas (EIP-1559).
    /// </summary>
    public ContractCallBuilder WithMaxPriorityFeePerGas(UInt256 maxPriorityFeePerGas)
    {
        _inner.WithMaxPriorityFeePerGas(maxPriorityFeePerGas);
        return this;
    }

    /// <summary>
    /// Sets the chain ID for replay protection. Defaults to 1.
    /// </summary>
    /// <param name="chainId">The chain identifier.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractCallBuilder WithChainId(uint chainId)
    {
        _inner.WithChainId(chainId);
        return this;
    }

    /// <summary>
    /// Sets the transaction nonce (sender's sequence number).
    /// </summary>
    /// <param name="nonce">The account nonce.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractCallBuilder WithNonce(ulong nonce)
    {
        _inner.WithNonce(nonce);
        return this;
    }

    /// <summary>
    /// Sets the sender address.
    /// </summary>
    /// <param name="sender">The address of the transaction sender.</param>
    /// <returns>This builder for chaining.</returns>
    public ContractCallBuilder WithSender(Address sender)
    {
        _inner.WithSender(sender);
        return this;
    }

    /// <summary>
    /// Builds an unsigned contract call <see cref="Transaction"/>.
    /// The data field contains the 4-byte method selector followed by any appended arguments.
    /// The returned transaction must be signed before submission.
    /// </summary>
    /// <returns>An unsigned contract call transaction.</returns>
    public Transaction Build()
    {
        var data = new byte[_selector.Length + _args.Length];
        _selector.CopyTo(data, 0);
        _args.CopyTo(data, _selector.Length);

        _inner.WithData(data);
        return _inner.Build();
    }

    /// <summary>
    /// Computes a 4-byte method selector from a method name using BLAKE3.
    /// This matches the selector convention used by the Basalt VM dispatch table.
    /// </summary>
    private static byte[] ComputeSelector(string methodName)
    {
        var hash = Blake3Hasher.Hash(Encoding.UTF8.GetBytes(methodName));
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(hashBytes);
        var selector = new byte[4];
        hashBytes[..4].CopyTo(selector);
        return selector;
    }
}
