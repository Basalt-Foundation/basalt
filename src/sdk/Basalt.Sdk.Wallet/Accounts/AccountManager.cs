using Basalt.Core;

namespace Basalt.Sdk.Wallet.Accounts;

/// <summary>
/// Manages a collection of <see cref="IAccount"/> instances with an optional active account.
/// </summary>
/// <remarks>
/// The first account added is automatically set as the active account.
/// Disposing the manager disposes all contained accounts.
/// </remarks>
public sealed class AccountManager : IDisposable
{
    private readonly Dictionary<Address, IAccount> _accounts = new();
    private bool _disposed;

    /// <summary>
    /// Gets or sets the currently active account used as the default for operations.
    /// </summary>
    /// <value>The active <see cref="IAccount"/>, or <c>null</c> if no account is active.</value>
    public IAccount? ActiveAccount { get; private set; }

    /// <summary>
    /// Adds an account to the manager. If this is the first account added,
    /// it is automatically set as the active account.
    /// </summary>
    /// <param name="account">The account to add.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="account"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when an account with the same address already exists in the manager.
    /// </exception>
    public void Add(IAccount account)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(account);

        if (!_accounts.TryAdd(account.Address, account))
            throw new ArgumentException(
                $"An account with address {account.Address} already exists.", nameof(account));

        if (_accounts.Count == 1)
            ActiveAccount = account;
    }

    /// <summary>
    /// Removes an account from the manager by its address.
    /// If the removed account was the active account, <see cref="ActiveAccount"/> is set to <c>null</c>.
    /// </summary>
    /// <param name="address">The address of the account to remove.</param>
    /// <returns><c>true</c> if the account was found and removed; otherwise, <c>false</c>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed.</exception>
    /// <remarks>
    /// H-16: The removed account is disposed to zero private key material.
    /// </remarks>
    public bool Remove(Address address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_accounts.Remove(address, out var removed))
            return false;

        if (ActiveAccount is not null && ActiveAccount.Address == address)
            ActiveAccount = null;

        // H-16: Dispose the removed account to zero private key material
        removed.Dispose();

        return true;
    }

    /// <summary>
    /// Gets an account by its address.
    /// </summary>
    /// <param name="address">The address to look up.</param>
    /// <returns>The <see cref="IAccount"/> if found; otherwise, <c>null</c>.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed.</exception>
    public IAccount? Get(Address address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _accounts.GetValueOrDefault(address);
    }

    /// <summary>
    /// Gets all accounts currently managed.
    /// </summary>
    /// <returns>A read-only collection of all <see cref="IAccount"/> instances.</returns>
    /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed.</exception>
    public IReadOnlyCollection<IAccount> GetAll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return _accounts.Values;
    }

    /// <summary>
    /// Sets the active account by address.
    /// </summary>
    /// <param name="address">The address of the account to set as active.</param>
    /// <exception cref="ObjectDisposedException">Thrown if the manager has been disposed.</exception>
    /// <exception cref="KeyNotFoundException">
    /// Thrown when no account with the specified address exists in the manager.
    /// </exception>
    public void SetActive(Address address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_accounts.TryGetValue(address, out var account))
            throw new KeyNotFoundException(
                $"No account with address {address} exists in the manager.");

        ActiveAccount = account;
    }

    /// <summary>
    /// Disposes all managed accounts and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var account in _accounts.Values)
            account.Dispose();

        _accounts.Clear();
        ActiveAccount = null;
        _disposed = true;
    }
}
