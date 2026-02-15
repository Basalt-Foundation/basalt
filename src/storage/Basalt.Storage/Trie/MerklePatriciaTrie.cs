using Basalt.Core;

namespace Basalt.Storage.Trie;

/// <summary>
/// Merkle Patricia Trie implementation using BLAKE3 hashing.
/// Provides cryptographically verifiable state storage with O(log n) lookups.
/// </summary>
public sealed class MerklePatriciaTrie
{
    private readonly ITrieNodeStore _store;
    private Hash256? _rootHash;

    public MerklePatriciaTrie(ITrieNodeStore store) : this(store, null) { }

    public MerklePatriciaTrie(ITrieNodeStore store, Hash256? rootHash)
    {
        _store = store;
        _rootHash = rootHash;
    }

    /// <summary>
    /// Current root hash of the trie.
    /// </summary>
    public Hash256 RootHash => _rootHash ?? Hash256.Zero;

    /// <summary>
    /// Get a value by key.
    /// </summary>
    public byte[]? Get(byte[] key)
    {
        if (_rootHash == null || _rootHash == Hash256.Zero)
            return null;

        var path = NibblePath.FromKey(key);
        return Get(_rootHash.Value, path);
    }

    /// <summary>
    /// Insert or update a key-value pair.
    /// </summary>
    public void Put(byte[] key, byte[] value)
    {
        var path = NibblePath.FromKey(key);

        if (_rootHash == null || _rootHash == Hash256.Zero)
        {
            var leaf = TrieNode.CreateLeaf(path, value);
            var hash = leaf.ComputeHash();
            _store.Put(hash, leaf);
            _rootHash = hash;
            return;
        }

        var newRootHash = Put(_rootHash.Value, path, value);
        _rootHash = newRootHash;
    }

    /// <summary>
    /// Delete a key from the trie.
    /// </summary>
    public bool Delete(byte[] key)
    {
        if (_rootHash == null || _rootHash == Hash256.Zero)
            return false;

        var path = NibblePath.FromKey(key);
        var result = Delete(_rootHash.Value, path);
        if (result == null)
        {
            _rootHash = Hash256.Zero;
            return true;
        }
        if (result == _rootHash)
            return false; // Key not found
        _rootHash = result;
        return true;
    }

    /// <summary>
    /// Generate a Merkle proof for a key.
    /// </summary>
    public MerkleProof? GenerateProof(byte[] key)
    {
        if (_rootHash == null || _rootHash == Hash256.Zero)
            return null;

        var path = NibblePath.FromKey(key);
        var proofNodes = new List<byte[]>();
        var found = CollectProof(_rootHash.Value, path, proofNodes);

        return new MerkleProof
        {
            Key = key,
            Value = found,
            ProofNodes = proofNodes,
            RootHash = _rootHash.Value,
        };
    }

    /// <summary>
    /// Verify a Merkle proof.
    /// </summary>
    public static bool VerifyProof(MerkleProof proof)
    {
        if (proof.ProofNodes.Count == 0)
            return proof.Value == null;

        // Rebuild the trie path from the proof nodes and verify root
        var store = new InMemoryTrieNodeStore();
        foreach (var nodeData in proof.ProofNodes)
        {
            var node = TrieNode.Decode(nodeData);
            var hash = node.ComputeHash();
            store.Put(hash, node);
        }

        var trie = new MerklePatriciaTrie(store, proof.RootHash);
        var value = trie.Get(proof.Key);

        if (proof.Value == null)
            return value == null;
        if (value == null)
            return false;
        return value.AsSpan().SequenceEqual(proof.Value);
    }

    #region Internal recursive operations

    private byte[]? Get(Hash256 nodeHash, NibblePath path)
    {
        var node = _store.Get(nodeHash);
        if (node == null)
            return null;

        switch (node.NodeType)
        {
            case TrieNodeType.Empty:
                return null;

            case TrieNodeType.Leaf:
            {
                if (path.Equals(node.Path))
                    return node.Value;
                return null;
            }

            case TrieNodeType.Extension:
            {
                int commonLen = path.CommonPrefixLength(node.Path);
                if (commonLen < node.Path.Length)
                    return null;
                return Get(node.ChildHash!.Value, path.Slice(node.Path.Length));
            }

            case TrieNodeType.Branch:
            {
                if (path.IsEmpty)
                    return node.BranchValue;
                byte nibble = path[0];
                if (!node.Children[nibble].HasValue)
                    return null;
                return Get(node.Children[nibble]!.Value, path.Slice(1));
            }

            default:
                return null;
        }
    }

