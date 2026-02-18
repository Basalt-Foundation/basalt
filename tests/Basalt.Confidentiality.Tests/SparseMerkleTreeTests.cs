using System.Text;
using Basalt.Confidentiality;
using Basalt.Core;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Confidentiality.Tests;

public class SparseMerkleTreeTests
{
    /// <summary>
    /// Helper: produce a deterministic Hash256 key from a human-readable label.
    /// </summary>
    private static Hash256 Key(string label) =>
        Blake3Hasher.Hash(Encoding.UTF8.GetBytes(label));

    // ── 1. Empty tree root determinism ──────────────────────────────────────

    [Fact]
    public void EmptyTree_HasDeterministicRoot()
    {
        var tree1 = new SparseMerkleTree();
        var tree2 = new SparseMerkleTree();

        tree1.Root.Should().Be(tree2.Root,
            "two freshly created trees must produce the same root");

        // The empty root must not be all-zeros (it is built from iterated hashing).
        tree1.Root.Should().NotBe(Hash256.Zero,
            "the empty-tree root is a chain of hashes, not raw zeros");

        tree1.Count.Should().Be(0);
    }

    // ── 2. Insert changes root ──────────────────────────────────────────────

    [Fact]
    public void Insert_ChangesRoot()
    {
        var tree = new SparseMerkleTree();
        Hash256 emptyRoot = tree.Root;

        tree.Insert(Key("alice"));

        tree.Root.Should().NotBe(emptyRoot,
            "inserting a key must change the Merkle root");
        tree.Count.Should().Be(1);
    }

    // ── 3. Double insert is idempotent ──────────────────────────────────────

    [Fact]
    public void Insert_Same_Key_Twice_IsIdempotent()
    {
        var tree = new SparseMerkleTree();
        Hash256 key = Key("bob");

        tree.Insert(key);
        Hash256 rootAfterFirst = tree.Root;

        tree.Insert(key);
        Hash256 rootAfterSecond = tree.Root;

        rootAfterSecond.Should().Be(rootAfterFirst,
            "inserting the same key twice must not change the root");
        tree.Count.Should().Be(1);
    }

    // ── 4. Delete restores original root ────────────────────────────────────

    [Fact]
    public void Delete_ReturnsToOriginalRoot()
    {
        var tree = new SparseMerkleTree();
        Hash256 emptyRoot = tree.Root;

        Hash256 key = Key("carol");
        tree.Insert(key);
        tree.Root.Should().NotBe(emptyRoot);

        tree.Delete(key);

        tree.Root.Should().Be(emptyRoot,
            "deleting the only key must restore the empty-tree root");
        tree.Count.Should().Be(0);
    }

    // ── 5. Delete nonexistent key is no-op ──────────────────────────────────

    [Fact]
    public void Delete_NonExistent_IsNoOp()
    {
        var tree = new SparseMerkleTree();
        Hash256 emptyRoot = tree.Root;

        tree.Delete(Key("ghost"));

        tree.Root.Should().Be(emptyRoot,
            "deleting a non-existent key must not change the root");
        tree.Count.Should().Be(0);
    }

    // ── 6. Contains returns true for inserted ───────────────────────────────

    [Fact]
    public void Contains_ReturnsTrue_ForInserted()
    {
        var tree = new SparseMerkleTree();
        Hash256 key = Key("dave");

        tree.Insert(key);

        tree.Contains(key).Should().BeTrue();
    }

    // ── 7. Contains returns false for deleted ───────────────────────────────

    [Fact]
    public void Contains_ReturnsFalse_ForDeleted()
    {
        var tree = new SparseMerkleTree();
        Hash256 key = Key("eve");

        tree.Insert(key);
        tree.Contains(key).Should().BeTrue();

        tree.Delete(key);
        tree.Contains(key).Should().BeFalse();
    }

    // ── 8. Membership proof generation and verification ─────────────────────

    [Fact]
    public void GenerateProof_Membership_Verifies()
    {
        var tree = new SparseMerkleTree();
        Hash256 key = Key("frank");
        tree.Insert(key);

        SparseMerkleProof proof = tree.GenerateProof(key);

        proof.IsIncluded.Should().BeTrue();
        proof.Siblings.Should().HaveCount(tree.Depth);

        bool valid = SparseMerkleTree.VerifyMembership(tree.Root, key, proof);
        valid.Should().BeTrue("the membership proof for an inserted key must verify");
    }

    // ── 9. Non-membership proof generation and verification ─────────────────

    [Fact]
    public void GenerateProof_NonMembership_Verifies()
    {
        var tree = new SparseMerkleTree();
        // Insert a different key so the tree is non-empty.
        tree.Insert(Key("grace"));

        Hash256 absentKey = Key("heidi");
        SparseMerkleProof proof = tree.GenerateProof(absentKey);

        proof.IsIncluded.Should().BeFalse();
        proof.Siblings.Should().HaveCount(tree.Depth);

        bool valid = SparseMerkleTree.VerifyNonMembership(tree.Root, absentKey, proof);
        valid.Should().BeTrue("the non-membership proof for an absent key must verify");
    }

    // ── 10. Membership proof fails against wrong root ───────────────────────

