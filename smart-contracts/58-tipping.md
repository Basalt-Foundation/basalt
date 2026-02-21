# Micropayment / Tipping Contract

## Category

Social Finance / Creator Economy

## Summary

A lightweight tipping contract that enables users to send small BST amounts with attached messages to recipients identified by BNS names or raw addresses. The contract supports tip aggregation where many small tips are batched into a single claimable balance, reducing gas costs for recipients. It includes content creator support features, a leaderboard system tracking top tippers and receivers, and configurable platform fees for sustainable operation.

## Why It's Useful

- **Market need**: Micropayments on most blockchains are impractical due to high gas costs relative to the tip amount. A dedicated tipping contract with aggregation makes sub-dollar payments economically viable.
- **User benefit**: Content creators, open-source developers, community contributors, and helpful forum participants can receive direct, censorship-resistant financial recognition for their work. Tippers can express appreciation with minimal friction.
- **Economic impact**: A functioning micropayment layer unlocks new business models (pay-per-article, tip-per-post, donation-funded projects) that are not viable with traditional payment rails due to processing fees.
- **Social engagement**: Public tip leaderboards and tip messages create a visible culture of appreciation within the ecosystem, encouraging positive contributions and content creation.
- **Developer integration**: Any dApp can embed tipping functionality (tip a post author, tip a tool creator, tip a governance contributor) using a shared contract, reducing development effort.

## Key Features

- **Send tips with messages**: Attach a short text message (up to 256 bytes) to each tip for context ("Great article!", "Thanks for the PR fix").
- **BNS name resolution**: Send tips to a BNS name instead of a raw address. The contract resolves the name to an address at send time.
- **Tip aggregation**: Small tips accumulate in the recipient's on-contract balance. Recipients claim their accumulated balance in a single transaction, amortizing gas costs across many tips.
- **Minimum tip amount**: Configurable minimum to prevent dust spam attacks.
- **Tip with token**: Support for tipping with BST-20 tokens in addition to native BST.
- **Recurring tips**: Set up recurring tip schedules (e.g., 1 BST per week to a content creator). Tips execute automatically when triggered by any caller.
- **Content tagging**: Tips can reference a content hash (article, post, code commit) for attribution tracking.
- **Leaderboards**: On-chain tracking of top tippers (by total amount given) and top receivers (by total amount received) over configurable time windows.
- **Platform fee**: Optional fee (default 0.5%) deducted from each tip, directed to a configurable treasury for contract maintenance.
- **Tip splitting**: Send a single tip that is split among multiple recipients (e.g., tip all contributors of a project proportionally).
- **Anonymous tipping**: Option to hide the tipper's address (recorded on-chain but flagged as anonymous in the contract's public view).
- **Refund window**: Brief refund window (e.g., 10 blocks) during which a tipper can cancel a tip before it is finalized.

## Basalt-Specific Advantages

- **BNS native resolution**: Basalt's Name Service enables frictionless tipping by name rather than address. "Tip alice.basalt 0.5 BST" is a natural UX that other chains cannot offer without additional infrastructure.
- **EIP-1559 low base fees**: Basalt's EIP-1559 implementation with low initial base fees makes micropayments economically viable. A 0.01 BST tip does not require a 0.10 BST gas fee.
- **AOT-compiled aggregation**: Tip aggregation, leaderboard updates, and batch operations execute at native speed, keeping gas costs minimal for the high-frequency, low-value transactions that tipping generates.
- **Ed25519 recurring tip authorization**: Recurring tip authorizations use Ed25519 signatures for delegation, which are gas-efficient to verify on Basalt.
- **Confidential tips via Pedersen commitments**: For privacy-sensitive tipping, amounts can be committed using Pedersen commitments. The recipient can verify and claim the amount using the commitment's blinding factor, keeping the tip amount private on-chain.
- **Social profile integration**: Tips are linked to the sender's and receiver's social profiles (contract 57), building reputation and creating a visible history of generosity and community support.
- **Flat state DB for balance tracking**: Per-recipient aggregated balances benefit from FlatStateDb's O(1) caches, avoiding Merkle trie overhead for the frequent read-modify-write pattern that each tip triggers.

