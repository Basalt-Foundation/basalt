using Basalt.Core;
using Basalt.Storage.RocksDb;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Tests for <see cref="TrieRetention"/> — the retention-window arithmetic that decides which state roots
/// a prune sweep must keep. Pure (no RocksDB), so these run everywhere.
/// </summary>
public class TrieRetentionTests
{
    // Deterministic per-block "state root": block n -> Hash256 with n in its last byte(s).
    private static Hash256 RootOf(ulong n)
    {
        var b = new byte[Hash256.Size];
        BitConverter.GetBytes(n).CopyTo(b, 0);
        b[^1] = 0xAA; // sentinel so block 0's root is non-zero (a genuine root, not "absent")
        return new Hash256(b);
    }

    private static Func<ulong, Hash256?> Chain(ulong height)
        => n => n <= height ? RootOf(n) : (Hash256?)null;

    [Fact]
    public void Window_SmallerThanChain_RetainsExactlyWindowPlusGenesis()
    {
        // Chain height 100, window 10 -> retain blocks [91..100] (10 roots) + genesis (block 0) = 11.
        var roots = TrieRetention.CollectRetainedRoots(latestBlockNumber: 100, windowSize: 10, Chain(100));

        roots.Should().HaveCount(11);
        roots.Should().Contain(RootOf(0));   // genesis
        roots.Should().Contain(RootOf(100)); // tip
        roots.Should().Contain(RootOf(91));  // window lower bound
        roots.Should().NotContain(RootOf(90)); // just outside the window
        roots.Should().NotContain(RootOf(50));
    }

    [Fact]
    public void Window_LargerThanChain_RetainsWholeChain()
    {
        var roots = TrieRetention.CollectRetainedRoots(latestBlockNumber: 5, windowSize: 1000, Chain(5));

        roots.Should().HaveCount(6); // blocks 0..5 all retained (genesis included in the window)
        for (ulong n = 0; n <= 5; n++)
            roots.Should().Contain(RootOf(n));
    }

    [Fact]
    public void WindowZero_RetainsOnlyGenesisAndTip()
    {
        var roots = TrieRetention.CollectRetainedRoots(latestBlockNumber: 42, windowSize: 0, Chain(42));

        roots.Should().HaveCount(2);
        roots.Should().Contain(RootOf(0));  // genesis
        roots.Should().Contain(RootOf(42)); // tip
    }

    [Fact]
    public void EmptyStore_NoTip_RetainsGenesisOnly()
    {
        // Genesis present but no canonical tip recorded yet.
        var roots = TrieRetention.CollectRetainedRoots(latestBlockNumber: null, windowSize: 1000, Chain(0));
        roots.Should().ContainSingle().Which.Should().Be(RootOf(0));
    }

    [Fact]
    public void CompletelyEmptyStore_RetainsNothing()
    {
        // No genesis, no tip: nothing to retain (nothing to prune against either).
        var roots = TrieRetention.CollectRetainedRoots(latestBlockNumber: null, windowSize: 1000, _ => null);
        roots.Should().BeEmpty();
    }

    [Fact]
    public void DuplicateStateRoots_Collapse()
    {
        // Empty blocks that repeat the previous state root must not inflate the retained set.
        var pinned = RootOf(7);
        var roots = TrieRetention.CollectRetainedRoots(
            latestBlockNumber: 20, windowSize: 10, n => n == 0 ? RootOf(0) : pinned);

        // Genesis + the single repeated root = 2 distinct roots.
        roots.Should().HaveCount(2);
        roots.Should().Contain(RootOf(0));
        roots.Should().Contain(pinned);
    }

    [Fact]
    public void TipInsideWindow_ZeroStateRootsSkipped()
    {
        // A block reporting a zero state root (e.g. a gap) is skipped, not retained as Hash256.Zero.
        var roots = TrieRetention.CollectRetainedRoots(
            latestBlockNumber: 3, windowSize: 10,
            n => n == 2 ? Hash256.Zero : RootOf(n));

        roots.Should().NotContain(Hash256.Zero);
        roots.Should().Contain(RootOf(3));
        roots.Should().HaveCount(3); // blocks 0,1,3 (block 2 zero-skipped)
    }
}
