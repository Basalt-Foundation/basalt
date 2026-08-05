using Basalt.Core;

namespace Basalt.Storage;

/// <summary>
/// Optional persistence layer for the flat account cache.
/// Implementations can write to RocksDB, files, etc.
/// </summary>
public interface IFlatStatePersistence
{
    /// <summary>
    /// Make persistent storage hold exactly <paramref name="accounts"/> and nothing else.
    ///
    /// Replacing rather than merging is the whole contract, and it is why this takes no deletion lists.
    /// A caller cannot supply them: the cache drops entries by design, and something that has dropped an
    /// entry cannot name what it is no longer responsible for. Folding a storage root into an account
    /// drops the cached copy so the next read reloads from the trie, and finishing a sync batch installs
    /// a fresh cache holding nothing at all, on a different instance from the one that loaded. An upsert
    /// therefore left every superseded record on disk at whatever version it had when it was last
    /// written, and a reload returned a union of records from different moments rather than a snapshot
    /// of one. That halted the testnet: a contract account the trie had moved past came back, was read
    /// in preference to the trie, and the node computed a state root nobody else could reproduce.
    ///
    /// Contract storage is not persisted at all and is removed wholesale by this call. Reloading it was
    /// worse than useless: a deletion could not survive to the flush that would have applied it, because
    /// the tracking sets are cleared every block to bound their growth, so a persisted slot outlived the
    /// chain's decision to remove it. What it bought was a warm read cache that the trie repopulates on
    /// demand anyway.
    /// </summary>
    /// <param name="accounts">The complete set of account entries to persist.</param>
    void Flush(IReadOnlyDictionary<Address, AccountState> accounts);

    /// <summary>
    /// Read what is persisted. The storage half is only ever counted, never trusted: see
    /// <see cref="Flush"/>.
    /// </summary>
    (IEnumerable<(Address, AccountState)> Accounts,
     IEnumerable<((Address, Hash256), byte[])> Storage) Load();
}
