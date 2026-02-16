using Basalt.Core;
using Basalt.Execution;

namespace Basalt.Sdk.Wallet.Transactions;

/// <summary>
/// Core fluent builder for constructing unsigned Basalt transactions.
/// Use the static factory methods to create a builder for each transaction type,
/// then chain <c>With*</c> calls to set fields, and finally call <see cref="Build"/>
/// to produce an unsigned <see cref="Transaction"/>.
/// </summary>
public sealed class TransactionBuilder
{
    private TransactionType _type;
    private ulong _nonce;
    private Address _sender;
    private Address _to;
    private UInt256 _value;
    private ulong _gasLimit = 21_000;
    private UInt256 _gasPrice = UInt256.One;
    private byte[] _data = [];
    private byte _priority;
    private uint _chainId = 1;

    /// <summary>
    /// Creates a new builder. Use the static factory methods instead.
    /// </summary>
    private TransactionBuilder(TransactionType type)
    {
        _type = type;
    }

    /// <summary>
    /// Creates a builder for a native token transfer transaction.
    /// </summary>
    public static TransactionBuilder Transfer() => new(TransactionType.Transfer);

    /// <summary>
    /// Creates a builder for a contract deployment transaction.
    /// </summary>
    public static TransactionBuilder ContractDeploy() => new(TransactionType.ContractDeploy);

    /// <summary>
    /// Creates a builder for a contract call transaction.
    /// </summary>
    public static TransactionBuilder ContractCall() => new(TransactionType.ContractCall);

    /// <summary>
    /// Creates a builder for a stake deposit transaction.
    /// </summary>
    public static TransactionBuilder StakeDeposit() => new(TransactionType.StakeDeposit);

    /// <summary>
    /// Creates a builder for a stake withdrawal transaction.
    /// </summary>
    public static TransactionBuilder StakeWithdraw() => new(TransactionType.StakeWithdraw);

    /// <summary>
    /// Creates a builder for a validator registration transaction.
    /// </summary>
    public static TransactionBuilder ValidatorRegister() => new(TransactionType.ValidatorRegister);

    /// <summary>
    /// Sets the transaction nonce (sender's sequence number).
    /// </summary>
    /// <param name="nonce">The account nonce.</param>
    /// <returns>This builder for chaining.</returns>
    public TransactionBuilder WithNonce(ulong nonce)
    {
        _nonce = nonce;
        return this;
    }

    /// <summary>
    /// Sets the sender address.
    /// </summary>
    /// <param name="sender">The address of the transaction sender.</param>
    /// <returns>This builder for chaining.</returns>
    public TransactionBuilder WithSender(Address sender)
    {
        _sender = sender;
        return this;
    }

    /// <summary>
    /// Sets the recipient address.
    /// </summary>
    /// <param name="to">The destination address.</param>
    /// <returns>This builder for chaining.</returns>
    public TransactionBuilder WithTo(Address to)
    {
        _to = to;
        return this;
    }

    /// <summary>
    /// Sets the value (amount of native tokens) to transfer.
    /// </summary>
    /// <param name="value">The transfer amount.</param>
    /// <returns>This builder for chaining.</returns>
    public TransactionBuilder WithValue(UInt256 value)
    {
        _value = value;
        return this;
    }

    /// <summary>
    /// Sets the gas limit for the transaction. Defaults to 21,000.
    /// </summary>
    /// <param name="gasLimit">The maximum gas units this transaction may consume.</param>
    /// <returns>This builder for chaining.</returns>
    public TransactionBuilder WithGasLimit(ulong gasLimit)
    {
        _gasLimit = gasLimit;
        return this;
    }

    /// <summary>
    /// Sets the gas price (fee per gas unit). Defaults to 1.
    /// </summary>
    /// <param name="gasPrice">The price per gas unit.</param>
    /// <returns>This builder for chaining.</returns>
    public TransactionBuilder WithGasPrice(UInt256 gasPrice)
    {
        _gasPrice = gasPrice;
        return this;
    }

    /// <summary>
    /// Sets the raw data payload for the transaction.
    /// </summary>
    /// <param name="data">The data bytes.</param>
    /// <returns>This builder for chaining.</returns>
    public TransactionBuilder WithData(byte[] data)
    {
        _data = data;
        return this;
    }

    /// <summary>
    /// Sets the transaction priority (0 = normal, higher = prioritized).
    /// </summary>
    /// <param name="priority">The priority level.</param>
    /// <returns>This builder for chaining.</returns>
    public TransactionBuilder WithPriority(byte priority)
    {
        _priority = priority;
        return this;
    }

    /// <summary>
    /// Sets the chain ID for replay protection. Defaults to 1.
    /// </summary>
    /// <param name="chainId">The chain identifier.</param>
    /// <returns>This builder for chaining.</returns>
    public TransactionBuilder WithChainId(uint chainId)
    {
        _chainId = chainId;
        return this;
    }

    /// <summary>
    /// Builds an unsigned <see cref="Transaction"/> from the current builder state.
    /// The returned transaction must be signed before submission.
    /// </summary>
    /// <returns>An unsigned transaction with all configured fields.</returns>
    public Transaction Build()
    {
        return new Transaction
        {
            Type = _type,
            Nonce = _nonce,
            Sender = _sender,
            To = _to,
            Value = _value,
            GasLimit = _gasLimit,
            GasPrice = _gasPrice,
            Data = _data,
            Priority = _priority,
            ChainId = _chainId,
        };
    }
}
