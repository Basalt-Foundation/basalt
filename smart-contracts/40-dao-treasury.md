# DAO Treasury

## Category

Governance / Finance

## Summary

A governance-controlled treasury contract that manages organizational funds through proposal-based spending. It supports budget categories with per-period spending limits, streaming allocations to contributors, automatic staking of idle funds via the StakingPool system contract, multi-token support, and quarterly reporting snapshots. The DAO Treasury is the financial backbone that turns on-chain governance decisions into real fund movements while maintaining accountability and transparency.

## Why It's Useful

- **Programmatic fund management**: DAOs need a treasury that automatically enforces spending limits, approval workflows, and budget categories rather than relying on ad hoc multisig decisions.
- **Contributor payments**: Streaming allocations allow continuous payment to contributors (developers, marketers, community managers) without requiring monthly governance votes for recurring expenses.
- **Yield on idle funds**: Treasuries often hold large reserves that sit idle. Auto-staking via StakingPool generates yield that grows the treasury without governance overhead.
- **Budget accountability**: Per-category spending limits (engineering, marketing, operations, grants) prevent any single budget area from consuming disproportionate resources.
- **Transparent reporting**: On-chain quarterly snapshots provide a public, auditable record of treasury inflows, outflows, and balances, essential for DAO member confidence.
- **Multi-token management**: DAOs accumulate various token types (native BST, BST-20 tokens, NFTs as assets). The treasury must manage all of them under a unified governance umbrella.
- **Grant distribution**: Community grant programs require structured disbursement with milestones and clawback provisions, which the treasury natively supports.

## Key Features

- Governance-gated spending: all withdrawals require a passing governance proposal (integrates with the Governance contract via cross-contract calls)
- Budget categories: named categories (e.g., "engineering", "marketing", "grants") with per-epoch spending caps in both native BST and BST-20 tokens
- Streaming allocations: create time-based payment streams to contributor addresses, with configurable start block, end block, and total amount; recipients claim accrued funds at any time
- Auto-staking: configurable percentage of idle native BST is staked to the StakingPool, with automatic reward claiming on a heartbeat
- Multi-token support: holds native BST and any BST-20 token; tracks balances per token address
- Quarterly snapshots: at configurable epoch boundaries, a snapshot event is emitted with total balances, inflows, outflows, and per-category spend for the period
- Emergency withdrawal: a supermajority governance vote can bypass category limits for emergency fund movements
- Deposit tracking: all incoming funds are logged with sender and purpose metadata
- Clawback on streams: governance can cancel an active stream and return unvested funds to the treasury
- Role-based access: treasurer role (appointed by governance) can create streams and manage budgets within pre-approved limits

## Basalt-Specific Advantages

- **Cross-contract governance integration**: Basalt's `Context.CallContract<T>()` allows the treasury to directly verify governance proposal status and execute approved spending, with reentrancy protection built into the runtime.
- **StakingPool auto-staking**: Native integration with the StakingPool system contract (0x...1005) means idle fund staking is a simple cross-contract call, not an external protocol dependency.
- **ZK compliance on disbursements**: Outgoing payments can be verified against the SchemaRegistry/IssuerRegistry to ensure recipients meet KYC/compliance requirements before funds are released, critical for regulated DAOs.
- **BST-3525 semi-fungible tokens for budget tranches**: Budget allocations can be represented as BST-3525 SFTs where each slot represents a budget category and the value represents the remaining allocation, enabling composable budget management.
- **BST-4626 vault pattern for yield**: Idle funds can be deposited into BST-4626 vaults for yield strategies beyond simple staking, with share-based accounting that integrates naturally with the treasury's balance tracking.
- **AOT-compiled deterministic execution**: Treasury operations (especially complex multi-step spend + stake + claim flows) execute with predictable gas costs due to AOT compilation, preventing unexpected reverts on large operations.
- **UInt256 precision**: All amounts use `UInt256`, preventing overflow on large treasury balances that could exceed uint64 limits on active chains.

## Token Standards Used

- **BST-20**: Treasury holds and disburses BST-20 fungible tokens, tracks per-token balances
- **BST-721**: Treasury can hold NFTs as organizational assets (domain names, art, collectibles)
- **BST-3525**: Budget tranches represented as semi-fungible tokens with slot = category, value = remaining allocation
- **BST-4626**: Idle fund yield optimization via vault deposits

## Integration Points

