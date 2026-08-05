using Basalt.Core;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Tests for <see cref="TrieReachability"/> — the storage-aware reachable-union walk that state-trie
/// pruning uses as its mark phase.
/// </summary>
/// <remarks>
/// The load-bearing property (H1): a prune that keeps ONLY the set this walk returns must never delete a
/// live contract-storage node. <see cref="MerklePatriciaTrie.CollectReachableNodes"/> walks world-trie
/// structure only and never decodes an account value, so it misses every storage node — which is exactly
/// why pruning cannot use it. <see cref="NegativeControl_WorldOnlyWalk_MissesStorageNodes"/> pins that gap.
/// </remarks>
public class TrieReachabilityTests
{
    private static Address Addr(byte b)
    {
        var a = new byte[Address.Size];
        a[^1] = b;
        return new Address(a);
    }

    private static Hash256 Slot(byte b)
    {
        var s = new byte[Hash256.Size];
        s[^1] = b;
        return new Hash256(s);
    }

    /// <summary>Builds an EOA plus a contract carrying several storage slots and returns the world state root.</summary>
    private static (TrieStateDb Db, Address Contract, Hash256 Root) BuildContractWithStorage(InMemoryTrieNodeStore store)
    {
        var db = new TrieStateDb(store);

        // A plain externally-owned account (no storage) to make the world trie branch.
        db.SetAccount(Addr(0x01), new AccountState { Balance = new UInt256(1000) });

        // A contract with several storage slots — the storage sub-trie the world-only walk misses.
        var contract = Addr(0x02);
        db.SetAccount(contract, new AccountState { Balance = new UInt256(5), AccountType = AccountType.Contract });
        db.SetStorage(contract, Slot(0x10), [0xAA, 0xBB]);
        db.SetStorage(contract, Slot(0x20), [0xCC, 0xDD]);
        db.SetStorage(contract, Slot(0x30), [0xEE]);

        var root = db.ComputeStateRoot();
        return (db, contract, root);
    }

    [Fact]
    public void CollectReachable_ContractWithStorage_StorageSurvivesPruneToUnion()
    {
        var store = new InMemoryTrieNodeStore();
        var (_, contract, root) = BuildContractWithStorage(store);
        root.Should().NotBe(Hash256.Zero);

        // Mark with the storage-aware walk, then prune to EXACTLY that set (delete everything else).
        var reachable = TrieReachability.CollectReachable(store, new[] { root });
        store.Prune(reachable);
        store.Count.Should().Be(reachable.Count); // nothing outside the union remained

        // Re-open at the pruned root: every storage slot AND the state root must survive intact.
        var reopened = new TrieStateDb(store, root);
        reopened.GetStorage(contract, Slot(0x10)).Should().BeEquivalentTo(new byte[] { 0xAA, 0xBB });
        reopened.GetStorage(contract, Slot(0x20)).Should().BeEquivalentTo(new byte[] { 0xCC, 0xDD });
        reopened.GetStorage(contract, Slot(0x30)).Should().BeEquivalentTo(new byte[] { 0xEE });
        reopened.ComputeStateRoot().Should().Be(root); // recompute over the pruned store == original root
    }

    [Fact]
    public void NegativeControl_WorldOnlyWalk_MissesStorageNodes()
    {
        var store = new InMemoryTrieNodeStore();
        var (_, _, root) = BuildContractWithStorage(store);

        var storageAware = TrieReachability.CollectReachable(store, new[] { root });
        var worldOnly = new MerklePatriciaTrie(store, root).CollectReachableNodes();

        // The storage-aware walk is a strict superset: it finds every world node the old walk finds,
        // PLUS the contract's storage nodes the old walk misses. Pruning to `worldOnly` would delete
        // exactly `storageAware \ worldOnly` — the live storage nodes — and corrupt state (H1).
        storageAware.IsSupersetOf(worldOnly).Should().BeTrue();      // finds every world node the old walk finds
        storageAware.Count.Should().BeGreaterThan(worldOnly.Count);  // ...and strictly more (the storage nodes)
    }

    [Fact]
    public void CollectReachable_UnionsAcrossMultipleRoots()
    {
        var store = new InMemoryTrieNodeStore();

        var db = new TrieStateDb(store);
        db.SetAccount(Addr(0x01), new AccountState { Balance = new UInt256(1) });
        var root1 = db.ComputeStateRoot();

        // Second version adds a contract with storage — root2 shares structure with root1 but adds nodes.
        var contract = Addr(0x02);
        db.SetAccount(contract, new AccountState { Balance = new UInt256(9), AccountType = AccountType.Contract });
        db.SetStorage(contract, Slot(0x42), [0x99]);
        var root2 = db.ComputeStateRoot();

        var union = TrieReachability.CollectReachable(store, new[] { root1, root2 });

        // The union keeps both historical roots reachable: pruning to it leaves BOTH versions openable.
        store.Prune(union);
        new TrieStateDb(store, root1).GetAccount(Addr(0x01)).Should().NotBeNull();
        var r2 = new TrieStateDb(store, root2);
        r2.GetStorage(contract, Slot(0x42)).Should().BeEquivalentTo(new byte[] { 0x99 });
        r2.ComputeStateRoot().Should().Be(root2);
    }

    [Fact]
    public void CollectReachable_MissingNode_ThrowsAndDeletesNothing()
    {
        var store = new InMemoryTrieNodeStore();
        var (_, _, root) = BuildContractWithStorage(store);

        // Simulate a corrupt/incomplete store: drop a node the root depends on. The mark phase must
        // fail loudly (under-marking would make a sweep delete live nodes), never silently under-count.
        var aRealNode = TrieReachability.CollectReachable(store, new[] { root }).First();
        store.Delete(aRealNode);

        var act = () => TrieReachability.CollectReachable(store, new[] { root });
        act.Should().Throw<InvalidOperationException>().WithMessage("*missing from the store*");
    }

    [Fact]
    public void CollectReachable_ZeroRoot_ReturnsEmpty()
    {
        var store = new InMemoryTrieNodeStore();
        TrieReachability.CollectReachable(store, new[] { Hash256.Zero }).Should().BeEmpty();
    }
}
