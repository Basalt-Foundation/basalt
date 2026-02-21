# On-Chain Index Fund

## Category

Decentralized Finance (DeFi) -- Passive Investment and Asset Management

## Summary

A BST-4626 vault contract that holds a weighted basket of BST-20 tokens tracking a defined index. Investors mint fund shares by depositing the constituent tokens (or a single token via automatic swapping), and the vault periodically rebalances to maintain target weights. Fund managers set the composition and earn management fees, while investors gain diversified exposure through a single token position.

## Why It's Useful

- **Passive Diversification**: Investors gain exposure to a basket of tokens without managing individual positions, reducing complexity and time commitment for portfolio management.
- **Reduced Transaction Costs**: Instead of separately buying and rebalancing N tokens, investors make a single deposit. The fund amortizes rebalancing costs across all participants.
- **Professional Management**: Fund managers can offer curated strategies (DeFi blue chips, stablecoins, L1 tokens, small-cap growth) that retail investors would struggle to replicate.
- **Automated Rebalancing**: Threshold-based or time-based rebalancing keeps the fund aligned with its target allocation, capturing rebalancing alpha and maintaining risk parameters.
- **Composability**: Fund shares (BST-4626 or BST-20) integrate with other DeFi protocols -- use them as collateral, deposit into yield aggregators, or trade on secondary markets.
- **Transparent Holdings**: All fund constituents, weights, and rebalancing history are visible on-chain, providing full transparency absent in traditional finance.
- **Index as a Benchmark**: On-chain indices serve as reference benchmarks for the broader Basalt ecosystem, aiding price discovery and performance measurement.

## Key Features

- **Configurable Index Composition**: Fund managers define the basket of BST-20 tokens and their target weights (basis points totaling 10,000). Weights can be updated through governance or manager action.
- **Time-Based Rebalancing**: Automatic rebalancing at configurable intervals (e.g., every 7,200 blocks / ~24 hours). Rebalance executes trades through the AMM DEX to restore target weights.
- **Threshold-Based Rebalancing**: If any constituent deviates from its target weight by more than a configurable threshold (e.g., 5%), rebalancing is triggered immediately.
- **Single-Token Deposit**: Investors can deposit a single token (e.g., BST); the contract automatically swaps into the constituent tokens at their target weights via the AMM.
- **Pro-Rata Deposit**: Investors can deposit the exact constituent tokens at target ratios for zero-slippage entry.
- **Share Minting/Burning**: Deposits mint BST-4626 vault shares proportional to the investor's contribution relative to total fund AUM. Withdrawals burn shares and return proportional constituents.
- **Management Fee**: Configurable annual management fee (basis points) accrued to the fund manager, deducted during rebalancing or share operations.
- **Performance Fee**: Optional performance fee on returns above a high-water mark.
- **Constituent Caps**: Maximum weight per constituent to prevent over-concentration.
- **Whitelist Constituents**: Only governance-approved tokens can be added to fund compositions.
- **Multi-Fund Factory**: A factory contract enables permissionless creation of new index funds with different compositions and strategies.
- **NAV Calculation**: On-chain net asset value calculation using AMM TWAP prices for each constituent.
- **Deposit/Withdrawal Queuing**: Large deposits or withdrawals can be queued to prevent price impact during rebalancing.
- **Emergency Redemption**: In case of constituent token failure, emergency redemption returns remaining constituent tokens pro-rata.

## Basalt-Specific Advantages

