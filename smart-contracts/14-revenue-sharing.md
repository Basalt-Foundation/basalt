# Revenue Sharing / Dividend Distributor

## Category

DeFi / Treasury Management / Yield Distribution

## Summary

A contract that collects protocol revenue from multiple sources and distributes it proportionally to eligible token holders or stakers. It supports both claim-based (pull) and push-based distribution models, with epoch-based snapshots ensuring fair pro-rata allocation. This is the standard mechanism for DAOs and protocols to return earnings to their stakeholders.

## Why It's Useful

- **Protocol Monetization**: As Basalt's DeFi ecosystem grows, protocols will generate fees from trading, lending, liquidations, and other activities. This contract provides the standard plumbing for returning those fees to token holders.
- **Stakeholder Alignment**: Revenue sharing aligns the incentives of governance token holders with the long-term success of the protocol. Token holders who participate in governance are directly rewarded when the protocol performs well.
- **DAO Treasury Distribution**: DAOs accumulate treasury funds from various sources. This contract provides a transparent, auditable, and automated way to distribute those funds to members based on their stake or token holdings.
- **Passive Income for Holders**: Token holders earn dividends simply by holding or staking their tokens, without needing to actively participate in liquidity provision or other DeFi strategies.
- **Composable Revenue Streams**: Multiple protocol contracts can direct their fees to a single Revenue Sharing contract, creating a unified dividend stream from diverse revenue sources.
- **Transparent Accounting**: All revenue inflows, distribution calculations, and claim histories are fully on-chain, providing complete auditability that off-chain revenue sharing mechanisms cannot match.

## Key Features

- **Epoch-Based Snapshots**: At the start of each distribution epoch, the contract snapshots total eligible stakes. Revenue collected during that epoch is divided proportionally based on the snapshot, preventing gaming by depositing just before distribution.
- **Claim-Based (Pull) Distribution**: Token holders call a `Claim()` function to withdraw their accumulated dividends. Unclaimed dividends roll over and remain claimable indefinitely (up to a configurable expiry).
- **Push-Based Distribution**: An optional mode where the contract (or a keeper) iterates through eligible holders and pushes dividends directly. Useful for small holder sets where gas is not a concern.
- **Multi-Token Revenue**: The contract can collect revenue in BST (native token) as well as any BST-20 token. Each revenue token has its own distribution accounting.
- **Weighted Distribution**: Distribution weight can be based on raw token balance, staked amount, time-weighted stake (rewarding longer stakers), or governance participation score.
- **Revenue Source Registration**: Admin registers approved revenue sources (contract addresses). Only registered sources can deposit revenue, preventing spam deposits.
- **Minimum Distribution Threshold**: Revenue is only distributed when the accumulated amount exceeds a configurable minimum, avoiding dust distributions that cost more in gas than they are worth.
- **Claim Expiry**: Optionally, unclaimed dividends expire after a configurable number of epochs. Expired dividends are recycled back into the next distribution pool.
- **Emergency Withdrawal**: Admin can pause distribution and withdraw funds in case of a critical bug, subject to a timelock enforced by the Governance contract.

## Basalt-Specific Advantages

- **Pedersen Commitment Confidentiality**: Individual dividend amounts can be hidden using Pedersen commitments. The contract publishes commitments to each holder's share, and holders prove their entitlement via range proofs without revealing the exact amount to other observers. This is valuable for institutional participants who do not want their revenue share publicly visible.
- **BST-4626 Vault Integration**: Unclaimed dividends can be automatically deposited into a BST-4626 vault to earn yield while waiting to be claimed. This means even lazy claimers earn returns on their pending dividends.
- **AOT-Compiled Snapshot Logic**: Epoch snapshot calculations involving iteration over staker maps execute as native compiled code. On EVM chains, iterating over large maps is prohibitively expensive; on Basalt, the AOT compilation makes this feasible even for thousands of stakers.
- **BLS Aggregate Signatures for Batch Claims**: Multiple holders can authorize a batch claim transaction signed with BLS aggregate signatures, allowing a single keeper transaction to process claims for many holders simultaneously with minimal signature overhead.
- **ZK Compliance for Regulated Distributions**: When the revenue originates from regulated activities, the contract can require ZK compliance proofs from claimants, ensuring that dividends are only distributed to verified participants without revealing their identity on-chain.
- **Ed25519 Signature Speed**: High-frequency revenue deposits from active protocols benefit from Basalt's fast Ed25519 signature verification, reducing the per-deposit overhead.
- **BLAKE3 Epoch Hashing**: Epoch identifiers and snapshot commitments use BLAKE3 for fast, collision-resistant hashing, ensuring snapshot integrity.

## Token Standards Used

- **BST-20**: Revenue tokens are BST-20 fungible tokens. The governance/dividend token whose holders receive distributions is also BST-20.
- **BST-4626 (Vault)**: The auto-compound feature wraps pending dividends in a BST-4626 vault to earn yield on idle funds.
- **BST-3525 (SFT)**: Distribution receipts can be represented as BST-3525 semi-fungible tokens, where each token slot represents a specific epoch's dividend entitlement. These receipts are transferable, allowing holders to sell their pending dividend claims.

