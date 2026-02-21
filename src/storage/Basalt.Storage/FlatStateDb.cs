using Basalt.Core;
using Basalt.Storage.Trie;
using Microsoft.Extensions.Logging;

namespace Basalt.Storage;

/// <summary>
/// O(1) flat cache over an MPT-backed TrieStateDb.
/// Reads hit the in-memory dictionaries first; writes go to both
/// the cache and the underlying trie so ComputeStateRoot() stays correct.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> This class is <b>not</b> thread-safe.
/// It is designed for single-threaded execution within a block processing pipeline.
/// API handlers or concurrent readers should call <see cref="Fork"/> to obtain an
/// isolated snapshot before reading state.</para>
/// </remarks>
public sealed class FlatStateDb : IStateDatabase
{
    private readonly TrieStateDb _trie;
    private readonly Dictionary<Address, AccountState> _accountCache;
    private readonly Dictionary<(Address, Hash256), byte[]> _storageCache;
    private readonly HashSet<Address> _deletedAccounts;
    private readonly HashSet<(Address, Hash256)> _deletedStorage;
    private readonly IFlatStatePersistence? _persistence;
    private readonly ILogger? _logger;

    /// <summary>
    /// Warning threshold for cache size. When either cache exceeds this number
    /// of entries a warning is logged to alert operators about potential memory pressure.
    /// </summary>
    private const int CacheSizeWarningThreshold = 1_000_000;

    /// <summary>
    /// Wrap an existing TrieStateDb with an empty flat cache.
    /// </summary>
    /// <param name="trie">The underlying Merkle Patricia Trie state database.</param>
    /// <param name="persistence">Optional persistence layer for flush/load (e.g., RocksDB).</param>
    /// <param name="logger">Optional logger for cache size warnings and diagnostics.</param>
    public FlatStateDb(TrieStateDb trie, IFlatStatePersistence? persistence = null, ILogger? logger = null)
    {
        _trie = trie;
        _accountCache = new Dictionary<Address, AccountState>();
        _storageCache = new Dictionary<(Address, Hash256), byte[]>();
        _deletedAccounts = new HashSet<Address>();
        _deletedStorage = new HashSet<(Address, Hash256)>();
        _persistence = persistence;
        _logger = logger;
    }

    /// <summary>
    /// Internal constructor for Fork() -- copies cache dictionaries.
    /// Forked instances never persist (no IFlatStatePersistence).
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

    /// <summary>
    /// Get the account state for the given address, or <c>null</c> if the account does not exist.
    /// Results from the trie are cached on first read.
    /// </summary>
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

    /// <summary>
    /// Set (or overwrite) the account state at the given address.
    /// Writes go to both the flat cache and the underlying trie.
    /// </summary>
    public void SetAccount(Address address, AccountState state)
    {
        _accountCache[address] = state;
        _deletedAccounts.Remove(address);
        _trie.SetAccount(address, state);
        CheckCacheSize();
    }

    /// <summary>
    /// Returns <c>true</c> if an account exists at the given address (checked in cache first, then trie).
    /// </summary>
    public bool AccountExists(Address address)
    {
        if (_deletedAccounts.Contains(address))
            return false;
        if (_accountCache.ContainsKey(address))
            return true;
        return _trie.AccountExists(address);
    }

    /// <summary>
    /// Delete the account at the given address and all its storage entries from the cache.
    /// </summary>
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

    /// <summary>
    /// Get a storage value for a contract slot, or <c>null</c> if not set.
    /// Results from the trie are cached on first read.
    /// </summary>
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

    /// <summary>
    /// Set a storage value for a contract slot.
    /// Writes go to both the flat cache and the underlying trie.
    /// </summary>
    public void SetStorage(Address contract, Hash256 key, byte[] value)
    {
        var cacheKey = (contract, key);
        _storageCache[cacheKey] = value;
        _deletedStorage.Remove(cacheKey);
        _trie.SetStorage(contract, key, value);
        CheckCacheSize();
    }

    /// <summary>
    /// Delete a storage slot for a contract.
    /// </summary>
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

