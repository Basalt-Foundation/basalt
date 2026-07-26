using Basalt.Core;
using Basalt.Crypto;
using Basalt.Storage.RocksDb;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Whether two nodes carrying state across several blocks agree on the state root.
///
/// The single-block tests in Basalt.Execution.Tests pass, and cannot do otherwise: both sides start
/// from a fresh state, so both begin with an identical and empty storage-trie cache. What the testnet
/// showed needs history. Three nodes computed three different roots for one block, which is what a root
/// that depends on a node's own call sequence looks like rather than on the data alone.
///
/// So this carries two long-lived databases, each with its own store, and walks them through blocks the
/// way a proposer and a follower actually diverge: one computes a root every block, the other is asked
/// less often.
/// </summary>
public class BuildVersusApplyOverBlocksTests
{
    private static Address Contract(byte seed) => new(Enumerable.Repeat(seed, 20).ToArray());

    private static AccountState ContractAccount(Hash256 storageRoot) => new()
    {
        Nonce = 0,
        Balance = UInt256.Zero,
        StorageRoot = storageRoot,
        CodeHash = Hash256.Zero,
        AccountType = AccountType.Contract,
        ComplianceHash = Hash256.Zero,
    };

    private static Hash256 Key(int i)
    {
        var k = new byte[32];
        BitConverter.GetBytes(i).CopyTo(k, 0);
        return new Hash256(k);
    }

    /// <summary>
    /// The same writes, in the same order, on two databases. One is asked for its root after every
    /// block. The other is asked only at the end, which is the difference between a node that proposes
    /// and one that merely follows and checks itself occasionally.
    ///
    /// A state root is a function of the data. If asking for it more often changes the answer, then no
    /// node can verify another's work, which is the property the whole chain rests on.
    /// </summary>
    [RocksDbFact]
    public void Computing_the_root_every_block_gives_the_same_answer_as_computing_it_once()
    {
        var pathA = Path.Combine(Path.GetTempPath(), "basalt-root-a-" + Guid.NewGuid().ToString("N"));
        var pathB = Path.Combine(Path.GetTempPath(), "basalt-root-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pathA);
        Directory.CreateDirectory(pathB);

        try
        {
            using var storeA = new RocksDbStore(pathA);
            using var storeB = new RocksDbStore(pathB);

            var eager = new TrieStateDb(new RocksDbTrieNodeStore(storeA));
            var lazy = new TrieStateDb(new RocksDbTrieNodeStore(storeB));

            var contract = Contract(0x42);
            eager.SetAccount(contract, ContractAccount(Hash256.Zero));
            lazy.SetAccount(contract, ContractAccount(Hash256.Zero));

            // Several blocks, each writing contract storage, which is what the diverging blocks on the
            // testnet had in common and what a plain transfer never touches.
            for (int block = 0; block < 8; block++)
            {
                for (int i = 0; i < 4; i++)
                {
                    var key = Key(block * 4 + i);
                    var value = new byte[] { (byte)block, (byte)i, 0xAB };
                    eager.SetStorage(contract, key, value);
                    lazy.SetStorage(contract, key, value);
                }

                // Asked every block. This is also what flushes storage roots into accounts and then
                // releases the storage tries, so the next block starts from a reconstructed one.
                eager.ComputeStateRoot();
            }

            Hash256 eagerRoot = eager.ComputeStateRoot();
            Hash256 lazyRoot = lazy.ComputeStateRoot();

            lazyRoot.Should().Be(eagerRoot,
                "the root has to be a function of the data, not of how often a node asked for it");
        }
        finally
        {
            Directory.Delete(pathA, recursive: true);
            Directory.Delete(pathB, recursive: true);
        }
    }

    /// <summary>
    /// Reading a contract's storage must not change what the root will be.
    ///
    /// Reads load a storage trie into the cache that the fold walks, so a node that served a query
    /// between two blocks would carry a different cache into the next fold than one that did not. Two
    /// nodes with identical data would then publish different roots, and which one is right would
    /// depend on who was asked a question.
    /// </summary>
    [RocksDbFact]
    public void Reading_storage_does_not_change_the_root()
    {
        var pathA = Path.Combine(Path.GetTempPath(), "basalt-read-a-" + Guid.NewGuid().ToString("N"));
        var pathB = Path.Combine(Path.GetTempPath(), "basalt-read-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(pathA);
        Directory.CreateDirectory(pathB);

        try
        {
            using var storeA = new RocksDbStore(pathA);
            using var storeB = new RocksDbStore(pathB);

            var quiet = new TrieStateDb(new RocksDbTrieNodeStore(storeA));
            var queried = new TrieStateDb(new RocksDbTrieNodeStore(storeB));

            var contract = Contract(0x77);
            quiet.SetAccount(contract, ContractAccount(Hash256.Zero));
            queried.SetAccount(contract, ContractAccount(Hash256.Zero));

            for (int block = 0; block < 5; block++)
            {
                for (int i = 0; i < 3; i++)
                {
                    var key = Key(block * 3 + i);
                    var value = new byte[] { (byte)block, (byte)i };
                    quiet.SetStorage(contract, key, value);
                    queried.SetStorage(contract, key, value);
                }

                quiet.ComputeStateRoot();
                queried.ComputeStateRoot();

                // One of them answers a read between blocks, the way an RPC node serves a query.
                queried.GetStorage(contract, Key(0));
            }

            queried.ComputeStateRoot().Should().Be(quiet.ComputeStateRoot(),
                "answering a query is not a state change");
        }
        finally
        {
            Directory.Delete(pathA, recursive: true);
            Directory.Delete(pathB, recursive: true);
        }
    }
}