- **Governance (0x...1005 area)**: Primary integration -- all spending proposals are validated against the Governance contract. The treasury checks proposal status before executing any withdrawal.
- **StakingPool (0x...1005)**: Auto-staking of idle native BST funds. The treasury creates a pool, delegates idle funds, claims rewards on heartbeat.
- **Escrow (0x...1003)**: Milestone-based grant payments can use the Escrow contract for conditional releases.
- **BNS (0x...1002)**: The treasury can own and manage BNS names on behalf of the DAO.
- **BridgeETH (0x...1008)**: Cross-chain treasury management -- bridge funds to/from Ethereum as approved by governance.
- **SchemaRegistry / IssuerRegistry**: Compliance verification on outgoing disbursements.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// DAO Treasury -- governance-controlled fund management with budgets,
/// streaming payments, auto-staking, and quarterly reporting.
/// </summary>
[BasaltContract]
public partial class DaoTreasury
{
    // --- Governance integration ---
    private readonly byte[] _governanceAddress;
    private readonly byte[] _stakingPoolAddress;

    // --- Roles ---
    private readonly StorageMap<string, bool> _treasurers;  // address hex -> isTreasurer
    private readonly StorageMap<string, string> _admin;

    // --- Budget categories ---
    private readonly StorageValue<ulong> _nextCategoryId;
    private readonly StorageMap<string, string> _categoryNames;            // catId -> name
    private readonly StorageMap<string, UInt256> _categoryEpochLimit;      // catId -> max spend per epoch
    private readonly StorageMap<string, UInt256> _categoryEpochSpent;      // "catId:epoch" -> spent this epoch
    private readonly StorageValue<ulong> _epochLength;                     // blocks per budget epoch

    // --- Streaming allocations ---
    private readonly StorageValue<ulong> _nextStreamId;
    private readonly StorageMap<string, string> _streamRecipients;         // streamId -> recipient hex
    private readonly StorageMap<string, UInt256> _streamTotalAmounts;      // streamId -> total
    private readonly StorageMap<string, ulong> _streamStartBlocks;         // streamId -> start
    private readonly StorageMap<string, ulong> _streamEndBlocks;           // streamId -> end
    private readonly StorageMap<string, UInt256> _streamClaimed;           // streamId -> claimed so far
    private readonly StorageMap<string, string> _streamStatus;             // streamId -> "active"|"cancelled"|"completed"
    private readonly StorageMap<string, string> _streamCategories;         // streamId -> catId

    // --- Auto-staking ---
    private readonly StorageValue<ulong> _stakingPoolId;
    private readonly StorageValue<uint> _autoStakePercentage;              // 0-100
    private readonly StorageValue<UInt256> _stakedBalance;

    // --- Deposits ---
    private readonly StorageValue<ulong> _nextDepositId;
    private readonly StorageMap<string, string> _depositSenders;
    private readonly StorageMap<string, UInt256> _depositAmounts;
    private readonly StorageMap<string, string> _depositPurpose;

    // --- Snapshots ---
    private readonly StorageValue<ulong> _lastSnapshotEpoch;
    private readonly StorageMap<string, UInt256> _snapshotBalance;         // epoch -> balance
    private readonly StorageMap<string, UInt256> _snapshotInflows;         // epoch -> total inflows
    private readonly StorageMap<string, UInt256> _snapshotOutflows;        // epoch -> total outflows

    // --- Epoch tracking ---
    private readonly StorageValue<UInt256> _epochInflows;
    private readonly StorageValue<UInt256> _epochOutflows;

