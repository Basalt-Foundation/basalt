using Basalt.Codec;
using Basalt.Core;

namespace Basalt.Storage.RocksDb;

/// <summary>
/// Persists the flat state cache to the RocksDB "state" column family.
/// Key format for accounts: [0x01][20-byte address]
/// Key format for storage:  [0x02][20-byte address][32-byte slot hash]
/// </summary>
public sealed class RocksDbFlatStatePersistence : IFlatStatePersistence
{
    private readonly RocksDbStore _store;
    private const byte AccountPrefix = 0x01;
    private const byte StoragePrefix = 0x02;

    public RocksDbFlatStatePersistence(RocksDbStore store)
    {
        _store = store;
    }

    public void Flush(
        IReadOnlyDictionary<Address, AccountState> accounts,
        IReadOnlyDictionary<(Address, Hash256), byte[]> storage,
        IReadOnlyCollection<Address> deletedAccounts,
        IReadOnlyCollection<(Address, Hash256)> deletedStorage)
    {
        using var batch = _store.CreateWriteBatch();

        // Write (upsert) live account and storage entries
        foreach (var (address, state) in accounts)
        {
            var key = MakeAccountKey(address);
            var value = EncodeAccountState(state);
            batch.Put(RocksDbStore.CF.State, key, value);
        }

        foreach (var ((contract, slot), value) in storage)
        {
            var key = MakeStorageKey(contract, slot);
            batch.Put(RocksDbStore.CF.State, key, value);
        }

        // Delete entries that were removed from state
        foreach (var address in deletedAccounts)
        {
            var key = MakeAccountKey(address);
            batch.Delete(RocksDbStore.CF.State, key);
        }

        foreach (var (contract, slot) in deletedStorage)
        {
            var key = MakeStorageKey(contract, slot);
            batch.Delete(RocksDbStore.CF.State, key);
        }

        batch.Commit();
    }

    public (IEnumerable<(Address, AccountState)> Accounts,
            IEnumerable<((Address, Hash256), byte[])> Storage) Load()
    {
        var accounts = new List<(Address, AccountState)>();
        var storage = new List<((Address, Hash256), byte[])>();

        foreach (var (key, value) in _store.Iterate(RocksDbStore.CF.State))
        {
            if (key.Length == 0) continue;

            if (key[0] == AccountPrefix && key.Length == 1 + Address.Size)
            {
                var addr = new Address(key.AsSpan(1, Address.Size));
                var state = DecodeAccountState(value);
                accounts.Add((addr, state));
            }
            else if (key[0] == StoragePrefix && key.Length == 1 + Address.Size + Hash256.Size)
            {
                var addr = new Address(key.AsSpan(1, Address.Size));
                var slot = new Hash256(key.AsSpan(1 + Address.Size, Hash256.Size));
                storage.Add(((addr, slot), value));
            }
        }

        return (accounts, storage);
    }

    private static byte[] MakeAccountKey(Address address)
    {
        var key = new byte[1 + Address.Size];
        key[0] = AccountPrefix;
        address.WriteTo(key.AsSpan(1));
        return key;
    }

    private static byte[] MakeStorageKey(Address contract, Hash256 slot)
    {
        var key = new byte[1 + Address.Size + Hash256.Size];
        key[0] = StoragePrefix;
        contract.WriteTo(key.AsSpan(1));
        slot.WriteTo(key.AsSpan(1 + Address.Size));
        return key;
    }

    private static byte[] EncodeAccountState(AccountState state)
    {
        // nonce(8) + balance(32) + storageRoot(32) + codeHash(32) + accountType(1) + complianceHash(32) = 137
        var buffer = new byte[137];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt64(state.Nonce);
        writer.WriteUInt256(state.Balance);
        writer.WriteHash256(state.StorageRoot);
        writer.WriteHash256(state.CodeHash);
        writer.WriteByte((byte)state.AccountType);
        writer.WriteHash256(state.ComplianceHash);
        return buffer;
    }

    private static AccountState DecodeAccountState(byte[] data)
    {
        var reader = new BasaltReader(data);
        return new AccountState
        {
            Nonce = reader.ReadUInt64(),
            Balance = reader.ReadUInt256(),
            StorageRoot = reader.ReadHash256(),
            CodeHash = reader.ReadHash256(),
            AccountType = (AccountType)reader.ReadByte(),
            ComplianceHash = reader.ReadHash256(),
        };
    }
}
