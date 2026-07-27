using Basalt.Core;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Whether the flat cache a node reloads at startup agrees with the trie it reloads alongside it.
///
/// It did not, and that is what halted the testnet. Folding a storage root into an account drops the
/// cached copy so the next read reloads from the trie, which is right in memory and leaves the persisted
/// copy behind on disk: a flush writes what the cache still holds and deletes what was deleted, and a
/// dropped entry is in neither list. The node restarted, reloaded a contract account one block stale,
/// executed against it, hashed the trie, and published a state root no peer could reproduce.
///
/// Nothing caught it. The startup consistency check compares ComputeStateRoot against the block header,
/// and ComputeStateRoot reads the trie alone, so a cache that contradicts the trie passes it every time.
/// </summary>
public class FlatStatePersistenceCoherenceTests
{
    private static Address Contract => new(Enumerable.Repeat((byte)0x42, 20).ToArray());

    private static Hash256 Slot(byte seed)
    {
        var key = new byte[32];
        key[0] = seed;
        return new Hash256(key);
    }

    private static AccountState ContractAccount() => new()
    {
        Nonce = 0,
        Balance = new UInt256(1_000),
        StorageRoot = Hash256.Zero,
        CodeHash = Hash256.Zero,
        AccountType = AccountType.Contract,
        ComplianceHash = Hash256.Zero,
    };

    /// <summary>
    /// A trivial persistence layer holding the same contract as the RocksDB one: the account set is
    /// replaced, storage is upserted and removed by name. Whether the store is RocksDB or a dictionary
    /// is not what any of this turns on.
    /// </summary>
    private sealed class DictionaryPersistence : IFlatStatePersistence
    {
        private readonly Dictionary<Address, AccountState> _accounts = [];
        private readonly Dictionary<(Address, Hash256), byte[]> _storage = [];

        public void Flush(
            IReadOnlyDictionary<Address, AccountState> accounts,
            IReadOnlyDictionary<(Address, Hash256), byte[]> storage,
            IReadOnlyCollection<(Address, Hash256)> deletedStorage)
        {
            _accounts.Clear();
            foreach (var (address, state) in accounts) _accounts[address] = state;
            foreach (var (key, value) in storage) _storage[key] = value;
            foreach (var key in deletedStorage) _storage.Remove(key);
        }

        public (IEnumerable<(Address, AccountState)> Accounts,
                IEnumerable<((Address, Hash256), byte[])> Storage) Load()
            => (_accounts.Select(e => (e.Key, e.Value)).ToList(),
                _storage.Select(e => (e.Key, e.Value)).ToList());
    }

    /// <summary>
    /// Advance a contract past a record that is already on disk, without that record being rewritten.
    ///
    /// This is the shape a sync swap leaves behind. Finishing a batch installs a fresh state over the
    /// same store, with its own empty cache, and every later flush upserts only what that cache holds.
    /// Entries it never touches are neither refreshed nor removed, so the persisted copy is a union of
    /// records from whenever each was last written. The fold makes it worse by design: it drops an
    /// account so the next read reloads from the trie, which guarantees the flush skips exactly the
    /// entries the trie has just moved past.
    /// </summary>
    private static (Hash256 Root, Hash256 Folded) AdvancePastAPersistedRecord(
        InMemoryTrieNodeStore store, DictionaryPersistence persistence)
    {
        var before = new FlatStateDb(new TrieStateDb(store), persistence);
        before.SetAccount(Contract, ContractAccount());
        before.SetStorage(Contract, Slot(1), [0xAA]);
        var firstRoot = before.ComputeStateRoot();
        before.GetAccount(Contract);   // a read puts it back in the cache, so the flush writes it
        before.FlushToPersistence();

        // The swap: a fresh state over the same store, holding nothing.
        var after = new FlatStateDb(new TrieStateDb(store, firstRoot), persistence);
        after.SetStorage(Contract, Slot(2), [0xBB]);
        var secondRoot = after.ComputeStateRoot();

        // Read the trie rather than the cache. Reading through the cache would put the account back into
        // it and the flush below would refresh the persisted copy, which is the one thing production does
        // not do: after the fold, nothing reads that contract again before shutdown.
        var folded = after.InnerTrie.GetAccount(Contract)!.Value.StorageRoot;
        after.FlushToPersistence();

        return (secondRoot, folded);
    }

