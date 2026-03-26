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
    /// <summary>Per-address index of storage slots for O(1) deletion instead of O(n) scan.</summary>
    private readonly Dictionary<Address, HashSet<Hash256>> _storageSlotsIndex;
    private readonly HashSet<Address> _deletedAccounts;
    private readonly HashSet<(Address, Hash256)> _deletedStorage;
    private readonly HashSet<(Address, Hash256)> _dirtyStorageKeys;
    private readonly HashSet<Address> _dirtyAccounts;
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
        _storageSlotsIndex = new Dictionary<Address, HashSet<Hash256>>();
        _deletedAccounts = new HashSet<Address>();
        _deletedStorage = new HashSet<(Address, Hash256)>();
        _dirtyStorageKeys = new HashSet<(Address, Hash256)>();
        _dirtyAccounts = new HashSet<Address>();
        _persistence = persistence;
        _logger = logger;
    }

    /// <summary>
    /// Internal constructor for Fork() — zero-copy.
    /// Caches start empty; reads fall through to the forked trie (which has all
    /// data via write-through). This makes Fork() O(1) regardless of cache size,
    /// eliminating the O(n) dictionary copy that saturated CPU when the storage
    /// cache grew large (400K+ TWAP snapshot entries after 24h of operation).
    /// Forked instances never persist (no IFlatStatePersistence).
    /// </summary>
    private FlatStateDb(TrieStateDb trie, Dictionary<Address, AccountState>? accountCache = null)
    {
        _trie = trie;
        _accountCache = accountCache ?? new Dictionary<Address, AccountState>();
        _storageCache = new Dictionary<(Address, Hash256), byte[]>();
        _storageSlotsIndex = new Dictionary<Address, HashSet<Hash256>>();
        _deletedAccounts = new HashSet<Address>();
        _deletedStorage = new HashSet<(Address, Hash256)>();
        _dirtyStorageKeys = new HashSet<(Address, Hash256)>();
        _dirtyAccounts = new HashSet<Address>();
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
        _dirtyAccounts.Add(address);
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
        _dirtyAccounts.Add(address);

        // Remove storage entries for this address.
        // Prefer the O(1) per-address index when available; fall back to O(n) scan
        // for forked instances that start with an empty index to avoid the expensive
        // deep copy during Fork().
        if (_storageSlotsIndex.TryGetValue(address, out var slots))
        {
            foreach (var slot in slots)
            {
                var cacheKey = (address, slot);
                _storageCache.Remove(cacheKey);
                _deletedStorage.Add(cacheKey);
            }
            _storageSlotsIndex.Remove(address);
        }
        else
        {
            // Fallback: scan _storageCache keys (O(n), but DeleteAccount is rare on forks)
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

        // Fall through to trie, cache on hit and update per-address index
        var fromTrie = _trie.GetStorage(contract, key);
        if (fromTrie != null)
        {
            _storageCache[cacheKey] = fromTrie;

            if (!_storageSlotsIndex.TryGetValue(contract, out var slots))
            {
                slots = new HashSet<Hash256>();
                _storageSlotsIndex[contract] = slots;
            }
            slots.Add(key);
        }

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
        _dirtyStorageKeys.Add(cacheKey);

        // Maintain per-address index
        if (!_storageSlotsIndex.TryGetValue(contract, out var slots))
        {
            slots = new HashSet<Hash256>();
            _storageSlotsIndex[contract] = slots;
        }
        slots.Add(key);

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
        _dirtyStorageKeys.Add(cacheKey);

        // Maintain per-address index
        if (_storageSlotsIndex.TryGetValue(contract, out var slots))
        {
            slots.Remove(key);
            if (slots.Count == 0) _storageSlotsIndex.Remove(contract);
        }

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
    /// <para>Storage byte[] values are shared by reference (copy-on-write semantics).
    /// This is safe because SetStorage always replaces the entire reference rather
    /// than mutating byte[] contents in place, so parent and fork cannot interfere.</para>
    /// <para>The forked instance does not have a persistence layer (forks never persist).</para>
    /// <para>Note: in-progress storage tries in the underlying TrieStateDb are not carried
    /// over to the fork — this is an accepted design trade-off (S-10).</para>
    /// </remarks>
    public IStateDatabase Fork()
    {
        // Fork the inner trie (creates OverlayTrieNodeStore).
        // Storage cache is NOT copied — the forked trie has all data via write-through,
        // so storage reads fall through to the trie and get cached on first access.
        // This makes Fork() O(1) instead of O(storage_entries) — critical because
        // per-block TWAP snapshots can accumulate 400K+ entries over 24h.
        //
        // Account cache IS copied — it's tiny (~50 entries for active accounts) and
        // avoids hundreds of RocksDB trie reads during sync block execution.
        return new FlatStateDb(
            (TrieStateDb)_trie.Fork(),
            new Dictionary<Address, AccountState>(_accountCache));
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

    public IReadOnlyCollection<(Address Contract, Hash256 Key)> GetModifiedStorageKeys() => _dirtyStorageKeys;

    public IReadOnlyCollection<Address> GetModifiedAccounts() => _dirtyAccounts;

    /// <summary>
    /// Clear the dirty-tracking sets (<see cref="_dirtyStorageKeys"/> and <see cref="_dirtyAccounts"/>).
    /// Call on the <b>canonical</b> FlatStateDb after each block finalization to prevent
    /// unbounded growth. These sets are only consumed by
    /// <see cref="TransactionExecutor.MergeForkState"/> which operates on <b>forks</b>,
    /// not the canonical instance — so clearing them on the canonical DB is always safe.
    /// </summary>
    public void ClearDirtyTracking()
    {
        _dirtyStorageKeys.Clear();
        _dirtyAccounts.Clear();
        // Also clear the underlying TrieStateDb's tracking sets, which have their own
        // _dirtyStorageKeys/_dirtyAccounts that grow independently.
        _trie.ClearDirtyTracking();
    }

    /// <summary>
    /// Compact the deletion guard sets (<see cref="_deletedStorage"/> and <see cref="_deletedAccounts"/>).
    /// Safe to call because <see cref="DeleteStorage"/> and <see cref="DeleteAccount"/> also
    /// call the underlying <see cref="TrieStateDb"/> Delete, which removes the key from the
    /// trie structure. After that, trie reads for deleted keys already return <c>null</c>,
    /// making the flat-cache guard redundant. Without compaction these sets grow unboundedly
    /// (one entry per TWAP prune per block) and contribute to memory pressure that triggers
    /// aggressive Gen 2 GC → 100% CPU.
    /// </summary>
    public void CompactDeletedSets()
    {
        _deletedStorage.Clear();
        _deletedAccounts.Clear();
    }

    /// <summary>
    /// Flush the current flat cache to persistent storage, including deletions.
    /// Call on shutdown or periodically after block finalization.
    /// </summary>
    public void FlushToPersistence()
    {
        if (_persistence == null) return;

        _persistence.Flush(_accountCache, _storageCache, _deletedAccounts, _deletedStorage);

        // After flush, compact deletion sets — the persisted store has the deletions
        // applied, and the trie's Delete() also removed the keys from the trie structure.
        // Keeping stale tombstones causes unbounded memory growth on long-running nodes.
        CompactDeletedSets();
        ClearDirtyTracking();
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
        {
            if (_storageCache.TryAdd(key, value))
            {
                // Maintain per-address index for DeleteAccount support
                var (contract, slot) = key;
                if (!_storageSlotsIndex.TryGetValue(contract, out var slots))
                {
                    slots = new HashSet<Hash256>();
                    _storageSlotsIndex[contract] = slots;
                }
                slots.Add(slot);
            }
        }
    }

    /// <summary>
    /// Hard cap for storage cache entries. When exceeded, the cache is cleared to prevent
    /// unbounded memory growth on long-running nodes. Reads will repopulate from the trie
    /// on demand. The threshold is set high enough to avoid impacting normal block processing.
    /// </summary>
    private const int MaxStorageCacheEntries = 500_000;

    /// <summary>
    /// Check cache size and evict if the storage cache exceeds the hard cap.
    /// Without eviction, _storageCache grows monotonically (entries are added on every
    /// read miss and write but only removed on delete) causing unbounded RAM growth.
    /// </summary>
    private void CheckCacheSize()
    {
        int storageCount = _storageCache.Count;

        if (storageCount > MaxStorageCacheEntries)
        {
            _storageCache.Clear();
            _storageSlotsIndex.Clear();
            _logger?.LogInformation(
                "FlatStateDb storage cache evicted ({Count} entries exceeded {Max} cap). " +
                "Reads will repopulate from trie on demand.",
                storageCount, MaxStorageCacheEntries);
        }
        else if (_logger != null && storageCount > CacheSizeWarningThreshold)
        {
            _logger.LogWarning(
                "FlatStateDb storage cache size ({Count}) approaching eviction threshold ({Max})",
                storageCount, MaxStorageCacheEntries);
        }
    }
}
