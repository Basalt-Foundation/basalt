using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Bridge;

/// <summary>
/// Verifies Merkle proofs for cross-chain bridge messages.
/// Used to validate that a deposit/withdrawal actually occurred on the source chain.
/// </summary>
public static class BridgeProofVerifier
{
    /// <summary>
    /// Verify a Merkle proof that a leaf is included in the tree with the given root.
    /// Uses BLAKE3 hashing consistent with Basalt's state trie.
    /// </summary>
    /// <param name="leaf">The leaf data (hashed deposit/withdrawal).</param>
    /// <param name="proof">Sibling hashes along the path to the root.</param>
    /// <param name="index">Position index of the leaf (determines left/right at each level).</param>
    /// <param name="root">Expected Merkle root.</param>
    /// <returns>True if the proof is valid.</returns>
    public static bool VerifyMerkleProof(byte[] leaf, byte[][] proof, ulong index, byte[] root)
    {
        if (proof.Length == 0 && root.Length == 32)
        {
            // Single leaf tree — leaf hash should equal root
            var leafHash = Blake3Hasher.Hash(leaf).ToArray();
            return leafHash.AsSpan().SequenceEqual(root);
        }

        var currentHash = Blake3Hasher.Hash(leaf).ToArray();

        for (int i = 0; i < proof.Length; i++)
        {
            var sibling = proof[i];
            if ((index & 1) == 0)
            {
                // Current is left child, sibling is right
                currentHash = HashPair(currentHash, sibling);
            }
            else
            {
                // Current is right child, sibling is left
                currentHash = HashPair(sibling, currentHash);
            }
            index >>= 1;
        }

        return currentHash.AsSpan().SequenceEqual(root);
    }

    /// <summary>
    /// Build a Merkle tree from a list of leaves and return the root + proof for a specific index.
    /// </summary>
    public static (byte[] Root, byte[][] Proof) BuildMerkleProof(byte[][] leaves, int leafIndex)
    {
        if (leaves.Length == 0)
            throw new ArgumentException("Cannot build proof from empty leaves");

        if (leafIndex < 0 || leafIndex >= leaves.Length)
            throw new ArgumentOutOfRangeException(nameof(leafIndex));

        // Hash all leaves
        var hashes = leaves.Select(l => Blake3Hasher.Hash(l).ToArray()).ToArray();

        // Pad to power of 2
        var size = 1;
        while (size < hashes.Length)
            size <<= 1;

        var padded = new byte[size][];
        for (int i = 0; i < size; i++)
            padded[i] = i < hashes.Length ? hashes[i] : new byte[32];

        var proof = new List<byte[]>();
        var idx = leafIndex;

        while (padded.Length > 1)
        {
            // Record sibling
            var siblingIdx = idx ^ 1;
            if (siblingIdx < padded.Length)
                proof.Add(padded[siblingIdx]);

            // Compute next level
            var nextLevel = new byte[padded.Length / 2][];
            for (int i = 0; i < nextLevel.Length; i++)
                nextLevel[i] = HashPair(padded[2 * i], padded[2 * i + 1]);

            padded = nextLevel;
            idx >>= 1;
        }

        return (padded[0], proof.ToArray());
    }

    /// <summary>
    /// Compute the Merkle root of a list of leaves.
    /// </summary>
    public static byte[] ComputeMerkleRoot(byte[][] leaves)
    {
        if (leaves.Length == 0)
            return new byte[32];

        var hashes = leaves.Select(l => Blake3Hasher.Hash(l).ToArray()).ToArray();

        // Pad to power of 2
        var size = 1;
        while (size < hashes.Length)
            size <<= 1;

        var current = new byte[size][];
        for (int i = 0; i < size; i++)
            current[i] = i < hashes.Length ? hashes[i] : new byte[32];

        while (current.Length > 1)
        {
            var next = new byte[current.Length / 2][];
            for (int i = 0; i < next.Length; i++)
                next[i] = HashPair(current[2 * i], current[2 * i + 1]);
            current = next;
        }

        return current[0];
    }

    private static byte[] HashPair(byte[] left, byte[] right)
    {
        var combined = new byte[left.Length + right.Length];
        left.CopyTo(combined, 0);
        right.CopyTo(combined, left.Length);
        return Blake3Hasher.Hash(combined).ToArray();
    }
}
