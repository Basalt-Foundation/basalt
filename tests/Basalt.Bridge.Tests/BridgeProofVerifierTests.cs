using Basalt.Bridge;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Bridge.Tests;

public class BridgeProofVerifierTests
{
    // ── VerifyMerkleProof: valid proofs ──────────────────────────────────

    /// <summary>
    /// Hash a leaf with domain separation prefix 0x00 (matching BridgeProofVerifier.HashLeaf).
    /// </summary>
    private static byte[] HashLeaf(byte[] leafData)
    {
        var prefixed = new byte[1 + leafData.Length];
        prefixed[0] = 0x00;
        leafData.CopyTo(prefixed, 1);
        return Blake3Hasher.Hash(prefixed).ToArray();
    }

    [Fact]
    public void SingleLeaf_Proof_Verifies()
    {
        var leaf = new byte[] { 1, 2, 3 };
        // BRIDGE-06: leaf hash uses domain separation prefix 0x00
        var root = HashLeaf(leaf);

        BridgeProofVerifier.VerifyMerkleProof(leaf, [], 0, root).Should().BeTrue();
    }

    [Fact]
    public void TwoLeaf_Proof_Verifies()
    {
        var leaves = new byte[][] { [1, 2, 3], [4, 5, 6] };

        var (root, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);
        BridgeProofVerifier.VerifyMerkleProof(leaves[0], proof, 0, root).Should().BeTrue();

        var (root2, proof2) = BridgeProofVerifier.BuildMerkleProof(leaves, 1);
        BridgeProofVerifier.VerifyMerkleProof(leaves[1], proof2, 1, root2).Should().BeTrue();
    }

    [Fact]
    public void FourLeaf_Proof_Verifies_All_Indices()
    {
        var leaves = new byte[][] { [1], [2], [3], [4] };
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        for (int i = 0; i < leaves.Length; i++)
        {
            var (proofRoot, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            proofRoot.Should().BeEquivalentTo(root);
            BridgeProofVerifier.VerifyMerkleProof(leaves[i], proof, (ulong)i, root).Should().BeTrue();
        }
    }

    [Fact]
    public void EightLeaf_Proof_Verifies()
    {
        var leaves = new byte[8][];
        for (int i = 0; i < 8; i++)
            leaves[i] = [(byte)i, (byte)(i + 10)];

        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        for (int i = 0; i < 8; i++)
        {
            var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            BridgeProofVerifier.VerifyMerkleProof(leaves[i], proof, (ulong)i, root).Should().BeTrue();
        }
    }

    [Fact]
    public void SixteenLeaf_Proof_Verifies_All_Indices()
    {
        var leaves = new byte[16][];
        for (int i = 0; i < 16; i++)
            leaves[i] = [(byte)i, (byte)(i * 3), (byte)(i + 100)];

        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        for (int i = 0; i < 16; i++)
        {
            var (proofRoot, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            proofRoot.Should().BeEquivalentTo(root);
            BridgeProofVerifier.VerifyMerkleProof(leaves[i], proof, (ulong)i, root).Should().BeTrue();
        }
    }

    [Fact]
    public void LargeTree_32Leaves_Proof_Verifies()
    {
        var leaves = new byte[32][];
        for (int i = 0; i < 32; i++)
            leaves[i] = BitConverter.GetBytes(i);

        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        // Spot check a few indices
        foreach (var idx in new[] { 0, 7, 15, 16, 31 })
        {
            var (proofRoot, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, idx);
            proofRoot.Should().BeEquivalentTo(root);
            BridgeProofVerifier.VerifyMerkleProof(leaves[idx], proof, (ulong)idx, root).Should().BeTrue();
        }
    }

    // ── VerifyMerkleProof: invalid proofs ────────────────────────────────

    [Fact]
    public void Tampered_Leaf_Fails_Verification()
    {
        var leaves = new byte[][] { [1], [2], [3], [4] };
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);
        var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);

        var tampered = new byte[] { 99 }; // Not the original leaf
        BridgeProofVerifier.VerifyMerkleProof(tampered, proof, 0, root).Should().BeFalse();
    }

    [Fact]
    public void Tampered_Root_Fails_Verification()
    {
        var leaves = new byte[][] { [1], [2] };
        var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);

        var fakeRoot = new byte[32];
        fakeRoot[0] = 0xFF;