    public DaoTreasury(byte[] governanceAddress, ulong epochLengthBlocks = 216000,
        uint autoStakePercentage = 50)
    {
        _governanceAddress = governanceAddress;
        _stakingPoolAddress = new byte[20];
        _stakingPoolAddress[18] = 0x10;
        _stakingPoolAddress[19] = 0x05;

        _treasurers = new StorageMap<string, bool>("dt_treas");
        _admin = new StorageMap<string, string>("dt_admin");
        _nextCategoryId = new StorageValue<ulong>("dt_ncat");
        _categoryNames = new StorageMap<string, string>("dt_cname");
        _categoryEpochLimit = new StorageMap<string, UInt256>("dt_clim");
        _categoryEpochSpent = new StorageMap<string, UInt256>("dt_cspent");
        _epochLength = new StorageValue<ulong>("dt_epoch");
        _nextStreamId = new StorageValue<ulong>("dt_nstr");
        _streamRecipients = new StorageMap<string, string>("dt_srec");
        _streamTotalAmounts = new StorageMap<string, UInt256>("dt_samt");
        _streamStartBlocks = new StorageMap<string, ulong>("dt_sstart");
        _streamEndBlocks = new StorageMap<string, ulong>("dt_send");
        _streamClaimed = new StorageMap<string, UInt256>("dt_sclaim");
        _streamStatus = new StorageMap<string, string>("dt_ssts");
        _streamCategories = new StorageMap<string, string>("dt_scat");
        _stakingPoolId = new StorageValue<ulong>("dt_spid");
        _autoStakePercentage = new StorageValue<uint>("dt_aspct");
        _stakedBalance = new StorageValue<UInt256>("dt_staked");
        _nextDepositId = new StorageValue<ulong>("dt_ndep");
        _depositSenders = new StorageMap<string, string>("dt_dsnd");
        _depositAmounts = new StorageMap<string, UInt256>("dt_damt");
        _depositPurpose = new StorageMap<string, string>("dt_dpur");
        _lastSnapshotEpoch = new StorageValue<ulong>("dt_lsnap");
        _snapshotBalance = new StorageMap<string, UInt256>("dt_snapbal");
        _snapshotInflows = new StorageMap<string, UInt256>("dt_snapin");
        _snapshotOutflows = new StorageMap<string, UInt256>("dt_snapout");
        _epochInflows = new StorageValue<UInt256>("dt_ein");
        _epochOutflows = new StorageValue<UInt256>("dt_eout");

        _admin.Set("admin", Convert.ToHexString(Context.Caller));
        _epochLength.Set(epochLengthBlocks);
        _autoStakePercentage.Set(autoStakePercentage);
    }

    // ===================== Deposits =====================

    [BasaltEntrypoint]
    public ulong Deposit(string purpose)
    {
        Context.Require(!Context.TxValue.IsZero, "TREASURY: must send value");
        var id = _nextDepositId.Get();
        _nextDepositId.Set(id + 1);

        var key = id.ToString();
        _depositSenders.Set(key, Convert.ToHexString(Context.Caller));
        _depositAmounts.Set(key, Context.TxValue);
        _depositPurpose.Set(key, purpose);
        _epochInflows.Set(_epochInflows.Get() + Context.TxValue);

        Context.Emit(new TreasuryDepositEvent
        {
            DepositId = id, Sender = Context.Caller,
            Amount = Context.TxValue, Purpose = purpose
        });
        return id;
    }

    // ===================== Budget Categories =====================

    [BasaltEntrypoint]
    public ulong CreateCategory(string name, UInt256 epochLimit)
    {
        RequireGovernance();
        var id = _nextCategoryId.Get();
        _nextCategoryId.Set(id + 1);

        _categoryNames.Set(id.ToString(), name);
        _categoryEpochLimit.Set(id.ToString(), epochLimit);

        Context.Emit(new CategoryCreatedEvent { CategoryId = id, Name = name, EpochLimit = epochLimit });
        return id;
    }

    [BasaltEntrypoint]
    public void UpdateCategoryLimit(ulong categoryId, UInt256 newLimit)
    {
        RequireGovernance();
        var key = categoryId.ToString();
        Context.Require(!string.IsNullOrEmpty(_categoryNames.Get(key)), "TREASURY: category not found");
        _categoryEpochLimit.Set(key, newLimit);
    }

    // ===================== Spending (Governance-Gated) =====================

    [BasaltEntrypoint]
    public void Spend(ulong categoryId, byte[] recipient, UInt256 amount, ulong governanceProposalId)
    {
        // Verify governance proposal passed
        var status = Context.CallContract<string>(_governanceAddress, "GetStatus", governanceProposalId);
        Context.Require(status == "executed", "TREASURY: proposal not executed");

        var catKey = categoryId.ToString();
        Context.Require(!string.IsNullOrEmpty(_categoryNames.Get(catKey)), "TREASURY: category not found");

        // Check budget limit
        var epoch = Context.BlockHeight / _epochLength.Get();
        var spentKey = catKey + ":" + epoch.ToString();
        var spent = _categoryEpochSpent.Get(spentKey);
        var limit = _categoryEpochLimit.Get(catKey);
        Context.Require(spent + amount <= limit, "TREASURY: exceeds category budget");

        _categoryEpochSpent.Set(spentKey, spent + amount);
        _epochOutflows.Set(_epochOutflows.Get() + amount);

        Context.TransferNative(recipient, amount);

        Context.Emit(new TreasurySpendEvent
        {
            CategoryId = categoryId, Recipient = recipient,
            Amount = amount, ProposalId = governanceProposalId
        });
    }

    // ===================== Streaming Allocations =====================

