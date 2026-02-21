using System.Security.Cryptography;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;

namespace Basalt.Sdk.Wallet.Accounts;

/// <summary>
/// A standard Basalt account backed by an Ed25519 key pair.
/// Provides transaction signing, message signing, and secure key disposal.
/// </summary>
/// <remarks>
/// Use the static factory methods <see cref="Create"/>, <see cref="FromPrivateKey(byte[])"/>,
/// or <see cref="FromPrivateKey(string)"/> to construct instances. The private key material
/// is zeroed on disposal via <see cref="CryptographicOperations.ZeroMemory"/>.
/// </remarks>
public class Account : IAccount
{
    private byte[] _privateKey;
    private bool _disposed;

    /// <inheritdoc />
    public Address Address { get; }

    /// <inheritdoc />
    public PublicKey PublicKey { get; }

    /// <summary>
    /// Initializes a new <see cref="Account"/> with the specified key material.
    /// </summary>
    /// <param name="privateKey">The 32-byte Ed25519 private key.</param>
    /// <param name="publicKey">The corresponding Ed25519 public key.</param>
    /// <param name="address">The address derived from the public key.</param>
    protected Account(byte[] privateKey, PublicKey publicKey, Address address)
    {
        _privateKey = privateKey;
        PublicKey = publicKey;
        Address = address;
    }

    /// <summary>
    /// Creates a new account with a freshly generated Ed25519 key pair.
    /// </summary>
    /// <returns>A new <see cref="Account"/> with a random key pair.</returns>
    public static Account Create()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var address = Ed25519Signer.DeriveAddress(publicKey);
        return new Account(privateKey, publicKey, address);
    }

    /// <summary>
    /// Imports an account from an existing Ed25519 private key.
    /// </summary>
    /// <param name="privateKey">The 32-byte Ed25519 private key.</param>
    /// <returns>An <see cref="Account"/> derived from the given key.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="privateKey"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="privateKey"/> is not 32 bytes.</exception>
    public static Account FromPrivateKey(byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        if (privateKey.Length != 32)
            throw new ArgumentException("Ed25519 private key must be exactly 32 bytes.", nameof(privateKey));

        var keyCopy = (byte[])privateKey.Clone();
        var publicKey = Ed25519Signer.GetPublicKey(keyCopy);
        var address = Ed25519Signer.DeriveAddress(publicKey);
        return new Account(keyCopy, publicKey, address);
    }

    /// <summary>
    /// Imports an account from an existing Ed25519 private key encoded as a hex string.
    /// </summary>
    /// <param name="hexPrivateKey">
    /// A 64-character hex string (optionally prefixed with "0x") representing the 32-byte private key.
    /// </param>
    /// <returns>An <see cref="Account"/> derived from the decoded key.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="hexPrivateKey"/> is null.</exception>
    /// <exception cref="FormatException">Thrown when <paramref name="hexPrivateKey"/> is not valid hex.</exception>
    /// <remarks>
    /// H-15: The intermediate <c>keyBytes</c> array is zeroed after use. The
    /// <see cref="FromPrivateKey(byte[])"/> overload clones the input, so
    /// zeroing the decoded hex bytes does not affect the Account's internal key.
    /// </remarks>
    public static Account FromPrivateKey(string hexPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(hexPrivateKey);

        var hex = hexPrivateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? hexPrivateKey[2..]
            : hexPrivateKey;

        var keyBytes = Convert.FromHexString(hex);
        try
        {
            return FromPrivateKey(keyBytes);
        }
        finally
        {
            // H-15: Zero intermediate key material
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    /// <inheritdoc />
    public Transaction SignTransaction(Transaction unsignedTx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(unsignedTx);

        return Transaction.Sign(unsignedTx, _privateKey);
    }

    /// <inheritdoc />
    public Signature SignMessage(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return Ed25519Signer.Sign(_privateKey, message);
    }

    /// <summary>
    /// Gets the raw private key bytes. Intended for internal use by derived classes
    /// and the keystore manager.
    /// </summary>
    /// <returns>The private key byte array. Do not cache or store references to this array.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the account has been disposed.</exception>
    internal byte[] GetPrivateKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _privateKey;
    }

    /// <summary>
    /// Releases all resources used by this account, securely zeroing the private key material.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the unmanaged resources used by this account and optionally releases
    /// the managed resources.
    /// </summary>
    /// <param name="disposing">
    /// <c>true</c> to release both managed and unmanaged resources;
    /// <c>false</c> to release only unmanaged resources.
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            CryptographicOperations.ZeroMemory(_privateKey);
        }

        _disposed = true;
    }
}
