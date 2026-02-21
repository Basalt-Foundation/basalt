# Protocol Fee Distributor

## Category

DeFi / Protocol Infrastructure / Staking Rewards

## Summary

A centralized fee collection and distribution contract that aggregates fees from multiple protocol contracts (DEX, lending, bridge, synthetic assets, etc.) and distributes them to governance token stakers based on their time-weighted stake. The contract uses epoch-based snapshots to calculate fair distributions, supports auto-compounding via a BST-4626 vault, and provides a unified interface for all protocol revenue flows. It is the backbone of the "stake to earn protocol fees" value proposition.

## Why It's Useful

- **Unified Fee Aggregation**: As the Basalt DeFi ecosystem grows, dozens of protocol contracts will generate fees independently. Without a centralized distributor, each protocol must build its own distribution mechanism, leading to fragmentation, inconsistency, and poor user experience. This contract provides a single point of aggregation.
- **Staker Incentive Alignment**: The fee distributor directly rewards governance token stakers with a share of all protocol revenue. This creates a strong incentive to stake and participate in governance, as the financial returns are directly tied to the ecosystem's success.
- **Time-Weighted Fairness**: By using time-weighted stake (not just snapshot-at-epoch-boundary), the contract rewards consistent stakers more than those who stake briefly around distribution events. This discourages stake-and-dump behavior.
- **Auto-Compound Efficiency**: The vault integration allows stakers to automatically reinvest their fee earnings, compounding their position without manual intervention. This is the DeFi equivalent of dividend reinvestment plans (DRIPs).
- **Protocol Revenue Visibility**: Aggregating all fees into a single contract provides a transparent, real-time view of total protocol revenue. This is valuable for governance decision-making, investor relations, and ecosystem health monitoring.
- **Keeper-Free Distribution**: Unlike push-based distribution models that require keepers to trigger payouts, the claim-based model allows stakers to withdraw their earnings at any time with no external dependency.
- **Multi-Token Fee Support**: Protocols may generate fees in different tokens (BST, stablecoins, synthetic tokens). The distributor handles multi-token accounting, distributing each fee token proportionally.

## Key Features

- **Protocol Registration**: An admin (or governance) registers protocol contracts as approved fee sources. Only registered protocols can deposit fees into the distributor.
- **Epoch-Based Snapshots**: At configurable intervals, the contract snapshots the total time-weighted stake. Fees deposited during an epoch are distributed based on that epoch's snapshot.
- **Time-Weighted Stake Calculation**: A staker's weight is proportional to (stake amount * blocks staked during epoch). A user who staked 1000 BST for the full epoch has twice the weight of a user who staked 1000 BST for half the epoch.
- **Claim-Based Distribution**: Stakers call `Claim()` to withdraw accumulated fees from all completed epochs. The contract calculates the pro-rata share across all epochs since the last claim.
- **Auto-Compound via Vault**: Stakers can opt into auto-compound mode. Instead of withdrawing fees in BST, the contract deposits their earnings into a BST-4626 vault that stakes the BST, compounding their position automatically.
- **Multi-Token Accounting**: Each registered protocol specifies which token it pays fees in. The distributor maintains separate accounting per fee token, and stakers can claim each token independently.
- **Fee Conversion**: An optional feature where non-BST fees are automatically converted to BST via the DEX before distribution. This simplifies the staker experience by providing all fees in a single token.
- **Minimum Claim Threshold**: A configurable minimum claim amount to prevent dust claims that waste gas. Unclaimed amounts below the threshold roll over to subsequent epochs.
- **Historical Fee Data**: The contract stores per-epoch fee totals, staker counts, and distribution amounts for complete historical auditability.
- **Emergency Withdrawal**: Admin (subject to governance timelock) can pause distribution and withdraw funds in case of a critical vulnerability, ensuring fund safety.
- **Decay Factor**: Optional time decay on unclaimed fees. If a staker does not claim for N epochs, their unclaimed fees gradually decay and are redistributed to active claimers. This prevents indefinite accumulation of dead capital.

## Basalt-Specific Advantages

