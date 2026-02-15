using Basalt.Core;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

public class MerklePatriciaTrieTests
{
    private MerklePatriciaTrie CreateTrie()
    {
        var store = new InMemoryTrieNodeStore();
        return new MerklePatriciaTrie(store);
    }

    [Fact]
    public void EmptyTrie_HasZeroRoot()
    {
        var trie = CreateTrie();
        trie.RootHash.Should().Be(Hash256.Zero);
    }

    [Fact]
    public void Put_SingleKey_CanRetrieve()
    {
        var trie = CreateTrie();
        var key = new byte[] { 0x01, 0x02, 0x03 };
        var value = new byte[] { 0xAA, 0xBB, 0xCC };

        trie.Put(key, value);
        var result = trie.Get(key);

        result.Should().NotBeNull();
        result.Should().Equal(value);
    }

    [Fact]
    public void Put_SingleKey_ChangesRoot()
    {
        var trie = CreateTrie();
        trie.RootHash.Should().Be(Hash256.Zero);

        trie.Put([0x01], [0xFF]);
        trie.RootHash.Should().NotBe(Hash256.Zero);
    }

    [Fact]
    public void Put_MultipleKeys_AllRetrievable()
    {
        var trie = CreateTrie();
        var entries = new Dictionary<byte[], byte[]>
        {
            { new byte[] { 0x01, 0x23 }, new byte[] { 0xAA } },
            { new byte[] { 0x01, 0x24 }, new byte[] { 0xBB } },
            { new byte[] { 0x02, 0x00 }, new byte[] { 0xCC } },
            { new byte[] { 0x03, 0x45, 0x67 }, new byte[] { 0xDD } },
        };

        foreach (var (key, value) in entries)
            trie.Put(key, value);

        foreach (var (key, value) in entries)
        {
            var result = trie.Get(key);
            result.Should().NotBeNull();
            result.Should().Equal(value);
        }
    }

    [Fact]
    public void Put_UpdateExistingKey_ReturnsNewValue()
    {
        var trie = CreateTrie();
        var key = new byte[] { 0x01, 0x02 };

        trie.Put(key, [0xAA]);
        trie.Put(key, [0xBB]);

        var result = trie.Get(key);
        result.Should().NotBeNull();
        result.Should().Equal([0xBB]);
    }