- **BST-4626 Vault Standard (Native)**: Basalt's BST-4626 standard provides the exact interface for yield-bearing vault shares. Fund shares are fully compatible with any protocol that supports BST-4626, enabling composability with yield aggregators, lending protocols, and other vaults out of the box.
- **AOT-Compiled Rebalancing**: The computationally intensive operations of calculating optimal trade amounts for multi-token rebalancing execute in AOT-compiled native code, significantly reducing gas costs compared to EVM-interpreted index fund contracts.
- **BST-3525 SFT Position Tracking**: Individual investment positions can be represented as BST-3525 semi-fungible tokens with metadata encoding entry price, entry weights, accumulated fees, and investment duration -- enabling rich position analytics and secondary market trading.
- **ZK Compliance for Regulated Funds**: Institutional index funds can require ZK compliance proofs for investors (accredited investor verification, jurisdiction checks) without revealing identity, enabling regulated fund structures on a public chain.
- **Confidential Holdings via Pedersen Commitments**: Fund managers can optionally hide portfolio weights using Pedersen commitments, revealing only that the weights sum to 10,000 basis points via range proofs. This prevents front-running of rebalancing trades by MEV bots.
- **Ed25519 Efficiency**: Frequent rebalancing transactions and NAV update oracles benefit from Ed25519's fast signature verification, enabling higher-frequency operations.
- **BLS Aggregate Governance**: Multi-sig fund management (e.g., investment committee) can use BLS signature aggregation for weight update approvals, reducing on-chain verification costs.

## Token Standards Used

- **BST-4626 (Vault)**: The primary interface for the index fund. Investors deposit and receive yield-bearing vault shares. Standard `deposit`, `withdraw`, `mint`, `redeem` interface.
- **BST-20**: All constituent tokens in the index are BST-20. Fund shares can also be represented as BST-20 for simpler integration.
- **BST-3525 (SFT)**: Optional advanced position representation with slot metadata for entry price, fee accrual, and investment duration.
- **BST-VC (Verifiable Credentials)**: Optional KYC/accredited investor credentials for regulated fund participation.

## Integration Points

- **Governance (0x0102)**: Governs constituent whitelist, fund creation parameters, fee caps, and emergency procedures. Governance can update index compositions for community-managed indices.
- **Escrow (0x0103)**: Large deposits/withdrawals processed through escrow to manage price impact and ensure atomicity of multi-token operations.
- **StakingPool (0x0105)**: Fund management fees can be directed to staking rewards. BST stakers may receive fee discounts on fund participation.
- **BNS (0x0101)**: Funds registered under BNS names (e.g., `defi-bluechip.index.basalt`, `stablecoin.index.basalt`).
- **SchemaRegistry (0x...1006)**: Accredited investor credential schemas for regulated fund access.
- **IssuerRegistry (0x...1007)**: Trusted credential issuers for compliance verification.
- **BridgeETH (0x...1008)**: Bridged ERC-20 tokens can be included in index compositions, enabling cross-chain diversified indices.
- **WBSLT (0x0100)**: Wrapped BST as a common constituent and deposit currency.

## Technical Sketch