- **AOT-Compiled Time-Weighted Calculations**: The time-weighted stake calculation requires iterating through staking events (deposits, withdrawals) within each epoch and computing weighted sums. As native AOT-compiled code, this runs orders of magnitude faster than equivalent EVM bytecode, enabling precise per-block granularity in weight calculations.
- **BST-4626 Vault Auto-Compound**: Basalt's first-class BST-4626 vault standard enables seamless auto-compounding. The distributor deposits fee earnings directly into a vault, and the vault's share-price mechanism handles the compounding math. On EVM chains, this requires custom integration with specific vault implementations; on Basalt, it is a standardized protocol.
- **Pedersen Commitment Confidential Claims**: Individual claim amounts can be hidden using Pedersen commitments. The contract publishes commitments for each staker's share, and stakers prove their entitlement via range proofs without revealing the exact amount. This prevents other stakers from inferring the size of each other's positions.
- **BLS Aggregate Signatures for Batch Claims**: Multiple stakers can authorize a batch claim signed with BLS aggregate signatures, allowing a single transaction to process many claims. This is particularly useful for vault auto-compound operations that process all opted-in stakers at once.
- **ZK Compliance for Institutional Stakers**: Institutional stakers who must comply with reporting requirements can provide ZK compliance proofs to receive fee distributions. The contract verifies compliance without revealing the institution's identity, satisfying regulatory requirements while maintaining on-chain privacy.
- **BLAKE3 Epoch Commitment**: Each epoch's snapshot is committed to on-chain via a BLAKE3 hash of the full staker-weight map. This provides a compact, verifiable commitment that can be used to prove distribution correctness without storing the full map.
- **Ed25519 Efficient Registration**: Protocol registration transactions use Ed25519 signatures, enabling fast verification of admin approvals.
- **BST-3525 Distribution Receipts**: Fee distribution receipts are BST-3525 semi-fungible tokens where the slot represents the epoch and the value represents the claimable amount. These receipts are transferable, allowing stakers to sell their pending fee claims.

## Token Standards Used

- **BST-20**: Fee tokens (BST, stablecoins, other protocol tokens) are BST-20 fungible tokens. The governance token used for staking weight is also BST-20.
- **BST-4626 (Vault)**: The auto-compound feature deposits fee earnings into a BST-4626 vault. Stakers receive vault shares that automatically appreciate as more fees are compounded.
- **BST-3525 (SFT)**: Distribution receipts are BST-3525 semi-fungible tokens with epoch-based slots. These provide transferable claims on fee distributions.

## Integration Points

- **StakingPool (0x...1005)**: The primary source of staking data. The distributor queries StakingPool for each address's staked amount and staking history to compute time-weighted stakes.
- **Governance (0x...1002)**: Protocol registration, parameter changes (epoch length, minimum threshold, decay factor), and emergency actions are governed via Governance proposals. The Governance contract itself may be a fee source (from proposal fees).
- **DEX / AMM Contract**: The DEX is typically the largest fee source. Trading fees are forwarded to the distributor at configurable intervals. The fee conversion feature also uses the DEX to swap non-BST fees to BST.
- **Lending Protocol**: Interest rate spreads and liquidation penalties generate fees that are forwarded to the distributor.
- **BridgeETH (0x...1008)**: Bridge fees collected on cross-chain transfers are forwarded to the distributor. Fees collected on the Ethereum side can be bridged back to Basalt for distribution.
- **Escrow (0x...1003)**: Escrow fees (from escrow creation and completion) are forwarded to the distributor.
- **BNS (0x...1001)**: BNS registration and renewal fees are forwarded to the distributor. The distributor itself registers a BNS name (e.g., "fees.protocol.bst").
- **SchemaRegistry (0x...1006)**: When compliance is required for fee claims, credential schemas are referenced from the SchemaRegistry.
- **IssuerRegistry (0x...1007)**: Validates credential issuers for ZK compliance proofs.

## Technical Sketch

