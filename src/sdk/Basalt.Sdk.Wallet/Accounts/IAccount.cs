using Basalt.Core;
using Basalt.Execution;

namespace Basalt.Sdk.Wallet.Accounts;

/// <summary>
/// Represents a Basalt account capable of signing transactions and messages.
/// </summary>
public interface IAccount : IDisposable
{
    /// <summary>
    /// Gets the 20-byte address derived from the account's public key.
    /// </summary>
    Address Address { get; }

    /// <summary>
    /// Gets the Ed25519 public key associated with this account.
    /// </summary>
    PublicKey PublicKey { get; }

    /// <summary>
    /// Signs an unsigned transaction using this account's private key.
    /// </summary>
    /// <param name="unsignedTx">The transaction to sign.</param>
    /// <returns>A new <see cref="Transaction"/> instance with the signature populated.</returns>
    Transaction SignTransaction(Transaction unsignedTx);

    /// <summary>
    /// Signs an arbitrary message using this account's Ed25519 private key.
    /// </summary>
    /// <param name="message">The raw message bytes to sign.</param>
    /// <returns>A 64-byte Ed25519 <see cref="Signature"/>.</returns>
    Signature SignMessage(ReadOnlySpan<byte> message);
}