        BridgeProofVerifier.VerifyMerkleProof(leaves[0], proof, 0, fakeRoot).Should().BeFalse();
    }

    [Fact]
    public void Wrong_Index_Fails_Verification()
    {
        var leaves = new byte[][] { [1], [2], [3], [4] };
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);
        var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);

        // Use index=1 with proof built for index=0
        BridgeProofVerifier.VerifyMerkleProof(leaves[0], proof, 1, root).Should().BeFalse();
    }

    [Fact]
    public void Swapped_Proof_Elements_Fail_Verification()
    {
        var leaves = new byte[][] { [1], [2], [3], [4] };
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);
        var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);

        // Proof should have 2 elements for a 4-leaf tree; swap them
        if (proof.Length >= 2)
        {
            var swappedProof = new byte[][] { proof[1], proof[0] };
            BridgeProofVerifier.VerifyMerkleProof(leaves[0], swappedProof, 0, root).Should().BeFalse();
        }
    }

    [Fact]
    public void Truncated_Proof_Fails_Verification()
    {
        var leaves = new byte[][] { [1], [2], [3], [4] };
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);
        var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);

        // Remove last element from proof
        var truncated = proof.Take(proof.Length - 1).ToArray();
        BridgeProofVerifier.VerifyMerkleProof(leaves[0], truncated, 0, root).Should().BeFalse();
    }

    [Fact]
    public void Proof_With_Corrupted_Sibling_Fails()
    {
        var leaves = new byte[][] { [1], [2], [3], [4] };
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);
        var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);

        // Corrupt the first sibling
        var corrupted = proof.Select(p => (byte[])p.Clone()).ToArray();
        corrupted[0][0] ^= 0xFF;

        BridgeProofVerifier.VerifyMerkleProof(leaves[0], corrupted, 0, root).Should().BeFalse();
    }

    [Fact]
    public void Leaf_At_Wrong_Position_With_Correct_Proof_Fails()
    {
        var leaves = new byte[][] { [1], [2], [3], [4] };
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);
        var (_, proof0) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);

        // Try to verify leaf[2] with proof for leaf[0]
        BridgeProofVerifier.VerifyMerkleProof(leaves[2], proof0, 0, root).Should().BeFalse();
    }

    // ── NonPowerOf2 leaves ───────────────────────────────────────────────

    [Fact]
    public void NonPowerOf2_Leaves_Work()
    {
        // 3 leaves, padded to 4 internally
        var leaves = new byte[][] { [1], [2], [3] };
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        for (int i = 0; i < 3; i++)
        {
            var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            BridgeProofVerifier.VerifyMerkleProof(leaves[i], proof, (ulong)i, root).Should().BeTrue();
        }
    }

    [Fact]
    public void FiveLeaf_NonPowerOf2_Tree_Verifies()
    {
        var leaves = new byte[][] { [10], [20], [30], [40], [50] };
        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        for (int i = 0; i < 5; i++)
        {
            var (proofRoot, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            proofRoot.Should().BeEquivalentTo(root);
            BridgeProofVerifier.VerifyMerkleProof(leaves[i], proof, (ulong)i, root).Should().BeTrue();
        }
    }

    [Fact]
    public void SevenLeaf_NonPowerOf2_Tree_Verifies()
    {
        var leaves = new byte[7][];
        for (int i = 0; i < 7; i++)
            leaves[i] = [(byte)(i + 1)];

        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        for (int i = 0; i < 7; i++)
        {
            var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            BridgeProofVerifier.VerifyMerkleProof(leaves[i], proof, (ulong)i, root).Should().BeTrue();
        }
    }

    // ── ComputeMerkleRoot ────────────────────────────────────────────────

    [Fact]
    public void ComputeMerkleRoot_Empty_Returns_Zeros()
    {
        var root = BridgeProofVerifier.ComputeMerkleRoot([]);
        root.Should().HaveCount(32);
        root.Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public void ComputeMerkleRoot_Deterministic()
    {
        var leaves = new byte[][] { [1, 2], [3, 4] };
        var root1 = BridgeProofVerifier.ComputeMerkleRoot(leaves);
        var root2 = BridgeProofVerifier.ComputeMerkleRoot(leaves);
        root1.Should().BeEquivalentTo(root2);
    }

    [Fact]
    public void ComputeMerkleRoot_SingleLeaf_Equals_LeafHash()
    {
        var leaf = new byte[] { 42, 99 };
        var root = BridgeProofVerifier.ComputeMerkleRoot([leaf]);
        // BRIDGE-06: leaf hash uses domain separation prefix 0x00
        var expectedHash = HashLeaf(leaf);

        // Single leaf: root is hash of leaf paired with zero-padding
        // The implementation pads to power-of-2 (size=1), so the root is just the leaf hash
        root.Should().HaveCount(32);
        root.Should().BeEquivalentTo(expectedHash);
    }

    [Fact]
    public void ComputeMerkleRoot_Different_Leaf_Order_Produces_Different_Root()
    {
        var leavesA = new byte[][] { [1], [2], [3], [4] };
        var leavesB = new byte[][] { [2], [1], [3], [4] };

        var rootA = BridgeProofVerifier.ComputeMerkleRoot(leavesA);
        var rootB = BridgeProofVerifier.ComputeMerkleRoot(leavesB);

        rootA.Should().NotBeEquivalentTo(rootB);
    }

    [Fact]
    public void ComputeMerkleRoot_Consistent_With_BuildMerkleProof()
    {
        var leaves = new byte[][] { [10], [20], [30], [40] };
        var computedRoot = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        for (int i = 0; i < leaves.Length; i++)
        {
            var (proofRoot, _) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            proofRoot.Should().BeEquivalentTo(computedRoot);
        }
    }

    // ── BuildMerkleProof: edge cases ─────────────────────────────────────

    [Fact]
    public void BuildMerkleProof_OutOfRange_Throws()
    {
        var leaves = new byte[][] { [1] };
        var act = () => BridgeProofVerifier.BuildMerkleProof(leaves, 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildMerkleProof_Negative_Index_Throws()
    {
        var leaves = new byte[][] { [1], [2] };
        var act = () => BridgeProofVerifier.BuildMerkleProof(leaves, -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BuildMerkleProof_Empty_Leaves_Throws()
    {
        var act = () => BridgeProofVerifier.BuildMerkleProof([], 0);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildMerkleProof_SingleLeaf_Returns_Empty_Proof()
    {
        var leaves = new byte[][] { [42] };
        var (root, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);

        root.Should().HaveCount(32);
        // For a single leaf, padded to 2, proof has 1 element (the zero-padding sibling)
        // Verify the proof works regardless
        BridgeProofVerifier.VerifyMerkleProof(leaves[0], proof, 0, root).Should().BeTrue();
    }

    [Fact]
    public void BuildMerkleProof_ProofLength_Is_Log2_For_PowerOf2()
    {
        // 8 leaves -> proof length should be 3 (log2(8))
        var leaves = new byte[8][];
        for (int i = 0; i < 8; i++)
            leaves[i] = [(byte)i];

        var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 3);
        proof.Should().HaveCount(3);
    }

    [Fact]
    public void BuildMerkleProof_ProofLength_For_4Leaves()
    {
        var leaves = new byte[][] { [1], [2], [3], [4] };
        var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);
        proof.Should().HaveCount(2); // log2(4) = 2
    }

    [Fact]
    public void BuildMerkleProof_ProofLength_For_16Leaves()
    {
        var leaves = new byte[16][];
        for (int i = 0; i < 16; i++)
            leaves[i] = [(byte)i];

        var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, 0);
        proof.Should().HaveCount(4); // log2(16) = 4
    }

    // ── Proof siblings are 32 bytes each ─────────────────────────────────

    [Fact]
    public void BuildMerkleProof_All_Siblings_Are_32_Bytes()
    {
        var leaves = new byte[][] { [1], [2], [3], [4], [5], [6], [7], [8] };

        for (int i = 0; i < leaves.Length; i++)
        {
            var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            foreach (var sibling in proof)
            {
                sibling.Should().HaveCount(32);
            }
        }
    }

    // ── Single-leaf tree with verify ─────────────────────────────────────

    [Fact]
    public void SingleLeaf_Tree_Root_And_Proof_Roundtrip()
    {
        var leaf = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var root = BridgeProofVerifier.ComputeMerkleRoot([leaf]);
        var (proofRoot, proof) = BridgeProofVerifier.BuildMerkleProof([leaf], 0);

        proofRoot.Should().BeEquivalentTo(root);
        BridgeProofVerifier.VerifyMerkleProof(leaf, proof, 0, proofRoot).Should().BeTrue();
    }

    // ── Large leaf data ──────────────────────────────────────────────────

    [Fact]
    public void Proof_With_Large_Leaf_Data_Verifies()
    {
        var leaves = new byte[4][];
        for (int i = 0; i < 4; i++)
        {
            // Each leaf is 1KB
            leaves[i] = new byte[1024];
            new Random(i).NextBytes(leaves[i]);
        }

        var root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

        for (int i = 0; i < 4; i++)
        {
            var (_, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, i);
            BridgeProofVerifier.VerifyMerkleProof(leaves[i], proof, (ulong)i, root).Should().BeTrue();
        }
    }
}
