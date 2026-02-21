# Liquid Staking Token (stBSLT)

## Category

Decentralized Finance (DeFi) -- Staking Derivatives

## Summary

A liquid staking protocol that allows users to stake BST through the contract and receive stBSLT, a BST-20 token representing their staked position plus accrued staking rewards. The stBSLT token trades freely and can be used throughout DeFi (as collateral, in AMM pools, in yield vaults), unlocking the liquidity of staked BST without sacrificing network security.

## Why It's Useful

- **Unlock Staked Capital**: Native BST staking locks tokens for the unbonding period, making them illiquid. stBSLT lets stakers participate in DeFi while still earning staking rewards, increasing capital efficiency.
- **Lower Staking Barrier**: Users can stake any amount (no minimum) via the liquid staking contract, which aggregates stake and manages validator selection. This democratizes access to staking rewards.
- **DeFi Composability**: stBSLT can be used as collateral in lending protocols, provided as liquidity in AMM pools (stBSLT/BST pair), or deposited into yield aggregators -- each layer adding incremental yield.
- **Network Security**: By making staking more attractive (liquid + composable), more BST gets staked, increasing the cost of attacking the Basalt network's BFT consensus.
- **Validator Diversification**: The protocol distributes staked BST across multiple validators based on performance and stake balance, reducing single-validator risk for stakers.

## Key Features

- **Exchange-Rate Model**: stBSLT uses an exchange rate model (not rebase). The stBSLT:BST ratio increases over time as staking rewards accrue, meaning 1 stBSLT becomes worth more BST over time.
- **Delegated Staking**: The contract delegates staked BST to a curated set of validators. Validator selection is based on performance metrics, commission rates, and stake diversification.
- **Instant Unstaking (with fee)**: Users can instantly swap stBSLT back to BST via an internal liquidity buffer, paying a small fee. Alternatively, they can request a standard unbonding that takes the full unbonding period.
- **Validator Set Management**: Governance or an automated oracle system manages the set of validators receiving delegation. Underperforming validators are removed, and stake is redistributed.
- **Slashing Insurance**: A reserve fund (percentage of staking rewards) covers slashing losses, so stBSLT holders are protected from individual validator slashing events up to the reserve amount.
- **Reward Distribution**: Staking rewards are claimed periodically and increase the BST backing of each stBSLT token. A fee (e.g., 10%) is taken for the protocol and node operators.
- **Withdrawal Queue**: When the instant liquidity buffer is depleted, users join a withdrawal queue processed as unbonding BST becomes available.
- **Oracle-Based Rate Updates**: The exchange rate is updated by an oracle that reports staking rewards and slashing events from the consensus layer.

## Basalt-Specific Advantages

- **Direct StakingPool Integration**: Basalt's native StakingPool system contract (0x0105) provides a direct on-chain interface for stake delegation and reward claiming. No cross-chain messaging or bridge is needed -- the liquid staking contract interacts directly with the consensus layer's staking mechanism.
- **AOT-Compiled Reward Compounding**: Reward claiming, validator rebalancing, and exchange rate updates run as native AOT-compiled code, making frequent reward compounding operations gas-efficient.
- **ZK Compliance for Institutional Staking**: Institutional stakers subject to regulatory requirements can provide ZK compliance proofs when staking, enabling compliant participation in network security without revealing identity. stBSLT transfers remain permissionless.
- **BST-3525 SFT Staking Positions**: Individual staking positions can be represented as BST-3525 tokens with metadata (entry date, entry exchange rate, validator allocation), enabling institutional accounting and secondary market trading of staking positions with known cost basis.
- **BLS Signature Aggregation for Validator Reports**: Validator performance reports can use BLS aggregate signatures for efficient on-chain verification, enabling automated validator set management based on cryptographically verified performance data.
- **Confidential Staking Amounts**: Pedersen commitments can hide the amount being staked or unstaked, protecting large stakers from front-running during stake/unstake operations.

## Token Standards Used

- **BST-20**: stBSLT is a standard BST-20 token, fully composable with all DeFi protocols. WBSLT (wrapped BST) is also BST-20.
- **BST-3525 (SFT)**: Optional per-position representation with entry metadata for institutional tracking.
- **BST-4626 (Vault)**: The stBSLT contract itself can implement BST-4626 (deposit BST, receive shares), making it natively composable with yield aggregators.

## Integration Points