## Integration Points

- **StakingPool (0x...1005)**: The primary source of stake-weighted distribution data. The contract queries StakingPool for each holder's staked amount at snapshot time.
- **Governance (0x...1002)**: Parameter changes (epoch length, minimum threshold, expiry period) are governed via Governance proposals. Emergency withdrawal is subject to Governance timelock.
- **Escrow (0x...1003)**: Revenue from protocol fees can be held in Escrow before being released to the distributor, providing a buffer and audit trail.
- **BNS (0x...1001)**: The contract registers a BNS name (e.g., "revenue.protocol.bst") for easy discoverability. Beneficiaries can also be resolved by BNS name.
- **SchemaRegistry (0x...1006)**: When ZK compliance is enabled, credential schemas for claimant verification are referenced from the SchemaRegistry.
- **IssuerRegistry (0x...1007)**: Validates the issuers of compliance credentials presented by claimants.
- **BridgeETH (0x...1008)**: Revenue collected on Ethereum (via the bridge) can be forwarded to this contract for distribution to Basalt-side holders.

## Technical Sketch

```csharp
// Contract type ID: 0x0109
[BasaltContract(0x0109)]
public partial class RevenueSharing : SdkContract, IDispatchable
{
    // --- Storage ---

    private StorageValue<Address> _admin;
    private StorageValue<Address> _dividendToken;         // BST-20 token whose holders earn dividends
    private StorageValue<ulong> _currentEpoch;
    private StorageValue<ulong> _epochLengthBlocks;
    private StorageValue<ulong> _lastSnapshotBlock;
    private StorageValue<UInt256> _minDistributionThreshold;
    private StorageValue<ulong> _claimExpiryEpochs;       // 0 = never expires
    private StorageValue<bool> _paused;
    private StorageValue<bool> _complianceRequired;

    // Per-epoch snapshots
    private StorageMap<ulong, UInt256> _epochTotalStake;       // epoch -> total eligible stake
    private StorageMap<ulong, UInt256> _epochRevenuePool;      // epoch -> total revenue collected

    // Per-account tracking
    private StorageMap<Address, ulong> _lastClaimedEpoch;      // account -> last epoch claimed
    private StorageMap<Address, UInt256> _cumulativeClaimed;    // account -> total claimed all time

    // Revenue source whitelist
    private StorageMap<Address, bool> _approvedSources;

    // Epoch-account stake snapshots (key: keccak(epoch ++ address))
    // In practice, stored as a nested map or composite key
    private StorageMap<Address, UInt256> _snapshotStakes;

    // --- Constructor ---

    public void Initialize(
        Address admin,
        Address dividendToken,
        ulong epochLengthBlocks,
        UInt256 minDistributionThreshold,
        ulong claimExpiryEpochs,
        bool complianceRequired)
    {
        _admin.Set(admin);
        _dividendToken.Set(dividendToken);
        _epochLengthBlocks.Set(epochLengthBlocks);
        _minDistributionThreshold.Set(minDistributionThreshold);
        _claimExpiryEpochs.Set(claimExpiryEpochs);
        _complianceRequired.Set(complianceRequired);
        _currentEpoch.Set(1);
        _lastSnapshotBlock.Set(Context.BlockNumber);
    }

    // --- Revenue Deposit ---

    public void DepositRevenue()
    {
        UInt256 amount = Context.TxValue;
        Require(!amount.IsZero, "Must deposit non-zero amount");
        Require(_approvedSources.Get(Context.Caller), "Not an approved revenue source");
        Require(!_paused.Get(), "Distribution paused");

        ulong epoch = _currentEpoch.Get();
        UInt256 current = _epochRevenuePool.Get(epoch);
        _epochRevenuePool.Set(epoch, current + amount);
    }

    // --- Epoch Management ---

    public void AdvanceEpoch()
    {
        ulong currentBlock = Context.BlockNumber;
        ulong lastSnapshot = _lastSnapshotBlock.Get();
        ulong epochLength = _epochLengthBlocks.Get();
        Require(currentBlock >= lastSnapshot + epochLength, "Epoch not yet elapsed");

        ulong epoch = _currentEpoch.Get();

        // Snapshot total stake from StakingPool
        UInt256 totalStake = QueryTotalEligibleStake();
        _epochTotalStake.Set(epoch, totalStake);

        // Advance to next epoch
        _currentEpoch.Set(epoch + 1);
        _lastSnapshotBlock.Set(currentBlock);
    }

    // --- Claim ---

    public UInt256 Claim()
    {
        Require(!_paused.Get(), "Distribution paused");

        if (_complianceRequired.Get())
            RequireCompliance(Context.Caller);

        Address caller = Context.Caller;
        ulong lastClaimed = _lastClaimedEpoch.Get(caller);
        ulong currentEpoch = _currentEpoch.Get();
        ulong expiryEpochs = _claimExpiryEpochs.Get();

        UInt256 totalOwed = UInt256.Zero;

        // Calculate from the epoch after last claimed up to (currentEpoch - 1)
        // Current epoch is still accumulating, so not yet distributable
        ulong startEpoch = lastClaimed + 1;
        if (expiryEpochs > 0 && currentEpoch > expiryEpochs + 1)
        {
            ulong earliest = currentEpoch - expiryEpochs;
            if (startEpoch < earliest)
                startEpoch = earliest;
        }

        for (ulong e = startEpoch; e < currentEpoch; e++)
        {
            UInt256 epochRevenue = _epochRevenuePool.Get(e);
            UInt256 epochTotalStake = _epochTotalStake.Get(e);
            if (epochTotalStake.IsZero || epochRevenue.IsZero)
                continue;

            UInt256 holderStake = GetSnapshotStake(caller, e);
            if (holderStake.IsZero)
                continue;

            UInt256 share = epochRevenue * holderStake / epochTotalStake;
            totalOwed += share;
        }

        Require(!totalOwed.IsZero, "Nothing to claim");

        _lastClaimedEpoch.Set(caller, currentEpoch - 1);
        _cumulativeClaimed.Set(caller,
            _cumulativeClaimed.Get(caller) + totalOwed);

        Context.TransferNative(caller, totalOwed);

        return totalOwed;
    }

    public UInt256 GetPendingDividends(Address account)
    {
        ulong lastClaimed = _lastClaimedEpoch.Get(account);
        ulong currentEpoch = _currentEpoch.Get();

        UInt256 total = UInt256.Zero;
        for (ulong e = lastClaimed + 1; e < currentEpoch; e++)
        {
            UInt256 epochRevenue = _epochRevenuePool.Get(e);
            UInt256 epochTotalStake = _epochTotalStake.Get(e);
            if (epochTotalStake.IsZero || epochRevenue.IsZero)
                continue;

            UInt256 holderStake = GetSnapshotStake(account, e);
            UInt256 share = epochRevenue * holderStake / epochTotalStake;
            total += share;
        }
        return total;
    }

    // --- Push Distribution ---

    public ulong PushDistribute(ulong epoch, ulong batchOffset, ulong batchSize)
    {
        Require(epoch < _currentEpoch.Get(), "Epoch not finalized");
        // Iterate over holders in batch and push their share
        // Returns number of holders processed
        // Keeper calls this repeatedly until all holders are processed
        return 0; // placeholder
    }

    // --- Admin ---

    public void RegisterRevenueSource(Address source)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _approvedSources.Set(source, true);
    }

    public void RemoveRevenueSource(Address source)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _approvedSources.Set(source, false);
    }

    public void Pause()
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _paused.Set(true);
    }

    public void Unpause()
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _paused.Set(false);
    }

    public void UpdateAdmin(Address newAdmin)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _admin.Set(newAdmin);
    }

    // --- Query ---

    public ulong CurrentEpoch() => _currentEpoch.Get();
    public UInt256 EpochRevenue(ulong epoch) => _epochRevenuePool.Get(epoch);
    public UInt256 EpochTotalStake(ulong epoch) => _epochTotalStake.Get(epoch);
    public UInt256 CumulativeClaimed(Address account) => _cumulativeClaimed.Get(account);
    public ulong LastClaimedEpoch(Address account) => _lastClaimedEpoch.Get(account);
    public bool IsApprovedSource(Address source) => _approvedSources.Get(source);

    // --- Internal ---

    private UInt256 QueryTotalEligibleStake()
    {
        // Cross-contract call to StakingPool to get total staked amount
        // In practice, this reads from StakingPool's storage
        return UInt256.Zero; // placeholder
    }

    private UInt256 GetSnapshotStake(Address account, ulong epoch)
    {
        // Returns the account's stake at the time of the epoch snapshot
        // Could read from StakingPool's historical data or from local snapshot
        return _snapshotStakes.Get(account); // simplified
    }

    private void RequireCompliance(Address account)
    {
        // Validate ZK compliance proof via SchemaRegistry + IssuerRegistry
    }
}
```

## Complexity

**Medium** -- The core distribution math (proportional share calculation) is straightforward. The complexity arises from epoch management, snapshot consistency (ensuring snapshots accurately reflect stake at the epoch boundary), handling claim expiry and recycling, and supporting both pull and push distribution modes. Multi-token revenue tracking and cross-contract queries to StakingPool add additional integration complexity. The push distribution batching logic must carefully handle gas limits and partial progress.

## Priority

**P1** -- Revenue sharing is a critical building block for any protocol with a governance token. Without a standard revenue distribution mechanism, each protocol must build its own, leading to fragmentation and inconsistency. This contract is essential for DAO treasuries, protocol fee distribution, and aligning stakeholder incentives. It should be available before or alongside the Governance contract's maturation, as many governance proposals will involve directing revenue flows.
