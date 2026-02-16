using Basalt.Core;
using Basalt.Execution;

namespace Basalt.Sdk.Wallet.Transactions;

/// <summary>
/// Convenience builder for staking-related transactions: stake deposits,
/// stake withdrawals, and validator registrations.
/// </summary>
public sealed class StakingBuilder
{
    private readonly TransactionBuilder _inner;

    private StakingBuilder(TransactionBuilder inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Creates a builder for a stake deposit transaction with the specified amount.
    /// </summary>
    /// <param name="amount">The amount of native tokens to stake.</param>
    /// <returns>A new staking builder configured for a deposit.</returns>
    public static StakingBuilder Deposit(UInt256 amount)
    {
        var inner = TransactionBuilder.StakeDeposit()
            .WithValue(amount);
        return new StakingBuilder(inner);
    }

    /// <summary>
    /// Creates a builder for a stake withdrawal transaction with the specified amount.
    /// </summary>
    /// <param name="amount">The amount of staked tokens to withdraw.</param>
    /// <returns>A new staking builder configured for a withdrawal.</returns>
    public static StakingBuilder Withdraw(UInt256 amount)
    {
        var inner = TransactionBuilder.StakeWithdraw()
            .WithValue(amount);
        return new StakingBuilder(inner);
    }

    /// <summary>
    /// Creates a builder for a validator registration transaction.
    /// The BLS public key is stored in the transaction data field.
    /// </summary>
    /// <param name="blsPublicKey">The 48-byte compressed BLS12-381 public key for the validator.</param>
    /// <returns>A new staking builder configured for validator registration.</returns>
    public static StakingBuilder RegisterValidator(byte[] blsPublicKey)
    {
        var inner = TransactionBuilder.ValidatorRegister()
            .WithData(blsPublicKey);
        return new StakingBuilder(inner);
    }

    /// <summary>
    /// Sets the gas limit for the staking transaction. Defaults to 21,000.
    /// </summary>
    /// <param name="gasLimit">The maximum gas units this transaction may consume.</param>
    /// <returns>This builder for chaining.</returns>
    public StakingBuilder WithGasLimit(ulong gasLimit)
    {
        _inner.WithGasLimit(gasLimit);
        return this;
    }

    /// <summary>
    /// Sets the gas price (fee per gas unit). Defaults to 1.
    /// </summary>
    /// <param name="gasPrice">The price per gas unit.</param>
    /// <returns>This builder for chaining.</returns>
    public StakingBuilder WithGasPrice(UInt256 gasPrice)
    {
        _inner.WithGasPrice(gasPrice);
        return this;
    }

    /// <summary>
    /// Sets the chain ID for replay protection. Defaults to 1.
    /// </summary>
    /// <param name="chainId">The chain identifier.</param>
    /// <returns>This builder for chaining.</returns>
    public StakingBuilder WithChainId(uint chainId)
    {
        _inner.WithChainId(chainId);
        return this;
    }

    /// <summary>
    /// Sets the transaction nonce (sender's sequence number).
    /// </summary>
    /// <param name="nonce">The account nonce.</param>
    /// <returns>This builder for chaining.</returns>
    public StakingBuilder WithNonce(ulong nonce)
    {
        _inner.WithNonce(nonce);
        return this;
    }

    /// <summary>
    /// Sets the sender address.
    /// </summary>
    /// <param name="sender">The address of the transaction sender.</param>
    /// <returns>This builder for chaining.</returns>
    public StakingBuilder WithSender(Address sender)
    {
        _inner.WithSender(sender);
        return this;
    }

    /// <summary>
    /// Builds an unsigned staking <see cref="Transaction"/>.
    /// The returned transaction must be signed before submission.
    /// </summary>
    /// <returns>An unsigned staking transaction.</returns>
    public Transaction Build() => _inner.Build();
}