    private Hash256 Put(Hash256 nodeHash, NibblePath path, byte[] value)
    {
        var node = _store.Get(nodeHash);
        if (node == null)
        {
            var leaf = TrieNode.CreateLeaf(path, value);
            var hash = leaf.ComputeHash();
            _store.Put(hash, leaf);
            return hash;
        }

        switch (node.NodeType)
        {
            case TrieNodeType.Empty:
            {
                var leaf = TrieNode.CreateLeaf(path, value);
                var hash = leaf.ComputeHash();
                _store.Put(hash, leaf);
                return hash;
            }

            case TrieNodeType.Leaf:
            {
                if (path.Equals(node.Path))
                {
                    // Update existing leaf
                    var newLeaf = TrieNode.CreateLeaf(path, value);
                    var hash = newLeaf.ComputeHash();
                    _store.Put(hash, newLeaf);
                    return hash;
                }

                // Split: create branch
                int commonLen = path.CommonPrefixLength(node.Path);
                return SplitLeaf(node, path, value, commonLen);
            }

            case TrieNodeType.Extension:
            {
                int commonLen = path.CommonPrefixLength(node.Path);

                if (commonLen == node.Path.Length)
                {
                    // Path fully matches extension — recurse into child
                    var newChildHash = Put(node.ChildHash!.Value, path.Slice(node.Path.Length), value);
                    var newExt = TrieNode.CreateExtension(node.Path, newChildHash);
                    var hash = newExt.ComputeHash();
                    _store.Put(hash, newExt);
                    return hash;
                }

                // Partial match — split extension
                return SplitExtension(node, path, value, commonLen);
            }

            case TrieNodeType.Branch:
            {
                if (path.IsEmpty)
                {
                    // Store value at branch
                    var newBranch = TrieNode.CreateBranch();
                    for (int i = 0; i < 16; i++)
                        newBranch.SetChild(i, node.Children[i]);
                    newBranch.SetBranchValue(value);
                    var hash = newBranch.ComputeHash();
                    _store.Put(hash, newBranch);
                    return hash;
                }

                byte nibble = path[0];
                Hash256 newChild;
                if (node.Children[nibble].HasValue)
                    newChild = Put(node.Children[nibble]!.Value, path.Slice(1), value);
                else
                {
                    var leaf = TrieNode.CreateLeaf(path.Slice(1), value);
                    newChild = leaf.ComputeHash();
                    _store.Put(newChild, leaf);
                }

                var updated = TrieNode.CreateBranch();
                for (int i = 0; i < 16; i++)
                    updated.SetChild(i, i == nibble ? newChild : node.Children[i]);
                updated.SetBranchValue(node.BranchValue);
                var h = updated.ComputeHash();
                _store.Put(h, updated);
                return h;
            }

            default:
                throw new InvalidOperationException($"Unknown node type: {node.NodeType}");
        }
    }

    private Hash256 SplitLeaf(TrieNode existingLeaf, NibblePath newPath, byte[] newValue, int commonLen)
    {
        var existingPath = existingLeaf.Path;
        var branch = TrieNode.CreateBranch();

        if (commonLen == existingPath.Length)
        {
            // Existing leaf becomes branch value
            branch.SetBranchValue(existingLeaf.Value);
        }
        else
        {
            var existingRemaining = existingPath.Slice(commonLen + 1);
            var existingNewLeaf = TrieNode.CreateLeaf(existingRemaining, existingLeaf.Value!);
            var existingHash = existingNewLeaf.ComputeHash();
            _store.Put(existingHash, existingNewLeaf);
            branch.SetChild(existingPath[commonLen], existingHash);
        }

        if (commonLen == newPath.Length)
        {
            branch.SetBranchValue(newValue);
        }
        else
        {
            var newRemaining = newPath.Slice(commonLen + 1);
            var newLeaf = TrieNode.CreateLeaf(newRemaining, newValue);
            var newHash = newLeaf.ComputeHash();
            _store.Put(newHash, newLeaf);
            branch.SetChild(newPath[commonLen], newHash);
        }

        var branchHash = branch.ComputeHash();
        _store.Put(branchHash, branch);

        if (commonLen > 0)
        {
            var ext = TrieNode.CreateExtension(newPath.Slice(0, commonLen), branchHash);
            var extHash = ext.ComputeHash();
            _store.Put(extHash, ext);
            return extHash;
        }

        return branchHash;
    }