    [BasaltEntrypoint]
    public ulong CreateStream(byte[] recipient, UInt256 totalAmount,
        ulong startBlock, ulong endBlock, ulong categoryId)
    {
        RequireTreasurerOrGovernance();
        Context.Require(endBlock > startBlock, "TREASURY: invalid stream period");
        Context.Require(!totalAmount.IsZero, "TREASURY: zero amount");

        var id = _nextStreamId.Get();
        _nextStreamId.Set(id + 1);

        var key = id.ToString();
        _streamRecipients.Set(key, Convert.ToHexString(recipient));
        _streamTotalAmounts.Set(key, totalAmount);
        _streamStartBlocks.Set(key, startBlock);
        _streamEndBlocks.Set(key, endBlock);
        _streamClaimed.Set(key, UInt256.Zero);
        _streamStatus.Set(key, "active");
        _streamCategories.Set(key, categoryId.ToString());

        Context.Emit(new StreamCreatedEvent
        {
            StreamId = id, Recipient = recipient,
            TotalAmount = totalAmount, StartBlock = startBlock, EndBlock = endBlock
        });
        return id;
    }

    [BasaltEntrypoint]
    public void ClaimStream(ulong streamId)
    {
        var key = streamId.ToString();
        Context.Require(_streamStatus.Get(key) == "active", "TREASURY: stream not active");

        var recipientHex = _streamRecipients.Get(key);
        Context.Require(Convert.ToHexString(Context.Caller) == recipientHex,
            "TREASURY: not stream recipient");

        var total = _streamTotalAmounts.Get(key);
        var startBlock = _streamStartBlocks.Get(key);
        var endBlock = _streamEndBlocks.Get(key);
        var claimed = _streamClaimed.Get(key);

        var currentBlock = Context.BlockHeight;
        if (currentBlock < startBlock) return;

        var elapsed = currentBlock >= endBlock ? endBlock - startBlock : currentBlock - startBlock;
        var duration = endBlock - startBlock;
        var vested = total * new UInt256(elapsed) / new UInt256(duration);
        var claimable = vested > claimed ? vested - claimed : UInt256.Zero;

        Context.Require(!claimable.IsZero, "TREASURY: nothing to claim");

        _streamClaimed.Set(key, claimed + claimable);
        _epochOutflows.Set(_epochOutflows.Get() + claimable);

        if (claimed + claimable >= total)
            _streamStatus.Set(key, "completed");

        Context.TransferNative(Convert.FromHexString(recipientHex), claimable);

        Context.Emit(new StreamClaimedEvent
        {
            StreamId = streamId, Recipient = Context.Caller, Amount = claimable
        });
    }

    [BasaltEntrypoint]
    public void CancelStream(ulong streamId)
    {
        RequireGovernance();
        var key = streamId.ToString();
        Context.Require(_streamStatus.Get(key) == "active", "TREASURY: stream not active");
        _streamStatus.Set(key, "cancelled");

        Context.Emit(new StreamCancelledEvent { StreamId = streamId });
    }

    // ===================== Auto-Staking =====================

    [BasaltEntrypoint]
    public void StakeIdleFunds()
    {
        RequireTreasurerOrGovernance();
        // Stake configured percentage of available balance to StakingPool
        var poolId = _stakingPoolId.Get();
        var percentage = _autoStakePercentage.Get();
        // Staking logic via cross-contract call to StakingPool.Delegate(poolId)
        // Amount set via Context.TxValue on the call
        Context.Emit(new AutoStakeEvent { PoolId = poolId, Percentage = percentage });
    }

    [BasaltEntrypoint]
    public void ClaimStakingRewards()
    {
        RequireTreasurerOrGovernance();
        var poolId = _stakingPoolId.Get();
        Context.CallContract(_stakingPoolAddress, "ClaimRewards", poolId);

        Context.Emit(new StakingRewardsClaimedEvent { PoolId = poolId });
    }

    // ===================== Snapshots =====================

    [BasaltEntrypoint]
    public void TakeSnapshot()
    {
        var currentEpoch = Context.BlockHeight / _epochLength.Get();
        Context.Require(currentEpoch > _lastSnapshotEpoch.Get(), "TREASURY: snapshot already taken");

        var epochKey = currentEpoch.ToString();
        // In a real implementation, balance would be read from contract's account
        _snapshotInflows.Set(epochKey, _epochInflows.Get());
        _snapshotOutflows.Set(epochKey, _epochOutflows.Get());
        _lastSnapshotEpoch.Set(currentEpoch);

        // Reset epoch counters
        _epochInflows.Set(UInt256.Zero);
        _epochOutflows.Set(UInt256.Zero);

        Context.Emit(new SnapshotTakenEvent { Epoch = currentEpoch });
    }

    // ===================== Admin =====================

