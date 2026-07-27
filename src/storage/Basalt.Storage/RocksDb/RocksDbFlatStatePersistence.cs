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

    public void Flush(IReadOnlyDictionary<Address, AccountState> accounts)
    {
        using var batch = _store.CreateWriteBatch();

        // Everything already there goes, then the accounts are written. That is the contract: what is on
        // disk afterwards is exactly what the cache holds, and no storage at all.
        //
        // Clearing unconditionally rather than from a list the caller supplies is the point. The
        // instance that reads what is on disk is often not the instance that writes, because a sync batch
        // swaps in a fresh state, so anything that depended on one remembering what it had seen simply
        // never ran on the nodes that sync.
        foreach (var (key, _) in _store.Iterate(RocksDbStore.CF.State))
        {
            if (key.Length == 0) continue;
            if (key[0] == AccountPrefix || key[0] == StoragePrefix)
                batch.Delete(RocksDbStore.CF.State, key);
        }

        // Allocate fresh key/value arrays per Put call — RocksDbSharp's WriteBatch
        // may store references until Commit(), so reusing buffers risks overwriting
        // earlier entries in the batch.
        foreach (var (address, state) in accounts)
        {
            var accountKey = MakeAccountKey(address);
            var stateBuffer = new byte[137];
            EncodeAccountStateInto(state, stateBuffer);
            batch.Put(RocksDbStore.CF.State, accountKey, stateBuffer);
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

    private static void EncodeAccountStateInto(AccountState state, byte[] buffer)
    {
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt64(state.Nonce);
        writer.WriteUInt256(state.Balance);
        writer.WriteHash256(state.StorageRoot);
        writer.WriteHash256(state.CodeHash);
        writer.WriteByte((byte)state.AccountType);
        writer.WriteHash256(state.ComplianceHash);
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