    private Hash256 SplitExtension(TrieNode existingExt, NibblePath newPath, byte[] newValue, int commonLen)
    {
        var branch = TrieNode.CreateBranch();

        // Remaining extension (after common prefix + 1 nibble for branch slot)
        if (commonLen + 1 < existingExt.Path.Length)
        {
            var remainingExtPath = existingExt.Path.Slice(commonLen + 1);
            var newExt = TrieNode.CreateExtension(remainingExtPath, existingExt.ChildHash!.Value);
            var newExtHash = newExt.ComputeHash();
            _store.Put(newExtHash, newExt);
            branch.SetChild(existingExt.Path[commonLen], newExtHash);
        }
        else
        {
            branch.SetChild(existingExt.Path[commonLen], existingExt.ChildHash!.Value);
        }

        // Insert new value
        if (commonLen == newPath.Length)
        {
            branch.SetBranchValue(newValue);
        }
        else
        {
            var newRemaining = newPath.Slice(commonLen + 1);
            var newLeaf = TrieNode.CreateLeaf(newRemaining, newValue);
            var newLeafHash = newLeaf.ComputeHash();
            _store.Put(newLeafHash, newLeaf);
            branch.SetChild(newPath[commonLen], newLeafHash);
        }

        var branchHash = branch.ComputeHash();
        _store.Put(branchHash, branch);

        if (commonLen > 0)
        {
            var ext = TrieNode.CreateExtension(newPath.Slice(0, commonLen), branchHash);
            var extHash = ext.ComputeHash();
            _store.Put(extHash, ext);
            return extHash;
        }

        return branchHash;
    }

    private Hash256? Delete(Hash256 nodeHash, NibblePath path)
    {
        var node = _store.Get(nodeHash);
        if (node == null)
            return nodeHash; // Not found

        switch (node.NodeType)
        {
            case TrieNodeType.Empty:
                return nodeHash;

            case TrieNodeType.Leaf:
            {
                if (path.Equals(node.Path))
                    return null; // Deleted
                return nodeHash; // Not found
            }

            case TrieNodeType.Extension:
            {
                int commonLen = path.CommonPrefixLength(node.Path);
                if (commonLen < node.Path.Length)
                    return nodeHash; // Not found

                var result = Delete(node.ChildHash!.Value, path.Slice(node.Path.Length));
                if (result == node.ChildHash)
                    return nodeHash; // Nothing changed

                if (result == null)
                    return null; // Child deleted entirely

                // Rebuild extension with new child, possibly merging
                return RebuildAfterDelete(result.Value, node.Path);
            }

            case TrieNodeType.Branch:
            {
                Hash256? result;
                if (path.IsEmpty)
                {
                    if (node.BranchValue == null)
                        return nodeHash; // Not found

                    var newBranch = TrieNode.CreateBranch();
                    for (int i = 0; i < 16; i++)
                        newBranch.SetChild(i, node.Children[i]);
                    newBranch.SetBranchValue(null);
                    return CompactBranch(newBranch);
                }

                byte nibble = path[0];
                if (!node.Children[nibble].HasValue)
                    return nodeHash;

                result = Delete(node.Children[nibble]!.Value, path.Slice(1));
                if (result == node.Children[nibble])
                    return nodeHash;

                var updated = TrieNode.CreateBranch();
                for (int i = 0; i < 16; i++)
                    updated.SetChild(i, i == nibble ? result : node.Children[i]);
                updated.SetBranchValue(node.BranchValue);

                return CompactBranch(updated);
            }

            default:
                return nodeHash;
        }
    }

