using System.Text.Json;
using System.Text.Json.Serialization;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Sdk.Wallet.Accounts;

/// <summary>
/// Provides static methods for saving and loading encrypted account keystores to and from disk.
/// </summary>
/// <remarks>
/// Keystores are JSON files containing AES-256-GCM encrypted private key material
/// with Argon2id key derivation, following the format defined by <see cref="Keystore"/>.
/// An additional <c>address</c> field is stored alongside the encrypted data for identification.
/// </remarks>
public static class KeystoreManager
{
    /// <summary>
    /// Encrypts a private key and saves it as a keystore file at the specified path.
    /// </summary>
    /// <param name="privateKey">The raw private key bytes to encrypt.</param>
    /// <param name="address">The account address associated with this key, stored as metadata.</param>
    /// <param name="password">The password used to derive the encryption key via Argon2id.</param>
    /// <param name="filePath">The file system path where the keystore will be written.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>A task that represents the asynchronous save operation.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="privateKey"/>, <paramref name="password"/>,
    /// or <paramref name="filePath"/> is null.
    /// </exception>
    public static async Task SaveAsync(
        byte[] privateKey,
        Address address,
        string password,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(filePath);

        var keystoreFile = Keystore.Encrypt(privateKey, password);

        var walletFile = new WalletKeystoreFile
        {
            Address = address.ToHexString(),
            Keystore = keystoreFile,
        };

        var json = JsonSerializer.Serialize(walletFile, WalletKeystoreJsonContext.Default.WalletKeystoreFile);

        await File.WriteAllTextAsync(filePath, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads and decrypts a keystore file, returning an <see cref="Account"/> instance.
    /// </summary>
    /// <param name="filePath">The file system path of the keystore to load.</param>
    /// <param name="password">The password used to decrypt the private key.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task that resolves to an <see cref="Account"/> constructed from the decrypted private key.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="filePath"/> or <paramref name="password"/> is null.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the keystore file contains invalid or corrupted data.
    /// </exception>
    public static async Task<Account> LoadAsync(
        string filePath,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(password);

        var json = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);

        var walletFile = JsonSerializer.Deserialize(json, WalletKeystoreJsonContext.Default.WalletKeystoreFile)
            ?? throw new InvalidOperationException("Failed to deserialize wallet keystore file.");

        var decryptedKey = Keystore.Decrypt(walletFile.Keystore, password);

        return Account.FromPrivateKey(decryptedKey);
    }
}

/// <summary>
/// Wrapper type that pairs a <see cref="KeystoreFile"/> with the account address
/// for identification without decryption.
/// </summary>
internal sealed class WalletKeystoreFile
{
    /// <summary>
    /// Gets or sets the hex-encoded account address (e.g., "0x...").
    /// </summary>
    [JsonPropertyName("address")]
    public required string Address { get; set; }

    /// <summary>
    /// Gets or sets the encrypted keystore data.
    /// </summary>
    [JsonPropertyName("keystore")]
    public required KeystoreFile Keystore { get; set; }
}

/// <summary>
/// Source-generated JSON serializer context for AOT-compatible serialization
/// of <see cref="WalletKeystoreFile"/>.
/// </summary>
[JsonSerializable(typeof(WalletKeystoreFile))]
internal partial class WalletKeystoreJsonContext : JsonSerializerContext;
