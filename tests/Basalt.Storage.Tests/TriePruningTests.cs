using Basalt.Core;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Tests for trie node garbage collection (H-01).
/// </summary>
public class TriePruningTests
{
    [Fact]
    public void CollectReachableNodes_EmptyTrie_ReturnsEmpty()
    {
        var store = new InMemoryTrieNodeStore();
        var trie = new MerklePatriciaTrie(store);
        var reachable = trie.CollectReachableNodes();
        reachable.Should().BeEmpty();
    }

    [Fact]
    public void CollectReachableNodes_SingleLeaf_ReturnsSingleHash()
    {
        var store = new InMemoryTrieNodeStore();
        var trie = new MerklePatriciaTrie(store);
        trie.Put([0x01], [0xAA]);

        var reachable = trie.CollectReachableNodes();
        reachable.Should().HaveCount(1);
        reachable.Should().Contain(trie.RootHash);
    }

    [Fact]
    public void Prune_RemovesStaleNodesAfterUpdate()
    {
        var store = new InMemoryTrieNodeStore();
        var trie = new MerklePatriciaTrie(store);

        // Insert initial value
        trie.Put([0x01], [0xAA]);
        int afterInsert = store.Count;

        // Update same key — creates new leaf, old leaf becomes stale
        trie.Put([0x01], [0xBB]);
        int afterUpdate = store.Count;
        afterUpdate.Should().BeGreaterThan(afterInsert);

        // Prune: only nodes reachable from current root should survive
        var reachable = trie.CollectReachableNodes();
        int removed = store.Prune(reachable);

        removed.Should().BeGreaterThan(0);
        store.Count.Should().Be(reachable.Count);

        // Verify trie still works after pruning
        trie.Get([0x01]).Should().BeEquivalentTo(new byte[] { 0xBB });
    }

    [Fact]
    public void Prune_RemovesStaleNodesAfterDelete()
    {
        var store = new InMemoryTrieNodeStore();
        var trie = new MerklePatriciaTrie(store);

        trie.Put([0x01], [0xAA]);
        trie.Put([0x02], [0xBB]);
        int beforeDelete = store.Count;

        trie.Delete([0x01]);
        int afterDelete = store.Count;

        // Stale nodes from pre-delete tree should be prunable
        var reachable = trie.CollectReachableNodes();
        int removed = store.Prune(reachable);

        removed.Should().BeGreaterThan(0);
        store.Count.Should().Be(reachable.Count);

        // Verify surviving data
        trie.Get([0x01]).Should().BeNull();
        trie.Get([0x02]).Should().BeEquivalentTo(new byte[] { 0xBB });
    }

    [Fact]
    public void Prune_LargerTrie_ReclainsSignificantSpace()
    {
        var store = new InMemoryTrieNodeStore();
        var trie = new MerklePatriciaTrie(store);

        // Insert 100 keys
        for (int i = 0; i < 100; i++)
            trie.Put([(byte)(i / 16), (byte)(i % 16)], [(byte)i]);

        int afterInserts = store.Count;

        // Update all keys — doubles the node count approximately
        for (int i = 0; i < 100; i++)
            trie.Put([(byte)(i / 16), (byte)(i % 16)], [(byte)(i + 100)]);

        int afterUpdates = store.Count;
        afterUpdates.Should().BeGreaterThan(afterInserts);

        // Prune should reclaim the stale nodes
        var reachable = trie.CollectReachableNodes();
        int removed = store.Prune(reachable);

        removed.Should().BeGreaterThan(0);
        store.Count.Should().BeLessThan(afterUpdates);

        // Verify all data still accessible
        for (int i = 0; i < 100; i++)
        {
            var val = trie.Get([(byte)(i / 16), (byte)(i % 16)]);
            val.Should().NotBeNull();
            val![0].Should().Be((byte)(i + 100));
        }
    }

    [Fact]
    public void Prune_SingleKey_NoStaleNodes()
    {
        var store = new InMemoryTrieNodeStore();
        var trie = new MerklePatriciaTrie(store);

        // A single insert creates exactly one leaf — no stale nodes
        trie.Put([0x01], [0xAA]);

        var reachable = trie.CollectReachableNodes();
        int removed = store.Prune(reachable);

        removed.Should().Be(0);
        store.Count.Should().Be(1);
    }
}
