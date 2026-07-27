using Basalt.Core;

namespace Basalt.Storage;

/// <summary>
/// Optional persistence layer for the flat state cache.
/// Implementations can write to RocksDB, files, etc.
/// </summary>
public interface IFlatStatePersistence
{
    /// <summary>
    /// Make persistent storage hold exactly <paramref name="accounts"/> and nothing else.
    ///
    /// Replacing rather than merging is the whole contract. An upsert leaves behind every record the
    /// cache no longer holds, at whatever version it had when it was last written, so what comes back
    /// from <see cref="Load"/> is a union of records from different moments rather than a snapshot of
    /// one. That is what halted the testnet: a contract account the trie had moved past was reloaded and
    /// read in preference to the trie, and the node computed a state root nobody else could reproduce.
    ///
    /// A cache that drops entries makes this unavoidable. Folding a storage root into an account drops
    /// the cached copy so the next read reloads from the trie, and finishing a sync batch installs a
    /// fresh cache that holds nothing at all. Neither can name what it is no longer responsible for, so
    /// only a replacing write keeps the persisted copy honest.
    /// </summary>
    /// <param name="accounts">The complete set of account entries to persist.</param>
    /// <param name="storage">Storage entries to write. Callers pass none: storage is read from the trie.</param>
    /// <param name="deletedStorage">Storage slots to remove, including any an older build persisted.</param>
    void Flush(
        IReadOnlyDictionary<Address, AccountState> accounts,
        IReadOnlyDictionary<(Address, Hash256), byte[]> storage,
        IReadOnlyCollection<(Address, Hash256)> deletedStorage);

    (IEnumerable<(Address, AccountState)> Accounts,
     IEnumerable<((Address, Hash256), byte[])> Storage) Load();
}
