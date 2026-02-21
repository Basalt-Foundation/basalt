# Yield Aggregator

## Category

Decentralized Finance (DeFi) -- Yield Optimization

## Summary

An automated yield optimization protocol that deposits user funds into the highest-yielding strategies across Basalt's DeFi ecosystem. The aggregator auto-compounds rewards by harvesting yield, swapping reward tokens, and re-depositing for compounding returns. Users deposit into BST-4626-compliant vaults and receive yield-bearing shares that appreciate over time.

## Why It's Useful

- **Automated Compounding**: Manual harvesting and re-depositing of DeFi yields is gas-intensive and time-consuming. The aggregator socializes these costs across all depositors, making compounding economical even for small positions.
- **Optimal Strategy Selection**: The protocol evaluates yields across multiple sources (lending protocols, staking pools, LP positions) and allocates capital to the best risk-adjusted opportunity, saving users the effort of constant monitoring.
- **Gas Cost Socialization**: Harvest transactions, swap fees, and re-deposit costs are shared across all vault depositors. A single harvest benefits thousands of users, reducing per-user cost by orders of magnitude.
- **Risk Diversification**: Multi-strategy vaults spread deposits across several yield sources, reducing the impact of any single protocol failure.
- **DeFi On-Ramp Simplification**: Users can deposit a single token and immediately start earning optimized yield without understanding the underlying DeFi mechanics.

## Key Features

- **BST-4626 Vault Interface**: Each vault implements the BST-4626 standard, making vault shares composable with lending protocols (as collateral), other yield aggregators, and portfolio management tools.
- **Strategy Pattern**: Each vault delegates to one or more Strategy contracts that implement specific yield-generating logic (e.g., lend on protocol X, stake in pool Y, provide LP on pair Z).
- **Auto-Harvest and Compound**: A public `harvest()` function triggers reward collection from underlying protocols, swaps rewards to the vault's deposit token via the AMM, and re-deposits for compounding.
- **Harvest Caller Incentive**: Callers of `harvest()` receive a small bounty (percentage of harvested yield) to incentivize timely compounding.
- **Performance Fee**: A configurable fee (e.g., 10-20%) on yield is taken at harvest time and directed to the protocol treasury or governance stakers.
- **Withdrawal Fee**: Optional small fee on withdrawals to prevent deposit-harvest-withdraw sandwich attacks.
- **Strategy Migration**: Governance can upgrade or migrate strategies without requiring users to withdraw and re-deposit.
- **Emergency Withdrawal**: Users can always withdraw their pro-rata share, even if strategies are paused, via an emergency withdrawal path that bypasses the strategy.
- **Multi-Strategy Allocation**: Vaults can split funds across multiple strategies with configurable allocation weights, balancing yield and risk.
- **Deposit Caps**: Per-vault caps to limit exposure to any single strategy or underlying protocol.

## Basalt-Specific Advantages

- **Native BST-4626 Composability**: Basalt's native vault standard means yield aggregator shares are automatically usable as collateral in lending protocols, as inputs to other yield strategies, or as components of structured products -- without adapter contracts.
- **AOT-Compiled Harvest Logic**: The harvest-swap-redeposit cycle runs as native AOT-compiled code, minimizing the gas cost of the complex multi-step compounding operation. This is especially impactful since harvest is called frequently (every few blocks).
- **ZK Compliance for Institutional Vaults**: Institutional-grade vaults can require ZK compliance proofs for depositors (KYC verification) while maintaining privacy, enabling regulated asset managers to participate in DeFi yield strategies.
- **Confidential Positions**: Pedersen commitments can hide deposit amounts, preventing front-runners from detecting large deposits that signal imminent harvest calls.
- **BST-3525 SFT Strategy Positions**: Individual strategy allocations can be represented as BST-3525 tokens with metadata (strategy type, entry date, accrued yield), enabling granular position management and secondary market trading of specific strategy exposures.
- **Ed25519 Signed Strategy Reports**: Off-chain strategy performance reports signed with Ed25519 can be verified on-chain for transparent yield reporting without relying on on-chain computation for complex APY calculations.

## Token Standards Used

- **BST-20**: Deposit tokens and reward tokens are BST-20. Vault shares are BST-20 (via BST-4626).
- **BST-4626 (Vault)**: Primary interface for all yield aggregator vaults. Deposits, withdrawals, share pricing, and yield accrual follow the BST-4626 standard.
- **BST-3525 (SFT)**: Optional representation of per-strategy allocations with rich metadata.

## Integration Points

