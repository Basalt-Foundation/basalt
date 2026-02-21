namespace Basalt.Sdk.Contracts;

/// <summary>
/// Abstraction over contract storage backends.
/// The default is an in-memory dictionary (tests / BasaltTestHost).
/// In production, the runtime wires a HostStorageProvider that bridges to on-chain state.
/// </summary>
public interface IStorageProvider
{
    void Set(string key, object? value);
    T Get<T>(string key);
    bool ContainsKey(string key);
    void Delete(string key);
}

/// <summary>
/// Default in-memory storage provider used by tests and BasaltTestHost.
/// </summary>
public sealed class InMemoryStorageProvider : IStorageProvider
{
    private Dictionary<string, object> _store = new();

    public void Set(string key, object? value) => _store[key] = value!;

    public T Get<T>(string key)
    {
        if (_store.TryGetValue(key, out var value))
            return (T)value;
        return default!;
    }

    public bool ContainsKey(string key) => _store.ContainsKey(key);

    public void Delete(string key) => _store.Remove(key);

    public void Clear() => _store.Clear();

    public Dictionary<string, object> Snapshot()
    {
        var snap = new Dictionary<string, object>(_store.Count);
        foreach (var kvp in _store)
            snap[kvp.Key] = kvp.Value;
        return snap;
    }

    public void Restore(Dictionary<string, object> snapshot)
    {
        _store = new Dictionary<string, object>(snapshot.Count);
        foreach (var kvp in snapshot)
            _store[kvp.Key] = kvp.Value;
    }
}

/// <summary>
/// Static storage backend used by StorageValue/StorageMap/StorageList.
/// Delegates to an IStorageProvider. Default: InMemoryStorageProvider.
/// Call SetProvider() to wire production (on-chain) or custom storage.
/// </summary>
/// <remarks>
/// <para><b>Thread safety (C-2):</b> The _provider field is a single shared mutable slot.
/// The execution layer serializes all contract execution under a Monitor lock
/// (TransactionExecutor), and ContractBridge.Setup() swaps the provider per execution
/// scope. If parallel execution is introduced, this must be migrated to AsyncLocal
/// or a scoped pattern.</para>
/// </remarks>
public static class ContractStorage
{
    private static readonly InMemoryStorageProvider DefaultProvider = new();
    private static IStorageProvider _provider = DefaultProvider;

    /// <summary>
    /// Replace the storage backend. Returns the previous provider.
    /// </summary>
    public static IStorageProvider SetProvider(IStorageProvider provider)
    {
        var prev = _provider;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        return prev;
    }

    /// <summary>
    /// Reset to the default in-memory provider.
    /// </summary>
    public static void ResetProvider()
    {
        _provider = DefaultProvider;
    }

    /// <summary>
    /// Current storage provider (for test assertions).
    /// </summary>
    public static IStorageProvider Provider => _provider;

    public static void Set(string key, object? value) => _provider.Set(key, value);

    public static T Get<T>(string key) => _provider.Get<T>(key);

    public static bool ContainsKey(string key) => _provider.ContainsKey(key);

    public static void Delete(string key) => _provider.Delete(key);

    public static void Clear()
    {
        if (_provider is InMemoryStorageProvider mem)
            mem.Clear();
        else
            ResetProvider();
    }

    /// <summary>
    /// Take a snapshot of all storage for rollback support.
    /// Only works with InMemoryStorageProvider.
    /// </summary>
    public static Dictionary<string, object> Snapshot()
    {
        if (_provider is InMemoryStorageProvider mem)
            return mem.Snapshot();
        return new Dictionary<string, object>();
    }

    /// <summary>
    /// Restore storage from a snapshot.
    /// Only works with InMemoryStorageProvider.
    /// </summary>
    public static void Restore(Dictionary<string, object> snapshot)
    {
        if (_provider is InMemoryStorageProvider mem)
            mem.Restore(snapshot);
    }
}

/// <summary>
/// Storage value for a single value in contract storage.
/// </summary>
public sealed class StorageValue<T> where T : struct
{
    private readonly string _key;
    public StorageValue(string key) => _key = key;

    public T Get() => ContractStorage.Get<T>(_key);
    public void Set(T value) => ContractStorage.Set(_key, value);
}

/// <summary>
/// Storage map for key-value storage in contracts.
/// </summary>
public sealed class StorageMap<TKey, TValue>
    where TKey : notnull
{
    private readonly string _prefix;
    public StorageMap(string prefix) => _prefix = prefix;

    private string FullKey(TKey key) => $"{_prefix}:{key}";

    public TValue Get(TKey key) => ContractStorage.Get<TValue>(FullKey(key));
    public void Set(TKey key, TValue value) => ContractStorage.Set(FullKey(key), value);
    public bool ContainsKey(TKey key) => ContractStorage.ContainsKey(FullKey(key));
    public void Delete(TKey key) => ContractStorage.Delete(FullKey(key));
}

/// <summary>
/// Storage list for ordered collection storage in contracts.
/// </summary>
public sealed class StorageList<T> where T : struct
{
    private readonly string _prefix;
    public StorageList(string prefix) => _prefix = prefix;

    private string CountKey => $"{_prefix}:__count";
    private string ItemKey(int index) => $"{_prefix}:{index}";

    public int Count => ContractStorage.Get<int>(CountKey);

    // M-2: Bounds-checked access
    public T Get(int index)
    {
        if (index < 0 || index >= Count)
            throw new ContractRevertException("StorageList: index out of bounds");
        return ContractStorage.Get<T>(ItemKey(index));
    }

    public void Add(T value)
    {
        var count = Count;
        ContractStorage.Set(ItemKey(count), value);
        ContractStorage.Set(CountKey, count + 1);
    }

    // M-2: Bounds-checked set
    public void Set(int index, T value)
    {
        if (index < 0 || index >= Count)
            throw new ContractRevertException("StorageList: index out of bounds");
        ContractStorage.Set(ItemKey(index), value);
    }
}