    /// <summary>
    /// Returns all accounts currently in the flat cache, excluding deleted accounts.
    /// </summary>
    /// <remarks>
    /// <b>Important:</b> This method only returns accounts that have been loaded into
    /// the in-memory cache (via <see cref="SetAccount"/> or a prior <see cref="GetAccount"/>
    /// that hit the trie). It does <b>not</b> enumerate the full set of accounts in the
    /// underlying <see cref="TrieStateDb"/>. For a complete account enumeration, iterate
    /// the trie directly.
    /// </remarks>
    public IEnumerable<(Address Address, AccountState State)> GetAllAccounts()
    {
        foreach (var (addr, state) in _accountCache)
        {
            if (!_deletedAccounts.Contains(addr))
                yield return (addr, state);
        }
    }

    /// <summary>
    /// Create a snapshot fork of this state database.
    /// The forked instance has independent cache dictionaries and a forked trie overlay,
    /// so writes to the fork do not affect the parent.
    /// </summary>
    /// <remarks>
    /// <para>Storage byte[] values are deep-copied to prevent cross-fork mutation.</para>
    /// <para>The forked instance does not have a persistence layer (forks never persist).</para>
    /// <para>Note: in-progress storage tries in the underlying TrieStateDb are not carried
    /// over to the fork — this is an accepted design trade-off (S-10).</para>
    /// </remarks>
    public IStateDatabase Fork()
    {
        // Fork the inner trie (creates OverlayTrieNodeStore)
        var forkedTrie = (TrieStateDb)_trie.Fork();

        // Deep-copy the storage cache to prevent cross-fork byte[] mutation
        var storageClone = new Dictionary<(Address, Hash256), byte[]>(_storageCache.Count);
        foreach (var (key, value) in _storageCache)
            storageClone[key] = (byte[])value.Clone();

        return new FlatStateDb(
            forkedTrie,
            new Dictionary<Address, AccountState>(_accountCache),
            storageClone,
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
    /// Flush the current flat cache to persistent storage, including deletions.
    /// Call on shutdown or periodically after block finalization.
    /// </summary>
    /// <remarks>
    /// <para><b>M-06 fix:</b> Deletion sets are <b>not</b> cleared after flush.
    /// Although the persisted store has the deletions applied, the underlying
    /// <see cref="TrieStateDb"/> still contains the old data (trie nodes are never pruned).
    /// Clearing the deletion sets would cause subsequent reads to fall through to the trie
    /// and return stale data for entries that were deleted.</para>
    /// </remarks>
    public void FlushToPersistence()
    {
        if (_persistence == null) return;

        _persistence.Flush(_accountCache, _storageCache, _deletedAccounts, _deletedStorage);

        // M-06: Do NOT clear _deletedAccounts / _deletedStorage here.
        // The trie still contains the old nodes, so the deletion guard must remain
        // active to prevent fallthrough reads returning stale data.
    }

    /// <summary>
    /// Load previously persisted flat state into the cache.
    /// Call on startup for warm restart.
    /// </summary>
    /// <remarks>
    /// <para><b>L-07:</b> Uses <c>TryAdd</c> (first-writer-wins semantics). If a key already
    /// exists in the cache from runtime operations, the persisted value is silently ignored.
    /// This is correct for warm restart where runtime state is fresher, but this method
    /// must be called <b>before</b> any runtime modifications to avoid stale reads.</para>
    /// </remarks>
    public void LoadFromPersistence()
    {
        if (_persistence == null) return;

        var (accounts, storage) = _persistence.Load();
        foreach (var (addr, state) in accounts)
            _accountCache.TryAdd(addr, state);
        foreach (var (key, value) in storage)
            _storageCache.TryAdd(key, value);
    }

    /// <summary>
    /// Log a warning if either cache exceeds the size threshold.
    /// This is a monitoring aid — no eviction is performed.
    /// </summary>
    private void CheckCacheSize()
    {
        if (_logger == null) return;

        int accountCount = _accountCache.Count;
        int storageCount = _storageCache.Count;

        if (accountCount > CacheSizeWarningThreshold)
        {
            _logger.LogWarning(
                "FlatStateDb account cache size ({Count}) exceeds warning threshold ({Threshold}). " +
                "Consider implementing cache eviction or increasing available memory.",
                accountCount, CacheSizeWarningThreshold);
        }

        if (storageCount > CacheSizeWarningThreshold)
        {
            _logger.LogWarning(
                "FlatStateDb storage cache size ({Count}) exceeds warning threshold ({Threshold}). " +
                "Consider implementing cache eviction or increasing available memory.",
                storageCount, CacheSizeWarningThreshold);
        }
    }
}
