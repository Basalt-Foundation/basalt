using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

public class InMemoryStateDbTests
{
    private InMemoryStateDb CreateDb() => new();

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

    // -----------------------------------------------------------------------
    // Basic account CRUD
    // -----------------------------------------------------------------------

    [Fact]
    public void GetAccount_NonExistent_ReturnsNull()
    {
        var db = CreateDb();
        db.GetAccount(CreateTestAddress(1)).Should().BeNull();
    }

    [Fact]
    public void SetAndGet_Account_RoundTrips()
    {
        var db = CreateDb();
        var address = CreateTestAddress(1);
        var state = MakeAccount(nonce: 10, balance: 500);

        db.SetAccount(address, state);
        var retrieved = db.GetAccount(address);

        retrieved.Should().NotBeNull();
        retrieved!.Value.Nonce.Should().Be(10);
        retrieved.Value.Balance.Should().Be((UInt256)500);
    }

    [Fact]
    public void AccountExists_BeforeSet_ReturnsFalse()
    {
        var db = CreateDb();
        db.AccountExists(CreateTestAddress(1)).Should().BeFalse();
    }

    [Fact]
    public void AccountExists_AfterSet_ReturnsTrue()
    {
        var db = CreateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, AccountState.Empty);
        db.AccountExists(address).Should().BeTrue();
    }

    [Fact]
    public void DeleteAccount_RemovesIt()
    {
        var db = CreateDb();
        var address = CreateTestAddress(1);
        db.SetAccount(address, AccountState.Empty);
        db.DeleteAccount(address);

        db.AccountExists(address).Should().BeFalse();
        db.GetAccount(address).Should().BeNull();
    }

    [Fact]
    public void DeleteAccount_NonExistent_DoesNotThrow()
    {
        var db = CreateDb();
        var act = () => db.DeleteAccount(CreateTestAddress(1));
        act.Should().NotThrow();
    }

    [Fact]
    public void SetAccount_OverwriteExisting_UpdatesState()
    {
        var db = CreateDb();
        var address = CreateTestAddress(1);

        db.SetAccount(address, MakeAccount(nonce: 1, balance: 100));
        db.SetAccount(address, MakeAccount(nonce: 2, balance: 200));

        var retrieved = db.GetAccount(address);
        retrieved!.Value.Nonce.Should().Be(2);
        retrieved.Value.Balance.Should().Be((UInt256)200);
    }

    // -----------------------------------------------------------------------
    // Account type variants
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(AccountType.ExternallyOwned)]
    [InlineData(AccountType.Contract)]
    [InlineData(AccountType.SystemContract)]
    [InlineData(AccountType.Validator)]
    public void SetAccount_AllTypes_PreservedOnRetrieval(AccountType type)
    {
        var db = CreateDb();
        var address = CreateTestAddress((int)type + 10);
        db.SetAccount(address, MakeAccount(type: type));

        var retrieved = db.GetAccount(address);
        retrieved.Should().NotBeNull();
        retrieved!.Value.AccountType.Should().Be(type);
    }

    // -----------------------------------------------------------------------
    // State root computation
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeStateRoot_EmptyDb_ReturnsZero()
    {
        var db = CreateDb();
        db.ComputeStateRoot().Should().Be(Hash256.Zero);
    }

    [Fact]
    public void ComputeStateRoot_WithAccounts_NonZero()
    {
        var db = CreateDb();
        db.SetAccount(CreateTestAddress(1), MakeAccount(nonce: 1, balance: 100));
        db.ComputeStateRoot().Should().NotBe(Hash256.Zero);
    }

    [Fact]
    public void ComputeStateRoot_DeterministicForSameAccounts()
    {
        var db1 = CreateDb();
        var db2 = CreateDb();

        for (int i = 0; i < 5; i++)
        {
            var state = MakeAccount(nonce: (ulong)i, balance: (ulong)(i * 100));
            db1.SetAccount(CreateTestAddress(i), state);
            db2.SetAccount(CreateTestAddress(i), state);
        }

        db1.ComputeStateRoot().Should().Be(db2.ComputeStateRoot());
    }

    [Fact]
    public void ComputeStateRoot_ChangesWhenAccountModified()
    {
        var db = CreateDb();
        db.SetAccount(CreateTestAddress(1), MakeAccount(balance: 100));
        var root1 = db.ComputeStateRoot();

        db.SetAccount(CreateTestAddress(1), MakeAccount(balance: 200));
        var root2 = db.ComputeStateRoot();

        root1.Should().NotBe(root2);
    }

    [Fact]
    public void ComputeStateRoot_ChangesWhenAccountAdded()
    {
        var db = CreateDb();
        db.SetAccount(CreateTestAddress(1), MakeAccount(balance: 100));
        var root1 = db.ComputeStateRoot();

        db.SetAccount(CreateTestAddress(2), MakeAccount(balance: 200));
        var root2 = db.ComputeStateRoot();

        root1.Should().NotBe(root2);
    }

    [Fact]
    public void ComputeStateRoot_ReturnsZeroAfterDeletingAllAccounts()
    {
        var db = CreateDb();
        db.SetAccount(CreateTestAddress(1), MakeAccount());
        db.SetAccount(CreateTestAddress(2), MakeAccount());
        db.ComputeStateRoot().Should().NotBe(Hash256.Zero);

        db.DeleteAccount(CreateTestAddress(1));
        db.DeleteAccount(CreateTestAddress(2));
        db.ComputeStateRoot().Should().Be(Hash256.Zero);
    }

    // -----------------------------------------------------------------------
    // GetAllAccounts
    // -----------------------------------------------------------------------

    [Fact]
    public void GetAllAccounts_EmptyDb_ReturnsEmpty()
    {
        var db = CreateDb();
        db.GetAllAccounts().Should().BeEmpty();
    }

    [Fact]
    public void GetAllAccounts_ReturnsAllInsertedAccounts()
    {
        var db = CreateDb();
        var addresses = new List<Address>();
        for (int i = 0; i < 5; i++)
        {
            var addr = CreateTestAddress(i);
            addresses.Add(addr);
            db.SetAccount(addr, MakeAccount(nonce: (ulong)i));
        }

        var all = db.GetAllAccounts().ToList();
        all.Should().HaveCount(5);
        foreach (var addr in addresses)
            all.Should().Contain(x => x.Address == addr);
    }

    [Fact]
    public void GetAllAccounts_AfterDelete_ExcludesDeleted()
    {
        var db = CreateDb();
        db.SetAccount(CreateTestAddress(1), MakeAccount());
        db.SetAccount(CreateTestAddress(2), MakeAccount());
        db.DeleteAccount(CreateTestAddress(1));

        var all = db.GetAllAccounts().ToList();
        all.Should().HaveCount(1);
        all[0].Address.Should().Be(CreateTestAddress(2));
    }

    // -----------------------------------------------------------------------
    // Storage operations
    // -----------------------------------------------------------------------

    [Fact]
    public void GetStorage_NonExistent_ReturnsNull()
    {
        var db = CreateDb();
        var contract = CreateTestAddress(1);
        var key = new Hash256(CreateTestBytes(32, 0xAA));
        db.GetStorage(contract, key).Should().BeNull();
    }

    [Fact]
    public void SetAndGetStorage_Roundtrips()
    {
        var db = CreateDb();
        var contract = CreateTestAddress(1);
        var key = new Hash256(CreateTestBytes(32, 0xAA));
        var value = new byte[] { 0x01, 0x02, 0x03 };

        db.SetStorage(contract, key, value);
        db.GetStorage(contract, key).Should().Equal(value);
    }

    [Fact]
    public void DeleteStorage_RemovesValue()
    {
        var db = CreateDb();
        var contract = CreateTestAddress(1);
        var key = new Hash256(CreateTestBytes(32, 0xAA));

        db.SetStorage(contract, key, [0x01]);
        db.DeleteStorage(contract, key);
        db.GetStorage(contract, key).Should().BeNull();
    }

    [Fact]
    public void Storage_MultipleKeysPerContract()
    {
        var db = CreateDb();
        var contract = CreateTestAddress(1);

        for (int i = 0; i < 10; i++)
        {
            var key = new Hash256(CreateTestBytes(32, (byte)(i + 1)));
            db.SetStorage(contract, key, [(byte)i]);
        }

        for (int i = 0; i < 10; i++)
        {
            var key = new Hash256(CreateTestBytes(32, (byte)(i + 1)));
            db.GetStorage(contract, key).Should().Equal([(byte)i]);
        }
    }

    [Fact]
    public void Storage_DifferentContracts_Isolated()
    {
        var db = CreateDb();
        var contract1 = CreateTestAddress(1);
        var contract2 = CreateTestAddress(2);
        var key = new Hash256(CreateTestBytes(32, 0xAA));

        db.SetStorage(contract1, key, [0x01]);
        db.SetStorage(contract2, key, [0x02]);

        db.GetStorage(contract1, key).Should().Equal([0x01]);
        db.GetStorage(contract2, key).Should().Equal([0x02]);
    }

    [Fact]
    public void Storage_OverwriteExisting_UpdatesValue()
    {
        var db = CreateDb();
        var contract = CreateTestAddress(1);
        var key = new Hash256(CreateTestBytes(32, 0xAA));

        db.SetStorage(contract, key, [0x01]);
        db.SetStorage(contract, key, [0x02]);

        db.GetStorage(contract, key).Should().Equal([0x02]);
    }

    [Fact]
    public void DeleteStorage_NonExistent_DoesNotThrow()
    {
        var db = CreateDb();
        var contract = CreateTestAddress(1);
        var key = new Hash256(CreateTestBytes(32, 0xAA));

        var act = () => db.DeleteStorage(contract, key);
        act.Should().NotThrow();
    }

    // -----------------------------------------------------------------------
    // Multiple accounts with rich state
    // -----------------------------------------------------------------------

    [Fact]
    public void ManyAccounts_DifferentTypes_AllRetrievable()
    {
        var db = CreateDb();
        var types = new[] { AccountType.ExternallyOwned, AccountType.Contract,
                           AccountType.SystemContract, AccountType.Validator };

        for (int i = 0; i < 20; i++)
        {
            var state = MakeAccount(
                nonce: (ulong)i,
                balance: (ulong)(i * 1000),
                type: types[i % types.Length]);
            db.SetAccount(CreateTestAddress(i), state);
        }

        for (int i = 0; i < 20; i++)
        {
            var account = db.GetAccount(CreateTestAddress(i));
            account.Should().NotBeNull();
            account!.Value.Nonce.Should().Be((ulong)i);
            account.Value.Balance.Should().Be((UInt256)(ulong)(i * 1000));
            account.Value.AccountType.Should().Be(types[i % types.Length]);
        }
    }
}
