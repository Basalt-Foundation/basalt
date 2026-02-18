using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Confidentiality;

/// <summary>
/// Proof structure for a Sparse Merkle Tree query.
/// Contains the sibling hashes along the path from the leaf to the root,
/// and a flag indicating whether the queried key is present in the tree.
/// </summary>
public sealed class SparseMerkleProof
{
    /// <summary>
    /// Sibling hashes along the path from the leaf to the root.
    /// <c>Siblings[0]</c> is the sibling at the deepest level (leaf level).
    /// <c>Siblings[depth - 1]</c> is the sibling at the level just below the root.
    /// The ordering matches the bottom-up verification traversal.
    /// </summary>
    public Hash256[] Siblings { get; }

    /// <summary>
    /// Whether the queried key exists in the tree (membership proof)
    /// or does not exist (non-membership proof).
    /// </summary>
    public bool IsIncluded { get; }

    public SparseMerkleProof(Hash256[] siblings, bool isIncluded)
    {
        Siblings = siblings ?? throw new ArgumentNullException(nameof(siblings));
        IsIncluded = isIncluded;
    }
}

/// <summary>
/// A compact (lazy) Sparse Merkle Tree for credential revocation.
///
/// Only non-default subtrees are stored in memory. Default hashes at each
/// level are pre-computed so that empty subtrees are represented implicitly
/// without allocating 2^256 nodes.
///
/// Issuers maintain their tree off-chain and publish the root on-chain.
/// ZK proofs can include proof of non-membership to demonstrate that a
/// credential has not been revoked.
///
/// <para><b>Hashing (BLAKE3):</b></para>
/// <list type="bullet">
///   <item>Leaf hash for a present key: BLAKE3(key_bytes)</item>
///   <item>Internal node hash: BLAKE3(left_child || right_child)</item>
///   <item>Default (absent) leaf value: <see cref="Hash256.Zero"/></item>
/// </list>
///
/// <para><b>Tree layout:</b></para>
/// The key bits are read MSB-first (bit 0 = MSB of byte 0). Bit 0 determines
/// the left/right branch at the root; bit (depth-1) determines the branch at
/// the deepest internal level, just above the leaves.
/// </summary>
public sealed class SparseMerkleTree
{
    private readonly int _depth;

    /// <summary>
    /// Pre-computed default hashes for each tree level.
    /// <c>_defaults[0]</c> = <see cref="Hash256.Zero"/> (default leaf).
    /// <c>_defaults[i]</c> = BLAKE3(_defaults[i-1] || _defaults[i-1]).
    /// <c>_defaults[_depth]</c> = root of a completely empty tree.
    /// Index i represents a subtree of height i (i.e., i levels below it).
    /// </summary>
    private readonly Hash256[] _defaults;

    /// <summary>
    /// Sparse node storage. Key = "level:hex_prefix" where level is the height
    /// above the leaves (0 = leaf, _depth = root) and hex_prefix encodes the
    /// path bits that identify a node at that level.
    /// Only nodes whose hash differs from the default are stored.
    /// </summary>
    private readonly Dictionary<string, Hash256> _nodes;

    /// <summary>
    /// Set of keys currently present in the tree for O(1) membership checks.
    /// </summary>
    private readonly HashSet<Hash256> _leaves;

    /// <summary>
    /// Gets the current Merkle root of the tree.
    /// For an empty tree this equals the pre-computed default root.
    /// </summary>
    public Hash256 Root
    {
        get
        {
            string rootKey = MakeNodeKey(_depth, ReadOnlySpan<byte>.Empty);
            return _nodes.TryGetValue(rootKey, out Hash256 v) ? v : _defaults[_depth];
        }
    }

    /// <summary>
    /// Gets the configured depth (number of levels from root to leaf) of the tree.
    /// </summary>
    public int Depth => _depth;

    /// <summary>
    /// Gets the number of keys currently stored in the tree.
    /// </summary>
    public int Count => _leaves.Count;