- **AMM DEX (0x0200)**: Reward token swaps during harvest. The router is used for multi-hop swaps to convert harvested rewards back to the deposit token.
- **Lending Protocol (0x0220)**: Strategy deposits into lending pools for interest yield. Vault shares can be used as collateral in lending.
- **StakingPool (0x0105)**: Strategy deposits into the native staking pool. Staking rewards are harvested and compounded.
- **Governance (0x0102)**: Controls performance fee rates, strategy approvals, allocation weights, deposit caps, and emergency pause.
- **BNS**: Vaults registered under BNS names (e.g., `bst-vault.yield.basalt`, `usdb-vault.yield.basalt`).
- **SchemaRegistry / IssuerRegistry**: ZK compliance for institutional vault deposits.

## Technical Sketch

```csharp
// ============================================================
// YieldVault -- BST-4626 compliant yield aggregator vault
// ============================================================

[BasaltContract(TypeId = 0x0240)]
public partial class YieldVault : SdkContract
{
    // --- Storage ---

    // Vault configuration
    private StorageValue<Address> _depositToken;
    private StorageValue<UInt256> _totalShares;
    private StorageValue<UInt256> _depositCap;

    // Share balances
    private StorageMap<Address, UInt256> _shares;

    // Strategy allocation
    private StorageValue<ulong> _strategyCount;
    private StorageMap<ulong, StrategyConfig> _strategies;

    // Fee configuration
    private StorageValue<uint> _performanceFeeBps;    // e.g., 1000 = 10%
    private StorageValue<uint> _withdrawalFeeBps;     // e.g., 10 = 0.1%
    private StorageValue<uint> _harvestBountyBps;     // e.g., 50 = 0.5%
    private StorageValue<Address> _feeRecipient;

    // Accounting
    private StorageValue<UInt256> _totalDeposited;
    private StorageValue<ulong> _lastHarvestBlock;

    // --- Structs ---

    public struct StrategyConfig
    {
        public ulong Id;
        public Address StrategyContract;
        public uint AllocationBps;          // e.g., 5000 = 50% of vault
        public UInt256 TotalDeployed;
        public bool Active;
    }

    // --- BST-4626 Standard Interface ---

    /// <summary>
    /// The underlying token managed by this vault.
    /// </summary>
    public Address Asset() => _depositToken.Get();

    /// <summary>
    /// Total amount of underlying tokens managed by the vault
    /// (across all strategies + idle balance).
    /// </summary>
    public UInt256 TotalAssets()
    {
        var idle = GetTokenBalance(_depositToken.Get());
        var deployed = GetTotalDeployed();
        return idle + deployed;
    }

    /// <summary>
    /// Convert an amount of assets to the equivalent number of shares.
    /// </summary>
    public UInt256 ConvertToShares(UInt256 assets)
    {
        var totalSupply = _totalShares.Get();
        if (totalSupply.IsZero) return assets;
        return (assets * totalSupply) / TotalAssets();
    }

    /// <summary>
    /// Convert an amount of shares to the equivalent underlying assets.
    /// </summary>
    public UInt256 ConvertToAssets(UInt256 shares)
    {
        var totalSupply = _totalShares.Get();
        if (totalSupply.IsZero) return shares;
        return (shares * TotalAssets()) / totalSupply;
    }

    /// <summary>
    /// Deposit underlying tokens and receive vault shares.
    /// </summary>
    public UInt256 Deposit(UInt256 assets, Address receiver)
    {
        Require(assets > UInt256.Zero, "ZERO_DEPOSIT");
        Require(TotalAssets() + assets <= _depositCap.Get(), "DEPOSIT_CAP");

        var shares = ConvertToShares(assets);
        Require(!shares.IsZero, "ZERO_SHARES");

        TransferTokenIn(_depositToken.Get(), Context.Sender, assets);
        MintShares(receiver, shares);

        _totalDeposited.Set(_totalDeposited.Get() + assets);
        EmitEvent("Deposit", Context.Sender, receiver, assets, shares);
        return shares;
    }

    /// <summary>
    /// Redeem vault shares for underlying tokens.
    /// </summary>
    public UInt256 Redeem(UInt256 shares, Address receiver, Address owner)
    {
        Require(shares > UInt256.Zero, "ZERO_SHARES");
        if (Context.Sender != owner)
            RequireAllowance(owner, Context.Sender, shares);

        var assets = ConvertToAssets(shares);

        // Apply withdrawal fee
        var fee = assets * _withdrawalFeeBps.Get() / 10000;
        var assetsAfterFee = assets - fee;

        BurnShares(owner, shares);

        // Ensure enough idle balance; withdraw from strategies if needed
        EnsureIdleBalance(assetsAfterFee);

        TransferTokenOut(_depositToken.Get(), receiver, assetsAfterFee);
        if (fee > UInt256.Zero)
            TransferTokenOut(_depositToken.Get(), _feeRecipient.Get(), fee);

        EmitEvent("Withdraw", Context.Sender, receiver, owner, assetsAfterFee, shares);
        return assetsAfterFee;
    }

    /// <summary>
    /// Maximum amount of assets that can be deposited for a given receiver.
    /// </summary>
    public UInt256 MaxDeposit(Address receiver)
    {
        var remaining = _depositCap.Get() - TotalAssets();
        return remaining;
    }

    /// <summary>
    /// Preview how many shares would be received for a deposit.
    /// </summary>
    public UInt256 PreviewDeposit(UInt256 assets) => ConvertToShares(assets);

    /// <summary>
    /// Preview how many assets would be received for a redemption.
    /// </summary>
    public UInt256 PreviewRedeem(UInt256 shares)
    {
        var assets = ConvertToAssets(shares);
        var fee = assets * _withdrawalFeeBps.Get() / 10000;
        return assets - fee;
    }

    // --- Harvest and Compounding ---

    /// <summary>
    /// Harvest rewards from all active strategies, swap to deposit token,
    /// and re-deploy for compounding. Caller receives a bounty.
    /// </summary>
    public UInt256 Harvest()
    {
        UInt256 totalHarvested = UInt256.Zero;

        var strategyCount = _strategyCount.Get();
        for (ulong i = 0; i < strategyCount; i++)
        {
            var config = _strategies.Get(i);
            if (!config.Active) continue;

            // Harvest rewards from strategy
            var harvested = HarvestStrategy(config.StrategyContract);
            totalHarvested += harvested;
        }

        if (totalHarvested.IsZero) return UInt256.Zero;

        // Deduct performance fee
        var perfFee = totalHarvested * _performanceFeeBps.Get() / 10000;
        if (perfFee > UInt256.Zero)
            TransferTokenOut(_depositToken.Get(), _feeRecipient.Get(), perfFee);

        // Pay harvest bounty to caller
        var bounty = totalHarvested * _harvestBountyBps.Get() / 10000;
        if (bounty > UInt256.Zero)
            TransferTokenOut(_depositToken.Get(), Context.Sender, bounty);

        var netHarvested = totalHarvested - perfFee - bounty;

        // Re-deploy harvested funds to strategies
        DeployToStrategies(netHarvested);

        _lastHarvestBlock.Set(Context.BlockNumber);
        EmitEvent("Harvested", Context.Sender, totalHarvested, perfFee, bounty);
        return totalHarvested;
    }

    // --- Strategy Management (Governance) ---

    /// <summary>
    /// Add a new yield strategy to the vault. Governance-only.
    /// </summary>
    public void AddStrategy(Address strategyContract, uint allocationBps)
    {
        RequireGovernance();
        ValidateTotalAllocation(allocationBps);

        var id = _strategyCount.Get();
        _strategies.Set(id, new StrategyConfig
        {
            Id = id,
            StrategyContract = strategyContract,
            AllocationBps = allocationBps,
            TotalDeployed = UInt256.Zero,
            Active = true
        });
        _strategyCount.Set(id + 1);
        EmitEvent("StrategyAdded", id, strategyContract, allocationBps);
    }

    /// <summary>
    /// Update allocation for a strategy. Governance-only.
    /// Triggers rebalancing.
    /// </summary>
    public void UpdateAllocation(ulong strategyId, uint newAllocationBps)
    {
        RequireGovernance();
        var config = _strategies.Get(strategyId);
        config.AllocationBps = newAllocationBps;
        _strategies.Set(strategyId, config);
        Rebalance();
    }

    /// <summary>
    /// Retire a strategy, withdrawing all funds. Governance-only.
    /// </summary>
    public void RetireStrategy(ulong strategyId)
    {
        RequireGovernance();
        var config = _strategies.Get(strategyId);

        // Withdraw all funds from strategy
        WithdrawFromStrategy(config.StrategyContract, config.TotalDeployed);

        config.Active = false;
        config.TotalDeployed = UInt256.Zero;
        config.AllocationBps = 0;
        _strategies.Set(strategyId, config);
        EmitEvent("StrategyRetired", strategyId);
    }

    /// <summary>
    /// Migrate funds from one strategy to another. Governance-only.
    /// </summary>
    public void MigrateStrategy(ulong fromId, ulong toId)
    {
        RequireGovernance();
        var from = _strategies.Get(fromId);
        var to = _strategies.Get(toId);

        var amount = WithdrawFromStrategy(from.StrategyContract, from.TotalDeployed);
        DeployToStrategy(to.StrategyContract, amount);

        to.TotalDeployed += amount;
        from.TotalDeployed = UInt256.Zero;
        from.Active = false;

        _strategies.Set(fromId, from);
        _strategies.Set(toId, to);
        EmitEvent("StrategyMigrated", fromId, toId, amount);
    }

    // --- Emergency ---

    /// <summary>
    /// Emergency withdrawal bypassing strategies. Governance-only.
    /// Pulls all funds from all strategies to vault idle balance.
    /// </summary>
    public void EmergencyWithdrawAll()
    {
        RequireGovernance();
        var count = _strategyCount.Get();
        for (ulong i = 0; i < count; i++)
        {
            var config = _strategies.Get(i);
            if (config.Active && config.TotalDeployed > UInt256.Zero)
            {
                WithdrawFromStrategy(config.StrategyContract, config.TotalDeployed);
                config.TotalDeployed = UInt256.Zero;
                _strategies.Set(i, config);
            }
        }
        EmitEvent("EmergencyWithdrawAll", Context.BlockNumber);
    }

    // --- Internal Helpers ---

    private void DeployToStrategies(UInt256 amount)
    {
        var count = _strategyCount.Get();
        for (ulong i = 0; i < count; i++)
        {
            var config = _strategies.Get(i);
            if (!config.Active) continue;

            var allocation = amount * config.AllocationBps / 10000;
            if (allocation > UInt256.Zero)
            {
                DeployToStrategy(config.StrategyContract, allocation);
                config.TotalDeployed += allocation;
                _strategies.Set(i, config);
            }
        }
    }

    private void EnsureIdleBalance(UInt256 needed)
    {
        var idle = GetTokenBalance(_depositToken.Get());
        if (idle >= needed) return;

        var deficit = needed - idle;
        // Withdraw from strategies proportionally
        WithdrawProportionally(deficit);
    }

    private UInt256 HarvestStrategy(Address strategy)
    {
        // Call strategy.harvest() which collects rewards, swaps via AMM,
        // and returns deposit token amount
        return Context.Call<UInt256>(strategy, "Harvest");
    }

    private void DeployToStrategy(Address strategy, UInt256 amount)
    {
        TransferTokenOut(_depositToken.Get(), strategy, amount);
        Context.Call(strategy, "Deploy", amount);
    }

    private UInt256 WithdrawFromStrategy(Address strategy, UInt256 amount)
    {
        return Context.Call<UInt256>(strategy, "Withdraw", amount);
    }

    private void Rebalance() { /* ... */ }
    private void WithdrawProportionally(UInt256 deficit) { /* ... */ }
    private UInt256 GetTotalDeployed() { /* Sum across strategies */ }
    private void ValidateTotalAllocation(uint additional) { /* ... */ }

    private void MintShares(Address to, UInt256 shares)
    {
        _shares.Set(to, _shares.Get(to) + shares);
        _totalShares.Set(_totalShares.Get() + shares);
    }

    private void BurnShares(Address from, UInt256 shares)
    {
        _shares.Set(from, _shares.Get(from) - shares);
        _totalShares.Set(_totalShares.Get() - shares);
    }

    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private UInt256 GetTokenBalance(Address token) { /* ... */ }
    private void RequireAllowance(Address owner, Address spender, UInt256 amount) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}


// ============================================================
// IYieldStrategy -- Strategy interface
// ============================================================

public interface IYieldStrategy
{
    /// <summary>Deposit funds into the underlying yield source.</summary>
    void Deploy(UInt256 amount);

    /// <summary>Withdraw funds from the underlying yield source.</summary>
    UInt256 Withdraw(UInt256 amount);

    /// <summary>Harvest rewards, swap to deposit token, return amount.</summary>
    UInt256 Harvest();

    /// <summary>Total value of deployed assets in deposit token terms.</summary>
    UInt256 TotalDeployed();

    /// <summary>Estimated APY in basis points.</summary>
    uint EstimatedApyBps();
}
```

## Complexity

**Medium** -- The vault mechanics (deposit/withdraw/share pricing) are well-defined by BST-4626. Strategy management adds moderate complexity with allocation, rebalancing, and migration logic. The harvest cycle (collect rewards, swap, re-deposit) requires multi-contract interaction but follows a predictable pattern. The primary complexity lies in handling edge cases: withdrawal exceeding idle balance, strategy failures during harvest, and accurate share pricing during multi-step operations.

## Priority

**P1** -- Yield aggregation is a significant user acquisition tool (simple deposit, auto-earn), but it depends on the underlying yield sources (lending protocol, staking pool, AMM) existing first. Should be deployed after P0 contracts (AMM, lending, stablecoin) are live and generating yield.