    [Fact]
    public void Get_NonExistentKey_ReturnsNull()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);

        var result = trie.Get([0x02]);
        result.Should().BeNull();
    }

    [Fact]
    public void Delete_ExistingKey_RemovesIt()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);
        trie.Put([0x02], [0xBB]);

        var deleted = trie.Delete([0x01]);
        deleted.Should().BeTrue();

        trie.Get([0x01]).Should().BeNull();
        trie.Get([0x02]).Should().Equal([0xBB]);
    }

    [Fact]
    public void Delete_NonExistentKey_ReturnsFalse()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);

        var deleted = trie.Delete([0x02]);
        deleted.Should().BeFalse();
    }

    [Fact]
    public void Delete_AllKeys_ReturnsZeroRoot()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);
        trie.Put([0x02], [0xBB]);

        trie.Delete([0x01]);
        trie.Delete([0x02]);

        trie.RootHash.Should().Be(Hash256.Zero);
    }

    [Fact]
    public void Determinism_SameInsertionsProduceSameRoot()
    {
        var trie1 = CreateTrie();
        var trie2 = CreateTrie();

        var entries = new[]
        {
            (new byte[] { 0x01, 0x23 }, new byte[] { 0xAA }),
            (new byte[] { 0x04, 0x56 }, new byte[] { 0xBB }),
            (new byte[] { 0x07, 0x89 }, new byte[] { 0xCC }),
        };

        foreach (var (key, value) in entries)
        {
            trie1.Put(key, value);
            trie2.Put(key, value);
        }

        trie1.RootHash.Should().Be(trie2.RootHash);
    }

    [Fact]
    public void Determinism_DifferentInsertionOrderProduceSameRoot()
    {
        var trie1 = CreateTrie();
        var trie2 = CreateTrie();

        var entries = new[]
        {
            (new byte[] { 0x01 }, new byte[] { 0xAA }),
            (new byte[] { 0x02 }, new byte[] { 0xBB }),
            (new byte[] { 0x03 }, new byte[] { 0xCC }),
        };

        // Insert in order
        foreach (var (key, value) in entries)
            trie1.Put(key, value);

        // Insert in reverse order
        for (int i = entries.Length - 1; i >= 0; i--)
            trie2.Put(entries[i].Item1, entries[i].Item2);

        trie1.RootHash.Should().Be(trie2.RootHash);
    }

    [Fact]
    public void MerkleProof_ValidProof_Verifies()
    {
        var trie = CreateTrie();
        trie.Put([0x01, 0x23], [0xAA, 0xBB]);
        trie.Put([0x04, 0x56], [0xCC, 0xDD]);

        var proof = trie.GenerateProof([0x01, 0x23]);
        proof.Should().NotBeNull();
        proof!.Value.Should().Equal([0xAA, 0xBB]);

        var valid = MerklePatriciaTrie.VerifyProof(proof);
        valid.Should().BeTrue();
    }

    [Fact]
    public void MerkleProof_TamperedValue_FailsVerification()
    {
        var trie = CreateTrie();
        trie.Put([0x01, 0x23], [0xAA, 0xBB]);

        var proof = trie.GenerateProof([0x01, 0x23]);
        proof.Should().NotBeNull();

        // Tamper with the value
        var tampered = new MerkleProof
        {
            Key = proof!.Key,
            Value = [0xFF, 0xFF], // Wrong value
            ProofNodes = proof.ProofNodes,
            RootHash = proof.RootHash,
        };

        var valid = MerklePatriciaTrie.VerifyProof(tampered);
        valid.Should().BeFalse();
    }

    [Fact]
    public void MerkleProof_NonExistentKey_ReturnsNullValue()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);

        var proof = trie.GenerateProof([0x02]);
        proof.Should().NotBeNull();
        proof!.Value.Should().BeNull();
    }

    [Fact]
    public void LargeDataSet_InsertAndRetrieve()
    {
        var trie = CreateTrie();
        var random = new Random(42);
        var entries = new Dictionary<string, byte[]>();

        // Insert 100 random keys
        for (int i = 0; i < 100; i++)
        {
            var key = new byte[20];
            random.NextBytes(key);
            var value = new byte[32];
            random.NextBytes(value);
            var hexKey = Convert.ToHexString(key);
            entries[hexKey] = value;
            trie.Put(key, value);
        }

        // Verify all entries
        foreach (var (hexKey, expectedValue) in entries)
        {
            var key = Convert.FromHexString(hexKey);
            var result = trie.Get(key);
            result.Should().NotBeNull();
            result.Should().Equal(expectedValue);
        }

        trie.RootHash.Should().NotBe(Hash256.Zero);
    }

    [Fact]
    public void SharedPrefix_HandlesBranchNodeCorrectly()
    {
        var trie = CreateTrie();

        // Keys that share a prefix and will need branch nodes
        trie.Put([0xAB, 0xCD, 0x01], [0x01]);
        trie.Put([0xAB, 0xCD, 0x02], [0x02]);
        trie.Put([0xAB, 0xCE, 0x03], [0x03]);
        trie.Put([0xAB, 0x00, 0x04], [0x04]);

        trie.Get([0xAB, 0xCD, 0x01]).Should().Equal([0x01]);
        trie.Get([0xAB, 0xCD, 0x02]).Should().Equal([0x02]);
        trie.Get([0xAB, 0xCE, 0x03]).Should().Equal([0x03]);
        trie.Get([0xAB, 0x00, 0x04]).Should().Equal([0x04]);
    }

    // -----------------------------------------------------------------------
    // Proof generation and verification: deeper coverage
    // -----------------------------------------------------------------------

    [Fact]
    public void GenerateProof_EmptyTrie_ReturnsNull()
    {
        var trie = CreateTrie();
        var proof = trie.GenerateProof([0x01]);
        proof.Should().BeNull();
    }

    [Fact]
    public void MerkleProof_SingleKeyTrie_Verifies()
    {
        var trie = CreateTrie();
        trie.Put([0xDE, 0xAD], [0xBE, 0xEF]);

        var proof = trie.GenerateProof([0xDE, 0xAD]);
        proof.Should().NotBeNull();
        proof!.Value.Should().Equal([0xBE, 0xEF]);
        proof.ProofNodes.Count.Should().BeGreaterThan(0);

        MerklePatriciaTrie.VerifyProof(proof).Should().BeTrue();
    }

    [Fact]
    public void MerkleProof_ManyKeys_AllVerify()
    {
        var trie = CreateTrie();
        var keys = new List<byte[]>();
        for (int i = 0; i < 20; i++)
        {
            var key = new byte[] { (byte)(i / 16), (byte)(i % 16), 0x00 };
            var value = new byte[] { (byte)i };
            trie.Put(key, value);
            keys.Add(key);
        }

        foreach (var key in keys)
        {
            var proof = trie.GenerateProof(key);
            proof.Should().NotBeNull();
            proof!.Value.Should().NotBeNull();
            MerklePatriciaTrie.VerifyProof(proof).Should().BeTrue();
        }
    }

    [Fact]
    public void MerkleProof_NonExistentKey_VerifiesAsAbsent()
    {
        var trie = CreateTrie();
        trie.Put([0x01, 0x00], [0xAA]);
        trie.Put([0x02, 0x00], [0xBB]);

        var proof = trie.GenerateProof([0x03, 0x00]);
        proof.Should().NotBeNull();
        proof!.Value.Should().BeNull();

        // A non-existence proof should still verify
        MerklePatriciaTrie.VerifyProof(proof).Should().BeTrue();
    }

    [Fact]
    public void MerkleProof_TamperedRootHash_FailsVerification()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);

        var proof = trie.GenerateProof([0x01]);
        proof.Should().NotBeNull();

        // Create a bogus root hash
        var fakeRoot = new Hash256(new byte[]
        {
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        });

        var tampered = new MerkleProof
        {
            Key = proof!.Key,
            Value = proof.Value,
            ProofNodes = proof.ProofNodes,
            RootHash = fakeRoot,
        };

        MerklePatriciaTrie.VerifyProof(tampered).Should().BeFalse();
    }

    [Fact]
    public void MerkleProof_EmptyProofNodes_NonNullValue_Fails()
    {
        // If there are zero proof nodes, the only valid outcome is Value == null
        var proof = new MerkleProof
        {
            Key = [0x01],
            Value = [0xAA],
            ProofNodes = new List<byte[]>(),
            RootHash = Hash256.Zero,
        };

        MerklePatriciaTrie.VerifyProof(proof).Should().BeFalse();
    }

    [Fact]
    public void MerkleProof_EmptyProofNodes_NullValue_Succeeds()
    {
        var proof = new MerkleProof
        {
            Key = [0x01],
            Value = null,
            ProofNodes = new List<byte[]>(),
            RootHash = Hash256.Zero,
        };

        MerklePatriciaTrie.VerifyProof(proof).Should().BeTrue();
    }

    [Fact]
    public void MerkleProof_AfterUpdate_OldProofInvalidates()
    {
        var trie = CreateTrie();
        trie.Put([0x01, 0x23], [0xAA]);

        var proofBeforeNullable = trie.GenerateProof([0x01, 0x23]);
        proofBeforeNullable.Should().NotBeNull();
        var proofBefore = proofBeforeNullable!;
        MerklePatriciaTrie.VerifyProof(proofBefore).Should().BeTrue();

        // Update the value
        trie.Put([0x01, 0x23], [0xBB]);

        // The old proof should no longer verify against the new root
        var proofWithNewRoot = new MerkleProof
        {
            Key = proofBefore.Key,
            Value = proofBefore.Value, // old value
            ProofNodes = proofBefore.ProofNodes,
            RootHash = trie.RootHash, // new root
        };
        MerklePatriciaTrie.VerifyProof(proofWithNewRoot).Should().BeFalse();
    }

    [Fact]
    public void MerkleProof_AfterUpdate_NewProofVerifies()
    {
        var trie = CreateTrie();
        trie.Put([0x01, 0x23], [0xAA]);

        trie.Put([0x01, 0x23], [0xBB]);
        var proof = trie.GenerateProof([0x01, 0x23]);
        proof.Should().NotBeNull();
        proof!.Value.Should().Equal([0xBB]);
        MerklePatriciaTrie.VerifyProof(proof).Should().BeTrue();
    }

    [Fact]
    public void MerkleProof_AfterDelete_KeyAbsent()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);
        trie.Put([0x02], [0xBB]);

        trie.Delete([0x01]);
        var proof = trie.GenerateProof([0x01]);
        proof.Should().NotBeNull();
        proof!.Value.Should().BeNull();
        MerklePatriciaTrie.VerifyProof(proof).Should().BeTrue();
    }

    [Fact]
    public void MerkleProof_WithBranchValue_Verifies()
    {
        // Create scenario where a branch node holds a value:
        // key [0x10] and key [0x10, 0x20] cause a branch at the nibble boundary
        var trie = CreateTrie();
        trie.Put([0x10], [0xAA]);
        trie.Put([0x10, 0x20], [0xBB]);

        // Both should be retrievable
        trie.Get([0x10]).Should().Equal([0xAA]);
        trie.Get([0x10, 0x20]).Should().Equal([0xBB]);

        // Both proofs should verify
        var proof1 = trie.GenerateProof([0x10]);
        proof1.Should().NotBeNull();
        MerklePatriciaTrie.VerifyProof(proof1!).Should().BeTrue();

        var proof2 = trie.GenerateProof([0x10, 0x20]);
        proof2.Should().NotBeNull();
        MerklePatriciaTrie.VerifyProof(proof2!).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Put / Get edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void Put_EmptyValue_Stores()
    {
        var trie = CreateTrie();
        trie.Put([0x01], []);
        trie.Get([0x01]).Should().NotBeNull();
        trie.Get([0x01]).Should().BeEmpty();
    }

    [Fact]
    public void Put_LargeValue_Stores()
    {
        var trie = CreateTrie();
        var largeValue = new byte[4096];
        new Random(123).NextBytes(largeValue);

        trie.Put([0x01, 0x02], largeValue);
        var result = trie.Get([0x01, 0x02]);
        result.Should().NotBeNull();
        result.Should().Equal(largeValue);
    }

    [Fact]
    public void Put_SingleByteKeys_AllNibbleCombinations()
    {
        var trie = CreateTrie();
        // Insert all 256 single-byte keys
        for (int i = 0; i < 256; i++)
        {
            trie.Put([(byte)i], [(byte)i]);
        }

        for (int i = 0; i < 256; i++)
        {
            var result = trie.Get([(byte)i]);
            result.Should().NotBeNull();
            result.Should().Equal([(byte)i]);
        }
    }

    [Fact]
    public void Get_FromEmptyTrie_ReturnsNull()
    {
        var trie = CreateTrie();
        trie.Get([0x01, 0x02]).Should().BeNull();
    }

    [Fact]
    public void Put_IdenticalKeys_OnlyLastValueSurvives()
    {
        var trie = CreateTrie();
        var key = new byte[] { 0x01, 0x02, 0x03 };

        for (int i = 0; i < 10; i++)
            trie.Put(key, [(byte)i]);

        trie.Get(key).Should().Equal([9]);
    }

    [Fact]
    public void Put_KeysFormingDeepTree_AllRetrievable()
    {
        var trie = CreateTrie();
        // Keys with progressively longer shared prefixes
        trie.Put([0xAB, 0xCD, 0xEF, 0x01], [0x01]);
        trie.Put([0xAB, 0xCD, 0xEF, 0x02], [0x02]);
        trie.Put([0xAB, 0xCD, 0xEF, 0x03], [0x03]);
        trie.Put([0xAB, 0xCD, 0x00, 0x01], [0x04]);
        trie.Put([0xAB, 0x00, 0x00, 0x01], [0x05]);

        trie.Get([0xAB, 0xCD, 0xEF, 0x01]).Should().Equal([0x01]);
        trie.Get([0xAB, 0xCD, 0xEF, 0x02]).Should().Equal([0x02]);
        trie.Get([0xAB, 0xCD, 0xEF, 0x03]).Should().Equal([0x03]);
        trie.Get([0xAB, 0xCD, 0x00, 0x01]).Should().Equal([0x04]);
        trie.Get([0xAB, 0x00, 0x00, 0x01]).Should().Equal([0x05]);
    }

    // -----------------------------------------------------------------------
    // Delete edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void Delete_FromEmptyTrie_ReturnsFalse()
    {
        var trie = CreateTrie();
        trie.Delete([0x01]).Should().BeFalse();
    }

    [Fact]
    public void Delete_SingleKeyTrie_ReturnsToEmptyState()
    {
        var trie = CreateTrie();
        trie.Put([0x01, 0x02], [0xAA]);
        trie.Delete([0x01, 0x02]).Should().BeTrue();
        trie.RootHash.Should().Be(Hash256.Zero);
        trie.Get([0x01, 0x02]).Should().BeNull();
    }

    [Fact]
    public void Delete_OneOfManyKeys_OthersUnchanged()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);
        trie.Put([0x02], [0xBB]);
        trie.Put([0x03], [0xCC]);

        var rootBefore = trie.RootHash;
        trie.Delete([0x02]).Should().BeTrue();
        trie.RootHash.Should().NotBe(rootBefore);

        trie.Get([0x01]).Should().Equal([0xAA]);
        trie.Get([0x02]).Should().BeNull();
        trie.Get([0x03]).Should().Equal([0xCC]);
    }

    [Fact]
    public void Delete_SameKeyTwice_SecondReturnsFalse()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);
        trie.Delete([0x01]).Should().BeTrue();
        trie.Delete([0x01]).Should().BeFalse();
    }

    [Fact]
    public void Delete_ThenReinsert_WorksCorrectly()
    {
        var trie = CreateTrie();
        trie.Put([0x01], [0xAA]);
        trie.Delete([0x01]);
        trie.Put([0x01], [0xBB]);

        trie.Get([0x01]).Should().Equal([0xBB]);
        trie.RootHash.Should().NotBe(Hash256.Zero);
    }

    [Fact]
    public void Delete_BranchCompaction_MergesCorrectly()
    {
        var trie = CreateTrie();
        // Create a branch by inserting keys that differ at the first nibble
        trie.Put([0x10], [0xAA]);
        trie.Put([0x20], [0xBB]);
        trie.Put([0x30], [0xCC]);

        // Delete middle key: branch should compact if only two children remain
        trie.Delete([0x20]);
        trie.Get([0x10]).Should().Equal([0xAA]);
        trie.Get([0x30]).Should().Equal([0xCC]);
        trie.Get([0x20]).Should().BeNull();
    }

    [Fact]
    public void Delete_AllKeysInReverse_ReturnsToZeroRoot()
    {
        var trie = CreateTrie();
        var keys = new List<byte[]>();
        for (int i = 0; i < 16; i++)
        {
            var key = new byte[] { (byte)(i * 16), 0x01 };
            trie.Put(key, [(byte)i]);
            keys.Add(key);
        }

        for (int i = keys.Count - 1; i >= 0; i--)
            trie.Delete(keys[i]).Should().BeTrue();

        trie.RootHash.Should().Be(Hash256.Zero);
    }

    // -----------------------------------------------------------------------
    // Determinism: additional scenarios
    // -----------------------------------------------------------------------

    [Fact]
    public void Determinism_DeleteAndReinsert_ProducesSameRoot()
    {
        var trie1 = CreateTrie();
        trie1.Put([0x01], [0xAA]);
        trie1.Put([0x02], [0xBB]);
        var root1 = trie1.RootHash;

        var trie2 = CreateTrie();
        trie2.Put([0x01], [0xAA]);
        trie2.Put([0x02], [0xBB]);
        trie2.Put([0x03], [0xCC]);
        trie2.Delete([0x03]);
        var root2 = trie2.RootHash;

        root1.Should().Be(root2);
    }

    [Fact]
    public void Determinism_UpdateToSameValue_ProducesSameRoot()
    {
        var trie1 = CreateTrie();
        trie1.Put([0x01], [0xAA]);

        var trie2 = CreateTrie();
        trie2.Put([0x01], [0xFF]);
        trie2.Put([0x01], [0xAA]); // update back to original

        trie1.RootHash.Should().Be(trie2.RootHash);
    }

    [Fact]
    public void Determinism_LargeRandomDataset_DifferentOrder()
    {
        var random = new Random(99);
        var entries = new List<(byte[] Key, byte[] Value)>();
        for (int i = 0; i < 50; i++)
        {
            var key = new byte[8];
            random.NextBytes(key);
            var value = new byte[16];
            random.NextBytes(value);
            entries.Add((key, value));
        }

        var trie1 = CreateTrie();
        foreach (var (key, value) in entries)
            trie1.Put(key, value);

        // Shuffle entries
        var shuffled = entries.OrderBy(_ => random.Next()).ToList();
        var trie2 = CreateTrie();
        foreach (var (key, value) in shuffled)
            trie2.Put(key, value);

        trie1.RootHash.Should().Be(trie2.RootHash);
    }

    // -----------------------------------------------------------------------
    // Branch node with value scenarios
    // -----------------------------------------------------------------------

    [Fact]
    public void BranchValue_ShortKeyIsPrefixOfLongerKey()
    {
        var trie = CreateTrie();
        // key [0x12] has nibble path [1, 2]
        // key [0x12, 0x34] has nibble path [1, 2, 3, 4]
        // At the branch after [1, 2], the short key's value should be stored as branch value
        trie.Put([0x12], [0xAA]);
        trie.Put([0x12, 0x34], [0xBB]);

        trie.Get([0x12]).Should().Equal([0xAA]);
        trie.Get([0x12, 0x34]).Should().Equal([0xBB]);
    }

    [Fact]
    public void BranchValue_DeleteShortKey_LongerKeyRemains()
    {
        var trie = CreateTrie();
        trie.Put([0x12], [0xAA]);
        trie.Put([0x12, 0x34], [0xBB]);

        trie.Delete([0x12]).Should().BeTrue();
        trie.Get([0x12]).Should().BeNull();
        trie.Get([0x12, 0x34]).Should().Equal([0xBB]);
    }

    [Fact]
    public void BranchValue_DeleteLongerKey_ShortKeyRemains()
    {
        var trie = CreateTrie();
        trie.Put([0x12], [0xAA]);
        trie.Put([0x12, 0x34], [0xBB]);

        trie.Delete([0x12, 0x34]).Should().BeTrue();
        trie.Get([0x12]).Should().Equal([0xAA]);
        trie.Get([0x12, 0x34]).Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Extension node splitting
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtensionSplit_InsertDivergingKey_SplitsCorrectly()
    {
        var trie = CreateTrie();
        // These two keys share the prefix 0xAB but diverge at the next nibble
        trie.Put([0xAB, 0xCD], [0x01]);
        trie.Put([0xAB, 0xEF], [0x02]);

        trie.Get([0xAB, 0xCD]).Should().Equal([0x01]);
        trie.Get([0xAB, 0xEF]).Should().Equal([0x02]);

        // Add a third key that shares only 0xA
        trie.Put([0xAC, 0x00], [0x03]);
        trie.Get([0xAC, 0x00]).Should().Equal([0x03]);
        trie.Get([0xAB, 0xCD]).Should().Equal([0x01]);
        trie.Get([0xAB, 0xEF]).Should().Equal([0x02]);
    }

    [Fact]
    public void ExtensionSplit_NewKeyMatchesExtensionExactly()
    {
        var trie = CreateTrie();
        // Create an extension node with path
        trie.Put([0xAA, 0xBB, 0x01], [0x01]);
        trie.Put([0xAA, 0xBB, 0x02], [0x02]);
        // Now insert a key whose path matches the extension exactly
        trie.Put([0xAA, 0xBB, 0x03], [0x03]);

        trie.Get([0xAA, 0xBB, 0x01]).Should().Equal([0x01]);
        trie.Get([0xAA, 0xBB, 0x02]).Should().Equal([0x02]);
        trie.Get([0xAA, 0xBB, 0x03]).Should().Equal([0x03]);
    }

    // -----------------------------------------------------------------------
    // Stress / robustness
    // -----------------------------------------------------------------------

    [Fact]
    public void Stress_InsertDeleteReinsert_ConsistentState()
    {
        var trie = CreateTrie();
        var random = new Random(42);
        var live = new Dictionary<string, byte[]>();

        for (int round = 0; round < 5; round++)
        {
            // Insert 20 random keys
            for (int i = 0; i < 20; i++)
            {
                var key = new byte[4];
                random.NextBytes(key);
                var value = new byte[8];
                random.NextBytes(value);
                var hexKey = Convert.ToHexString(key);
                live[hexKey] = value;
                trie.Put(key, value);
            }

            // Delete 10 random existing keys
            var toDelete = live.Keys.OrderBy(_ => random.Next()).Take(10).ToList();
            foreach (var hexKey in toDelete)
            {
                var key = Convert.FromHexString(hexKey);
                trie.Delete(key);
                live.Remove(hexKey);
            }

            // Verify all remaining keys
            foreach (var (hexKey, expectedValue) in live)
            {
                var key = Convert.FromHexString(hexKey);
                var result = trie.Get(key);
                result.Should().NotBeNull($"Key {hexKey} should exist in round {round}");
                result.Should().Equal(expectedValue);
            }
        }
    }

    // -----------------------------------------------------------------------
    // Constructor with existing root hash
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_WithExistingRoot_CanRetrieveData()
    {
        var store = new InMemoryTrieNodeStore();
        var trie1 = new MerklePatriciaTrie(store);
        trie1.Put([0x01], [0xAA]);
        trie1.Put([0x02], [0xBB]);
        var savedRoot = trie1.RootHash;

        // Create a new trie instance pointing to the same store and root
        var trie2 = new MerklePatriciaTrie(store, savedRoot);
        trie2.RootHash.Should().Be(savedRoot);
        trie2.Get([0x01]).Should().Equal([0xAA]);
        trie2.Get([0x02]).Should().Equal([0xBB]);
    }

    [Fact]
    public void Constructor_WithNullRoot_StartsEmpty()
    {
        var store = new InMemoryTrieNodeStore();
        var trie = new MerklePatriciaTrie(store, null);
        trie.RootHash.Should().Be(Hash256.Zero);
        trie.Get([0x01]).Should().BeNull();
    }
}
