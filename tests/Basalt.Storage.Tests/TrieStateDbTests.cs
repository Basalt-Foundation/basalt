using Basalt.Core;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

public class TrieStateDbTests
{
    private TrieStateDb CreateStateDb()
    {
        var store = new InMemoryTrieNodeStore();
        return new TrieStateDb(store);
    }

    [Fact]
    public void Empty_StateRoot_IsZero()
    {
        var db = CreateStateDb();
        db.ComputeStateRoot().Should().Be(Hash256.Zero);
    }

    [Fact]
    public void SetAndGet_Account()
    {
        var db = CreateStateDb();
        var address = CreateTestAddress(1);
        var state = new AccountState
        {
            Nonce = 5,
            Balance = (UInt256)1000,
            StorageRoot = Hash256.Zero,
            CodeHash = Hash256.Zero,
            AccountType = AccountType.ExternallyOwned,
            ComplianceHash = Hash256.Zero,
        };

        db.SetAccount(address, state);
        var retrieved = db.GetAccount(address);

        retrieved.Should().NotBeNull();
        retrieved!.Value.Nonce.Should().Be(5);
        retrieved.Value.Balance.Should().Be((UInt256)1000);
    }

    [Fact]
    public void AccountExists_AfterSet()
    {
        var db = CreateStateDb();
        var address = CreateTestAddress(1);

        db.AccountExists(address).Should().BeFalse();
        db.SetAccount(address, AccountState.Empty);
        db.AccountExists(address).Should().BeTrue();
    }

    [Fact]
    public void DeleteAccount_RemovesIt()
    {
        var db = CreateStateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, AccountState.Empty);