    /// <summary>
    /// The account a restart reads has to be the one its trie holds.
    ///
    /// Reading a record the trie has moved past is how a node ends up a block behind on one contract
    /// while hashing a trie that is not, and the first call into that contract then lands on a state root
    /// only that node computes.
    /// </summary>
    [Fact]
    public void A_reloaded_contract_account_carries_the_storage_root_the_trie_holds()
    {
        var store = new InMemoryTrieNodeStore();
        var persistence = new DictionaryPersistence();
        var (root, folded) = AdvancePastAPersistedRecord(store, persistence);

        var restarted = new FlatStateDb(new TrieStateDb(store, root), persistence);
        restarted.LoadFromPersistence();

        restarted.GetAccount(Contract)!.Value.StorageRoot.Should().Be(folded,
            "the account a restart reads has to be the one the trie holds, not a copy the trie has moved past");
    }

    /// <summary>
    /// The same restart, judged by the only thing that matters to a peer.
    ///
    /// A stale account is harmless until something writes to that contract, at which point the write
    /// lands on a record nobody else has and the root parts company with the network. That is the shape
    /// the testnet took: the block was valid and reproducible from the trie, and the three nodes that had
    /// restarted could not reproduce it.
    /// </summary>
    [Fact]
    public void A_restarted_node_reaches_the_same_root_as_one_that_never_stopped()
    {
        var store = new InMemoryTrieNodeStore();
        var persistence = new DictionaryPersistence();
        var (root, _) = AdvancePastAPersistedRecord(store, persistence);

        var restarted = new FlatStateDb(new TrieStateDb(store, root), persistence);
        restarted.LoadFromPersistence();

        // A node that never stopped reads the same trie with no warm cache at all.
        var neverStopped = new FlatStateDb(new TrieStateDb(store, root));

        restarted.SetStorage(Contract, Slot(3), [0xCC]);
        neverStopped.SetStorage(Contract, Slot(3), [0xCC]);

        restarted.ComputeStateRoot().Should().Be(neverStopped.ComputeStateRoot(),
            "a node that restarted and one that did not hold the same data, so they owe each other the same root");
    }

    /// <summary>
    /// A storage slot the chain deleted must not come back after a restart.
    ///
    /// DeleteStorage records the slot so the next flush can remove the persisted copy, and the trie
    /// forgets it immediately. But CompactDeletedSets runs on every block, to stop those sets growing
    /// without bound, so by the time anything flushes there is nothing left to tell the persisted copy
    /// about. Its record stays. Reloading it hands back a value for a slot the trie says is empty, and
    /// the node reads one thing while hashing another.
    ///
    /// TWAP prunes storage on every block, so this is the common case rather than a corner of it.
    /// </summary>
    [Fact]
    public void A_deleted_storage_slot_does_not_come_back_after_a_restart()
    {
        var store = new InMemoryTrieNodeStore();
        var persistence = new DictionaryPersistence();

        var live = new FlatStateDb(new TrieStateDb(store), persistence);
        live.SetAccount(Contract, ContractAccount());
        live.SetStorage(Contract, Slot(1), [0xAA]);
        live.ComputeStateRoot();
        live.FlushToPersistence();

        // The chain removes the slot, and the block that did it finishes the way every block finishes.
        live.DeleteStorage(Contract, Slot(1));
        var root = live.ComputeStateRoot();
        live.ClearDirtyTracking();
        live.CompactDeletedSets();
        live.FlushToPersistence();

        var restarted = new FlatStateDb(new TrieStateDb(store, root), persistence);
        restarted.LoadFromPersistence();

        restarted.GetStorage(Contract, Slot(1)).Should().BeNull(
            "the slot was deleted from the trie, so a restart must not read a value for it");
    }

