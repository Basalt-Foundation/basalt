using Basalt.Core;

namespace Basalt.Storage;

/// <summary>
/// Optional persistence layer for the flat state cache.
/// Implementations can write to RocksDB, files, etc.
/// </summary>
public interface IFlatStatePersistence
{
    void Flush(
        IReadOnlyDictionary<Address, AccountState> accounts,
        IReadOnlyDictionary<(Address, Hash256), byte[]> storage);

    (IEnumerable<(Address, AccountState)> Accounts,
     IEnumerable<((Address, Hash256), byte[])> Storage) Load();
}
