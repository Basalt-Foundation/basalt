using System.Security.Cryptography;
using Basalt.Sdk.Wallet.Accounts;

namespace Basalt.Sdk.Wallet.HdWallet;

/// <summary>
/// Hierarchical deterministic wallet using BIP-39 mnemonics and SLIP-0010 Ed25519 derivation.
/// Generates multiple accounts from a single seed phrase.
/// </summary>
public sealed class HdWallet : IDisposable
{
    private readonly byte[] _seed;
    private readonly Dictionary<uint, Account> _cachedAccounts = [];
    private bool _disposed;

    /// <summary>The mnemonic phrase used to create this wallet (null if created from seed directly).</summary>
    public string? MnemonicPhrase { get; }

    private HdWallet(byte[] seed, string? mnemonic)
    {
        _seed = seed;
        MnemonicPhrase = mnemonic;
    }

    /// <summary>
    /// Create a new HD wallet with a fresh mnemonic phrase.
    /// </summary>
    /// <param name="wordCount">Number of mnemonic words (12 or 24). Default: 24 (256-bit entropy).</param>
    /// <returns>A new HD wallet with a randomly generated seed.</returns>
    public static HdWallet Create(int wordCount = 24)
    {
        var mnemonic = Mnemonic.Generate(wordCount);
        var seed = Mnemonic.ToSeed(mnemonic);
        return new HdWallet(seed, mnemonic);
    }

    /// <summary>
    /// Recover an HD wallet from an existing mnemonic phrase.
    /// </summary>
    /// <param name="mnemonic">Space-separated BIP-39 mnemonic phrase.</param>
    /// <param name="passphrase">Optional BIP-39 passphrase.</param>
    /// <returns>The recovered HD wallet.</returns>
    public static HdWallet FromMnemonic(string mnemonic, string passphrase = "")
    {
        if (!Mnemonic.Validate(mnemonic))
            throw new ArgumentException("Invalid BIP-39 mnemonic phrase.", nameof(mnemonic));

        var seed = Mnemonic.ToSeed(mnemonic, passphrase);
        return new HdWallet(seed, mnemonic);
    }

    /// <summary>
    /// Create an HD wallet directly from a 64-byte seed.
    /// </summary>
    /// <param name="seed">64-byte BIP-39 seed.</param>
    /// <returns>The HD wallet.</returns>
    public static HdWallet FromSeed(byte[] seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        if (seed.Length != 64)
            throw new ArgumentException("Seed must be exactly 64 bytes.", nameof(seed));

        var copy = new byte[64];
        seed.CopyTo(copy.AsSpan());
        return new HdWallet(copy, null);
    }

    /// <summary>
    /// Get an account at the specified index using Basalt's default derivation path.
    /// Path: m/44'/9000'/0'/0'/{index}'
    /// </summary>
    /// <param name="index">Account index (0-based).</param>
    /// <returns>The derived account.</returns>
    public Account GetAccount(uint index = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cachedAccounts.TryGetValue(index, out var cached))
            return cached;

        var path = DerivationPath.Basalt(index);
        var privateKey = HdKeyDerivation.DerivePath(_seed, path);
        var account = Account.FromPrivateKey(privateKey);

        // Zero the intermediate key (Account made its own copy)
        CryptographicOperations.ZeroMemory(privateKey);

        _cachedAccounts[index] = account;
        return account;
    }

    /// <summary>
    /// Get an account at a custom derivation path.
    /// </summary>
    /// <param name="path">Custom derivation path (all levels must be hardened).</param>
    /// <returns>The derived account.</returns>
    public Account GetAccountAtPath(DerivationPath path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var privateKey = HdKeyDerivation.DerivePath(_seed, path);
        var account = Account.FromPrivateKey(privateKey);
        CryptographicOperations.ZeroMemory(privateKey);
        return account;
    }

    /// <summary>
    /// Get a validator account at the specified index.
    /// Uses Basalt default path for Ed25519 key.
    /// BLS key is derived from a separate path: m/44'/9000'/1'/0'/{index}'
    /// </summary>
    /// <param name="index">Validator index (0-based).</param>
    /// <returns>The derived validator account with both Ed25519 and BLS keys.</returns>
    public ValidatorAccount GetValidatorAccount(uint index = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Ed25519 key from standard path
        var ed25519Path = DerivationPath.Basalt(index);
        var ed25519Key = HdKeyDerivation.DerivePath(_seed, ed25519Path);

        // BLS key from validator-specific path (account' = 1 instead of 0)
        var blsPath = DerivationPath.Parse($"m/44'/9000'/1'/0'/{index}'");
        var blsKey = HdKeyDerivation.DerivePath(_seed, blsPath);

        var account = ValidatorAccount.FromKeys(ed25519Key, blsKey);

        CryptographicOperations.ZeroMemory(ed25519Key);
        CryptographicOperations.ZeroMemory(blsKey);

        return account;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CryptographicOperations.ZeroMemory(_seed);

        foreach (var account in _cachedAccounts.Values)
            account.Dispose();
        _cachedAccounts.Clear();
    }
}