    /// <summary>
    /// The same deletion, judged by the state root, which is what a peer checks.
    ///
    /// A resurrected slot is invisible to a write, because SetStorage reaches the trie whatever the cache
    /// held. It shows up when something reads before deciding, which is what a contract does: the name
    /// service reads the owner slot to find out whether a name is taken. So the write below is made to
    /// depend on the read, the way a call does.
    /// </summary>
    [Fact]
    public void A_restart_after_a_deletion_reaches_the_same_root_as_a_node_that_never_stopped()
    {
        var store = new InMemoryTrieNodeStore();
        var persistence = new DictionaryPersistence();

        var live = new FlatStateDb(new TrieStateDb(store), persistence);
        live.SetAccount(Contract, ContractAccount());
        live.SetStorage(Contract, Slot(1), [0xAA]);
        live.ComputeStateRoot();
        live.FlushToPersistence();

        live.DeleteStorage(Contract, Slot(1));
        var root = live.ComputeStateRoot();
        live.ClearDirtyTracking();
        live.CompactDeletedSets();
        live.FlushToPersistence();

        var restarted = new FlatStateDb(new TrieStateDb(store, root), persistence);
        restarted.LoadFromPersistence();
        var neverStopped = new FlatStateDb(new TrieStateDb(store, root));

        // Read, then write what the read decided.
        restarted.SetStorage(Contract, Slot(2), restarted.GetStorage(Contract, Slot(1)) ?? [0x00]);
        neverStopped.SetStorage(Contract, Slot(2), neverStopped.GetStorage(Contract, Slot(1)) ?? [0x00]);

        restarted.ComputeStateRoot().Should().Be(neverStopped.ComputeStateRoot(),
            "a restarted node and one that never stopped hold the same data, so they owe each other the same root");
    }

    /// <summary>
    /// A flush leaves behind exactly what the cache holds, so a record it has dropped cannot outlive it.
    ///
    /// Dropping on load makes reading a superseded record safe, which is a guard rather than a cure: the
    /// record is still written, still reloaded, and still has to be recognised as wrong every time. This
    /// is the source. A cache that drops entries cannot name what it is no longer responsible for, so
    /// only a replacing write keeps the persisted copy to a single moment instead of a union of several.
    ///
    /// The shape below is the one the sync path produces on every batch: finishing installs a fresh
    /// state over the same store, holding nothing, and everything it never touches was left on disk by
    /// the instance it replaced.
    /// </summary>
    [Fact]
    public void A_flush_leaves_behind_only_what_the_cache_holds()
    {
        var store = new InMemoryTrieNodeStore();
        var persistence = new DictionaryPersistence();

        var before = new FlatStateDb(new TrieStateDb(store), persistence);
        before.SetAccount(Contract, ContractAccount());
        before.SetStorage(Contract, Slot(1), [0xAA]);
        var firstRoot = before.ComputeStateRoot();
        before.GetAccount(Contract);
        before.FlushToPersistence();

        // The swap, and a block's worth of work on the state that replaced it.
        var after = new FlatStateDb(new TrieStateDb(store, firstRoot), persistence);
        after.SetStorage(Contract, Slot(2), [0xBB]);
        var secondRoot = after.ComputeStateRoot();
        after.FlushToPersistence();

        // Nothing validates here. This asks what is actually on disk, which is what a build that trusted
        // it would read, and what the guard has to keep recognising as wrong for as long as it is there.
        var (persistedAccounts, _) = persistence.Load();
        var stale = persistedAccounts.Where(entry =>
            entry.Item1 == Contract && entry.Item2.StorageRoot != OnTrie(store, secondRoot).StorageRoot);

        stale.Should().BeEmpty(
            "a flush replaces the account set, so a record the cache dropped is gone rather than left "
            + "behind for the next start to reload");
    }

    private static AccountState OnTrie(InMemoryTrieNodeStore store, Hash256 root)
        => new TrieStateDb(store, root).GetAccount(Contract)!.Value;

    /// <summary>
    /// A database that has been told it cannot vouch for its state must not write that state down.
    ///
    /// Refusing a block happens after executing it, so the mutations are already on the state while the
    /// chain stays a block behind them. Persisting on the way out makes that permanent and the node
    /// reloads into a state no peer shares. Three validators are in exactly that condition, and it is why
    /// they are wedged rather than merely stopped.
    /// </summary>
    [Fact]
    public void A_state_marked_inconsistent_is_not_persisted()
    {
        var store = new InMemoryTrieNodeStore();
        var persistence = new DictionaryPersistence();

        var good = new FlatStateDb(new TrieStateDb(store), persistence);
        good.SetAccount(Contract, ContractAccount());
        var root = good.ComputeStateRoot();
        good.FlushToPersistence();

        // Executed a block, then found it could not be vouched for.
        good.SetAccount(Contract, ContractAccount() with { Balance = new UInt256(999_999) });
        good.MarkInconsistent();
        good.FlushToPersistence();

        var restarted = new FlatStateDb(new TrieStateDb(store, root), persistence);
        restarted.LoadFromPersistence();

        restarted.GetAccount(Contract)!.Value.Balance.Should().Be(new UInt256(1_000),
            "the refused block's mutations must not survive the restart that is supposed to recover from them");
    }
}
