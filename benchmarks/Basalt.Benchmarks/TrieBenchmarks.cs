using BenchmarkDotNet.Attributes;
using Basalt.Crypto;
using Basalt.Storage.Trie;

namespace Basalt.Benchmarks;

/// <summary>
/// Benchmarks for Merkle Patricia Trie operations.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class TrieBenchmarks
{
    private byte[][] _keys = null!;
    private byte[][] _values = null!;
    private MerklePatriciaTrie _populatedTrie = null!;
    private InMemoryTrieNodeStore _populatedStore = null!;

    [Params(100, 1000, 10000)]
    public int KeyCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _keys = new byte[KeyCount][];
        _values = new byte[KeyCount][];

        for (int i = 0; i < KeyCount; i++)
        {
            // Create keys by hashing the index (deterministic, uniformly distributed)
            var indexBytes = BitConverter.GetBytes(i);
            var hash = Blake3Hasher.Hash(indexBytes);
            _keys[i] = hash.ToArray();

            _values[i] = new byte[32];
            BitConverter.GetBytes(i).CopyTo(_values[i], 0);
        }

        // Pre-populate a trie for read benchmarks
        _populatedStore = new InMemoryTrieNodeStore();
        _populatedTrie = new MerklePatriciaTrie(_populatedStore);
        for (int i = 0; i < KeyCount; i++)
            _populatedTrie.Put(_keys[i], _values[i]);
    }

    [Benchmark]
    public void Trie_Insert_All()
    {
        var store = new InMemoryTrieNodeStore();
        var trie = new MerklePatriciaTrie(store);
        for (int i = 0; i < KeyCount; i++)
            trie.Put(_keys[i], _values[i]);
    }

    [Benchmark]
    public int Trie_Get_All()
    {
        int found = 0;
        for (int i = 0; i < KeyCount; i++)
        {
            var val = _populatedTrie.Get(_keys[i]);
            if (val != null) found++;
        }
        return found;
    }

    [Benchmark]
    public void Trie_Update_All()
    {
        for (int i = 0; i < KeyCount; i++)
        {
            var newVal = new byte[32];
            BitConverter.GetBytes(i + 1).CopyTo(newVal, 0);
            _populatedTrie.Put(_keys[i], newVal);
        }
    }

    [Benchmark]
    public int Trie_GenerateProof()
    {
        // Generate proof for first 10 keys
        int proofCount = 0;
        var count = Math.Min(10, KeyCount);
        for (int i = 0; i < count; i++)
        {
            var proof = _populatedTrie.GenerateProof(_keys[i]);
            if (proof != null) proofCount++;
        }
        return proofCount;
    }
}
