using Basalt.Core;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

public class FlatStateDbTests
{
    private static (FlatStateDb Flat, TrieStateDb Trie) CreateFlatStateDb()
    {
        var store = new InMemoryTrieNodeStore();
        var trie = new TrieStateDb(store);
        var flat = new FlatStateDb(trie);
        return (flat, trie);
    }

    // -----------------------------------------------------------------------
    // Basic account CRUD
    // -----------------------------------------------------------------------

    [Fact]
    public void GetAccount_NonExistent_ReturnsNull()
    {
        var (db, _) = CreateFlatStateDb();
        db.GetAccount(CreateTestAddress(1)).Should().BeNull();
    }

    [Fact]
    public void SetAndGet_Account_Roundtrips()
    {
        var (db, _) = CreateFlatStateDb();
        var address = CreateTestAddress(1);
        var state = MakeAccount(nonce: 5, balance: 1000);

        db.SetAccount(address, state);
        var retrieved = db.GetAccount(address);

        retrieved.Should().NotBeNull();
        retrieved!.Value.Nonce.Should().Be(5);
        retrieved.Value.Balance.Should().Be((UInt256)1000);
    }

    [Fact]
    public void AccountExists_BeforeSet_ReturnsFalse()
    {
        var (db, _) = CreateFlatStateDb();
        db.AccountExists(CreateTestAddress(1)).Should().BeFalse();
    }

