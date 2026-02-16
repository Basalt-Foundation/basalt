using System.Security.Cryptography;
using Basalt.Crypto;

namespace Basalt.Sdk.Wallet.Accounts;

/// <summary>
/// A validator account that holds both an Ed25519 key pair (for transactions)
/// and a BLS12-381 key pair (for consensus message signing).
/// </summary>
/// <remarks>
/// BLS private keys are 32 bytes with <c>privateKey[0] &amp;= 0x3F</c> masking applied
/// to ensure the scalar is less than the BLS12-381 field modulus.
/// BLS public keys are 48-byte compressed G1 points.
/// BLS signatures are 96-byte compressed G2 points.
/// </remarks>
public sealed class ValidatorAccount : Account
{
    private byte[] _blsPrivateKey;
    private bool _blsDisposed;

    /// <summary>
    /// Gets the 48-byte compressed BLS12-381 public key (G1 point).
    /// </summary>
    public byte[] BlsPublicKey { get; }

    private ValidatorAccount(
        byte[] ed25519PrivateKey,
        Core.PublicKey publicKey,
        Core.Address address,
        byte[] blsPrivateKey,
        byte[] blsPublicKey)
        : base(ed25519PrivateKey, publicKey, address)
    {
        _blsPrivateKey = blsPrivateKey;
        BlsPublicKey = blsPublicKey;
    }

    /// <summary>
    /// Creates a new validator account with freshly generated Ed25519 and BLS12-381 key pairs.
    /// </summary>
    /// <returns>A new <see cref="ValidatorAccount"/> with random key pairs.</returns>
    public static new ValidatorAccount Create()
    {
        var (ed25519Key, publicKey) = Ed25519Signer.GenerateKeyPair();
        var address = Ed25519Signer.DeriveAddress(publicKey);

        var blsPrivateKey = RandomNumberGenerator.GetBytes(32);
        blsPrivateKey[0] &= 0x3F;

        var blsPublicKey = BlsSigner.GetPublicKeyStatic(blsPrivateKey);

        return new ValidatorAccount(ed25519Key, publicKey, address, blsPrivateKey, blsPublicKey);
    }

    /// <summary>
    /// Imports a validator account from existing Ed25519 and BLS12-381 private keys.
    /// </summary>
    /// <param name="ed25519PrivateKey">The 32-byte Ed25519 private key.</param>
    /// <param name="blsPrivateKey">
    /// The 32-byte BLS12-381 private key. The first byte will be masked with
    /// <c>0x3F</c> to ensure the scalar is within the valid range.
    /// </param>
    /// <returns>A <see cref="ValidatorAccount"/> derived from the given keys.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="ed25519PrivateKey"/> or <paramref name="blsPrivateKey"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when either key is not exactly 32 bytes.
    /// </exception>
    public static ValidatorAccount FromKeys(byte[] ed25519PrivateKey, byte[] blsPrivateKey)
    {
        ArgumentNullException.ThrowIfNull(ed25519PrivateKey);
        ArgumentNullException.ThrowIfNull(blsPrivateKey);

        if (ed25519PrivateKey.Length != 32)
            throw new ArgumentException("Ed25519 private key must be exactly 32 bytes.", nameof(ed25519PrivateKey));
        if (blsPrivateKey.Length != 32)
            throw new ArgumentException("BLS private key must be exactly 32 bytes.", nameof(blsPrivateKey));

        var ed25519Copy = (byte[])ed25519PrivateKey.Clone();
        var publicKey = Ed25519Signer.GetPublicKey(ed25519Copy);
        var address = Ed25519Signer.DeriveAddress(publicKey);

        var blsCopy = (byte[])blsPrivateKey.Clone();
        blsCopy[0] &= 0x3F;

        var blsPublicKey = BlsSigner.GetPublicKeyStatic(blsCopy);

        return new ValidatorAccount(ed25519Copy, publicKey, address, blsCopy, blsPublicKey);
    }

    /// <summary>
    /// Signs a consensus message using the BLS12-381 private key.
    /// </summary>
    /// <param name="message">The raw message bytes to sign.</param>
    /// <returns>A 96-byte BLS signature (compressed G2 point).</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the account has been disposed.</exception>
    public byte[] SignConsensusMessage(ReadOnlySpan<byte> message)
    {
        ObjectDisposedException.ThrowIf(_blsDisposed, this);

        var signer = new BlsSigner();
        return signer.Sign(_blsPrivateKey, message);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (!_blsDisposed && disposing)
        {
            CryptographicOperations.ZeroMemory(_blsPrivateKey);
            _blsDisposed = true;
        }

        base.Dispose(disposing);
    }
}
