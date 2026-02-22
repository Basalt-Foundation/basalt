using System.Buffers.Binary;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Storage;

/// <summary>
/// In-memory state database for development and testing.
/// Uses Dictionary-based storage with naive state root computation.
/// </summary>
/// <remarks>
/// <b>Thread safety:</b> This class is <b>not</b> thread-safe.
/// It is designed for single-threaded execution within a block processing pipeline.
/// Concurrent readers should call <see cref="Fork"/> to obtain an isolated snapshot.
/// </remarks>
public sealed class InMemoryStateDb : IStateDatabase
{
    private readonly Dictionary<Address, AccountState> _accounts = new();
    private readonly Dictionary<(Address, Hash256), byte[]> _storage = new();
    private readonly HashSet<(Address, Hash256)> _dirtyStorageKeys = new();

    public AccountState? GetAccount(Address address)
    {
        return _accounts.TryGetValue(address, out var state) ? state : null;
    }

    public void SetAccount(Address address, AccountState state)
    {
        _accounts[address] = state;
    }

    public bool AccountExists(Address address)
    {
        return _accounts.ContainsKey(address);
    }

    public void DeleteAccount(Address address)
    {
        _accounts.Remove(address);
    }

    /// <summary>
    /// Compute a naive state root by sorting accounts and hashing all states.
    /// </summary>
    /// <remarks>
    /// <para><b>M-04:</b> This root hash algorithm is <b>not interchangeable</b> with
    /// <see cref="TrieStateDb.ComputeStateRoot"/> which uses a Merkle Patricia Trie.
    /// The same state will produce different root hashes in each implementation.
    /// Do not compare roots across implementations.</para>
    /// <para><b>M-05:</b> This method hashes account states only (including the
    /// <c>StorageRoot</c> field). However, <c>SetStorage()</c> does not update
    /// <c>StorageRoot</c> in account state, so storage modifications are invisible
    /// to this root computation. Use <see cref="TrieStateDb"/> for storage-aware roots.</para>
    /// </remarks>
    public Hash256 ComputeStateRoot()
    {
        if (_accounts.Count == 0)
            return Hash256.Zero;

        // Naive state root: sort accounts by address, hash all states together
        using var hasher = Blake3Hasher.CreateIncremental();
        var addrBytes = new byte[Address.Size];
        var buffer = new byte[8 + 32 + 32 + 32 + 1 + 32]; // nonce + balance + storageRoot + codeHash + type + compliance
        foreach (var (address, state) in _accounts.OrderBy(kv => kv.Key))
        {
            address.WriteTo(addrBytes);
            hasher.Update(addrBytes);

            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(), state.Nonce);
            state.Balance.WriteTo(buffer.AsSpan(8));
            state.StorageRoot.WriteTo(buffer.AsSpan(40));
            state.CodeHash.WriteTo(buffer.AsSpan(72));
            buffer[104] = (byte)state.AccountType;
            state.ComplianceHash.WriteTo(buffer.AsSpan(105));
            hasher.Update(buffer);
        }

        return hasher.Finalize();
    }

    /// <summary>
    /// Create a snapshot fork of this state database.
    /// Storage byte[] values are deep-copied to prevent cross-fork mutation.
    /// </summary>
    public IStateDatabase Fork()
    {
        var fork = new InMemoryStateDb();
        foreach (var (addr, state) in _accounts)
            fork._accounts[addr] = state;
        foreach (var (key, value) in _storage)
            fork._storage[key] = (byte[])value.Clone();
        return fork;
    }

    public IEnumerable<(Address Address, AccountState State)> GetAllAccounts()
    {
        return _accounts.Select(kv => (kv.Key, kv.Value));
    }

    public byte[]? GetStorage(Address contract, Hash256 key)
    {
        return _storage.TryGetValue((contract, key), out var value) ? value : null;
    }

    public void SetStorage(Address contract, Hash256 key, byte[] value)
    {
        _storage[(contract, key)] = value;
        _dirtyStorageKeys.Add((contract, key));
    }

    public void DeleteStorage(Address contract, Hash256 key)
    {
        _storage.Remove((contract, key));
        _dirtyStorageKeys.Add((contract, key));
    }

    public IReadOnlyCollection<(Address Contract, Hash256 Key)> GetModifiedStorageKeys() => _dirtyStorageKeys;
}
