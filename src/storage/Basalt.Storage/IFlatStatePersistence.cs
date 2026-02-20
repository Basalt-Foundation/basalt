using Basalt.Core;

namespace Basalt.Storage;

/// <summary>
/// Optional persistence layer for the flat state cache.
/// Implementations can write to RocksDB, files, etc.
/// </summary>
public interface IFlatStatePersistence
{
    /// <summary>
    /// Flush the current cache state to persistent storage, including deletions.
    /// </summary>
    /// <param name="accounts">Account entries to write (upsert).</param>
    /// <param name="storage">Storage entries to write (upsert).</param>
    /// <param name="deletedAccounts">Accounts that were deleted and must be removed from persistence.</param>
    /// <param name="deletedStorage">Storage slots that were deleted and must be removed from persistence.</param>
    void Flush(
        IReadOnlyDictionary<Address, AccountState> accounts,
        IReadOnlyDictionary<(Address, Hash256), byte[]> storage,
        IReadOnlyCollection<Address> deletedAccounts,
        IReadOnlyCollection<(Address, Hash256)> deletedStorage);

    (IEnumerable<(Address, AccountState)> Accounts,
     IEnumerable<((Address, Hash256), byte[])> Storage) Load();
}
