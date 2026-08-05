using Basalt.Core;
using Basalt.Storage.RocksDb;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Integration tests for <see cref="RocksDbTriePruner"/> against a real on-disk RocksDB store. Proves
/// the sweep reclaims stale trie nodes while keeping every node reachable from the retained roots —
/// including contract storage — and that its safety guards fire.
/// </summary>
public sealed class RocksDbTriePrunerTests : IDisposable
{
    private readonly string _dir;
    private readonly RocksDbStore _store;

    public RocksDbTriePrunerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "basalt-prune-" + Guid.NewGuid().ToString("N"));
        _store = new RocksDbStore(_dir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

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

    private int OnDiskNodeCount()
    {
        int n = 0;
        foreach (var _ in _store.Iterate(RocksDbStore.CF.TrieNodes)) n++;
        return n;
    }

    private static readonly Address Contract = Addr(0x02);

    /// <summary>Writes version 1 then version 2 of a state (contract + storage), persisting both to disk.</summary>
    private (Hash256 Root1, Hash256 Root2) BuildTwoVersions()
    {
        var node = new RocksDbTrieNodeStore(_store);
        var db = new TrieStateDb(node);

        // Version 1: an EOA plus a contract with two storage slots.
        db.SetAccount(Addr(0x01), new AccountState { Balance = new UInt256(1000) });
        db.SetAccount(Contract, new AccountState { Balance = new UInt256(5), AccountType = AccountType.Contract });
        db.SetStorage(Contract, Slot(0x10), [0xAA]);
        db.SetStorage(Contract, Slot(0x20), [0xBB]);
        var root1 = db.ComputeStateRoot();

        // Version 2: mutate the EOA, rewrite slot 0x10, add slot 0x30. This orphans the v1 account nodes
        // and the v1 storage sub-trie nodes — exactly what the sweep should reclaim.
        db.SetAccount(Addr(0x01), new AccountState { Balance = new UInt256(2000) });
        db.SetStorage(Contract, Slot(0x10), [0xCC]);
        db.SetStorage(Contract, Slot(0x30), [0xDD]);
        var root2 = db.ComputeStateRoot();

        root1.Should().NotBe(root2);
        return (root1, root2);
    }

    [RocksDbFact]
    public void Prune_RetainingLatestRoot_ReclaimsStaleNodes_KeepsLiveStateAndStorage()
    {
        var (_, root2) = BuildTwoVersions();
        int before = OnDiskNodeCount();
        before.Should().BeGreaterThan(0);

        var pruner = new RocksDbTriePruner(_store);
        var stats = pruner.Prune(new[] { root2 });

        stats.Deleted.Should().BeGreaterThan(0);                 // v1 orphans reclaimed
        stats.TotalScanned.Should().Be(before);
        stats.Retained.Should().Be(before - stats.Deleted);
        OnDiskNodeCount().Should().Be(stats.Retained);           // disk now holds exactly the live set

        // The retained version is fully intact: account, both surviving slots, and the state root itself.
        var reopened = new TrieStateDb(new RocksDbTrieNodeStore(_store), root2);
        reopened.GetAccount(Addr(0x01))!.Value.Balance.Should().Be(new UInt256(2000));
        reopened.GetStorage(Contract, Slot(0x10)).Should().BeEquivalentTo(new byte[] { 0xCC });
        reopened.GetStorage(Contract, Slot(0x20)).Should().BeEquivalentTo(new byte[] { 0xBB });
        reopened.GetStorage(Contract, Slot(0x30)).Should().BeEquivalentTo(new byte[] { 0xDD });
        reopened.ComputeStateRoot().Should().Be(root2);
    }

    [RocksDbFact]
    public void Prune_RetainingBothRoots_KeepsBothVersionsResolvable()
    {
        var (root1, root2) = BuildTwoVersions();

        var pruner = new RocksDbTriePruner(_store);
        pruner.Prune(new[] { root1, root2 });

        // Both historical roots stay openable — the union kept every node either references.
        new TrieStateDb(new RocksDbTrieNodeStore(_store), root1)
            .GetStorage(Contract, Slot(0x10)).Should().BeEquivalentTo(new byte[] { 0xAA });
        var r2 = new TrieStateDb(new RocksDbTrieNodeStore(_store), root2);
        r2.GetStorage(Contract, Slot(0x10)).Should().BeEquivalentTo(new byte[] { 0xCC });
        r2.ComputeStateRoot().Should().Be(root2);
    }

    [RocksDbFact]
    public void Prune_TripwireFires_WhenMarkWouldDeleteEverything_DeletesNothing()
    {
        BuildTwoVersions();
        int before = OnDiskNodeCount();

        // Retaining only the zero root marks nothing reachable, so the whole CF is a delete candidate.
        // The tripwire must abort and leave every node in place.
        var pruner = new RocksDbTriePruner(_store);
        var act = () => pruner.Prune(new[] { Hash256.Zero });

        act.Should().Throw<TriePruneAbortedException>();
        OnDiskNodeCount().Should().Be(before); // nothing deleted
    }

    [RocksDbFact]
    public void Prune_MissingRetainedNode_Aborts_DeletesNothing()
    {
        var (_, root2) = BuildTwoVersions();
        int before = OnDiskNodeCount();

        // Corrupt the store: remove a node root2 depends on. The mark must abort and delete nothing.
        var reachable = TrieReachability.CollectReachable(new RocksDbTrieNodeStore(_store), new[] { root2 });
        var victim = reachable.First();
        _store.Delete(RocksDbStore.CF.TrieNodes, VictimKey(victim));

        var pruner = new RocksDbTriePruner(_store);
        var act = () => pruner.Prune(new[] { root2 });

        // The diagnosis matters as much as the abort. The victim was deleted from the live store, so the
        // pruner must say so: that is what separates a node that is genuinely gone from one that exists
        // but is invisible through the pinned snapshot, and the two have opposite fixes.
        var thrown = act.Should().Throw<MissingTrieNodeDiagnosisException>()
            .WithMessage("*missing from the pinned snapshot*").Which;
        thrown.Hash.Should().Be(victim);
        thrown.PresentInLiveStore.Should().BeFalse();
        thrown.Root.Should().Be(root2, "the walk must name the retained root it was following");

        OnDiskNodeCount().Should().Be(before - 1); // only our manual delete; the sweep deleted nothing
    }

    [RocksDbFact]
    public void Prune_ThenCompact_Succeeds()
    {
        var (_, root2) = BuildTwoVersions();
        var pruner = new RocksDbTriePruner(_store);
        pruner.Prune(new[] { root2 });
        pruner.Compact(); // must not throw; state still resolves afterwards

        new TrieStateDb(new RocksDbTrieNodeStore(_store), root2)
            .ComputeStateRoot().Should().Be(root2);
    }

    private static byte[] VictimKey(Hash256 hash)
    {
        var k = new byte[Hash256.Size];
        hash.WriteTo(k);
        return k;
    }
}
