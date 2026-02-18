using Basalt.Core;
using Basalt.Storage.Trie;

namespace Basalt.Storage;

/// <summary>
/// O(1) flat cache over an MPT-backed TrieStateDb.
/// Reads hit the in-memory dictionaries first; writes go to both
/// the cache and the underlying trie so ComputeStateRoot() stays correct.
/// </summary>
public sealed class FlatStateDb : IStateDatabase
{
    private readonly TrieStateDb _trie;
    private readonly Dictionary<Address, AccountState> _accountCache;
    private readonly Dictionary<(Address, Hash256), byte[]> _storageCache;
    private readonly HashSet<Address> _deletedAccounts;
    private readonly HashSet<(Address, Hash256)> _deletedStorage;
    private readonly IFlatStatePersistence? _persistence;

    /// <summary>
    /// Wrap an existing TrieStateDb with an empty flat cache.
    /// </summary>
    public FlatStateDb(TrieStateDb trie, IFlatStatePersistence? persistence = null)
    {
        _trie = trie;
        _accountCache = new Dictionary<Address, AccountState>();
        _storageCache = new Dictionary<(Address, Hash256), byte[]>();
        _deletedAccounts = new HashSet<Address>();
        _deletedStorage = new HashSet<(Address, Hash256)>();
        _persistence = persistence;
    }

    /// <summary>
    /// Internal constructor for Fork() -- copies cache dictionaries.
    /// </summary>
    private FlatStateDb(
        TrieStateDb trie,
        Dictionary<Address, AccountState> accountCache,
        Dictionary<(Address, Hash256), byte[]> storageCache,
        HashSet<Address> deletedAccounts,
        HashSet<(Address, Hash256)> deletedStorage)
    {
        _trie = trie;
        _accountCache = accountCache;
        _storageCache = storageCache;
        _deletedAccounts = deletedAccounts;
        _deletedStorage = deletedStorage;
        _persistence = null; // Forks never persist
    }

    /// <summary>
    /// Expose the inner trie for callers that need direct access
    /// (e.g., proof generation, trie inspection).
    /// </summary>
    public TrieStateDb InnerTrie => _trie;

    public AccountState? GetAccount(Address address)
    {
        if (_deletedAccounts.Contains(address))
            return null;

        if (_accountCache.TryGetValue(address, out var cached))
            return cached;

        // Fall through to trie, cache on hit
        var fromTrie = _trie.GetAccount(address);
        if (fromTrie.HasValue)
            _accountCache[address] = fromTrie.Value;

        return fromTrie;
    }

    public void SetAccount(Address address, AccountState state)
    {
        _accountCache[address] = state;
        _deletedAccounts.Remove(address);
        _trie.SetAccount(address, state);
    }

    public bool AccountExists(Address address)
    {
        if (_deletedAccounts.Contains(address))
            return false;
        if (_accountCache.ContainsKey(address))
            return true;
        return _trie.AccountExists(address);
    }

    public void DeleteAccount(Address address)
    {
        _accountCache.Remove(address);
        _deletedAccounts.Add(address);

        // Remove storage entries for this address from cache
        var keysToRemove = new List<(Address, Hash256)>();
        foreach (var key in _storageCache.Keys)
        {
            if (key.Item1 == address)
                keysToRemove.Add(key);
        }
        foreach (var key in keysToRemove)
        {
            _storageCache.Remove(key);
            _deletedStorage.Add(key);
        }

        _trie.DeleteAccount(address);
    }

    public byte[]? GetStorage(Address contract, Hash256 key)
    {
        var cacheKey = (contract, key);

        if (_deletedStorage.Contains(cacheKey))
            return null;

        if (_storageCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Fall through to trie, cache on hit
        var fromTrie = _trie.GetStorage(contract, key);
        if (fromTrie != null)
            _storageCache[cacheKey] = fromTrie;

        return fromTrie;
    }

    public void SetStorage(Address contract, Hash256 key, byte[] value)
    {
        var cacheKey = (contract, key);
        _storageCache[cacheKey] = value;
        _deletedStorage.Remove(cacheKey);
        _trie.SetStorage(contract, key, value);
    }

    public void DeleteStorage(Address contract, Hash256 key)
    {
        var cacheKey = (contract, key);
        _storageCache.Remove(cacheKey);
        _deletedStorage.Add(cacheKey);
        _trie.DeleteStorage(contract, key);
    }

    public Hash256 ComputeStateRoot()
    {
        return _trie.ComputeStateRoot();
    }

    public IEnumerable<(Address Address, AccountState State)> GetAllAccounts()
    {
        foreach (var (addr, state) in _accountCache)
        {
            if (!_deletedAccounts.Contains(addr))
                yield return (addr, state);
        }
    }

    public IStateDatabase Fork()
    {
        // Fork the inner trie (creates OverlayTrieNodeStore)
        var forkedTrie = (TrieStateDb)_trie.Fork();

        // Shallow-copy the cache dictionaries
        return new FlatStateDb(
            forkedTrie,
            new Dictionary<Address, AccountState>(_accountCache),
            new Dictionary<(Address, Hash256), byte[]>(_storageCache),
            new HashSet<Address>(_deletedAccounts),
            new HashSet<(Address, Hash256)>(_deletedStorage));
    }

    /// <summary>
    /// Generate a Merkle proof for an account.
    /// </summary>
    public MerkleProof? GenerateAccountProof(Address address)
    {
        return _trie.GenerateAccountProof(address);
    }

    /// <summary>
    /// Generate a Merkle proof for a storage slot.
    /// </summary>
    public MerkleProof? GenerateStorageProof(Address contract, Hash256 key)
    {
        return _trie.GenerateStorageProof(contract, key);
    }

    /// <summary>
    /// Flush the current flat cache to persistent storage.
    /// Call on shutdown or periodically after block finalization.
    /// </summary>
    public void FlushToPersistence()
    {
        _persistence?.Flush(_accountCache, _storageCache);
    }

    /// <summary>
    /// Load previously persisted flat state into the cache.
    /// Call on startup for warm restart.
    /// </summary>
    public void LoadFromPersistence()
    {
        if (_persistence == null) return;

        var (accounts, storage) = _persistence.Load();
        foreach (var (addr, state) in accounts)
            _accountCache.TryAdd(addr, state);
        foreach (var (key, value) in storage)
            _storageCache.TryAdd(key, value);
    }
}
