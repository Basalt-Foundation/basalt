using Basalt.Core;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

public class TrieNodeTests
{
    // -----------------------------------------------------------------------
    // Empty node
    // -----------------------------------------------------------------------

    [Fact]
    public void EmptyNode_EncodeDecode_Roundtrips()
    {
        var node = TrieNode.CreateEmpty();
        node.NodeType.Should().Be(TrieNodeType.Empty);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.NodeType.Should().Be(TrieNodeType.Empty);
    }

    [Fact]
    public void EmptyNode_ComputeHash_IsConsistent()
    {
        var node1 = TrieNode.CreateEmpty();
        var node2 = TrieNode.CreateEmpty();

        node1.ComputeHash().Should().Be(node2.ComputeHash());
    }

    // -----------------------------------------------------------------------
    // Leaf node
    // -----------------------------------------------------------------------

    [Fact]
    public void LeafNode_EncodeDecode_Roundtrips()
    {
        var path = NibblePath.FromKey([0xAB, 0xCD]);
        var value = new byte[] { 0x01, 0x02, 0x03 };
        var node = TrieNode.CreateLeaf(path, value);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.NodeType.Should().Be(TrieNodeType.Leaf);
        decoded.Value.Should().Equal(value);
        decoded.Path.Length.Should().Be(path.Length);
        for (int i = 0; i < path.Length; i++)
            decoded.Path[i].Should().Be(path[i]);
    }

    [Fact]
    public void LeafNode_EmptyValue_EncodeDecode_Roundtrips()
    {
        var path = NibblePath.FromKey([0x01]);
        var node = TrieNode.CreateLeaf(path, []);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.NodeType.Should().Be(TrieNodeType.Leaf);
        decoded.Value.Should().BeEmpty();
    }

    [Fact]
    public void LeafNode_LargeValue_EncodeDecode_Roundtrips()
    {
        var path = NibblePath.FromKey([0xAA]);
        var value = new byte[512];
        new Random(42).NextBytes(value);
        var node = TrieNode.CreateLeaf(path, value);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.Value.Should().Equal(value);
    }

    [Fact]
    public void LeafNode_OddLengthPath_EncodeDecode_Roundtrips()
    {
        // Create a path with 3 nibbles (odd length)
        var path = NibblePath.FromKey([0xAB, 0xC0]).Slice(0, 3);
        var node = TrieNode.CreateLeaf(path, [0xFF]);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.NodeType.Should().Be(TrieNodeType.Leaf);
        decoded.Path.Length.Should().Be(3);
        decoded.Path[0].Should().Be(0x0A);
        decoded.Path[1].Should().Be(0x0B);
        decoded.Path[2].Should().Be(0x0C);
        decoded.Value.Should().Equal([0xFF]);
    }

    [Fact]
    public void LeafNode_EmptyPath_EncodeDecode_Roundtrips()
    {
        var path = new NibblePath([], 0, 0);
        var node = TrieNode.CreateLeaf(path, [0x42]);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.NodeType.Should().Be(TrieNodeType.Leaf);
        decoded.Path.Length.Should().Be(0);
        decoded.Value.Should().Equal([0x42]);
    }

    [Fact]
    public void LeafNode_SameContent_SameHash()
    {
        var path = NibblePath.FromKey([0xAB]);
        var node1 = TrieNode.CreateLeaf(path, [0x01]);
        var node2 = TrieNode.CreateLeaf(path, [0x01]);

        node1.ComputeHash().Should().Be(node2.ComputeHash());
    }

    [Fact]
    public void LeafNode_DifferentValue_DifferentHash()
    {
        var path = NibblePath.FromKey([0xAB]);
        var node1 = TrieNode.CreateLeaf(path, [0x01]);
        var node2 = TrieNode.CreateLeaf(path, [0x02]);

        node1.ComputeHash().Should().NotBe(node2.ComputeHash());
    }

    [Fact]
    public void LeafNode_DifferentPath_DifferentHash()
    {
        var node1 = TrieNode.CreateLeaf(NibblePath.FromKey([0xAB]), [0x01]);
        var node2 = TrieNode.CreateLeaf(NibblePath.FromKey([0xCD]), [0x01]);

        node1.ComputeHash().Should().NotBe(node2.ComputeHash());
    }

    // -----------------------------------------------------------------------
    // Extension node
    // -----------------------------------------------------------------------