```csharp
// Contract type ID: 0x010F
[BasaltContract(0x010F)]
public partial class FeeDistributor : SdkContract, IDispatchable
{
    // --- Storage ---

    private StorageValue<Address> _admin;
    private StorageValue<ulong> _currentEpoch;
    private StorageValue<ulong> _epochLengthBlocks;
    private StorageValue<ulong> _epochStartBlock;
    private StorageValue<UInt256> _minClaimThreshold;
    private StorageValue<ulong> _decayStartEpochs;        // epochs before decay begins (0 = no decay)
    private StorageValue<ulong> _decayRateBps;             // basis points per epoch after decay starts
    private StorageValue<bool> _paused;
    private StorageValue<Address> _compoundVault;           // BST-4626 vault for auto-compound

    // Registered fee sources
    private StorageMap<Address, bool> _registeredProtocols;
    private StorageMap<Address, Address> _protocolFeeToken;  // protocol -> fee token address

    // Per-epoch aggregates
    private StorageMap<ulong, UInt256> _epochTotalFees;              // epoch -> total BST fees
    private StorageMap<ulong, UInt256> _epochTotalTimeWeightedStake; // epoch -> sum of all TWS

    // Per-epoch per-token fees (for multi-token support)
    // Key: epoch * MAX_TOKENS + tokenIndex
    private StorageValue<ulong> _registeredTokenCount;
    private StorageMap<ulong, Address> _tokenByIndex;
    private StorageMap<Address, ulong> _tokenIndex;
    private StorageMap<ulong, UInt256> _epochTokenFees;     // compositeKey -> fees in that token

    // Per-staker tracking
    private StorageMap<Address, ulong> _lastClaimedEpoch;
    private StorageMap<Address, UInt256> _cumulativeClaimed;
    private StorageMap<Address, bool> _autoCompoundEnabled;

    // Per-staker per-epoch time-weighted stake
    // In practice: composite key or cross-contract query
    private StorageMap<Address, ulong> _stakerLastUpdateBlock;
    private StorageMap<Address, UInt256> _stakerAccumulatedWeight;

    // --- Constructor ---

    public void Initialize(
        Address admin,
        ulong epochLengthBlocks,
        UInt256 minClaimThreshold,
        ulong decayStartEpochs,
        ulong decayRateBps,
        Address compoundVault)
    {
        _admin.Set(admin);
        _epochLengthBlocks.Set(epochLengthBlocks);
        _minClaimThreshold.Set(minClaimThreshold);
        _decayStartEpochs.Set(decayStartEpochs);
        _decayRateBps.Set(decayRateBps);
        _compoundVault.Set(compoundVault);
        _currentEpoch.Set(1);
        _epochStartBlock.Set(Context.BlockNumber);
    }

    // --- Fee Deposit (called by registered protocols) ---

    public void DepositFees()
    {
        Require(_registeredProtocols.Get(Context.Caller), "Not a registered protocol");
        Require(!_paused.Get(), "Distribution paused");

        UInt256 amount = Context.TxValue;
        Require(!amount.IsZero, "Must deposit non-zero fees");

        ulong epoch = _currentEpoch.Get();
        _epochTotalFees.Set(epoch, _epochTotalFees.Get(epoch) + amount);
    }

    public void DepositTokenFees(Address token, UInt256 amount)
    {
        Require(_registeredProtocols.Get(Context.Caller), "Not a registered protocol");
        Require(!_paused.Get(), "Distribution paused");
        Require(!amount.IsZero, "Must deposit non-zero fees");

        // Transfer BST-20 tokens from protocol to this contract
        TransferTokensFrom(token, Context.Caller, Context.ContractAddress, amount);

        ulong epoch = _currentEpoch.Get();
        ulong tokenIdx = GetOrRegisterTokenIndex(token);
        ulong compositeKey = epoch * 1000 + tokenIdx;
        _epochTokenFees.Set(compositeKey,
            _epochTokenFees.Get(compositeKey) + amount);
    }

    // --- Epoch Management ---

    public void AdvanceEpoch()
    {
        ulong currentBlock = Context.BlockNumber;
        ulong epochStart = _epochStartBlock.Get();
        ulong epochLength = _epochLengthBlocks.Get();
        Require(currentBlock >= epochStart + epochLength, "Epoch not elapsed");

        ulong epoch = _currentEpoch.Get();

        // Snapshot total time-weighted stake from StakingPool
        UInt256 totalTWS = ComputeTotalTimeWeightedStake(epoch);
        _epochTotalTimeWeightedStake.Set(epoch, totalTWS);

        // Advance
        _currentEpoch.Set(epoch + 1);
        _epochStartBlock.Set(currentBlock);
    }

    // --- Staker: Checkpoint ---

    public void Checkpoint()
    {
        // Called by stakers (or automatically on stake/unstake) to update their
        // time-weighted stake accumulator for the current epoch.
        Address staker = Context.Caller;
        ulong currentBlock = Context.BlockNumber;
        ulong lastUpdate = _stakerLastUpdateBlock.Get(staker);

        if (lastUpdate > 0)
        {
            ulong blocksDelta = currentBlock - lastUpdate;
            UInt256 currentStake = GetStakerStake(staker);
            UInt256 weightDelta = currentStake * blocksDelta;
            _stakerAccumulatedWeight.Set(staker,
                _stakerAccumulatedWeight.Get(staker) + weightDelta);
        }

        _stakerLastUpdateBlock.Set(staker, currentBlock);
    }

    // --- Staker: Claim ---

    public UInt256 Claim()
    {
        Require(!_paused.Get(), "Distribution paused");
        Address staker = Context.Caller;
        ulong lastClaimed = _lastClaimedEpoch.Get(staker);
        ulong currentEpoch = _currentEpoch.Get();

        UInt256 totalOwed = UInt256.Zero;

        for (ulong e = lastClaimed + 1; e < currentEpoch; e++)
        {
            UInt256 epochFees = _epochTotalFees.Get(e);
            UInt256 epochTotalTWS = _epochTotalTimeWeightedStake.Get(e);

            if (epochTotalTWS.IsZero || epochFees.IsZero)
                continue;

            UInt256 stakerTWS = GetStakerTimeWeightedStake(staker, e);
            if (stakerTWS.IsZero)
                continue;

            UInt256 share = epochFees * stakerTWS / epochTotalTWS;

            // Apply decay if applicable
            ulong decayStart = _decayStartEpochs.Get();
            if (decayStart > 0)
            {
                ulong epochsUnclaimed = currentEpoch - e;
                if (epochsUnclaimed > decayStart)
                {
                    ulong decayEpochs = epochsUnclaimed - decayStart;
                    ulong decayBps = decayEpochs * _decayRateBps.Get();
                    if (decayBps >= 10000)
                        continue; // fully decayed
                    share = share * (10000 - decayBps) / 10000;
                }
            }

            totalOwed += share;
        }

        Require(totalOwed >= _minClaimThreshold.Get(), "Below minimum claim threshold");

        _lastClaimedEpoch.Set(staker, currentEpoch - 1);
        _cumulativeClaimed.Set(staker, _cumulativeClaimed.Get(staker) + totalOwed);

        // Auto-compound or direct transfer
        if (_autoCompoundEnabled.Get(staker))
        {
            DepositToVault(staker, totalOwed);
        }
        else
        {
            Context.TransferNative(staker, totalOwed);
        }

        return totalOwed;
    }

    public UInt256 ClaimTokenFees(Address token)
    {
        Require(!_paused.Get(), "Distribution paused");
        Address staker = Context.Caller;
        ulong lastClaimed = _lastClaimedEpoch.Get(staker);
        ulong currentEpoch = _currentEpoch.Get();
        ulong tokenIdx = _tokenIndex.Get(token);

        UInt256 totalOwed = UInt256.Zero;

        for (ulong e = lastClaimed + 1; e < currentEpoch; e++)
        {
            ulong compositeKey = e * 1000 + tokenIdx;
            UInt256 epochTokenFees = _epochTokenFees.Get(compositeKey);
            UInt256 epochTotalTWS = _epochTotalTimeWeightedStake.Get(e);

            if (epochTotalTWS.IsZero || epochTokenFees.IsZero)
                continue;

            UInt256 stakerTWS = GetStakerTimeWeightedStake(staker, e);
            UInt256 share = epochTokenFees * stakerTWS / epochTotalTWS;
            totalOwed += share;
        }

        Require(!totalOwed.IsZero, "Nothing to claim");

        TransferTokensTo(token, staker, totalOwed);
        return totalOwed;
    }

    // --- Auto-Compound ---

    public void EnableAutoCompound()
    {
        _autoCompoundEnabled.Set(Context.Caller, true);
    }

    public void DisableAutoCompound()
    {
        _autoCompoundEnabled.Set(Context.Caller, false);
    }

    public bool IsAutoCompoundEnabled(Address staker) => _autoCompoundEnabled.Get(staker);

    // --- Query ---

    public UInt256 GetPendingFees(Address staker)
    {
        ulong lastClaimed = _lastClaimedEpoch.Get(staker);
        ulong currentEpoch = _currentEpoch.Get();
        UInt256 total = UInt256.Zero;

        for (ulong e = lastClaimed + 1; e < currentEpoch; e++)
        {
            UInt256 epochFees = _epochTotalFees.Get(e);
            UInt256 epochTotalTWS = _epochTotalTimeWeightedStake.Get(e);
            if (epochTotalTWS.IsZero || epochFees.IsZero) continue;

            UInt256 stakerTWS = GetStakerTimeWeightedStake(staker, e);
            total += epochFees * stakerTWS / epochTotalTWS;
        }
        return total;
    }

    public ulong CurrentEpoch() => _currentEpoch.Get();
    public UInt256 EpochTotalFees(ulong epoch) => _epochTotalFees.Get(epoch);
    public UInt256 EpochTotalTimeWeightedStake(ulong epoch) => _epochTotalTimeWeightedStake.Get(epoch);
    public UInt256 CumulativeClaimed(Address staker) => _cumulativeClaimed.Get(staker);
    public ulong LastClaimedEpoch(Address staker) => _lastClaimedEpoch.Get(staker);
    public bool IsRegisteredProtocol(Address protocol) => _registeredProtocols.Get(protocol);

    public UInt256 GetTotalProtocolRevenue()
    {
        ulong currentEpoch = _currentEpoch.Get();
        UInt256 total = UInt256.Zero;
        for (ulong e = 1; e <= currentEpoch; e++)
            total += _epochTotalFees.Get(e);
        return total;
    }

    // --- Admin ---

    public void RegisterProtocol(Address protocol, Address feeToken)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _registeredProtocols.Set(protocol, true);
        _protocolFeeToken.Set(protocol, feeToken);
    }

    public void UnregisterProtocol(Address protocol)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _registeredProtocols.Set(protocol, false);
    }

    public void SetEpochLength(ulong newLength)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        Require(newLength >= 10, "Epoch too short");
        _epochLengthBlocks.Set(newLength);
    }

    public void SetMinClaimThreshold(UInt256 newThreshold)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _minClaimThreshold.Set(newThreshold);
    }

    public void SetCompoundVault(Address newVault)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _compoundVault.Set(newVault);
    }

    public void SetDecayParameters(ulong startEpochs, ulong rateBps)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        Require(rateBps <= 5000, "Decay rate too aggressive");
        _decayStartEpochs.Set(startEpochs);
        _decayRateBps.Set(rateBps);
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

    public void EmergencyWithdraw(Address to, UInt256 amount)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        // Subject to governance timelock in practice
        Context.TransferNative(to, amount);
    }

    // --- Internal ---

    private UInt256 ComputeTotalTimeWeightedStake(ulong epoch)
    {
        // Cross-contract call to StakingPool to aggregate all stakers' TWS for the epoch
        // In practice, this reads from staking event history or checkpoint data
        return UInt256.Zero; // placeholder
    }

    private UInt256 GetStakerTimeWeightedStake(Address staker, ulong epoch)
    {
        // Returns the staker's time-weighted stake for a specific epoch
        // TWS = sum of (stake_amount * blocks_at_that_stake_level) during the epoch
        return _stakerAccumulatedWeight.Get(staker); // simplified
    }

    private UInt256 GetStakerStake(Address staker)
    {
        // Cross-contract call to StakingPool.GetStake(staker)
        return UInt256.Zero; // placeholder
    }

    private void DepositToVault(Address staker, UInt256 amount)
    {
        // Cross-contract call to BST-4626 vault.Deposit(amount, staker)
        // The vault stakes the BST and issues shares to the staker
        Address vault = _compoundVault.Get();
        // vault.Deposit(amount, staker);
    }

    private ulong GetOrRegisterTokenIndex(Address token)
    {
        ulong existing = _tokenIndex.Get(token);
        if (existing > 0) return existing;

        ulong idx = _registeredTokenCount.Get() + 1;
        _registeredTokenCount.Set(idx);
        _tokenByIndex.Set(idx, token);
        _tokenIndex.Set(token, idx);
        return idx;
    }

    private void TransferTokensFrom(Address token, Address from, Address to, UInt256 amount)
    {
        // Cross-contract call to BST-20 token.TransferFrom(from, to, amount)
    }

    private void TransferTokensTo(Address token, Address to, UInt256 amount)
    {
        // Cross-contract call to BST-20 token.Transfer(to, amount)
    }
}
```

## Complexity

**High** -- The fee distributor is one of the most integration-heavy contracts in the ecosystem. It interacts with every fee-generating protocol, StakingPool, and the BST-4626 vault. The time-weighted stake calculation is the primary source of complexity: it requires tracking stake changes within each epoch at per-block granularity, which involves either maintaining on-chain event logs or relying on checkpoint-based approximation. The multi-token accounting adds a dimension of complexity, as each fee token has its own per-epoch distribution pool. The decay mechanism requires careful arithmetic to avoid underflow and ensure fairness. The auto-compound integration must handle vault deposit failures gracefully. Testing requires simulating complex scenarios with multiple protocols depositing fees in different tokens, stakers entering and exiting at various points within epochs, and claims spanning many epochs.

## Priority

**P1** -- The fee distributor is the linchpin of the "stake to earn" value proposition. Without it, governance token staking has no direct financial incentive beyond governance voting power. Every mature DeFi protocol (Curve, Sushi, GMX) has a fee distribution mechanism, and its absence is a significant gap. This contract should be built as soon as the StakingPool and at least one fee-generating protocol (e.g., the DEX) are operational. It is the contract that transforms passive governance tokens into productive assets, driving staking participation and protocol alignment.