    /// <summary>
    /// Creates a new Sparse Merkle Tree.
    /// </summary>
    /// <param name="depth">
    /// Tree depth (number of levels from root to leaf).
    /// Defaults to 256 for bit-level addressing on 32-byte keys.
    /// Must be between 1 and 256 inclusive.
    /// </param>
    public SparseMerkleTree(int depth = 256)
    {
        if (depth < 1 || depth > 256)
            throw new ArgumentOutOfRangeException(nameof(depth), depth,
                "Tree depth must be between 1 and 256 inclusive.");

        _depth = depth;
        _nodes = new Dictionary<string, Hash256>();
        _leaves = new HashSet<Hash256>();

        // Pre-compute default hashes bottom-up.
        _defaults = new Hash256[depth + 1];
        _defaults[0] = Hash256.Zero;
        for (int i = 1; i <= depth; i++)
        {
            _defaults[i] = Blake3Hasher.HashPair(_defaults[i - 1], _defaults[i - 1]);
        }
    }

    /// <summary>
    /// Insert a key into the tree, marking the corresponding leaf as present.
    /// The leaf value is set to BLAKE3(key) to prevent second-preimage attacks.
    /// If the key is already present, this is a no-op.
    /// </summary>
    /// <param name="key">The 32-byte key to insert.</param>
    public void Insert(Hash256 key)
    {
        if (!_leaves.Add(key))
            return; // Already present.

        byte[] keyBytes = key.ToArray();
        Hash256 leafHash = Blake3Hasher.Hash(keyBytes);

        // Set the leaf node (level 0, full path = all _depth bits).
        SetOrRemoveNode(0, keyBytes, 0, _depth, leafHash);

        // Update all ancestors bottom-up.
        UpdateAncestors(keyBytes);
    }

    /// <summary>
    /// Delete a key from the tree, resetting the corresponding leaf to the default value.
    /// If the key is not present, this is a no-op.
    /// </summary>
    /// <param name="key">The 32-byte key to delete.</param>
    public void Delete(Hash256 key)
    {
        if (!_leaves.Remove(key))
            return; // Not present.

        byte[] keyBytes = key.ToArray();

        // Reset the leaf to default (remove from sparse store).
        SetOrRemoveNode(0, keyBytes, 0, _depth, Hash256.Zero);

        // Update all ancestors bottom-up.
        UpdateAncestors(keyBytes);
    }

    /// <summary>
    /// Check whether a key is present in the tree.
    /// </summary>
    /// <param name="key">The 32-byte key to look up.</param>
    /// <returns><c>true</c> if the key has been inserted and not deleted.</returns>
    public bool Contains(Hash256 key) => _leaves.Contains(key);

    /// <summary>
    /// Generate a Merkle proof for the given key.
    /// The proof contains sibling hashes along the path and an inclusion flag.
    /// Works for both membership and non-membership proofs.
    /// </summary>
    /// <param name="key">The 32-byte key to generate a proof for.</param>
    /// <returns>A <see cref="SparseMerkleProof"/> that can be verified against the root.</returns>
    public SparseMerkleProof GenerateProof(Hash256 key)
    {
        byte[] keyBytes = key.ToArray();
        var siblings = new Hash256[_depth];

        // Walk from the leaf (level 0) upward to just below the root (level _depth - 1).
        // At each level, collect the sibling's hash.
        //
        // At level L (height above leaf), the node is identified by a prefix of
        // (_depth - L) bits. The distinguishing bit between the two children of
        // the node at level (L+1) is bit index (_depth - L - 1).
        //
        // siblings[0] = sibling at the leaf level (deepest).
        // siblings[_depth - 1] = sibling of the root's two children.
        for (int i = 0; i < _depth; i++)
        {
            // i corresponds to level i. The node at level i is identified by the
            // first (_depth - i) bits. The sibling differs at bit index (_depth - i - 1).
            int bitIndex = _depth - i - 1;
            int siblingBit = 1 - GetBit(keyBytes, bitIndex);

            // The sibling path: same prefix bits, but bit at bitIndex is flipped.
            siblings[i] = GetNodeWithFlippedBit(i, keyBytes, bitIndex, siblingBit);
        }

        bool isIncluded = _leaves.Contains(key);
        return new SparseMerkleProof(siblings, isIncluded);
    }

    /// <summary>
    /// Verify that a key IS present in the tree with the given root.
    /// Reconstructs the root from BLAKE3(key) and the sibling path,
    /// then compares to the expected root.
    /// </summary>
    /// <param name="root">The expected Merkle root.</param>
    /// <param name="key">The key claimed to be a member.</param>
    /// <param name="proof">The Sparse Merkle proof.</param>
    /// <returns><c>true</c> if the proof is valid and the key is a member.</returns>
    public static bool VerifyMembership(Hash256 root, Hash256 key, SparseMerkleProof proof)
    {
        if (proof is null || !proof.IsIncluded || proof.Siblings is null)
            return false;

        int depth = proof.Siblings.Length;
        if (depth < 1 || depth > 256)
            return false;

        Hash256 reconstructed = ReconstructRoot(key, Blake3Hasher.Hash(key.ToArray()), proof.Siblings, depth);
        return reconstructed == root;
    }