```csharp
// ============================================================
// IndexFundFactory -- Create and manage index funds
// ============================================================

[BasaltContract(TypeId = 0x0304)]
public partial class IndexFundFactory : SdkContract
{
    private StorageValue<ulong> _nextFundId;
    private StorageMap<ulong, Address> _funds;
    private StorageMap<Address, bool> _approvedConstituents;
    private StorageValue<uint> _maxManagementFeeBps;
    private StorageValue<uint> _maxPerformanceFeeBps;

    /// <summary>
    /// Create a new index fund with specified composition.
    /// </summary>
    public Address CreateFund(
        string name,
        Address[] constituents,
        uint[] weightsBps,
        uint managementFeeBps,
        uint performanceFeeBps,
        ulong rebalanceIntervalBlocks,
        uint rebalanceThresholdBps,
        Address ammRouter)
    {
        Require(constituents.Length == weightsBps.Length, "LENGTH_MISMATCH");
        Require(constituents.Length >= 2, "MIN_2_CONSTITUENTS");
        Require(managementFeeBps <= _maxManagementFeeBps.Get(), "FEE_TOO_HIGH");
        Require(performanceFeeBps <= _maxPerformanceFeeBps.Get(), "PERF_FEE_TOO_HIGH");

        // Verify weights sum to 10000 bps
        uint totalWeight = 0;
        for (int i = 0; i < weightsBps.Length; i++)
        {
            Require(_approvedConstituents.Get(constituents[i]), "CONSTITUENT_NOT_APPROVED");
            Require(weightsBps[i] > 0, "ZERO_WEIGHT");
            totalWeight += weightsBps[i];
        }
        Require(totalWeight == 10000, "WEIGHTS_MUST_SUM_TO_10000");

        ulong fundId = _nextFundId.Get();
        _nextFundId.Set(fundId + 1);

        Address fundAddress = DeriveFundAddress(fundId);
        _funds.Set(fundId, fundAddress);

        // Initialize the fund contract
        InitializeFund(fundAddress, name, constituents, weightsBps,
                      managementFeeBps, performanceFeeBps,
                      rebalanceIntervalBlocks, rebalanceThresholdBps,
                      Context.Sender, ammRouter);

        EmitEvent("FundCreated", fundId, fundAddress, name, Context.Sender);
        return fundAddress;
    }

    public void ApproveConstituent(Address token)
    {
        RequireGovernance();
        _approvedConstituents.Set(token, true);
    }

    private Address DeriveFundAddress(ulong fundId) { /* ... */ }
    private void InitializeFund(Address fund, string name, Address[] constituents,
        uint[] weights, uint mgmtFee, uint perfFee, ulong rebalInterval,
        uint rebalThreshold, Address manager, Address router) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}


// ============================================================
// IndexFund -- BST-4626 vault tracking a token index
// ============================================================

[BasaltContract(TypeId = 0x0305)]
public partial class IndexFund : SdkContract
{
    // --- Storage ---

    private StorageValue<string> _name;
    private StorageValue<Address> _manager;
    private StorageValue<Address> _ammRouter;

    // Constituent tokens
    private StorageValue<uint> _constituentCount;
    private StorageMap<uint, Address> _constituents;          // index => token address
    private StorageMap<uint, uint> _targetWeightsBps;         // index => target weight
    private StorageMap<Address, uint> _constituentIndex;      // token => index (reverse lookup)

    // BST-4626 vault state
    private StorageValue<UInt256> _totalShares;
    private StorageMap<Address, UInt256> _shares;

    // Fee parameters
    private StorageValue<uint> _managementFeeBps;
    private StorageValue<uint> _performanceFeeBps;
    private StorageValue<UInt256> _highWaterMark;             // NAV per share high-water mark
    private StorageValue<UInt256> _accruedManagementFees;
    private StorageValue<ulong> _lastFeeAccrualBlock;

    // Rebalancing parameters
    private StorageValue<ulong> _rebalanceIntervalBlocks;
    private StorageValue<uint> _rebalanceThresholdBps;
    private StorageValue<ulong> _lastRebalanceBlock;

    // Total AUM tracking
    private StorageMap<uint, UInt256> _constituentBalances;   // index => held amount

    // --- BST-4626 Interface ---

    /// <summary>
    /// Deposit a single token and receive vault shares.
    /// The contract swaps the deposit into constituent tokens at target weights.
    /// </summary>
    public UInt256 Deposit(UInt256 assets, Address receiver)
    {
        Require(assets > UInt256.Zero, "ZERO_DEPOSIT");

        // Accrue management fees before calculating shares
        AccrueManagementFee();

        UInt256 totalAssets = TotalAssets();
        UInt256 sharesToMint;

        if (_totalShares.Get().IsZero)
        {
            sharesToMint = assets;
        }
        else
        {
            sharesToMint = (assets * _totalShares.Get()) / totalAssets;
        }

        Require(!sharesToMint.IsZero, "ZERO_SHARES");

        // Accept deposit token (native BST)
        Require(Context.TxValue >= assets, "INSUFFICIENT_DEPOSIT");

        // Swap into constituent tokens at target weights
        SwapIntoConstituents(assets);

        // Mint shares
        _shares.Set(receiver, _shares.Get(receiver) + sharesToMint);
        _totalShares.Set(_totalShares.Get() + sharesToMint);

        EmitEvent("Deposit", Context.Sender, receiver, assets, sharesToMint);
        return sharesToMint;
    }

    /// <summary>
    /// Deposit constituent tokens directly at target ratios (zero slippage).
    /// </summary>
    public UInt256 DepositConstituents(UInt256[] amounts, Address receiver)
    {
        uint count = _constituentCount.Get();
        Require((uint)amounts.Length == count, "LENGTH_MISMATCH");

        AccrueManagementFee();

        // Verify amounts are proportional to target weights
        // and transfer constituent tokens
        UInt256 depositValue = UInt256.Zero;
        for (uint i = 0; i < count; i++)
        {
            if (!amounts[i].IsZero)
            {
                Address token = _constituents.Get(i);
                TransferTokenIn(token, Context.Sender, amounts[i]);
                _constituentBalances.Set(i,
                    _constituentBalances.Get(i) + amounts[i]);
                depositValue = depositValue + GetTokenValue(token, amounts[i]);
            }
        }

        UInt256 totalAssets = TotalAssets();
        UInt256 sharesToMint;

        if (_totalShares.Get().IsZero)
            sharesToMint = depositValue;
        else
            sharesToMint = (depositValue * _totalShares.Get()) / totalAssets;

        _shares.Set(receiver, _shares.Get(receiver) + sharesToMint);
        _totalShares.Set(_totalShares.Get() + sharesToMint);

        EmitEvent("ConstituentDeposit", Context.Sender, receiver, depositValue, sharesToMint);
        return sharesToMint;
    }

    /// <summary>
    /// Withdraw by burning vault shares. Returns proportional constituent tokens.
    /// </summary>
    public UInt256 Withdraw(UInt256 shares, Address receiver, Address owner)
    {
        Require(shares > UInt256.Zero, "ZERO_SHARES");
        Require(_shares.Get(owner) >= shares, "INSUFFICIENT_SHARES");

        if (Context.Sender != owner)
        {
            // Check allowance (not shown for brevity)
        }

        AccrueManagementFee();
        AccruePerformanceFee();

        UInt256 totalShares = _totalShares.Get();
        uint count = _constituentCount.Get();

        // Calculate proportional constituent amounts
        UInt256 totalAssetsReturned = UInt256.Zero;
        for (uint i = 0; i < count; i++)
        {
            UInt256 balance = _constituentBalances.Get(i);
            UInt256 amount = (balance * shares) / totalShares;

            if (!amount.IsZero)
            {
                _constituentBalances.Set(i, balance - amount);
                Address token = _constituents.Get(i);
                TransferTokenOut(token, receiver, amount);
                totalAssetsReturned = totalAssetsReturned + GetTokenValue(token, amount);
            }
        }

        _shares.Set(owner, _shares.Get(owner) - shares);
        _totalShares.Set(totalShares - shares);

        EmitEvent("Withdraw", Context.Sender, receiver, owner, totalAssetsReturned, shares);
        return totalAssetsReturned;
    }

    // --- Rebalancing ---

    /// <summary>
    /// Execute rebalancing if conditions are met (time or threshold).
    /// Can be called by anyone; the caller may receive a small reward.
    /// </summary>
    public void Rebalance()
    {
        bool timeTriggered = Context.BlockNumber >=
            _lastRebalanceBlock.Get() + _rebalanceIntervalBlocks.Get();
        bool thresholdTriggered = CheckThresholdDeviation();

        Require(timeTriggered || thresholdTriggered, "REBALANCE_NOT_NEEDED");

        AccrueManagementFee();

        uint count = _constituentCount.Get();
        UInt256 totalValue = TotalAssets();

        for (uint i = 0; i < count; i++)
        {
            Address token = _constituents.Get(i);
            UInt256 currentBalance = _constituentBalances.Get(i);
            UInt256 currentValue = GetTokenValue(token, currentBalance);

            // Target value = totalValue * targetWeight / 10000
            UInt256 targetValue = (totalValue * _targetWeightsBps.Get(i)) / 10000;

            if (currentValue > targetValue)
            {
                // Over-weight: sell excess
                UInt256 excessValue = currentValue - targetValue;
                UInt256 sellAmount = (currentBalance * excessValue) / currentValue;
                SellConstituent(i, sellAmount);
            }
            else if (currentValue < targetValue)
            {
                // Under-weight: buy more
                UInt256 deficitValue = targetValue - currentValue;
                BuyConstituent(i, deficitValue);
            }
        }

        _lastRebalanceBlock.Set(Context.BlockNumber);
        EmitEvent("Rebalanced", Context.BlockNumber, totalValue);
    }

    /// <summary>
    /// Update target weights. Manager-only, subject to governance constraints.
    /// </summary>
    public void UpdateWeights(uint[] newWeightsBps)
    {
        RequireManager();
        uint count = _constituentCount.Get();
        Require((uint)newWeightsBps.Length == count, "LENGTH_MISMATCH");

        uint totalWeight = 0;
        for (uint i = 0; i < count; i++)
        {
            totalWeight += newWeightsBps[i];
        }
        Require(totalWeight == 10000, "WEIGHTS_MUST_SUM_TO_10000");

        for (uint i = 0; i < count; i++)
        {
            _targetWeightsBps.Set(i, newWeightsBps[i]);
        }

        EmitEvent("WeightsUpdated", newWeightsBps);
    }

    /// <summary>
    /// Add a new constituent to the index. Manager-only.
    /// </summary>
    public void AddConstituent(Address token, uint weightBps)
    {
        RequireManager();
        uint count = _constituentCount.Get();
        _constituents.Set(count, token);
        _constituentIndex.Set(token, count);
        _targetWeightsBps.Set(count, weightBps);
        _constituentCount.Set(count + 1);

        // Adjust existing weights (caller must update all weights after adding)
        EmitEvent("ConstituentAdded", token, weightBps);
    }

    // --- Fee Management ---

    /// <summary>
    /// Claim accrued management fees. Manager-only.
    /// </summary>
    public UInt256 ClaimFees()
    {
        RequireManager();
        AccrueManagementFee();
        AccruePerformanceFee();

        UInt256 fees = _accruedManagementFees.Get();
        Require(!fees.IsZero, "NO_FEES");

        _accruedManagementFees.Set(UInt256.Zero);

        // Mint fee shares to manager
        _shares.Set(_manager.Get(), _shares.Get(_manager.Get()) + fees);
        _totalShares.Set(_totalShares.Get() + fees);

        EmitEvent("FeesClaimed", _manager.Get(), fees);
        return fees;
    }

    // --- BST-4626 View Methods ---

    public UInt256 TotalAssets()
    {
        UInt256 total = UInt256.Zero;
        uint count = _constituentCount.Get();
        for (uint i = 0; i < count; i++)
        {
            Address token = _constituents.Get(i);
            UInt256 balance = _constituentBalances.Get(i);
            total = total + GetTokenValue(token, balance);
        }
        return total;
    }

    public UInt256 TotalSupply() => _totalShares.Get();

    public UInt256 BalanceOf(Address account) => _shares.Get(account);

    public UInt256 ConvertToShares(UInt256 assets)
    {
        UInt256 totalSupply = _totalShares.Get();
        if (totalSupply.IsZero) return assets;
        return (assets * totalSupply) / TotalAssets();
    }

    public UInt256 ConvertToAssets(UInt256 shares)
    {
        UInt256 totalSupply = _totalShares.Get();
        if (totalSupply.IsZero) return shares;
        return (shares * TotalAssets()) / totalSupply;
    }

    public UInt256 NavPerShare()
    {
        UInt256 totalSupply = _totalShares.Get();
        if (totalSupply.IsZero) return UInt256.Zero;
        return (TotalAssets() * 1_000_000_000_000_000_000) / totalSupply; // 18 decimals
    }

    // --- Query Methods ---

    public uint GetConstituentCount() => _constituentCount.Get();
    public Address GetConstituent(uint index) => _constituents.Get(index);
    public uint GetTargetWeight(uint index) => _targetWeightsBps.Get(index);
    public UInt256 GetConstituentBalance(uint index) => _constituentBalances.Get(index);
    public ulong GetLastRebalanceBlock() => _lastRebalanceBlock.Get();
    public uint GetManagementFeeBps() => _managementFeeBps.Get();

    public uint GetCurrentWeightBps(uint index)
    {
        UInt256 totalValue = TotalAssets();
        if (totalValue.IsZero) return 0;
        Address token = _constituents.Get(index);
        UInt256 value = GetTokenValue(token, _constituentBalances.Get(index));
        return (uint)((value * 10000) / totalValue);
    }

    // --- Internal Helpers ---

    private void AccrueManagementFee()
    {
        ulong blocksSinceAccrual = Context.BlockNumber - _lastFeeAccrualBlock.Get();
        if (blocksSinceAccrual == 0) return;

        UInt256 totalAssets = TotalAssets();
        // Annual fee applied per block: totalAssets * feeBps / 10000 / blocksPerYear
        UInt256 fee = (totalAssets * _managementFeeBps.Get() * blocksSinceAccrual)
                      / (10000 * 2628000);
        _accruedManagementFees.Set(_accruedManagementFees.Get() + fee);
        _lastFeeAccrualBlock.Set(Context.BlockNumber);
    }

    private void AccruePerformanceFee()
    {
        UInt256 nav = NavPerShare();
        UInt256 hwm = _highWaterMark.Get();
        if (nav > hwm)
        {
            UInt256 gain = nav - hwm;
            UInt256 fee = (gain * _performanceFeeBps.Get()) / 10000;
            _accruedManagementFees.Set(_accruedManagementFees.Get() + fee);
            _highWaterMark.Set(nav);
        }
    }

    private bool CheckThresholdDeviation()
    {
        uint threshold = _rebalanceThresholdBps.Get();
        uint count = _constituentCount.Get();
        for (uint i = 0; i < count; i++)
        {
            uint currentWeight = GetCurrentWeightBps(i);
            uint targetWeight = _targetWeightsBps.Get(i);
            uint deviation = currentWeight > targetWeight
                ? currentWeight - targetWeight : targetWeight - currentWeight;
            if (deviation > threshold)
                return true;
        }
        return false;
    }

    private void SwapIntoConstituents(UInt256 amount) { /* swap via AMM router */ }
    private void SellConstituent(uint index, UInt256 amount) { /* sell via AMM */ }
    private void BuyConstituent(uint index, UInt256 value) { /* buy via AMM */ }
    private UInt256 GetTokenValue(Address token, UInt256 amount) { /* AMM TWAP price */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private void RequireManager() { Require(Context.Sender == _manager.Get(), "NOT_MANAGER"); }
}
```

## Complexity

**Medium** -- The core vault logic (deposit, withdraw, share math) follows the well-established BST-4626 standard. Weight-based rebalancing through AMM swaps is conceptually straightforward but requires careful handling of slippage, multi-token swap routing, and rounding errors in weight calculations. Fee accrual (management and performance with high-water mark) adds moderate accounting complexity. The primary challenge is ensuring NAV calculations are manipulation-resistant (TWAP vs spot prices) and that rebalancing trades do not create extractable MEV.

## Priority

**P2** -- On-chain index funds are a valuable passive investment tool but depend on a mature ecosystem of BST-20 tokens, reliable AMM liquidity, and accurate price oracles. They should be deployed after core DeFi infrastructure (DEX, lending, stablecoins) is stable and sufficient constituent tokens exist to compose meaningful indices. However, they are an excellent product for attracting passive investors and institutional capital.
