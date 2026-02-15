namespace Basalt.Sdk.Contracts;

/// <summary>
/// Static storage backend used by StorageValue/StorageMap/StorageList.
/// In production, this is wired to host interface calls via native function pointers.
/// In testing, BasaltTestHost provides an in-memory implementation.
/// </summary>
public static class ContractStorage
{
    private static Dictionary<string, object> _store = new();

    public static void Set(string key, object? value) => _store[key] = value!;

    public static T Get<T>(string key)
    {
        if (_store.TryGetValue(key, out var value))
            return (T)value;
        return default!;
    }

    public static bool ContainsKey(string key) => _store.ContainsKey(key);

    public static void Delete(string key) => _store.Remove(key);

    public static void Clear() => _store.Clear();

    /// <summary>
    /// Take a snapshot of all storage for rollback support.
    /// </summary>
    public static Dictionary<string, object> Snapshot()
    {
        var snap = new Dictionary<string, object>(_store.Count);
        foreach (var kvp in _store)
            snap[kvp.Key] = kvp.Value;
        return snap;
    }

    /// <summary>
    /// Restore storage from a snapshot.
    /// </summary>
    public static void Restore(Dictionary<string, object> snapshot)
    {
        _store = new Dictionary<string, object>(snapshot.Count);
        foreach (var kvp in snapshot)
            _store[kvp.Key] = kvp.Value;
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

    public T Get(int index) => ContractStorage.Get<T>(ItemKey(index));

    public void Add(T value)
    {
        var count = Count;
        ContractStorage.Set(ItemKey(count), value);
        ContractStorage.Set(CountKey, count + 1);
    }

    public void Set(int index, T value) => ContractStorage.Set(ItemKey(index), value);
}