    [Fact]
    public void AccountExists_AfterSet_ReturnsTrue()
    {
        var (db, _) = CreateFlatStateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, AccountState.Empty);
        db.AccountExists(address).Should().BeTrue();
    }

    [Fact]
    public void DeleteAccount_RemovesIt()
    {
        var (db, _) = CreateFlatStateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, MakeAccount());

        db.DeleteAccount(address);
        db.AccountExists(address).Should().BeFalse();
        db.GetAccount(address).Should().BeNull();
    }

    [Fact]
    public void SetAccount_OverwriteExisting_UpdatesState()
    {
        var (db, _) = CreateFlatStateDb();
        var address = CreateTestAddress(1);

        db.SetAccount(address, MakeAccount(nonce: 1, balance: 100));
        db.SetAccount(address, MakeAccount(nonce: 2, balance: 200));

        var retrieved = db.GetAccount(address);
        retrieved!.Value.Nonce.Should().Be(2);
        retrieved.Value.Balance.Should().Be((UInt256)200);
    }

    [Theory]
    [InlineData(AccountType.ExternallyOwned)]
    [InlineData(AccountType.Contract)]
    [InlineData(AccountType.SystemContract)]
    [InlineData(AccountType.Validator)]
    public void AccountState_AllAccountTypes_Roundtrip(AccountType type)
    {
        var (db, _) = CreateFlatStateDb();
        var address = CreateTestAddress((int)type + 10);

        db.SetAccount(address, MakeAccount(type: type));
        var retrieved = db.GetAccount(address);

        retrieved.Should().NotBeNull();
        retrieved!.Value.AccountType.Should().Be(type);
    }

    // -----------------------------------------------------------------------
    // Storage CRUD
    // -----------------------------------------------------------------------

    [Fact]
    public void GetStorage_NonExistent_ReturnsNull()
    {
        var (db, _) = CreateFlatStateDb();
        var key = new Hash256(CreateTestBytes(32, 0xAA));
        db.GetStorage(CreateTestAddress(1), key).Should().BeNull();
    }

    [Fact]
    public void SetAndGetStorage_Roundtrips()
    {
        var (db, _) = CreateFlatStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var storageKey = new Hash256(CreateTestBytes(32, 0xAA));
        var value = new byte[] { 0x01, 0x02, 0x03 };

        db.SetStorage(contract, storageKey, value);
        db.GetStorage(contract, storageKey).Should().Equal(value);
    }

    [Fact]
    public void DeleteStorage_RemovesValue()
    {
        var (db, _) = CreateFlatStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var key = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(contract, key, [0x01]);
        db.DeleteStorage(contract, key);

        db.GetStorage(contract, key).Should().BeNull();
    }

    [Fact]
    public void Storage_MultipleKeysPerContract()
    {
        var (db, _) = CreateFlatStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        for (int i = 0; i < 20; i++)
        {
            var key = new Hash256(CreateTestBytes(32, (byte)(i + 1)));
            db.SetStorage(contract, key, [(byte)i, (byte)(i * 2)]);
        }

        for (int i = 0; i < 20; i++)
        {
            var key = new Hash256(CreateTestBytes(32, (byte)(i + 1)));
            db.GetStorage(contract, key).Should().Equal([(byte)i, (byte)(i * 2)]);
        }
    }

    [Fact]
    public void Storage_DifferentContracts_Isolated()
    {
        var (db, _) = CreateFlatStateDb();
        var c1 = CreateTestAddress(1);
        var c2 = CreateTestAddress(2);
        db.SetAccount(c1, MakeAccount(type: AccountType.Contract));
        db.SetAccount(c2, MakeAccount(type: AccountType.Contract));

        var key = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(c1, key, [0x01]);
        db.SetStorage(c2, key, [0x02]);

        db.GetStorage(c1, key).Should().Equal([0x01]);
        db.GetStorage(c2, key).Should().Equal([0x02]);
    }

    // -----------------------------------------------------------------------
    // State root correctness (critical -- must match bare TrieStateDb)
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeStateRoot_EmptyDb_ReturnsZero()
    {
        var (db, _) = CreateFlatStateDb();
        db.ComputeStateRoot().Should().Be(Hash256.Zero);
    }

    [Fact]
    public void ComputeStateRoot_MatchesTrieStateDb()
    {
        // Same operations on bare TrieStateDb and FlatStateDb must produce identical roots
        var store1 = new InMemoryTrieNodeStore();
        var bareTrie = new TrieStateDb(store1);

        var store2 = new InMemoryTrieNodeStore();
        var wrappedTrie = new TrieStateDb(store2);
        var flatDb = new FlatStateDb(wrappedTrie);

        for (int i = 0; i < 10; i++)
        {
            var addr = CreateTestAddress(i);
            var state = MakeAccount(nonce: (ulong)i, balance: (ulong)(i * 100));
            bareTrie.SetAccount(addr, state);
            flatDb.SetAccount(addr, state);
        }

        // Add some storage
        var contract = CreateTestAddress(0);
        bareTrie.SetAccount(contract, MakeAccount(type: AccountType.Contract));
        flatDb.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var storageKey = new Hash256(CreateTestBytes(32, 0xAA));
        bareTrie.SetStorage(contract, storageKey, [0x01, 0x02]);
        flatDb.SetStorage(contract, storageKey, [0x01, 0x02]);

        bareTrie.ComputeStateRoot().Should().Be(flatDb.ComputeStateRoot());
    }

    [Fact]
    public void ComputeStateRoot_ChangesWhenAccountModified()
    {
        var (db, _) = CreateFlatStateDb();
        var root0 = db.ComputeStateRoot();

        db.SetAccount(CreateTestAddress(1), MakeAccount(balance: 100));
        var root1 = db.ComputeStateRoot();

        db.SetAccount(CreateTestAddress(2), MakeAccount(balance: 200));
        var root2 = db.ComputeStateRoot();

        root0.Should().NotBe(root1);
        root1.Should().NotBe(root2);
    }

    [Fact]
    public void ComputeStateRoot_ReflectsStorageChanges()
    {
        var (db, _) = CreateFlatStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));
        var rootBefore = db.ComputeStateRoot();

        db.SetStorage(contract, new Hash256(CreateTestBytes(32, 0xAA)), [0x01]);
        var rootAfter = db.ComputeStateRoot();

        rootBefore.Should().NotBe(rootAfter);
    }

    [Fact]
    public void ComputeStateRoot_DeterministicWithStorage()
    {
        var store1 = new InMemoryTrieNodeStore();
        var trie1 = new TrieStateDb(store1);
        var flat1 = new FlatStateDb(trie1);

        var store2 = new InMemoryTrieNodeStore();
        var trie2 = new TrieStateDb(store2);
        var flat2 = new FlatStateDb(trie2);

        var contract = CreateTestAddress(1);
        var storageKey = new Hash256(CreateTestBytes(32, 0xAA));

        flat1.SetAccount(contract, MakeAccount(type: AccountType.Contract));
        flat1.SetStorage(contract, storageKey, [0x01, 0x02]);

        flat2.SetAccount(contract, MakeAccount(type: AccountType.Contract));
        flat2.SetStorage(contract, storageKey, [0x01, 0x02]);

        flat1.ComputeStateRoot().Should().Be(flat2.ComputeStateRoot());
    }

    // -----------------------------------------------------------------------
    // Cache behavior
    // -----------------------------------------------------------------------

    [Fact]
    public void GetAccount_CachesOnFirstRead()
    {
        var (db, trie) = CreateFlatStateDb();
        var address = CreateTestAddress(1);

        // Write directly to the inner trie (bypassing cache)
        trie.SetAccount(address, MakeAccount(nonce: 42));

        // First read should go to trie and cache the result
        var first = db.GetAccount(address);
        first.Should().NotBeNull();
        first!.Value.Nonce.Should().Be(42);

        // Account should now be in GetAllAccounts (cached)
        db.GetAllAccounts().Should().Contain(x => x.Address == address);
    }

    [Fact]
    public void DeleteAccount_PreventsTrieFallthrough()
    {
        var (db, _) = CreateFlatStateDb();
        var address = CreateTestAddress(1);

        db.SetAccount(address, MakeAccount(nonce: 1));
        db.DeleteAccount(address);

        // Must return null even though trie might still have the account
        // (well, trie also deletes, but the deleted set ensures correctness)
        db.GetAccount(address).Should().BeNull();
        db.AccountExists(address).Should().BeFalse();
    }

    [Fact]
    public void DeleteStorage_PreventsTrieFallthrough()
    {
        var (db, _) = CreateFlatStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var key = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(contract, key, [0x01]);
        db.DeleteStorage(contract, key);

        db.GetStorage(contract, key).Should().BeNull();
    }

    [Fact]
    public void DeleteAccount_ClearsStorageFromCache()
    {
        var (db, _) = CreateFlatStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var key = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(contract, key, [0x01]);

        db.DeleteAccount(contract);

        db.GetStorage(contract, key).Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Fork semantics
    // -----------------------------------------------------------------------

    [Fact]
    public void Fork_IsolatesWritesFromParent()
    {
        var (db, _) = CreateFlatStateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, MakeAccount(nonce: 1, balance: 100));

        var fork = db.Fork();
        fork.SetAccount(address, MakeAccount(nonce: 2, balance: 200));

        // Parent unchanged
        var parentAccount = db.GetAccount(address);
        parentAccount!.Value.Nonce.Should().Be(1);
        parentAccount.Value.Balance.Should().Be((UInt256)100);

        // Fork has new value
        var forkAccount = fork.GetAccount(address);
        forkAccount!.Value.Nonce.Should().Be(2);
        forkAccount.Value.Balance.Should().Be((UInt256)200);
    }

    [Fact]
    public void Fork_PreservesExistingCache()
    {
        var (db, _) = CreateFlatStateDb();
        var addr1 = CreateTestAddress(1);
        var addr2 = CreateTestAddress(2);
        db.SetAccount(addr1, MakeAccount(nonce: 1));
        db.SetAccount(addr2, MakeAccount(nonce: 2));

        var fork = db.Fork();

        // Both accounts visible in fork
        fork.GetAccount(addr1)!.Value.Nonce.Should().Be(1);
        fork.GetAccount(addr2)!.Value.Nonce.Should().Be(2);
    }

    [Fact]
    public void Fork_StateRootMatchesBareTrieFork()
    {
        // Apply identical ops to bare TrieStateDb and FlatStateDb, fork both,
        // apply more ops, verify identical roots
        var store1 = new InMemoryTrieNodeStore();
        var bareTrie = new TrieStateDb(store1);

        var store2 = new InMemoryTrieNodeStore();
        var wrappedTrie = new TrieStateDb(store2);
        var flatDb = new FlatStateDb(wrappedTrie);

        // Pre-fork ops
        for (int i = 0; i < 5; i++)
        {
            var addr = CreateTestAddress(i);
            var state = MakeAccount(nonce: (ulong)i, balance: (ulong)(i * 100));
            bareTrie.SetAccount(addr, state);
            flatDb.SetAccount(addr, state);
        }

        var bareFork = bareTrie.Fork();
        var flatFork = flatDb.Fork();

        // Post-fork ops
        for (int i = 5; i < 10; i++)
        {
            var addr = CreateTestAddress(i);
            var state = MakeAccount(nonce: (ulong)i, balance: (ulong)(i * 100));
            bareFork.SetAccount(addr, state);
            flatFork.SetAccount(addr, state);
        }

        bareFork.ComputeStateRoot().Should().Be(flatFork.ComputeStateRoot());
    }

    [Fact]
    public void Fork_DeleteInFork_DoesNotAffectParent()
    {
        var (db, _) = CreateFlatStateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, MakeAccount(nonce: 1));

        var fork = db.Fork();
        fork.DeleteAccount(address);

        fork.GetAccount(address).Should().BeNull();
        db.GetAccount(address).Should().NotBeNull();
    }

    [Fact]
    public void Fork_StorageIsolated()
    {
        var (db, _) = CreateFlatStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var key = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(contract, key, [0x01]);

        var fork = db.Fork();
        fork.SetStorage(contract, key, [0x02]);

        db.GetStorage(contract, key).Should().Equal([0x01]);
        fork.GetStorage(contract, key).Should().Equal([0x02]);
    }

    // -----------------------------------------------------------------------
    // GetAllAccounts
    // -----------------------------------------------------------------------

    [Fact]
    public void GetAllAccounts_ReturnsCachedAccounts()
    {
        var (db, _) = CreateFlatStateDb();
        for (int i = 0; i < 5; i++)
            db.SetAccount(CreateTestAddress(i), MakeAccount(nonce: (ulong)i));

        var all = db.GetAllAccounts().ToList();
        all.Should().HaveCount(5);
    }

    [Fact]
    public void GetAllAccounts_ExcludesDeletedAccounts()
    {
        var (db, _) = CreateFlatStateDb();
        db.SetAccount(CreateTestAddress(1), MakeAccount(nonce: 1));
        db.SetAccount(CreateTestAddress(2), MakeAccount(nonce: 2));
        db.DeleteAccount(CreateTestAddress(1));

        var all = db.GetAllAccounts().ToList();
        all.Should().HaveCount(1);
        all[0].Address.Should().Be(CreateTestAddress(2));
    }

    // -----------------------------------------------------------------------
    // Merkle proof pass-through
    // -----------------------------------------------------------------------

    [Fact]
    public void GenerateAccountProof_DelegatesToTrie()
    {
        var (db, trie) = CreateFlatStateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, MakeAccount(nonce: 42, balance: 1000));

        var flatProof = db.GenerateAccountProof(address);
        var trieProof = trie.GenerateAccountProof(address);

        flatProof.Should().NotBeNull();
        trieProof.Should().NotBeNull();
        flatProof!.Value.Should().Equal(trieProof!.Value);
    }

    [Fact]
    public void GenerateStorageProof_DelegatesToTrie()
    {
        var (db, trie) = CreateFlatStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var storageKey = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(contract, storageKey, [0x01, 0x02, 0x03]);

        var flatProof = db.GenerateStorageProof(contract, storageKey);
        var trieProof = trie.GenerateStorageProof(contract, storageKey);

        flatProof.Should().NotBeNull();
        trieProof.Should().NotBeNull();
        flatProof!.Value.Should().Equal(trieProof!.Value);
    }

    // -----------------------------------------------------------------------
    // State root after delete all
    // -----------------------------------------------------------------------

    [Fact]
    public void StateRoot_AfterDeleteAllAccounts_IsZero()
    {
        var (db, _) = CreateFlatStateDb();
        var addrs = new List<Address>();
        for (int i = 0; i < 5; i++)
        {
            var addr = CreateTestAddress(i);
            addrs.Add(addr);
            db.SetAccount(addr, MakeAccount(nonce: (ulong)i));
        }
        db.ComputeStateRoot().Should().NotBe(Hash256.Zero);

        foreach (var addr in addrs)
            db.DeleteAccount(addr);

        db.ComputeStateRoot().Should().Be(Hash256.Zero);
    }

    [Fact]
    public void ManyAccounts_ProducesDeterministicRoot()
    {
        var (db, _) = CreateFlatStateDb();
        for (int i = 0; i < 50; i++)
        {
            db.SetAccount(CreateTestAddress(i), MakeAccount(nonce: (ulong)i, balance: (ulong)(i * 100)));
        }

        var root = db.ComputeStateRoot();
        root.Should().NotBe(Hash256.Zero);

        for (int i = 0; i < 50; i++)
        {
            var account = db.GetAccount(CreateTestAddress(i));
            account.Should().NotBeNull();
            account!.Value.Nonce.Should().Be((ulong)i);
        }
    }

    // -----------------------------------------------------------------------
    // InnerTrie property
    // -----------------------------------------------------------------------

    [Fact]
    public void InnerTrie_ExposesUnderlyingTrieStateDb()
    {
        var (db, trie) = CreateFlatStateDb();
        db.InnerTrie.Should().BeSameAs(trie);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static AccountState MakeAccount(ulong nonce = 0, ulong balance = 0,
        AccountType type = AccountType.ExternallyOwned)
    {
        return new AccountState
        {
            Nonce = nonce,
            Balance = (UInt256)balance,
            StorageRoot = Hash256.Zero,
            CodeHash = Hash256.Zero,
            AccountType = type,
            ComplianceHash = Hash256.Zero,
        };
    }

    private static Address CreateTestAddress(int seed)
    {
        return new Address(CreateTestBytes(Address.Size, (byte)seed));
    }

    private static byte[] CreateTestBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)(seed + i);
        return bytes;
    }
}