    /// <summary>
    /// Verify that a key is NOT present in the tree with the given root.
    /// Reconstructs the root from the default leaf value (<see cref="Hash256.Zero"/>)
    /// and the sibling path, then compares to the expected root.
    /// </summary>
    /// <param name="root">The expected Merkle root.</param>
    /// <param name="key">The key claimed to be absent.</param>
    /// <param name="proof">The Sparse Merkle proof.</param>
    /// <returns><c>true</c> if the proof is valid and the key is not a member.</returns>
    public static bool VerifyNonMembership(Hash256 root, Hash256 key, SparseMerkleProof proof)
    {
        if (proof is null || proof.IsIncluded || proof.Siblings is null)
            return false;

        int depth = proof.Siblings.Length;
        if (depth < 1 || depth > 256)
            return false;

        Hash256 reconstructed = ReconstructRoot(key, Hash256.Zero, proof.Siblings, depth);
        return reconstructed == root;
    }

    // -----------------------------------------------------------------------
    // Static verification helper
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reconstruct the Merkle root by hashing the leaf value upward through
    /// the sibling path. The path direction at each level is determined by the
    /// key bits, read in reverse order (deepest bit first).
    /// </summary>
    private static Hash256 ReconstructRoot(Hash256 key, Hash256 leafValue, Hash256[] siblings, int depth)
    {
        byte[] keyBytes = key.ToArray();
        Hash256 current = leafValue;

        for (int i = 0; i < depth; i++)
        {
            // At proof index i (level i, height above leaf), the distinguishing
            // bit is at position (depth - i - 1) in the key.
            int bitIndex = depth - i - 1;
            int bit = GetBit(keyBytes, bitIndex);

            if (bit == 0)
                current = Blake3Hasher.HashPair(current, siblings[i]);
            else
                current = Blake3Hasher.HashPair(siblings[i], current);
        }

        return current;
    }

    // -----------------------------------------------------------------------
    // Internal tree operations
    // -----------------------------------------------------------------------

    /// <summary>
    /// After changing a leaf, recompute all ancestor nodes along the path
    /// from the leaf up to the root.
    /// </summary>
    private void UpdateAncestors(byte[] keyBytes)
    {
        for (int level = 1; level <= _depth; level++)
        {
            // A node at 'level' is the parent of two children at (level - 1).
            // It is identified by the first (_depth - level) bits of the path.
            // Its children share those bits but differ at bit index (_depth - level),
            // which is the ((_depth - level))-th bit from MSB.
            int splitBitIndex = _depth - level;

            Hash256 leftChild = GetNodeByPrefix(level - 1, keyBytes, splitBitIndex, 0);
            Hash256 rightChild = GetNodeByPrefix(level - 1, keyBytes, splitBitIndex, 1);
            Hash256 parentHash = Blake3Hasher.HashPair(leftChild, rightChild);

            // Store or remove the parent node.
            int parentPrefixLen = _depth - level;
            string parentKey = MakeNodeKey(level, keyBytes.AsSpan(), 0, parentPrefixLen);

            if (parentHash == _defaults[level])
                _nodes.Remove(parentKey);
            else
                _nodes[parentKey] = parentHash;
        }
    }

    /// <summary>
    /// Get the hash of the node at the given level whose path matches keyBytes
    /// in the prefix but has the specified bit value at <paramref name="bitIndex"/>.
    /// </summary>
    private Hash256 GetNodeByPrefix(int level, byte[] keyBytes, int bitIndex, int bitValue)
    {
        int prefixLen = _depth - level;

        // Build the prefix: first 'prefixLen' bits from keyBytes, but override bit at bitIndex.
        // The node key is made from these bits.
        string nodeKey = MakeNodeKeyWithOverride(level, keyBytes, prefixLen, bitIndex, bitValue);
        return _nodes.TryGetValue(nodeKey, out Hash256 v) ? v : _defaults[level];
    }

