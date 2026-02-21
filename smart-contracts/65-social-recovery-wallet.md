# Social Recovery Wallet

## Category

Security Infrastructure / Account Abstraction

## Summary

A smart contract wallet with a guardian-based social recovery system that enables key rotation when the owner loses access to their private key. If the owner loses their key, a configurable M-of-N threshold of guardians can authorize a key rotation to restore access. Guardians are identified by BNS names for usability, and a time delay on recovery operations prevents malicious guardian collusion. The wallet also supports daily spending limits that allow routine transactions without guardian involvement, creating a balance between security and usability.

## Why It's Useful

- **Market need**: Private key loss is the single largest cause of permanent fund loss in blockchain. Estimates suggest billions of dollars in cryptocurrency are permanently inaccessible due to lost keys. Social recovery provides a practical solution without introducing centralized custodians.
- **User benefit**: Users can recover access to their funds through trusted contacts (friends, family, colleagues) without relying on a centralized service. The time delay on recovery prevents attackers who compromise guardians from immediately draining funds.
- **Security model**: The M-of-N guardian threshold means no single guardian can initiate recovery alone. Combined with the time delay, the owner has a window to cancel malicious recovery attempts.
- **Usability**: Daily spending limits allow routine transactions (gas payments, small purchases) without guardian approval, while large transfers require guardian confirmation. BNS-named guardians are easier to manage than raw addresses.
- **Institutional use**: Businesses can configure multi-signature approval for large transactions while allowing routine operations with single-signature convenience.

## Key Features

- **Guardian system**: Configurable set of guardians (3-7 recommended) identified by BNS names or addresses. Guardians can be added, removed, or replaced by the owner.
- **M-of-N recovery**: Configurable threshold (e.g., 3-of-5) for recovery approval. Multiple guardians must independently submit recovery approvals.
- **Time-delayed recovery**: Recovery has a configurable delay period (default 48 hours). The owner can cancel recovery during this window, preventing surprise attacks.
- **Key rotation**: Recovery replaces the wallet's controlling key with a new key chosen by the guardian consensus. The old key is permanently deactivated.
- **Daily spending limit**: Configurable daily limit for transactions that do not require guardian approval. Transactions exceeding the limit require M-of-N guardian signatures.
- **Transaction batching**: Execute multiple transactions in a single call for gas efficiency.
- **Guardian incentives**: Optional small reward for guardians who participate in recovery, incentivizing availability.
- **Guardian rotation**: The owner can rotate guardians at any time. Guardian rotation takes effect after a delay to prevent malicious guardian replacement before a recovery attempt.
- **Emergency lockdown**: Owner or any single guardian can freeze the wallet, blocking all transactions until unfrozen by M-of-N guardians.
- **Inheritance mode**: Optional dead-man's switch that initiates recovery after a configurable inactivity period, enabling estate planning.
- **Whitelist**: Pre-approved addresses that bypass spending limits (e.g., known DEX contracts, frequent recipients).
- **Transaction history**: On-chain record of all wallet transactions for auditing.

## Basalt-Specific Advantages

- **BNS-named guardians**: Guardians are identified by their BNS names (e.g., "alice.basalt", "bob.basalt"), making guardian management intuitive. Users remember "my guardians are alice, bob, and carol" rather than managing hex addresses.
- **Ed25519 signature verification**: Guardian recovery approvals use Ed25519 signatures, native to Basalt and gas-efficient. Each guardian signs the recovery request with their Ed25519 key, and the wallet contract verifies all signatures on-chain.
- **BLS aggregate signatures**: For M-of-N threshold operations, guardian signatures can be aggregated using Basalt's BLS12-381 support, reducing the on-chain verification cost from N individual signature checks to a single aggregate verification.
- **Escrow for time-delayed operations**: Time-delayed recovery uses the Escrow system contract to hold the new key commitment, ensuring atomicity of the recovery operation after the delay period.
- **ZK guardian identity**: Guardians can prove their identity through BST-VC credentials verified by the ZK compliance layer, ensuring that recovery is authorized by verified individuals without revealing their real-world identity on-chain.
- **Governance for dispute resolution**: If a recovery is contested (owner claims guardians are acting maliciously), the dispute can be escalated to the Governance contract for community resolution.
- **AOT-compiled signature verification**: Multi-signature verification (up to 7 guardian signatures per recovery) executes at native speed, keeping gas costs reasonable even for complex M-of-N operations.
- **Social profile integration**: Guardian relationships can be reflected in social profiles (contract 57), building a web of trust that strengthens the overall security model.
- **Flat state DB for spending tracking**: Daily spending limit tracking requires frequent reads and writes to the per-wallet spending accumulator. FlatStateDb's O(1) caches make this constant-time.