    private Hash256? CompactBranch(TrieNode branch)
    {
        // Count remaining children
        int childCount = 0;
        int singleChild = -1;
        for (int i = 0; i < 16; i++)
        {
            if (branch.Children[i].HasValue)
            {
                childCount++;
                singleChild = i;
            }
        }

        if (childCount == 0 && branch.BranchValue == null)
            return null;

        if (childCount == 0 && branch.BranchValue != null)
        {
            // Convert to leaf with empty path
            var leaf = TrieNode.CreateLeaf(new NibblePath([], 0, 0), branch.BranchValue);
            var hash = leaf.ComputeHash();
            _store.Put(hash, leaf);
            return hash;
        }

        if (childCount == 1 && branch.BranchValue == null)
        {
            // Merge single child upward
            var childHash = branch.Children[singleChild]!.Value;
            var childNode = _store.Get(childHash);
            if (childNode != null)
            {
                // Prepend the nibble to the child's path
                var nibbleBytes = new byte[] { (byte)(singleChild << 4) };
                var prefix = new NibblePath(nibbleBytes, 0, 1);

                switch (childNode.NodeType)
                {
                    case TrieNodeType.Leaf:
                    {
                        var mergedPath = ConcatPaths(prefix, childNode.Path);
                        var leaf = TrieNode.CreateLeaf(mergedPath, childNode.Value!);
                        var hash = leaf.ComputeHash();
                        _store.Put(hash, leaf);
                        return hash;
                    }
                    case TrieNodeType.Extension:
                    {
                        var mergedPath = ConcatPaths(prefix, childNode.Path);
                        var ext = TrieNode.CreateExtension(mergedPath, childNode.ChildHash!.Value);
                        var hash = ext.ComputeHash();
                        _store.Put(hash, ext);
                        return hash;
                    }
                }
            }
        }

        // Keep as branch
        var h = branch.ComputeHash();
        _store.Put(h, branch);
        return h;
    }

    private Hash256 RebuildAfterDelete(Hash256 childHash, NibblePath extensionPath)
    {
        var childNode = _store.Get(childHash);
        if (childNode != null)
        {
            switch (childNode.NodeType)
            {
                case TrieNodeType.Leaf:
                {
                    var mergedPath = ConcatPaths(extensionPath, childNode.Path);
                    var leaf = TrieNode.CreateLeaf(mergedPath, childNode.Value!);
                    var hash = leaf.ComputeHash();
                    _store.Put(hash, leaf);
                    return hash;
                }
                case TrieNodeType.Extension:
                {
                    var mergedPath = ConcatPaths(extensionPath, childNode.Path);
                    var ext = TrieNode.CreateExtension(mergedPath, childNode.ChildHash!.Value);
                    var hash = ext.ComputeHash();
                    _store.Put(hash, ext);
                    return hash;
                }
            }
        }

        var newExt = TrieNode.CreateExtension(extensionPath, childHash);
        var h = newExt.ComputeHash();
        _store.Put(h, newExt);
        return h;
    }

    private byte[]? CollectProof(Hash256 nodeHash, NibblePath path, List<byte[]> proofNodes)
    {
        var node = _store.Get(nodeHash);
        if (node == null)
            return null;

        proofNodes.Add(node.Encode());

        switch (node.NodeType)
        {
            case TrieNodeType.Empty:
                return null;

            case TrieNodeType.Leaf:
                return path.Equals(node.Path) ? node.Value : null;

            case TrieNodeType.Extension:
            {
                int commonLen = path.CommonPrefixLength(node.Path);
                if (commonLen < node.Path.Length)
                    return null;
                return CollectProof(node.ChildHash!.Value, path.Slice(node.Path.Length), proofNodes);
            }

            case TrieNodeType.Branch:
            {
                if (path.IsEmpty)
                    return node.BranchValue;
                byte nibble = path[0];
                if (!node.Children[nibble].HasValue)
                    return null;
                return CollectProof(node.Children[nibble]!.Value, path.Slice(1), proofNodes);
            }

            default:
                return null;
        }
    }

    private static NibblePath ConcatPaths(NibblePath a, NibblePath b)
    {
        int totalLength = a.Length + b.Length;
        var combined = new byte[(totalLength + 1) / 2];

        for (int i = 0; i < totalLength; i++)
        {
            byte nibble = i < a.Length ? a[i] : b[i - a.Length];
            int byteIndex = i / 2;
            if (i % 2 == 0)
                combined[byteIndex] = (byte)(nibble << 4);
            else
                combined[byteIndex] |= nibble;
        }

        return new NibblePath(combined, 0, totalLength);
    }

    #endregion
}

/// <summary>
/// Merkle proof for a key-value pair.
/// </summary>
public sealed class MerkleProof
{
    public required byte[] Key { get; init; }
    public byte[]? Value { get; init; }
    public required List<byte[]> ProofNodes { get; init; }
    public required Hash256 RootHash { get; init; }
}