    [Fact]
    public void ExtensionNode_EncodeDecode_Roundtrips()
    {
        var path = NibblePath.FromKey([0xAB]);
        var childHash = new Hash256(new byte[]
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        });
        var node = TrieNode.CreateExtension(path, childHash);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.NodeType.Should().Be(TrieNodeType.Extension);
        decoded.ChildHash.Should().Be(childHash);
        decoded.Path.Length.Should().Be(path.Length);
        for (int i = 0; i < path.Length; i++)
            decoded.Path[i].Should().Be(path[i]);
    }

    [Fact]
    public void ExtensionNode_OddPath_EncodeDecode_Roundtrips()
    {
        var path = NibblePath.FromKey([0xAB, 0xC0]).Slice(0, 3); // 3 nibbles
        var childHash = new Hash256(new byte[32]); // zero hash
        var node = TrieNode.CreateExtension(path, childHash);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.NodeType.Should().Be(TrieNodeType.Extension);
        decoded.Path.Length.Should().Be(3);
    }

    [Fact]
    public void ExtensionNode_SameContent_SameHash()
    {
        var path = NibblePath.FromKey([0xAB]);
        var childHash = new Hash256(new byte[]
        {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        });

        var node1 = TrieNode.CreateExtension(path, childHash);
        var node2 = TrieNode.CreateExtension(path, childHash);

        node1.ComputeHash().Should().Be(node2.ComputeHash());
    }

    // -----------------------------------------------------------------------
    // Branch node
    // -----------------------------------------------------------------------

    [Fact]
    public void BranchNode_NoChildren_NoValue_EncodeDecode_Roundtrips()
    {
        var node = TrieNode.CreateBranch();

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.NodeType.Should().Be(TrieNodeType.Branch);
        decoded.BranchValue.Should().BeNull();
        for (int i = 0; i < 16; i++)
            decoded.Children[i].Should().BeNull();
    }

    [Fact]
    public void BranchNode_WithChildren_EncodeDecode_Roundtrips()
    {
        var node = TrieNode.CreateBranch();
        var hash0 = new Hash256(CreateTestBytes(32, 0x01));
        var hash5 = new Hash256(CreateTestBytes(32, 0x05));
        var hashF = new Hash256(CreateTestBytes(32, 0x0F));

        node.SetChild(0, hash0);
        node.SetChild(5, hash5);
        node.SetChild(15, hashF);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.NodeType.Should().Be(TrieNodeType.Branch);
        decoded.Children[0].Should().Be(hash0);
        decoded.Children[5].Should().Be(hash5);
        decoded.Children[15].Should().Be(hashF);

        // Unset children should be null
        decoded.Children[1].Should().BeNull();
        decoded.Children[7].Should().BeNull();
    }

    [Fact]
    public void BranchNode_WithValue_EncodeDecode_Roundtrips()
    {
        var node = TrieNode.CreateBranch();
        node.SetBranchValue([0xAA, 0xBB, 0xCC]);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.BranchValue.Should().Equal([0xAA, 0xBB, 0xCC]);
    }

    [Fact]
    public void BranchNode_WithChildrenAndValue_EncodeDecode_Roundtrips()
    {
        var node = TrieNode.CreateBranch();
        var hash3 = new Hash256(CreateTestBytes(32, 0x33));
        node.SetChild(3, hash3);
        node.SetBranchValue([0xDE, 0xAD]);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.Children[3].Should().Be(hash3);
        decoded.BranchValue.Should().Equal([0xDE, 0xAD]);
    }

    [Fact]
    public void BranchNode_AllChildrenSet_EncodeDecode_Roundtrips()
    {
        var node = TrieNode.CreateBranch();
        var hashes = new Hash256[16];
        for (int i = 0; i < 16; i++)
        {
            hashes[i] = new Hash256(CreateTestBytes(32, (byte)(i + 1)));
            node.SetChild(i, hashes[i]);
        }

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        for (int i = 0; i < 16; i++)
            decoded.Children[i].Should().Be(hashes[i]);
    }

    [Fact]
    public void BranchNode_NullValue_EncodesCorrectly()
    {
        var node = TrieNode.CreateBranch();
        node.SetBranchValue([0x01]); // Set then unset
        node.SetBranchValue(null);

        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.BranchValue.Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Hash caching and IsDirty flag
    // -----------------------------------------------------------------------

    [Fact]
    public void NewNode_IsDirty()
    {
        var node = TrieNode.CreateLeaf(NibblePath.FromKey([0x01]), [0xAA]);
        node.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void ComputeHash_ClearsIsDirty()
    {
        var node = TrieNode.CreateLeaf(NibblePath.FromKey([0x01]), [0xAA]);
        node.ComputeHash();
        node.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void ComputeHash_ReturnsConsistentResult()
    {
        var node = TrieNode.CreateLeaf(NibblePath.FromKey([0x01]), [0xAA]);
        var hash1 = node.ComputeHash();
        var hash2 = node.ComputeHash(); // Should use cached value
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void MarkDirty_InvalidatesCache()
    {
        var branch = TrieNode.CreateBranch();
        var hash1 = branch.ComputeHash();
        branch.IsDirty.Should().BeFalse();

        branch.MarkDirty();
        branch.IsDirty.Should().BeTrue();

        // Recomputing should give the same hash for the same content
        var hash2 = branch.ComputeHash();
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void SetChild_MarksDirty()
    {
        var branch = TrieNode.CreateBranch();
        branch.ComputeHash();
        branch.IsDirty.Should().BeFalse();

        branch.SetChild(0, new Hash256(CreateTestBytes(32, 0x01)));
        branch.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void SetBranchValue_MarksDirty()
    {
        var branch = TrieNode.CreateBranch();
        branch.ComputeHash();
        branch.IsDirty.Should().BeFalse();

        branch.SetBranchValue([0x01]);
        branch.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void DecodedNode_IsNotDirty()
    {
        var node = TrieNode.CreateLeaf(NibblePath.FromKey([0x01]), [0xAA]);
        var encoded = node.Encode();
        var decoded = TrieNode.Decode(encoded);

        decoded.IsDirty.Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Cross-type hashing: different types produce different hashes
    // -----------------------------------------------------------------------

    [Fact]
    public void DifferentNodeTypes_DifferentHashes()
    {
        var empty = TrieNode.CreateEmpty();
        var leaf = TrieNode.CreateLeaf(NibblePath.FromKey([0x01]), [0xAA]);
        var branch = TrieNode.CreateBranch();

        var hashes = new[]
        {
            empty.ComputeHash(),
            leaf.ComputeHash(),
            branch.ComputeHash(),
        };

        hashes.Distinct().Should().HaveCount(3);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static byte[] CreateTestBytes(int length, byte seed)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)(seed + i);
        return bytes;
    }
}