## Token Standards Used

- **BST-20**: Token transfers managed through the wallet contract, subject to spending limits and guardian policies.
- **BST-721**: NFT transfers managed through the wallet contract.
- **BST-VC**: Optional guardian identity verification credentials.

## Integration Points

- **BNS (0x...1002)**: Guardian identification by BNS name. BNS-linked wallet discovery.
- **Escrow (0x...1004)**: Time-delayed recovery operations. Key commitment held in escrow during delay period.
- **Governance (0x...1003)**: Dispute resolution for contested recovery attempts.
- **SchemaRegistry (0x...1006)**: Guardian identity credential schemas.
- **IssuerRegistry (0x...1007)**: Trusted issuers for guardian identity verification.
- **Social Profile (contract 57)**: Guardian trust relationships displayed on profiles.
- **WBSLT (0x...1001)**: Wrapped BST management through the wallet contract.

## Technical Sketch

```csharp
// ---- Data Structures ----

public enum WalletStatus : byte
{
    Active = 0,
    Locked = 1,
    RecoveryPending = 2
}

public enum TransactionType : byte
{
    NativeTransfer = 0,
    TokenTransfer = 1,
    ContractCall = 2,
    GuardianManagement = 3
}

public struct WalletConfig
{
    public Address Owner;                      // Current controlling key
    public ulong GuardianCount;
    public ulong RecoveryThreshold;            // M in M-of-N
    public ulong RecoveryDelaySeconds;         // Default 48 hours
    public UInt256 DailySpendingLimit;
    public ulong GuardianRotationDelaySeconds; // Default 24 hours
    public ulong InactivityTimeoutSeconds;     // 0 = disabled (no inheritance)
    public ulong LastActivityTimestamp;
    public WalletStatus Status;
    public ulong Nonce;                        // Transaction nonce
}

public struct Guardian
{
    public Address GuardianAddress;
    public string BnsName;
    public ulong AddedAt;
    public ulong EffectiveAt;                  // After rotation delay
    public bool Active;
}

public struct RecoveryRequest
{
    public ulong RequestId;
    public Address NewOwner;                   // Proposed new controlling key
    public ulong InitiatedAt;
    public ulong ExecutableAt;                 // After delay period
    public ulong ApprovalsReceived;
    public bool Cancelled;
    public bool Executed;
}

public struct SpendingRecord
{
    public ulong DayTimestamp;                 // Day boundary (midnight UTC)
    public UInt256 AmountSpent;
}

public struct PendingTransaction
{
    public ulong TxId;
    public Address Destination;
    public UInt256 Value;
    public byte[] Data;
    public TransactionType Type;
    public ulong CreatedAt;
    public ulong ApprovalsReceived;
    public bool Executed;
    public bool Cancelled;
}

public struct WhitelistEntry
{
    public Address Whitelisted;
    public string Label;
    public ulong AddedAt;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x020E)]
public partial class SocialRecoveryWallet : SdkContractBase
{
    // Storage
    private StorageValue<WalletConfig> _config;
    private StorageMap<ulong, RecoveryRequest> _recoveryRequests;
    private StorageMap<ulong, PendingTransaction> _pendingTxs;
    private StorageValue<ulong> _nextRequestId;
    private StorageValue<ulong> _nextTxId;

    // Composite key storage:
    // Guardian keyed by BLAKE3(guardianAddress)
    // Guardian list keyed by index for enumeration
    // Recovery approval keyed by BLAKE3(requestId || guardianAddress) -> bool
    // Pending tx approval keyed by BLAKE3(txId || guardianAddress) -> bool
    // SpendingRecord keyed by day timestamp
    // WhitelistEntry keyed by whitelisted address
    // Guardian pending rotation keyed by guardian address

    // --- Initialization ---

    /// <summary>
    /// Initialize the wallet with owner key, guardians, and configuration.
    /// Called once during deployment.
    /// </summary>
    public void Initialize(
        Address owner,
        Address[] guardianAddresses,
        string[] guardianBnsNames,
        ulong recoveryThreshold,
        ulong recoveryDelaySeconds,
        UInt256 dailySpendingLimit,
        ulong guardianRotationDelaySeconds,
        ulong inactivityTimeoutSeconds
    );

    // --- Owner Operations ---

    /// <summary>
    /// Execute a transaction from the wallet. If the transaction value
    /// is within the daily spending limit, it executes immediately.
    /// If it exceeds the limit, it becomes a pending transaction
    /// requiring guardian approval.
    /// </summary>
    public ulong ExecuteTransaction(
        Address destination,
        UInt256 value,
        byte[] data,
        TransactionType txType
    );

    /// <summary>
    /// Execute a batch of transactions. Each is subject to the daily
    /// spending limit (cumulative for the day).
    /// </summary>
    public void ExecuteBatch(
        Address[] destinations,
        UInt256[] values,
        byte[][] datas,
        TransactionType[] txTypes
    );

    /// <summary>
    /// Update the daily spending limit. Takes effect after guardian
    /// rotation delay to prevent abuse.
    /// </summary>
    public void UpdateSpendingLimit(UInt256 newLimit);

    // --- Guardian Management ---

    /// <summary>
    /// Add a new guardian. Takes effect after the guardian rotation delay.
    /// Only callable by owner.
    /// </summary>
    public void AddGuardian(Address guardianAddress, string bnsName);

    /// <summary>
    /// Remove a guardian. Takes effect after the guardian rotation delay.
    /// Cannot reduce below the recovery threshold.
    /// Only callable by owner.
    /// </summary>
    public void RemoveGuardian(Address guardianAddress);

    /// <summary>
    /// Replace a guardian with a new one. Atomic operation.
    /// Takes effect after the guardian rotation delay.
    /// </summary>
    public void ReplaceGuardian(
        Address oldGuardian,
        Address newGuardian,
        string newBnsName
    );

    /// <summary>
    /// Update the recovery threshold. Only callable by owner.
    /// Must be between 1 and guardianCount.
    /// </summary>
    public void UpdateRecoveryThreshold(ulong newThreshold);

    // --- Whitelist Management ---

    /// <summary>
    /// Add an address to the spending whitelist.
    /// Whitelisted addresses bypass the daily spending limit.
    /// Takes effect after guardian rotation delay.
    /// </summary>
    public void AddToWhitelist(Address addr, string label);

    /// <summary>
    /// Remove an address from the whitelist.
    /// </summary>
    public void RemoveFromWhitelist(Address addr);

    // --- Recovery Process ---

    /// <summary>
    /// Initiate a recovery request as a guardian. Proposes a new
    /// owner key. Requires M-of-N guardian approvals.
    /// The initiating guardian's approval is counted.
    /// </summary>
    public ulong InitiateRecovery(Address newOwner);

    /// <summary>
    /// Approve a pending recovery request as a guardian.
    /// Each guardian can approve only once per request.
    /// </summary>
    public void ApproveRecovery(ulong requestId);

    /// <summary>
    /// Execute a recovery after the delay period has passed and
    /// the threshold number of approvals have been received.
    /// Replaces the owner key with the new key.
    /// Callable by anyone.
    /// </summary>
    public void ExecuteRecovery(ulong requestId);

    /// <summary>
    /// Cancel a pending recovery. Only callable by the current owner.
    /// This is the owner's defense against malicious guardian collusion.
    /// </summary>
    public void CancelRecovery(ulong requestId);

    // --- Emergency Operations ---

    /// <summary>
    /// Lock the wallet (emergency freeze). Callable by owner or
    /// any single guardian. Blocks all transactions.
    /// </summary>
    public void LockWallet();

    /// <summary>
    /// Unlock the wallet. Requires M-of-N guardian approvals.
    /// Uses the same approval mechanism as recovery.
    /// </summary>
    public ulong InitiateUnlock();

    /// <summary>
    /// Approve a pending unlock request as a guardian.
    /// </summary>
    public void ApproveUnlock(ulong requestId);

    /// <summary>
    /// Execute unlock after threshold approvals received.
    /// </summary>
    public void ExecuteUnlock(ulong requestId);

    // --- Pending Transaction Approval ---

    /// <summary>
    /// Approve a pending transaction (over spending limit) as a guardian.
    /// </summary>
    public void ApproveTransaction(ulong txId);

    /// <summary>
    /// Execute a pending transaction after sufficient approvals.
    /// </summary>
    public void ExecutePendingTransaction(ulong txId);

    /// <summary>
    /// Cancel a pending transaction. Only callable by owner.
    /// </summary>
    public void CancelPendingTransaction(ulong txId);

    // --- Inheritance (Dead Man's Switch) ---

    /// <summary>
    /// Heartbeat function. Owner calls periodically to reset the
    /// inactivity timer. If the timer expires without a heartbeat,
    /// guardians can initiate recovery (inheritance mode).
    /// </summary>
    public void Heartbeat();

    /// <summary>
    /// Check if the inactivity timeout has expired.
    /// If true, guardians can initiate recovery without the delay period.
    /// </summary>
    public bool IsInactivityExpired();

    // --- View Functions ---

    public WalletConfig GetConfig();
    public Guardian GetGuardian(Address guardianAddress);
    public ulong GetGuardianCount();
    public RecoveryRequest GetRecoveryRequest(ulong requestId);
    public PendingTransaction GetPendingTransaction(ulong txId);
    public UInt256 GetDailySpent();
    public UInt256 GetRemainingDailyLimit();
    public bool IsWhitelisted(Address addr);
    public bool IsGuardian(Address addr);
    public ulong GetNonce();
    public WalletStatus GetStatus();

    /// <summary>
    /// Get the number of approvals for a recovery request.
    /// </summary>
    public ulong GetRecoveryApprovalCount(ulong requestId);

    /// <summary>
    /// Check if a specific guardian has approved a recovery request.
    /// </summary>
    public bool HasGuardianApproved(ulong requestId, Address guardian);

    /// <summary>
    /// Get the wallet's total balance (native BST).
    /// </summary>
    public UInt256 GetBalance();

    // --- Internal ---

    /// <summary>
    /// Verify the caller is the wallet owner.
    /// </summary>
    private void RequireOwner();

    /// <summary>
    /// Verify the caller is an active guardian.
    /// </summary>
    private void RequireGuardian();

    /// <summary>
    /// Check if a transaction is within the daily spending limit.
    /// Updates the daily spending accumulator.
    /// </summary>
    private bool IsWithinDailyLimit(UInt256 value);

    /// <summary>
    /// Execute a raw transaction from the wallet contract.
    /// </summary>
    private void ExecuteRaw(
        Address destination,
        UInt256 value,
        byte[] data
    );

    /// <summary>
    /// Reset the daily spending counter if a new day has started.
    /// </summary>
    private void MaybeResetDailyCounter();

    /// <summary>
    /// Resolve a BNS name to an address for guardian verification.
    /// </summary>
    private Address ResolveBns(string bnsName);
}
```

## Complexity

**High** -- The contract implements a complete wallet abstraction with multiple interacting security mechanisms: M-of-N guardian consensus for recovery and high-value transactions, time-delayed operations with cancellation windows, daily spending limits with automatic reset, guardian rotation with its own delay period, emergency lockdown and unlock flows, inheritance dead-man's switch, and transaction whitelisting. Each mechanism must be carefully implemented to prevent bypass attacks. The interaction between spending limits, whitelist, pending transactions, and wallet lock state creates a complex permission matrix. Guardian rotation timing must be coordinated with recovery delay to prevent the owner from replacing all guardians just before a legitimate recovery attempt.

## Priority

**P0** -- Social recovery is critical infrastructure that directly addresses the number one barrier to mainstream blockchain adoption: the risk of permanent fund loss from key mismanagement. A secure, user-friendly wallet with guardian-based recovery makes Basalt accessible to non-technical users and provides peace of mind for all users. This should be among the first contracts deployed, as it provides the foundation for safe participation in all other ecosystem activities.
