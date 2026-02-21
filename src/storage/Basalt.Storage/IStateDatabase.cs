using Basalt.Core;

namespace Basalt.Storage;

/// <summary>
/// Interface for the state database that stores account states.
/// </summary>
public interface IStateDatabase
{
    AccountState? GetAccount(Address address);
    void SetAccount(Address address, AccountState state);
    bool AccountExists(Address address);
    void DeleteAccount(Address address);
    /// <summary>
    /// Compute the state root hash.
    /// </summary>
    /// <remarks>
    /// The root hash algorithm is implementation-specific. <see cref="InMemoryStateDb"/>
    /// uses a naive sorted hash; <see cref="TrieStateDb"/> uses a Merkle Patricia Trie.
    /// Roots from different implementations are <b>not comparable</b>.
    /// </remarks>
    Hash256 ComputeStateRoot();
    IEnumerable<(Address Address, AccountState State)> GetAllAccounts();

    // Storage operations for contract state
    byte[]? GetStorage(Address contract, Hash256 key);
    void SetStorage(Address contract, Hash256 key, byte[] value);
    void DeleteStorage(Address contract, Hash256 key);

    /// <summary>
    /// Creates a lightweight fork of this state database.
    /// Writes to the fork do not affect the original.
    /// Used for speculative block building during consensus proposals.
    /// </summary>
    IStateDatabase Fork();
}

/// <summary>
/// Account state in the Basalt blockchain.
/// </summary>
public readonly struct AccountState
{
    public ulong Nonce { get; init; }
    public UInt256 Balance { get; init; }
    public Hash256 StorageRoot { get; init; }
    public Hash256 CodeHash { get; init; }
    public AccountType AccountType { get; init; }
    public Hash256 ComplianceHash { get; init; }

    public static AccountState Empty => new()
    {
        Nonce = 0,
        Balance = UInt256.Zero,
        StorageRoot = Hash256.Zero,
        CodeHash = Hash256.Zero,
        AccountType = AccountType.ExternallyOwned,
        ComplianceHash = Hash256.Zero,
    };
}

/// <summary>
/// Type of account in the Basalt blockchain.
/// </summary>
public enum AccountType : byte
{
    ExternallyOwned = 0,
    Contract = 1,
    SystemContract = 2,
    Validator = 3,
}
