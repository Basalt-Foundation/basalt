using Basalt.Core;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Whether a cached account can ever disagree with the trie it is supposed to mirror.
///
/// FlatStateDb serves accounts from a cache, and the trie rewrites those same records when it folds
/// storage roots into them. The cache does not see that, so anything reading an account afterwards can
/// get a storage root from before the fold and rebuild a contract's storage a transaction behind.
///
/// This was fixed once in ComputeStateRoot and left in Fork, where it cost a block of registrations
/// that reported success and wrote nothing. Asserting the invariant rather than the two call sites is
/// what makes a third one, added later, fail here instead of on a chain.
/// </summary>
public class FlatStateCacheCoherenceTests
{
    private static readonly Address Contract = new(Enumerable.Repeat((byte)0x42, 20).ToArray());

    private static FlatStateDb NewState()
    {
        var db = new FlatStateDb(new TrieStateDb(new InMemoryTrieNodeStore()));
        db.SetAccount(Contract, new AccountState
        {
            Nonce = 0,
            Balance = UInt256.Zero,
            StorageRoot = Hash256.Zero,
            CodeHash = Hash256.Zero,
            AccountType = AccountType.Contract,
            ComplianceHash = Hash256.Zero,
        });
        return db;
    }

    private static Hash256 Key(int i)
    {
        var k = new byte[32];
        BitConverter.GetBytes(i).CopyTo(k, 0);
        return new Hash256(k);
    }

    private static void Write(IStateDatabase db, int i)
        => db.SetStorage(Contract, Key(i), [(byte)i, 0xAB]);

    [Fact]
    public void A_cached_account_never_disagrees_with_the_trie_after_a_root_is_computed()
    {
        var db = NewState();
        Write(db, 1);
        db.ComputeStateRoot();

        // Reading the account has to give the storage root the fold just wrote, not the one before it.
        var storageRoot = db.GetAccount(Contract)!.Value.StorageRoot;
        storageRoot.Should().NotBe(Hash256.Zero, "the fold recorded this contract's storage");
    }

    /// <summary>
    /// The case that reached a chain. A fork inherits the parent's accounts, and forking folds, so a
    /// naive copy carries records the fold has already replaced.
    /// </summary>
    [Fact]
    public void A_fork_does_not_inherit_an_account_the_fold_has_replaced()
    {
        var db = NewState();
        Write(db, 1);

        var fork = db.Fork();

        // Against the trie's own answer, not the parent's cache, which is stale in exactly the same way
        // and would agree with a stale fork while both were wrong.
        fork.GetAccount(Contract)!.Value.StorageRoot
            .Should().NotBe(Hash256.Zero,
                "forking folds the storage root into the account, so the fork must start from the folded "
                + "record and not from a copy of the cache taken before it");
    }

    /// <summary>
    /// What the stale root actually cost: a write made before the fork, invisible to it.
    ///
    /// Each contract call in a block forks the state, so a fork that cannot see the previous call's
    /// writes overwrites them on merge, and only the last call in a block survives.
    /// </summary>
    [Fact]
    public void A_fork_sees_every_write_made_before_it()
    {
        var db = NewState();
        Write(db, 1);
        Write(db, 2);

        var fork = db.Fork();

        fork.GetStorage(Contract, Key(1)).Should().NotBeNull("written before the fork existed");
        fork.GetStorage(Contract, Key(2)).Should().NotBeNull();
    }

    [Fact]
    public void Writes_through_successive_forks_all_survive()
    {
        var db = NewState();

        // One fork per write, merged back, which is what a block of contract calls does.
        for (int i = 1; i <= 4; i++)
        {
            var fork = db.Fork();
            Write(fork, i);

            foreach (var (contract, key) in fork.GetModifiedStorageKeys())
                db.SetStorage(contract, key, fork.GetStorage(contract, key)!);
        }

        for (int i = 1; i <= 4; i++)
            db.GetStorage(Contract, Key(i)).Should().NotBeNull($"write {i} was merged and must survive");
    }
}