        db.DeleteAccount(address);
        db.AccountExists(address).Should().BeFalse();
        db.GetAccount(address).Should().BeNull();
    }

    [Fact]
    public void StateRoot_ChangesWithAccountModification()
    {
        var db = CreateStateDb();
        var root0 = db.ComputeStateRoot();

        db.SetAccount(CreateTestAddress(1), new AccountState
        {
            Nonce = 0,
            Balance = (UInt256)100,
            StorageRoot = Hash256.Zero,
            CodeHash = Hash256.Zero,
            AccountType = AccountType.ExternallyOwned,
            ComplianceHash = Hash256.Zero,
        });
        var root1 = db.ComputeStateRoot();

        db.SetAccount(CreateTestAddress(2), new AccountState
        {
            Nonce = 0,
            Balance = (UInt256)200,
            StorageRoot = Hash256.Zero,
            CodeHash = Hash256.Zero,
            AccountType = AccountType.ExternallyOwned,
            ComplianceHash = Hash256.Zero,
        });
        var root2 = db.ComputeStateRoot();

        root0.Should().NotBe(root1);
        root1.Should().NotBe(root2);
    }

    [Fact]
    public void StateRoot_Deterministic_SameAccountsSameRoot()
    {
        var db1 = CreateStateDb();
        var db2 = CreateStateDb();

        var accounts = new (Address Addr, AccountState State)[]
        {
            (CreateTestAddress(1), new AccountState
            {
                Nonce = 1, Balance = (UInt256)100,
                StorageRoot = Hash256.Zero, CodeHash = Hash256.Zero,
                AccountType = AccountType.ExternallyOwned, ComplianceHash = Hash256.Zero,
            }),
            (CreateTestAddress(2), new AccountState
            {
                Nonce = 2, Balance = (UInt256)200,
                StorageRoot = Hash256.Zero, CodeHash = Hash256.Zero,
                AccountType = AccountType.Contract, ComplianceHash = Hash256.Zero,
            }),
        };

        foreach (var (addr, state) in accounts)
        {
            db1.SetAccount(addr, state);
            db2.SetAccount(addr, state);
        }

        db1.ComputeStateRoot().Should().Be(db2.ComputeStateRoot());
    }

    [Fact]
    public void ContractStorage_SetAndGet()
    {
        var db = CreateStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, new AccountState
        {
            Nonce = 0, Balance = UInt256.Zero,
            StorageRoot = Hash256.Zero, CodeHash = Hash256.Zero,
            AccountType = AccountType.Contract, ComplianceHash = Hash256.Zero,
        });

        var storageKey = new Hash256(CreateTestBytes(32, 0xAA));
        var value = new byte[] { 0x01, 0x02, 0x03 };

        db.SetStorage(contract, storageKey, value);
        var result = db.GetStorage(contract, storageKey);

        result.Should().NotBeNull();
        result.Should().Equal(value);
    }

    [Fact]
    public void ContractStorage_DeleteKey()
    {
        var db = CreateStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, new AccountState
        {
            Nonce = 0, Balance = UInt256.Zero,
            StorageRoot = Hash256.Zero, CodeHash = Hash256.Zero,
            AccountType = AccountType.Contract, ComplianceHash = Hash256.Zero,
        });

        var storageKey = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(contract, storageKey, [0x01]);
        db.DeleteStorage(contract, storageKey);

        db.GetStorage(contract, storageKey).Should().BeNull();
    }

    [Fact]
    public void ManyAccounts_ProducesDeterministicRoot()
    {
        var db = CreateStateDb();
        for (int i = 0; i < 50; i++)
        {
            db.SetAccount(CreateTestAddress(i), new AccountState
            {
                Nonce = (ulong)i,
                Balance = (UInt256)(ulong)(i * 100),
                StorageRoot = Hash256.Zero,
                CodeHash = Hash256.Zero,
                AccountType = AccountType.ExternallyOwned,
                ComplianceHash = Hash256.Zero,
            });
        }

        var root = db.ComputeStateRoot();
        root.Should().NotBe(Hash256.Zero);

        // Verify we can still read all accounts
        for (int i = 0; i < 50; i++)
        {
            var account = db.GetAccount(CreateTestAddress(i));
            account.Should().NotBeNull();
            account!.Value.Nonce.Should().Be((ulong)i);
        }
    }

    [Fact]
    public void MerkleProof_AccountExists()
    {
        var db = CreateStateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, new AccountState
        {
            Nonce = 42, Balance = (UInt256)1000,
            StorageRoot = Hash256.Zero, CodeHash = Hash256.Zero,
            AccountType = AccountType.ExternallyOwned, ComplianceHash = Hash256.Zero,
        });

        var proof = db.GenerateAccountProof(address);
        proof.Should().NotBeNull();
        proof!.Value.Should().NotBeNull();

        var valid = MerklePatriciaTrie.VerifyProof(proof);
        valid.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Account proof: additional scenarios
    // -----------------------------------------------------------------------

    [Fact]
    public void MerkleProof_NonExistentAccount_ReturnsNullValue()
    {
        var db = CreateStateDb();
        db.SetAccount(CreateTestAddress(1), MakeAccount());

        var proof = db.GenerateAccountProof(CreateTestAddress(99));
        proof.Should().NotBeNull();
        proof!.Value.Should().BeNull();
    }

    [Fact]
    public void MerkleProof_MultipleAccounts_AllVerify()
    {
        var db = CreateStateDb();
        for (int i = 0; i < 10; i++)
        {
            db.SetAccount(CreateTestAddress(i), MakeAccount(nonce: (ulong)i, balance: (ulong)(i * 100)));
        }

        for (int i = 0; i < 10; i++)
        {
            var proof = db.GenerateAccountProof(CreateTestAddress(i));
            proof.Should().NotBeNull();
            proof!.Value.Should().NotBeNull();
            MerklePatriciaTrie.VerifyProof(proof).Should().BeTrue();
        }
    }

    [Fact]
    public void MerkleProof_AfterAccountUpdate_NewProofVerifies()
    {
        var db = CreateStateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, MakeAccount(nonce: 1, balance: 100));

        db.SetAccount(address, MakeAccount(nonce: 2, balance: 200));

        var proof = db.GenerateAccountProof(address);
        proof.Should().NotBeNull();
        MerklePatriciaTrie.VerifyProof(proof!).Should().BeTrue();
    }

    [Fact]
    public void MerkleProof_AfterAccountDelete_ShowsAbsent()
    {
        var db = CreateStateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, MakeAccount());
        db.DeleteAccount(address);

        var proof = db.GenerateAccountProof(address);
        // Proof may be null (empty trie) or have null value
        if (proof != null)
        {
            proof.Value.Should().BeNull();
        }
    }

    // -----------------------------------------------------------------------
    // Storage proof
    // -----------------------------------------------------------------------

    [Fact]
    public void StorageProof_ExistingSlot_Verifies()
    {
        var db = CreateStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var storageKey = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(contract, storageKey, [0x01, 0x02, 0x03]);

        var proof = db.GenerateStorageProof(contract, storageKey);
        proof.Should().NotBeNull();
        proof!.Value.Should().Equal([0x01, 0x02, 0x03]);
        MerklePatriciaTrie.VerifyProof(proof).Should().BeTrue();
    }

    [Fact]
    public void StorageProof_NonExistentSlot_ReturnsNullValue()
    {
        var db = CreateStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));
        db.SetStorage(contract, new Hash256(CreateTestBytes(32, 0xAA)), [0x01]);

        var missingKey = new Hash256(CreateTestBytes(32, 0xBB));
        var proof = db.GenerateStorageProof(contract, missingKey);
        proof.Should().NotBeNull();
        proof!.Value.Should().BeNull();
    }

    [Fact]
    public void StorageProof_MultipleSlots_AllVerify()
    {
        var db = CreateStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        for (int i = 0; i < 10; i++)
        {
            var key = new Hash256(CreateTestBytes(32, (byte)(i + 1)));
            db.SetStorage(contract, key, [(byte)i]);
        }

        for (int i = 0; i < 10; i++)
        {
            var key = new Hash256(CreateTestBytes(32, (byte)(i + 1)));
            var proof = db.GenerateStorageProof(contract, key);
            proof.Should().NotBeNull();
            proof!.Value.Should().NotBeNull();
            MerklePatriciaTrie.VerifyProof(proof).Should().BeTrue();
        }
    }

    // -----------------------------------------------------------------------
    // Storage operations
    // -----------------------------------------------------------------------

    [Fact]
    public void Storage_MultipleKeysPerContract_AllRetrievable()
    {
        var db = CreateStateDb();
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
            var result = db.GetStorage(contract, key);
            result.Should().NotBeNull();
            result.Should().Equal([(byte)i, (byte)(i * 2)]);
        }
    }

    [Fact]
    public void Storage_OverwriteSlot_ReturnsNewValue()
    {
        var db = CreateStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var key = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(contract, key, [0x01]);
        db.SetStorage(contract, key, [0x02]);

        db.GetStorage(contract, key).Should().Equal([0x02]);
    }

    [Fact]
    public void Storage_DifferentContracts_Isolated()
    {
        var db = CreateStateDb();
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

    [Fact]
    public void DeleteStorage_RemovesSlot()
    {
        var db = CreateStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));

        var key = new Hash256(CreateTestBytes(32, 0xAA));
        db.SetStorage(contract, key, [0x01]);
        db.DeleteStorage(contract, key);

        db.GetStorage(contract, key).Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // State root and storage interaction
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeStateRoot_ReflectsStorageChanges()
    {
        var db = CreateStateDb();
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
        var db1 = new TrieStateDb(store1);
        var store2 = new InMemoryTrieNodeStore();
        var db2 = new TrieStateDb(store2);

        var contract = CreateTestAddress(1);
        var storageKey = new Hash256(CreateTestBytes(32, 0xAA));

        db1.SetAccount(contract, MakeAccount(type: AccountType.Contract));
        db1.SetStorage(contract, storageKey, [0x01, 0x02]);

        db2.SetAccount(contract, MakeAccount(type: AccountType.Contract));
        db2.SetStorage(contract, storageKey, [0x01, 0x02]);

        db1.ComputeStateRoot().Should().Be(db2.ComputeStateRoot());
    }

    // -----------------------------------------------------------------------
    // Delete account and storage trie cleanup
    // -----------------------------------------------------------------------

    [Fact]
    public void DeleteAccount_ClearsStorageTrie()
    {
        var db = CreateStateDb();
        var contract = CreateTestAddress(1);
        db.SetAccount(contract, MakeAccount(type: AccountType.Contract));
        db.SetStorage(contract, new Hash256(CreateTestBytes(32, 0xAA)), [0x01]);

        db.DeleteAccount(contract);
        db.AccountExists(contract).Should().BeFalse();
        // After deleting the account, the storage trie cache should be gone.
        // Re-querying storage for a deleted account should return null (fresh empty trie).
        db.GetStorage(contract, new Hash256(CreateTestBytes(32, 0xAA))).Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // GetAllAccounts throws for trie-backed store
    // -----------------------------------------------------------------------

    [Fact]
    public void GetAllAccounts_ThrowsNotSupported()
    {
        var db = CreateStateDb();
        db.SetAccount(CreateTestAddress(1), MakeAccount());

        var act = () => db.GetAllAccounts().ToList();
        act.Should().Throw<NotSupportedException>();
    }

    // -----------------------------------------------------------------------
    // Account state encoding roundtrip (tested indirectly via SetAccount/GetAccount)
    // -----------------------------------------------------------------------

    [Fact]
    public void AccountState_FullRoundtrip_AllFields()
    {
        var db = CreateStateDb();
        var address = CreateTestAddress(1);

        var codeHash = new Hash256(CreateTestBytes(32, 0xCC));
        var complianceHash = new Hash256(CreateTestBytes(32, 0xDD));

        var state = new AccountState
        {
            Nonce = ulong.MaxValue,
            Balance = (UInt256)999999999UL,
            StorageRoot = Hash256.Zero,
            CodeHash = codeHash,
            AccountType = AccountType.Contract,
            ComplianceHash = complianceHash,
        };

        db.SetAccount(address, state);
        var retrieved = db.GetAccount(address);

        retrieved.Should().NotBeNull();
        retrieved!.Value.Nonce.Should().Be(ulong.MaxValue);
        retrieved.Value.Balance.Should().Be((UInt256)999999999UL);
        retrieved.Value.CodeHash.Should().Be(codeHash);
        retrieved.Value.AccountType.Should().Be(AccountType.Contract);
        retrieved.Value.ComplianceHash.Should().Be(complianceHash);
    }

    [Theory]
    [InlineData(AccountType.ExternallyOwned)]
    [InlineData(AccountType.Contract)]
    [InlineData(AccountType.SystemContract)]
    [InlineData(AccountType.Validator)]
    public void AccountState_AllAccountTypes_Roundtrip(AccountType type)
    {
        var db = CreateStateDb();
        var address = CreateTestAddress((int)type + 10);

        db.SetAccount(address, MakeAccount(type: type));
        var retrieved = db.GetAccount(address);

        retrieved.Should().NotBeNull();
        retrieved!.Value.AccountType.Should().Be(type);
    }

    // -----------------------------------------------------------------------
    // State root after various mutations
    // -----------------------------------------------------------------------

    [Fact]
    public void StateRoot_AfterDeleteAllAccounts_IsZero()
    {
        var db = CreateStateDb();
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
    public void StateRoot_IdenticalOperations_SameRoot()
    {
        var store1 = new InMemoryTrieNodeStore();
        var db1 = new TrieStateDb(store1);
        var store2 = new InMemoryTrieNodeStore();
        var db2 = new TrieStateDb(store2);

        for (int i = 0; i < 10; i++)
        {
            var state = MakeAccount(nonce: (ulong)i, balance: (ulong)(i * 1000));
            db1.SetAccount(CreateTestAddress(i), state);
            db2.SetAccount(CreateTestAddress(i), state);
        }

        db1.ComputeStateRoot().Should().Be(db2.ComputeStateRoot());
    }

    // -----------------------------------------------------------------------
    // Constructor with existing state root
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_WithExistingStateRoot_RetrievesData()
    {
        var store = new InMemoryTrieNodeStore();
        var db1 = new TrieStateDb(store);
        var address = CreateTestAddress(1);
        db1.SetAccount(address, MakeAccount(nonce: 42, balance: 1000));
        var savedRoot = db1.ComputeStateRoot();

        // Create new TrieStateDb against the same store with the saved root
        var db2 = new TrieStateDb(store, savedRoot);
        var account = db2.GetAccount(address);
        account.Should().NotBeNull();
        account!.Value.Nonce.Should().Be(42);
        account.Value.Balance.Should().Be((UInt256)1000);
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
