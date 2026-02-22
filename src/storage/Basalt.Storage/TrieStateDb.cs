using Basalt.Codec;
using Basalt.Core;
using Basalt.Storage.Trie;

namespace Basalt.Storage;

/// <summary>
/// MPT-backed state database that provides cryptographically verifiable state roots.
/// Each account's state is stored as a leaf in the world state trie.
/// Contract storage uses per-account sub-tries.
/// </summary>
public sealed class TrieStateDb : IStateDatabase
{
    private readonly ITrieNodeStore _nodeStore;
    private MerklePatriciaTrie _worldTrie;
    private readonly Dictionary<Address, MerklePatriciaTrie> _storageTries = new();
    private readonly HashSet<(Address, Hash256)> _dirtyStorageKeys = new();

    public TrieStateDb(ITrieNodeStore nodeStore) : this(nodeStore, Hash256.Zero) { }

    public TrieStateDb(ITrieNodeStore nodeStore, Hash256 stateRoot)
    {
        _nodeStore = nodeStore;
        _worldTrie = new MerklePatriciaTrie(nodeStore, stateRoot == Hash256.Zero ? null : stateRoot);
    }

    public AccountState? GetAccount(Address address)
    {
        var key = AddressToKey(address);
        var data = _worldTrie.Get(key);
        if (data == null)
            return null;
        return DecodeAccountState(data);
    }

    public void SetAccount(Address address, AccountState state)
    {
        var key = AddressToKey(address);
        var data = EncodeAccountState(state);
        _worldTrie.Put(key, data);
    }

    public bool AccountExists(Address address)
    {
        var key = AddressToKey(address);
        return _worldTrie.Get(key) != null;
    }

    public void DeleteAccount(Address address)
    {
        var key = AddressToKey(address);
        _worldTrie.Delete(key);
        _storageTries.Remove(address);
    }

    public Hash256 ComputeStateRoot()
    {
        // Flush any pending storage trie changes into account states
        foreach (var (address, storageTrie) in _storageTries)
        {
            var account = GetAccount(address);
            if (account.HasValue)
            {
                var updated = new AccountState
                {
                    Nonce = account.Value.Nonce,
                    Balance = account.Value.Balance,
                    StorageRoot = storageTrie.RootHash,
                    CodeHash = account.Value.CodeHash,
                    AccountType = account.Value.AccountType,
                    ComplianceHash = account.Value.ComplianceHash,
                };
                var key = AddressToKey(address);
                _worldTrie.Put(key, EncodeAccountState(updated));
            }
        }

        return _worldTrie.RootHash;
    }

    /// <summary>
    /// Create a forked snapshot of this state database.
    /// </summary>
    /// <remarks>
    /// <para><b>Invariant (H-02):</b> The <c>_storageTries</c> dictionary is NOT carried over
    /// to the forked instance. <see cref="ComputeStateRoot"/> is called first to flush all
    /// pending storage trie roots into account state, so the fork reconstructs storage tries
    /// from the committed storage root on demand. Any storage mutations after the last
    /// <c>ComputeStateRoot()</c> call but before <c>Fork()</c> are correctly captured because
    /// <c>Fork()</c> calls <c>ComputeStateRoot()</c> internally.</para>
    /// <para>Callers that bypass <see cref="FlatStateDb"/> and use <c>TrieStateDb.Fork()</c>
    /// directly should be aware of this: the fork sees committed storage state only.</para>
    /// </remarks>
    public IStateDatabase Fork()
    {
        var currentRoot = ComputeStateRoot();
        var overlay = new OverlayTrieNodeStore(_nodeStore);
        return new TrieStateDb(overlay, currentRoot);
    }

    public IEnumerable<(Address Address, AccountState State)> GetAllAccounts()
    {
        // For the trie-backed store, we iterate over all leaves
        // This is a best-effort implementation — real production would use a separate index
        throw new NotSupportedException(
            "Iterating all accounts is not efficiently supported by the trie-backed store. " +
            "Use InMemoryStateDb for development or maintain a separate index.");
    }

    public byte[]? GetStorage(Address contract, Hash256 key)
    {
        var trie = GetOrCreateStorageTrie(contract);
        var storageKey = new byte[Hash256.Size];
        key.WriteTo(storageKey);
        return trie.Get(storageKey);
    }

    public void SetStorage(Address contract, Hash256 key, byte[] value)
    {
        var trie = GetOrCreateStorageTrie(contract);
        var storageKey = new byte[Hash256.Size];
        key.WriteTo(storageKey);
        trie.Put(storageKey, value);
        _dirtyStorageKeys.Add((contract, key));
    }

    public void DeleteStorage(Address contract, Hash256 key)
    {
        var trie = GetOrCreateStorageTrie(contract);
        var storageKey = new byte[Hash256.Size];
        key.WriteTo(storageKey);
        trie.Delete(storageKey);
        _dirtyStorageKeys.Add((contract, key));
    }

    public IReadOnlyCollection<(Address Contract, Hash256 Key)> GetModifiedStorageKeys() => _dirtyStorageKeys;

    /// <summary>
    /// Generate a Merkle proof for an account.
    /// </summary>
    public MerkleProof? GenerateAccountProof(Address address)
    {
        var key = AddressToKey(address);
        return _worldTrie.GenerateProof(key);
    }

    /// <summary>
    /// Generate a Merkle proof for a storage slot.
    /// </summary>
    public MerkleProof? GenerateStorageProof(Address contract, Hash256 key)
    {
        var trie = GetOrCreateStorageTrie(contract);
        var storageKey = new byte[Hash256.Size];
        key.WriteTo(storageKey);
        return trie.GenerateProof(storageKey);
    }

    private MerklePatriciaTrie GetOrCreateStorageTrie(Address contract)
    {
        if (_storageTries.TryGetValue(contract, out var existing))
            return existing;

        var account = GetAccount(contract);
        var storageRoot = account?.StorageRoot ?? Hash256.Zero;
        var trie = new MerklePatriciaTrie(
            _nodeStore,
            storageRoot == Hash256.Zero ? null : storageRoot);
        _storageTries[contract] = trie;
        return trie;
    }

    private static byte[] AddressToKey(Address address)
    {
        var key = new byte[Address.Size];
        address.WriteTo(key);
        return key;
    }

    private static byte[] EncodeAccountState(AccountState state)
    {
        // nonce(8) + balance(32) + storageRoot(32) + codeHash(32) + accountType(1) + complianceHash(32) = 137
        var buffer = new byte[137];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt64(state.Nonce);
        writer.WriteUInt256(state.Balance);
        writer.WriteHash256(state.StorageRoot);
        writer.WriteHash256(state.CodeHash);
        writer.WriteByte((byte)state.AccountType);
        writer.WriteHash256(state.ComplianceHash);
        return buffer;
    }

    private static AccountState DecodeAccountState(byte[] data)
    {
        var reader = new BasaltReader(data);
        return new AccountState
        {
            Nonce = reader.ReadUInt64(),
            Balance = reader.ReadUInt256(),
            StorageRoot = reader.ReadHash256(),
            CodeHash = reader.ReadHash256(),
            AccountType = (AccountType)reader.ReadByte(),
            ComplianceHash = reader.ReadHash256(),
        };
    }
}