## Token Standards Used

- **BST-20**: Native token standard for tips denominated in BST-20 tokens (WBSLT and other fungible tokens).

## Integration Points

- **BNS (0x...1002)**: Name resolution for tipping by BNS name. Reverse lookup for displaying tip activity with human-readable names.
- **WBSLT (0x...1001)**: Wrapped BST for tip payments when using BST-20 token transfers rather than native BST value transfers.
- **Escrow (0x...1004)**: Recurring tip funds held in escrow for scheduled distribution.
- **Social Profile (contract 57)**: Tip history displayed on social profiles. Reputation score contributions from tipping activity.
- **Governance (0x...1003)**: Community governance over platform fee rates and minimum tip amounts.
- **Content Monetization (contract 59)**: Tipping as a lightweight alternative to subscription-based content monetization.

## Technical Sketch

```csharp
// ---- Data Structures ----

public struct Tip
{
    public ulong TipId;
    public Address Sender;
    public Address Recipient;
    public UInt256 Amount;
    public UInt256 FeeDeducted;
    public string Message;                  // Max 256 bytes
    public byte[] ContentHash;              // Optional: hash of referenced content
    public ulong Timestamp;
    public ulong BlockNumber;
    public bool Anonymous;
    public bool Refunded;
}

public struct RecipientBalance
{
    public Address Recipient;
    public UInt256 PendingBalance;          // Accumulated unclaimed tips
    public UInt256 TotalReceived;           // Lifetime total
    public ulong TipCount;                 // Number of tips received
    public ulong LastClaimTimestamp;
}

public struct TipperStats
{
    public Address Tipper;
    public UInt256 TotalGiven;
    public ulong TipCount;
}

public struct RecurringTip
{
    public ulong RecurringId;
    public Address Sender;
    public Address Recipient;
    public UInt256 AmountPerPeriod;
    public ulong PeriodSeconds;
    public ulong NextExecutionTime;
    public ulong EndTime;                   // 0 = indefinite
    public UInt256 TotalDeposited;          // Prepaid balance
    public UInt256 TotalDistributed;
    public bool Active;
}

public struct TipSplit
{
    public Address Recipient;
    public ulong ShareBps;                  // Basis points (out of 10000)
}

public struct LeaderboardEntry
{
    public Address User;
    public UInt256 Amount;
    public ulong Rank;
    public ulong PeriodStart;
    public ulong PeriodEnd;
}

// ---- Contract API ----

[SdkContract(TypeId = 0x0207)]
public partial class TippingContract : SdkContractBase
{
    // Storage
    private StorageMap<ulong, Tip> _tips;
    private StorageMap<Address, RecipientBalance> _balances;
    private StorageMap<Address, TipperStats> _tipperStats;
    private StorageMap<ulong, RecurringTip> _recurringTips;
    private StorageValue<ulong> _nextTipId;
    private StorageValue<ulong> _nextRecurringId;
    private StorageValue<UInt256> _minimumTip;
    private StorageValue<ulong> _platformFeeBps;        // Default 50 = 0.5%
    private StorageValue<Address> _treasury;
    private StorageValue<ulong> _refundWindowBlocks;    // Default 10

    // --- Tipping ---

    /// <summary>
    /// Send a tip to a recipient address with an optional message.
    /// Caller sends the tip amount as tx value.
    /// Platform fee is deducted and sent to treasury.
    /// Remaining amount is added to recipient's pending balance.
    /// </summary>
    public ulong SendTip(
        Address recipient,
        string message,
        byte[] contentHash,
        bool anonymous
    );

    /// <summary>
    /// Send a tip to a BNS name. Resolves the name to an address
    /// via the BNS contract, then processes the tip.
    /// </summary>
    public ulong SendTipByName(
        string bnsName,
        string message,
        byte[] contentHash,
        bool anonymous
    );

    /// <summary>
    /// Send a tip split among multiple recipients according to
    /// specified share percentages (basis points, must sum to 10000).
    /// </summary>
    public ulong SendSplitTip(
        TipSplit[] splits,
        string message,
        byte[] contentHash,
        bool anonymous
    );

    /// <summary>
    /// Refund a tip within the refund window (before finalization).
    /// Returns the full tip amount (including fee) to the sender.
    /// </summary>
    public void RefundTip(ulong tipId);

    // --- Claiming ---

    /// <summary>
    /// Claim all pending tips as a single withdrawal.
    /// Transfers the accumulated balance to the caller.
    /// </summary>
    public UInt256 ClaimBalance();

    /// <summary>
    /// Claim a partial amount from the pending balance.
    /// Useful for recipients who want to leave some balance for
    /// gas-efficient future claims.
    /// </summary>
    public void ClaimPartial(UInt256 amount);

    // --- Recurring Tips ---

    /// <summary>
    /// Set up a recurring tip. Caller deposits prepaid funds
    /// that are distributed on the specified schedule.
    /// </summary>
    public ulong CreateRecurringTip(
        Address recipient,
        UInt256 amountPerPeriod,
        ulong periodSeconds,
        ulong endTime
    );

    /// <summary>
    /// Execute a pending recurring tip. Callable by anyone when
    /// the next execution time has passed. Caller receives a small
    /// gas rebate from the prepaid funds.
    /// </summary>
    public void ExecuteRecurringTip(ulong recurringId);

    /// <summary>
    /// Cancel a recurring tip. Remaining prepaid funds are returned
    /// to the sender.
    /// </summary>
    public void CancelRecurringTip(ulong recurringId);

    /// <summary>
    /// Top up the prepaid balance for a recurring tip.
    /// </summary>
    public void TopUpRecurringTip(ulong recurringId);

    // --- Leaderboards ---

    /// <summary>
    /// Snapshot the top tippers and receivers for the current period.
    /// Callable once per period by anyone.
    /// </summary>
    public void SnapshotLeaderboard(ulong topN);

    /// <summary>
    /// Get the top tippers leaderboard for a given period.
    /// </summary>
    public LeaderboardEntry GetTopTipper(ulong periodStart, ulong rank);

    /// <summary>
    /// Get the top receivers leaderboard for a given period.
    /// </summary>
    public LeaderboardEntry GetTopReceiver(ulong periodStart, ulong rank);

    // --- View Functions ---

    public Tip GetTip(ulong tipId);
    public RecipientBalance GetBalance(Address recipient);
    public TipperStats GetTipperStats(Address tipper);
    public RecurringTip GetRecurringTip(ulong recurringId);
    public UInt256 GetMinimumTip();
    public ulong GetPlatformFeeBps();

    /// <summary>
    /// Get the total tips sent to a specific content hash.
    /// Useful for content platforms to display tip totals per post.
    /// </summary>
    public UInt256 GetContentTipTotal(byte[] contentHash);

    /// <summary>
    /// Get the number of unique tippers for a recipient.
    /// </summary>
    public ulong GetUniqueTipperCount(Address recipient);

    // --- Admin (Governance) ---

    /// <summary>
    /// Update the platform fee rate. Governance-controlled.
    /// Maximum 5% (500 bps).
    /// </summary>
    public void SetPlatformFee(ulong basisPoints);

    /// <summary>
    /// Update the minimum tip amount.
    /// </summary>
    public void SetMinimumTip(UInt256 minimum);

    /// <summary>
    /// Update the refund window duration in blocks.
    /// </summary>
    public void SetRefundWindow(ulong blocks);

    /// <summary>
    /// Update the treasury address.
    /// </summary>
    public void SetTreasury(Address newTreasury);
}
```

## Complexity

**Low** -- The core tipping logic (send, aggregate, claim) is straightforward value transfer with balance tracking. Recurring tips add moderate complexity through time-based execution and prepaid fund management. Leaderboard snapshots require sorting, but the data model is simple. The refund window introduces a brief finalization delay but does not significantly complicate the state machine. BNS resolution is a single cross-contract call.

## Priority

**P1** -- Micropayments are a foundational social finance primitive. The tipping contract drives daily engagement, supports content creators, and creates visible economic activity in the ecosystem. Its low complexity and high user-facing impact make it an early deployment candidate that benefits from the BNS and social profile infrastructure.
