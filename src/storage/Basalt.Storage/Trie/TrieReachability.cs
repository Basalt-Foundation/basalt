using Basalt.Core;

namespace Basalt.Storage.Trie;

/// <summary>
/// Computes the full set of trie-node hashes reachable from a set of state roots, <b>including every
/// account's storage sub-trie</b>. This is the mark phase of state-trie pruning.
/// </summary>
/// <remarks>
/// <para><b>Why this exists (H1).</b> <see cref="MerklePatriciaTrie.CollectReachableNodes"/> walks
/// world-trie structure only (Extension/Branch children) and never decodes an account value, so it
/// misses every contract-storage node. Storage sub-tries live in the <b>same</b> node store /
/// <c>CF.TrieNodes</c> keyspace, content-addressed. A prune sweep marked with the world-only walk would
/// delete every live storage node and corrupt state. Pruning MUST use this storage-aware walk.</para>
/// <para>Because nodes are content-addressed, a node reachable from <b>any</b> retained root is in the
/// union and can never be wrongly deleted, provided the union is complete — which is exactly what this
/// walk guarantees (world spine + every non-empty <c>StorageRoot</c> sub-trie, unioned across roots).</para>
/// </remarks>
public static class TrieReachability
{
    // Account value layout (TrieStateDb.EncodeAccountState): Nonce(8) Balance(32) StorageRoot(32) ...
    private const int AccountStorageRootOffset = 40;

    /// <summary>
    /// Returns every trie-node hash reachable from any root in <paramref name="roots"/>, walking the world
    /// trie and each account's non-empty storage sub-trie. Zero roots are skipped.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A node referenced by a retained root is missing from <paramref name="store"/>. Under-marking here
    /// would make a sweep delete live nodes, so this is fatal: the caller MUST abort the sweep and delete
    /// nothing.
    /// </exception>
    public static HashSet<Hash256> CollectReachable(ITrieNodeStore store, IEnumerable<Hash256> roots)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(roots);

        var reachable = new HashSet<Hash256>();
        // Explicit work stack (root, isWorldTrie) to keep depth off the call stack.
        var stack = new Stack<(Hash256 Hash, bool IsWorldTrie)>();
        foreach (var root in roots)
        {
            if (root != Hash256.Zero)
                stack.Push((root, true));
        }

        while (stack.Count > 0)
        {
            var (nodeHash, isWorldTrie) = stack.Pop();
            if (nodeHash == Hash256.Zero || !reachable.Add(nodeHash))
                continue; // empty or already visited (also breaks any cycle)

            var node = store.Get(nodeHash)
                ?? throw new InvalidOperationException(
                    $"Trie pruning: node {nodeHash.ToHexString()} referenced by a retained root is missing " +
                    "from the store. Aborting sweep (deleting nothing).");

            switch (node.NodeType)
            {
                case TrieNodeType.Extension:
                    if (node.ChildHash.HasValue)
                        stack.Push((node.ChildHash.Value, isWorldTrie));
                    break;

                case TrieNodeType.Branch:
                    foreach (var child in node.Children)
                    {
                        if (child.HasValue)
                            stack.Push((child.Value, isWorldTrie));
                    }
                    // A branch can itself carry a terminal value (a key that ends at the branch).
                    if (isWorldTrie)
                        PushStorageRoot(node.BranchValue, stack);
                    break;

                case TrieNodeType.Leaf:
                    if (isWorldTrie)
                        PushStorageRoot(node.Value, stack);
                    break;
            }
        }

        return reachable;
    }

    /// <summary>If <paramref name="accountValue"/> is an account carrying a non-empty storage root, queue that sub-trie.</summary>
    private static void PushStorageRoot(byte[]? accountValue, Stack<(Hash256, bool)> stack)
    {
        if (accountValue == null || accountValue.Length < AccountStorageRootOffset + Hash256.Size)
            return; // not an account-shaped value; nothing to descend

        var storageRoot = new Hash256(accountValue.AsSpan(AccountStorageRootOffset, Hash256.Size));
        if (storageRoot != Hash256.Zero)
            stack.Push((storageRoot, false)); // storage sub-trie: values are raw slots, never accounts
    }
}