    [BasaltEntrypoint]
    public void AppointTreasurer(byte[] treasurer)
    {
        RequireGovernance();
        _treasurers.Set(Convert.ToHexString(treasurer), true);
    }

    [BasaltEntrypoint]
    public void RemoveTreasurer(byte[] treasurer)
    {
        RequireGovernance();
        _treasurers.Set(Convert.ToHexString(treasurer), false);
    }

    [BasaltEntrypoint]
    public void SetAutoStakePercentage(uint percentage)
    {
        RequireGovernance();
        Context.Require(percentage <= 100, "TREASURY: invalid percentage");
        _autoStakePercentage.Set(percentage);
    }

    // ===================== Views =====================

    [BasaltView]
    public string GetCategoryName(ulong categoryId) => _categoryNames.Get(categoryId.ToString()) ?? "";

    [BasaltView]
    public UInt256 GetCategoryLimit(ulong categoryId) => _categoryEpochLimit.Get(categoryId.ToString());

    [BasaltView]
    public UInt256 GetCategorySpent(ulong categoryId, ulong epoch)
        => _categoryEpochSpent.Get(categoryId.ToString() + ":" + epoch.ToString());

    [BasaltView]
    public string GetStreamStatus(ulong streamId) => _streamStatus.Get(streamId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetStreamClaimed(ulong streamId) => _streamClaimed.Get(streamId.ToString());

    [BasaltView]
    public UInt256 GetSnapshotInflows(ulong epoch) => _snapshotInflows.Get(epoch.ToString());

    [BasaltView]
    public UInt256 GetSnapshotOutflows(ulong epoch) => _snapshotOutflows.Get(epoch.ToString());

    [BasaltView]
    public bool IsTreasurer(byte[] addr) => _treasurers.Get(Convert.ToHexString(addr));

    // ===================== Internal =====================

    private void RequireGovernance()
    {
        // Caller must be the governance contract
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(_governanceAddress),
            "TREASURY: not governance");
    }

    private void RequireTreasurerOrGovernance()
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(
            _treasurers.Get(callerHex) ||
            callerHex == Convert.ToHexString(_governanceAddress),
            "TREASURY: not authorized");
    }
}

// ===================== Events =====================

[BasaltEvent]
public class TreasuryDepositEvent
{
    [Indexed] public ulong DepositId { get; set; }
    [Indexed] public byte[] Sender { get; set; } = null!;
    public UInt256 Amount { get; set; }
    public string Purpose { get; set; } = "";
}

[BasaltEvent]
public class TreasurySpendEvent
{
    [Indexed] public ulong CategoryId { get; set; }
    [Indexed] public byte[] Recipient { get; set; } = null!;
    public UInt256 Amount { get; set; }
    public ulong ProposalId { get; set; }
}

[BasaltEvent]
public class CategoryCreatedEvent
{
    [Indexed] public ulong CategoryId { get; set; }
    public string Name { get; set; } = "";
    public UInt256 EpochLimit { get; set; }
}

[BasaltEvent]
public class StreamCreatedEvent
{
    [Indexed] public ulong StreamId { get; set; }
    [Indexed] public byte[] Recipient { get; set; } = null!;
    public UInt256 TotalAmount { get; set; }
    public ulong StartBlock { get; set; }
    public ulong EndBlock { get; set; }
}

[BasaltEvent]
public class StreamClaimedEvent
{
    [Indexed] public ulong StreamId { get; set; }
    [Indexed] public byte[] Recipient { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class StreamCancelledEvent
{
    [Indexed] public ulong StreamId { get; set; }
}

[BasaltEvent]
public class AutoStakeEvent
{
    public ulong PoolId { get; set; }
    public uint Percentage { get; set; }
}

[BasaltEvent]
public class StakingRewardsClaimedEvent
{
    public ulong PoolId { get; set; }
}

[BasaltEvent]
public class SnapshotTakenEvent
{
    [Indexed] public ulong Epoch { get; set; }
}
```

## Complexity

**High** -- The DAO Treasury integrates multiple subsystems (governance verification, budget management, streaming payments, auto-staking, snapshots) into a single contract. Each subsystem has its own state management and invariants. The streaming payment vesting calculation must handle edge cases (partial epochs, cancellations mid-stream). Cross-contract calls to Governance and StakingPool add integration complexity and require careful reentrancy handling.

## Priority

**P1** -- While the MultisigWallet provides basic shared custody, a full DAO Treasury is the natural next step for any protocol or community that wants programmatic fund management. It is a high-priority contract for ecosystem maturity, though it depends on the Governance contract already being deployed and battle-tested. Many DeFi protocols and DAOs will need this within the first year of mainnet.
