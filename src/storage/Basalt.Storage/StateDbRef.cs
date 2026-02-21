using Basalt.Core;

namespace Basalt.Storage;

/// <summary>
/// Thread-safe mutable reference to the canonical state database.
/// All consumers (API, faucet, consensus) share the same <see cref="StateDbRef"/>
/// instance, so when the consensus layer swaps the underlying state after sync,
/// every reader immediately sees the new canonical state.
/// </summary>
/// <remarks>
/// <para><b>Multi-read consistency (M-03):</b> While the <c>volatile</c> reference swap
/// is atomic for a single read, callers performing multiple sequential operations
/// (e.g., <see cref="GetAccount"/> then <see cref="GetStorage"/>) could observe state
/// from two different databases if <see cref="Swap"/> occurs between calls.
/// Use <see cref="Snapshot"/> to obtain a forked copy for consistent multi-read paths
/// (API handlers, balance queries that touch multiple accounts, etc.).</para>
/// </remarks>
public sealed class StateDbRef : IStateDatabase
{
    private volatile IStateDatabase _inner;

    public StateDbRef(IStateDatabase inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// Atomically replace the underlying state database.
    /// Called by <c>NodeCoordinator</c> after a successful sync fork-and-swap.
    /// </summary>
    public void Swap(IStateDatabase newState)
    {
        _inner = newState ?? throw new ArgumentNullException(nameof(newState));
    }

    /// <summary>The current underlying state database.</summary>
    public IStateDatabase Inner => _inner;

    /// <summary>
    /// Return a forked snapshot of the current state for consistent multi-read access.
    /// The returned database is isolated from subsequent <see cref="Swap"/> calls.
    /// Preferred over direct reads when multiple values must be read consistently.
    /// </summary>
    public IStateDatabase Snapshot() => _inner.Fork();

    // ── IStateDatabase delegation ──────────────────────────────────────

    public AccountState? GetAccount(Address address) => _inner.GetAccount(address);
    public void SetAccount(Address address, AccountState state) => _inner.SetAccount(address, state);
    public bool AccountExists(Address address) => _inner.AccountExists(address);
    public void DeleteAccount(Address address) => _inner.DeleteAccount(address);
    public Hash256 ComputeStateRoot() => _inner.ComputeStateRoot();
    public IEnumerable<(Address Address, AccountState State)> GetAllAccounts() => _inner.GetAllAccounts();

    public byte[]? GetStorage(Address contract, Hash256 key) => _inner.GetStorage(contract, key);
    public void SetStorage(Address contract, Hash256 key, byte[] value) => _inner.SetStorage(contract, key, value);
    public void DeleteStorage(Address contract, Hash256 key) => _inner.DeleteStorage(contract, key);

    public IStateDatabase Fork() => _inner.Fork();
}