    [Fact]
    public void VerifyMembership_FailsForWrongRoot()
    {
        var tree = new SparseMerkleTree();
        Hash256 key = Key("ivan");
        tree.Insert(key);

        SparseMerkleProof proof = tree.GenerateProof(key);

        // Use a fabricated root that differs from the real one.
        Hash256 wrongRoot = Key("wrong-root");

        bool valid = SparseMerkleTree.VerifyMembership(wrongRoot, key, proof);
        valid.Should().BeFalse("membership proof must not verify against the wrong root");
    }

    // ── 11. Non-membership proof fails for key that is present ──────────────

    [Fact]
    public void VerifyNonMembership_FailsForPresentKey()
    {
        var tree = new SparseMerkleTree();
        Hash256 key = Key("judy");
        tree.Insert(key);

        // Generate a membership proof (IsIncluded = true).
        SparseMerkleProof membershipProof = tree.GenerateProof(key);

        // VerifyNonMembership must reject it because IsIncluded is true.
        bool valid = SparseMerkleTree.VerifyNonMembership(tree.Root, key, membershipProof);
        valid.Should().BeFalse(
            "non-membership verification must fail when the proof indicates inclusion");
    }

    // ── 12. Multiple inserts, all membership proofs verify ──────────────────

    [Fact]
    public void MultipleInserts_AllVerify()
    {
        var tree = new SparseMerkleTree();
        Hash256[] keys = new[]
        {
            Key("key-0"),
            Key("key-1"),
            Key("key-2"),
            Key("key-3"),
            Key("key-4"),
        };

        foreach (Hash256 k in keys)
            tree.Insert(k);

        tree.Count.Should().Be(5);

        Hash256 root = tree.Root;

        foreach (Hash256 k in keys)
        {
            SparseMerkleProof proof = tree.GenerateProof(k);
            proof.IsIncluded.Should().BeTrue();
            SparseMerkleTree.VerifyMembership(root, k, proof).Should().BeTrue(
                $"membership proof for {k} must verify");
        }

        // Also verify a key that was NOT inserted gives a valid non-membership proof.
        Hash256 absent = Key("key-absent");
        SparseMerkleProof absentProof = tree.GenerateProof(absent);
        absentProof.IsIncluded.Should().BeFalse();
        SparseMerkleTree.VerifyNonMembership(root, absent, absentProof).Should().BeTrue();
    }

    // ── 13. Insert / delete / insert — proofs work ──────────────────────────

    [Fact]
    public void Insert_Delete_Insert_ProofsWork()
    {
        var tree = new SparseMerkleTree();

        Hash256 keyA = Key("alpha");
        Hash256 keyB = Key("bravo");

        // Insert A.
        tree.Insert(keyA);
        tree.Contains(keyA).Should().BeTrue();

        // Delete A.
        tree.Delete(keyA);
        tree.Contains(keyA).Should().BeFalse();

        // Insert B.
        tree.Insert(keyB);
        tree.Contains(keyB).Should().BeTrue();
        tree.Count.Should().Be(1);

        Hash256 root = tree.Root;

        // B membership proof must verify.
        SparseMerkleProof proofB = tree.GenerateProof(keyB);
        proofB.IsIncluded.Should().BeTrue();
        SparseMerkleTree.VerifyMembership(root, keyB, proofB).Should().BeTrue(
            "B was inserted and should have a valid membership proof");

        // A non-membership proof must verify.
        SparseMerkleProof proofA = tree.GenerateProof(keyA);
        proofA.IsIncluded.Should().BeFalse();
        SparseMerkleTree.VerifyNonMembership(root, keyA, proofA).Should().BeTrue(
            "A was deleted and should have a valid non-membership proof");
    }

    // ── 14. Shallow tree (depth = 8) works correctly ────────────────────────

    [Fact]
    public void SmallDepthTree_WorksCorrectly()
    {
        const int depth = 8;
        var tree = new SparseMerkleTree(depth);

        tree.Depth.Should().Be(depth);

        // Empty root must be deterministic and non-zero.
        Hash256 emptyRoot = tree.Root;
        emptyRoot.Should().NotBe(Hash256.Zero);

        // Another instance with the same depth must produce the same empty root.
        new SparseMerkleTree(depth).Root.Should().Be(emptyRoot);

        // Insert a key and verify membership.
        Hash256 key = Key("shallow-key");
        tree.Insert(key);
        tree.Count.Should().Be(1);
        tree.Root.Should().NotBe(emptyRoot);

        SparseMerkleProof proof = tree.GenerateProof(key);
        proof.IsIncluded.Should().BeTrue();
        proof.Siblings.Should().HaveCount(depth);
        SparseMerkleTree.VerifyMembership(tree.Root, key, proof).Should().BeTrue();

        // Non-membership for absent key.
        Hash256 absent = Key("shallow-absent");
        SparseMerkleProof absentProof = tree.GenerateProof(absent);
        absentProof.IsIncluded.Should().BeFalse();
        absentProof.Siblings.Should().HaveCount(depth);
        SparseMerkleTree.VerifyNonMembership(tree.Root, absent, absentProof).Should().BeTrue();

        // Delete and confirm root returns to empty.
        tree.Delete(key);
        tree.Root.Should().Be(emptyRoot);
        tree.Count.Should().Be(0);
    }
}