- **StakingPool (0x0105)**: Core integration for delegating stake, claiming rewards, processing unbonding. The liquid staking contract is a primary consumer of StakingPool's API.
- **AMM DEX (0x0200)**: stBSLT/BST and stBSLT/USDB liquidity pools provide secondary market liquidity and price discovery for stBSLT.
- **Lending Protocol (0x0220)**: stBSLT accepted as collateral, enabling leveraged staking (stake BST -> get stBSLT -> borrow BST -> stake again).
- **Yield Aggregator (0x0240)**: stBSLT can be deposited into yield vaults for additional compounding.
- **Governance (0x0102)**: Controls validator set criteria, fee parameters, slashing insurance reserve size, instant unstake fee, and emergency pause.
- **BNS**: Registered as `stbslt.basalt` and `liquid-staking.basalt`.
- **SchemaRegistry / IssuerRegistry**: ZK compliance for institutional staking deposits.

## Technical Sketch

```csharp
// ============================================================
// LiquidStaking -- stBSLT liquid staking contract
// ============================================================

[BasaltContract(TypeId = 0x0250)]
public partial class LiquidStaking : SdkContract
{
    // --- Storage ---

    // stBSLT token balances and total supply
    private StorageValue<UInt256> _totalShares;
    private StorageMap<Address, UInt256> _shares;

    // Total BST managed (staked + buffered + pending)
    private StorageValue<UInt256> _totalPooledBst;

    // Instant unstake liquidity buffer
    private StorageValue<UInt256> _liquidityBuffer;
    private StorageValue<UInt256> _bufferTargetBps; // target % of total as buffer

    // Validator delegation
    private StorageValue<ulong> _validatorCount;
    private StorageMap<ulong, ValidatorAllocation> _validators;

    // Withdrawal queue
    private StorageValue<ulong> _queueHead;
    private StorageValue<ulong> _queueTail;
    private StorageMap<ulong, WithdrawalRequest> _withdrawalQueue;

    // Fee and reserve
    private StorageValue<uint> _protocolFeeBps;
    private StorageValue<uint> _operatorFeeBps;
    private StorageValue<uint> _instantUnstakeFeeBps;
    private StorageValue<UInt256> _slashingReserve;
    private StorageValue<uint> _slashingReserveBps;

    // StakingPool system contract address
    private StorageValue<Address> _stakingPool;

    // Oracle for rate updates
    private StorageValue<Address> _oracle;

    // --- Structs ---

    public struct ValidatorAllocation
    {
        public Address ValidatorAddress;
        public UInt256 DelegatedAmount;
        public uint TargetAllocationBps;
        public bool Active;
    }

    public struct WithdrawalRequest
    {
        public ulong RequestId;
        public Address Requester;
        public UInt256 SharesAmount;
        public UInt256 BstAmount;       // calculated at request time
        public ulong RequestBlock;
        public bool Claimed;
    }

    // --- Exchange Rate ---

    /// <summary>
    /// Get the current exchange rate: BST per stBSLT share.
    /// Increases over time as staking rewards accrue.
    /// </summary>
    public UInt256 GetExchangeRate()
    {
        var totalShares = _totalShares.Get();
        if (totalShares.IsZero) return 1_000_000_000_000_000_000UL; // 1:1 initial
        return (_totalPooledBst.Get() * 1_000_000_000_000_000_000UL) / totalShares;
    }

    /// <summary>
    /// Convert BST amount to stBSLT shares.
    /// </summary>
    public UInt256 ConvertToShares(UInt256 bstAmount)
    {
        var totalShares = _totalShares.Get();
        var totalBst = _totalPooledBst.Get();
        if (totalShares.IsZero || totalBst.IsZero) return bstAmount;
        return (bstAmount * totalShares) / totalBst;
    }

    /// <summary>
    /// Convert stBSLT shares to BST amount.
    /// </summary>
    public UInt256 ConvertToBst(UInt256 sharesAmount)
    {
        var totalShares = _totalShares.Get();
        var totalBst = _totalPooledBst.Get();
        if (totalShares.IsZero) return sharesAmount;
        return (sharesAmount * totalBst) / totalShares;
    }

    // --- Staking ---

    /// <summary>
    /// Stake BST and receive stBSLT. The contract delegates the BST
    /// to validators via the StakingPool system contract.
    /// </summary>
    public UInt256 Stake(UInt256 bstAmount)
    {
        Require(bstAmount > UInt256.Zero, "ZERO_AMOUNT");

        // Transfer BST from sender
        TransferNativeIn(Context.Sender, bstAmount);

        var shares = ConvertToShares(bstAmount);
        Require(!shares.IsZero, "ZERO_SHARES");

        MintShares(Context.Sender, shares);

        _totalPooledBst.Set(_totalPooledBst.Get() + bstAmount);

        // Allocate: some to buffer, rest delegated
        var bufferAmount = AllocateToBuffer(bstAmount);
        var stakeAmount = bstAmount - bufferAmount;

        if (stakeAmount > UInt256.Zero)
            DelegateToValidators(stakeAmount);

        EmitEvent("Staked", Context.Sender, bstAmount, shares);
        return shares;
    }

    /// <summary>
    /// Instant unstake: redeem stBSLT for BST immediately using
    /// the liquidity buffer. Charges an instant unstake fee.
    /// </summary>
    public UInt256 InstantUnstake(UInt256 sharesAmount)
    {
        Require(sharesAmount > UInt256.Zero, "ZERO_SHARES");
        Require(_shares.Get(Context.Sender) >= sharesAmount, "INSUFFICIENT_SHARES");

        var bstAmount = ConvertToBst(sharesAmount);
        var fee = bstAmount * _instantUnstakeFeeBps.Get() / 10000;
        var netAmount = bstAmount - fee;

        Require(_liquidityBuffer.Get() >= netAmount, "INSUFFICIENT_BUFFER");

        BurnShares(Context.Sender, sharesAmount);
        _totalPooledBst.Set(_totalPooledBst.Get() - bstAmount);
        _liquidityBuffer.Set(_liquidityBuffer.Get() - netAmount);

        // Fee stays in pool, benefiting remaining stakers
        TransferNativeOut(Context.Sender, netAmount);

        EmitEvent("InstantUnstake", Context.Sender, sharesAmount, netAmount, fee);
        return netAmount;
    }

    /// <summary>
    /// Request standard unstake via the unbonding process.
    /// BST is available after the unbonding period completes.
    /// </summary>
    public ulong RequestUnstake(UInt256 sharesAmount)
    {
        Require(sharesAmount > UInt256.Zero, "ZERO_SHARES");
        Require(_shares.Get(Context.Sender) >= sharesAmount, "INSUFFICIENT_SHARES");

        var bstAmount = ConvertToBst(sharesAmount);
        BurnShares(Context.Sender, sharesAmount);
        _totalPooledBst.Set(_totalPooledBst.Get() - bstAmount);

        // Initiate unbonding from validators
        InitiateUnbonding(bstAmount);

        var requestId = _queueTail.Get();
        _withdrawalQueue.Set(requestId, new WithdrawalRequest
        {
            RequestId = requestId,
            Requester = Context.Sender,
            SharesAmount = sharesAmount,
            BstAmount = bstAmount,
            RequestBlock = Context.BlockNumber,
            Claimed = false
        });
        _queueTail.Set(requestId + 1);

        EmitEvent("UnstakeRequested", Context.Sender, requestId, sharesAmount, bstAmount);
        return requestId;
    }

    /// <summary>
    /// Claim BST from a completed withdrawal request.
    /// </summary>
    public UInt256 ClaimWithdrawal(ulong requestId)
    {
        var request = _withdrawalQueue.Get(requestId);
        Require(request.Requester == Context.Sender, "NOT_REQUESTER");
        Require(!request.Claimed, "ALREADY_CLAIMED");
        Require(IsUnbondingComplete(request.RequestBlock), "UNBONDING_PENDING");

        request.Claimed = true;
        _withdrawalQueue.Set(requestId, request);

        TransferNativeOut(Context.Sender, request.BstAmount);
        EmitEvent("WithdrawalClaimed", requestId, request.BstAmount);
        return request.BstAmount;
    }

    // --- Reward Reporting (Oracle) ---

    /// <summary>
    /// Oracle reports staking rewards accrued since last report.
    /// Increases total pooled BST, thereby increasing exchange rate.
    /// </summary>
    public void ReportRewards(UInt256 rewardAmount)
    {
        RequireOracle();

        // Deduct protocol and operator fees
        var protocolFee = rewardAmount * _protocolFeeBps.Get() / 10000;
        var operatorFee = rewardAmount * _operatorFeeBps.Get() / 10000;

        // Allocate to slashing reserve
        var reserveAlloc = rewardAmount * _slashingReserveBps.Get() / 10000;
        _slashingReserve.Set(_slashingReserve.Get() + reserveAlloc);

        var netRewards = rewardAmount - protocolFee - operatorFee - reserveAlloc;

        // Increase total pooled BST (exchange rate goes up)
        _totalPooledBst.Set(_totalPooledBst.Get() + netRewards);

        // Mint shares for protocol and operator fees
        if (protocolFee > UInt256.Zero)
        {
            var feeShares = ConvertToShares(protocolFee);
            MintShares(GetProtocolFeeRecipient(), feeShares);
        }

        EmitEvent("RewardsReported", rewardAmount, netRewards, protocolFee);
    }

    /// <summary>
    /// Oracle reports a slashing event on a validator.
    /// The slashing reserve absorbs the loss first.
    /// </summary>
    public void ReportSlashing(Address validator, UInt256 slashedAmount)
    {
        RequireOracle();

        var reserve = _slashingReserve.Get();
        if (reserve >= slashedAmount)
        {
            _slashingReserve.Set(reserve - slashedAmount);
        }
        else
        {
            // Reserve depleted; remaining loss socialized to stakers
            var uninsuredLoss = slashedAmount - reserve;
            _slashingReserve.Set(UInt256.Zero);
            _totalPooledBst.Set(_totalPooledBst.Get() - uninsuredLoss);
        }

        // Remove or reduce allocation to slashed validator
        DeactivateValidator(validator);
        EmitEvent("SlashingReported", validator, slashedAmount);
    }

    // --- Validator Management (Governance) ---

    /// <summary>
    /// Add a validator to the delegation set. Governance-only.
    /// </summary>
    public void AddValidator(Address validatorAddress, uint targetAllocationBps)
    {
        RequireGovernance();
        var id = _validatorCount.Get();
        _validators.Set(id, new ValidatorAllocation
        {
            ValidatorAddress = validatorAddress,
            DelegatedAmount = UInt256.Zero,
            TargetAllocationBps = targetAllocationBps,
            Active = true
        });
        _validatorCount.Set(id + 1);
        EmitEvent("ValidatorAdded", validatorAddress, targetAllocationBps);
    }

    /// <summary>
    /// Rebalance delegations across validators to match target allocations.
    /// </summary>
    public void Rebalance()
    {
        var totalDelegated = GetTotalDelegated();
        var count = _validatorCount.Get();
        for (ulong i = 0; i < count; i++)
        {
            var v = _validators.Get(i);
            if (!v.Active) continue;

            var targetAmount = totalDelegated * v.TargetAllocationBps / 10000;
            if (v.DelegatedAmount < targetAmount)
                DelegateToValidator(v.ValidatorAddress, targetAmount - v.DelegatedAmount);
            else if (v.DelegatedAmount > targetAmount)
                UndelegateFromValidator(v.ValidatorAddress, v.DelegatedAmount - targetAmount);
        }
    }

    // --- Queries ---

    public UInt256 TotalPooledBst() => _totalPooledBst.Get();
    public UInt256 TotalShares() => _totalShares.Get();
    public UInt256 GetBufferBalance() => _liquidityBuffer.Get();
    public UInt256 GetSlashingReserve() => _slashingReserve.Get();
    public WithdrawalRequest GetWithdrawalRequest(ulong id) => _withdrawalQueue.Get(id);

    // --- Internal Helpers ---

    private void MintShares(Address to, UInt256 amount)
    {
        _shares.Set(to, _shares.Get(to) + amount);
        _totalShares.Set(_totalShares.Get() + amount);
    }

    private void BurnShares(Address from, UInt256 amount)
    {
        _shares.Set(from, _shares.Get(from) - amount);
        _totalShares.Set(_totalShares.Get() - amount);
    }

    private UInt256 AllocateToBuffer(UInt256 amount) { /* ... */ }
    private void DelegateToValidators(UInt256 amount) { /* ... */ }
    private void DelegateToValidator(Address validator, UInt256 amount) { /* ... */ }
    private void UndelegateFromValidator(Address validator, UInt256 amount) { /* ... */ }
    private void InitiateUnbonding(UInt256 amount) { /* ... */ }
    private bool IsUnbondingComplete(ulong requestBlock) { /* ... */ }
    private void DeactivateValidator(Address validator) { /* ... */ }
    private UInt256 GetTotalDelegated() { /* ... */ }
    private Address GetProtocolFeeRecipient() { /* ... */ }
    private void TransferNativeIn(Address from, UInt256 amount) { /* ... */ }
    private void TransferNativeOut(Address to, UInt256 amount) { /* ... */ }
    private void RequireOracle() { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**Medium** -- The core staking/unstaking mechanics and exchange rate model are straightforward. Complexity arises from validator set management, slashing insurance accounting, the withdrawal queue with unbonding period tracking, liquidity buffer management, and the interaction between the liquid staking contract and the StakingPool system contract. The oracle-based reward reporting adds an external dependency that must be carefully secured.

## Priority

**P1** -- Liquid staking is a high-value protocol that significantly increases network security (more BST staked) while unlocking DeFi composability. It depends on the StakingPool system contract (already deployed) and benefits from existing AMM pools (for stBSLT/BST pair) and lending markets (for stBSLT collateral). Should be deployed after core DeFi primitives.