    /// <summary>
    /// Get the hash of the sibling node at the given level, where the sibling
    /// has the bit at <paramref name="bitIndex"/> flipped to <paramref name="siblingBit"/>.
    /// </summary>
    private Hash256 GetNodeWithFlippedBit(int level, byte[] keyBytes, int bitIndex, int siblingBit)
    {
        int prefixLen = _depth - level;
        string nodeKey = MakeNodeKeyWithOverride(level, keyBytes, prefixLen, bitIndex, siblingBit);
        return _nodes.TryGetValue(nodeKey, out Hash256 v) ? v : _defaults[level];
    }

    /// <summary>
    /// Set or remove a node at level 0 (leaf) or any level.
    /// </summary>
    private void SetOrRemoveNode(int level, byte[] keyBytes, int startBit, int prefixLen, Hash256 value)
    {
        string nodeKey = MakeNodeKey(level, keyBytes.AsSpan(), startBit, prefixLen);
        if (value == _defaults[level])
            _nodes.Remove(nodeKey);
        else
            _nodes[nodeKey] = value;
    }

    // -----------------------------------------------------------------------
    // Node key construction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a dictionary key for a node identified by its level and path prefix.
    /// The prefix is the first <paramref name="prefixLen"/> bits of <paramref name="keyBytes"/>
    /// starting at bit index <paramref name="startBit"/>.
    /// </summary>
    private static string MakeNodeKey(int level, ReadOnlySpan<byte> keyBytes, int startBit = 0, int prefixLen = 0)
    {
        if (prefixLen <= 0)
            return string.Concat("L", level.ToString());

        // Pack the prefix bits into bytes for hex encoding.
        int byteCount = (prefixLen + 7) / 8;
        Span<byte> packed = byteCount <= 64 ? stackalloc byte[byteCount] : new byte[byteCount];
        packed.Clear();

        for (int i = 0; i < prefixLen; i++)
        {
            int srcBitIndex = startBit + i;
            int srcByteIndex = srcBitIndex / 8;
            int srcBitOffset = 7 - (srcBitIndex % 8);

            int bit;
            if (srcByteIndex < keyBytes.Length)
                bit = (keyBytes[srcByteIndex] >> srcBitOffset) & 1;
            else
                bit = 0;

            if (bit == 1)
                packed[i / 8] |= (byte)(1 << (7 - (i % 8)));
        }

        return string.Concat("L", level.ToString(), ":", Convert.ToHexString(packed));
    }

    /// <summary>
    /// Build a dictionary key for a node with a specific bit overridden.
    /// Uses bits [0..prefixLen-1] from <paramref name="keyBytes"/>, but forces
    /// the bit at <paramref name="overrideBitIndex"/> to <paramref name="overrideBitValue"/>.
    /// </summary>
    private static string MakeNodeKeyWithOverride(
        int level, byte[] keyBytes, int prefixLen, int overrideBitIndex, int overrideBitValue)
    {
        if (prefixLen <= 0)
            return string.Concat("L", level.ToString());

        int byteCount = (prefixLen + 7) / 8;
        Span<byte> packed = byteCount <= 64 ? stackalloc byte[byteCount] : new byte[byteCount];
        packed.Clear();

        for (int i = 0; i < prefixLen; i++)
        {
            int bit;
            if (i == overrideBitIndex)
            {
                bit = overrideBitValue;
            }
            else
            {
                int srcByteIndex = i / 8;
                int srcBitOffset = 7 - (i % 8);
                bit = srcByteIndex < keyBytes.Length
                    ? (keyBytes[srcByteIndex] >> srcBitOffset) & 1
                    : 0;
            }

            if (bit == 1)
                packed[i / 8] |= (byte)(1 << (7 - (i % 8)));
        }

        return string.Concat("L", level.ToString(), ":", Convert.ToHexString(packed));
    }

    // -----------------------------------------------------------------------
    // Bit extraction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Get bit at position <paramref name="index"/> from the key bytes.
    /// Bit 0 = MSB of byte 0. Bit 7 = LSB of byte 0. Bit 8 = MSB of byte 1. Etc.
    /// </summary>
    private static int GetBit(byte[] keyBytes, int index)
    {
        int byteIndex = index / 8;
        int bitOffset = 7 - (index % 8);

        if (byteIndex >= keyBytes.Length)
            return 0;

        return (keyBytes[byteIndex] >> bitOffset) & 1;
    }
}
