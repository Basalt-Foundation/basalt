# Lending/Borrowing Protocol

## Category

Decentralized Finance (DeFi) -- Money Markets

## Summary

An overcollateralized lending and borrowing protocol enabling users to deposit assets to earn interest and borrow against their collateral at variable interest rates determined by pool utilization. Deposit receipts are represented as BST-4626 vault shares, and the protocol includes health factor monitoring, liquidation mechanisms with flash liquidation support, and multi-asset collateral baskets.

## Why It's Useful

- **Earn Yield on Idle Assets**: Token holders can deposit into lending pools and earn interest paid by borrowers, providing passive yield without the impermanent loss risk of AMM liquidity provision.
- **Access Liquidity Without Selling**: Borrowers can access capital by collateralizing their holdings, enabling leverage, hedging, or short-term liquidity needs without triggering taxable sale events.
- **Interest Rate Discovery**: Variable rates based on utilization create a market-driven cost of capital, signaling supply/demand dynamics for each asset.
- **Liquidation Ecosystem**: The liquidation mechanism creates a profitable opportunity for liquidation bots, which in turn keep the protocol solvent and the broader DeFi ecosystem healthy.
- **Composability Foundation**: Lending receipts (vault shares) can be used in other DeFi protocols -- as collateral, in yield aggregators, or as components of structured products.

## Key Features

- **Multi-Asset Lending Pools**: Each supported asset has its own lending pool with independent interest rate parameters.
- **Variable Interest Rates**: Interest rates follow a utilization curve: low rates when pools are underutilized, sharply rising rates near full utilization to incentivize repayment and new deposits.
- **BST-4626 Deposit Receipts**: Deposits mint vault shares (BST-4626) representing the depositor's pro-rata claim on the pool's assets plus accrued interest. Shares appreciate over time as interest accrues.
- **Overcollateralized Borrowing**: Borrowers must maintain collateral value above the borrowed amount times the collateral factor (e.g., 150%). Each asset has its own collateral factor based on risk.
- **Health Factor Monitoring**: A user's health factor = (collateral value * collateral factor) / borrowed value. Below 1.0 triggers liquidation eligibility.
- **Liquidation Mechanism**: Liquidators repay a portion of an underwater borrower's debt and receive the equivalent collateral plus a liquidation bonus (e.g., 5-10%).
- **Flash Liquidation**: Liquidators can use flash loans to execute liquidations without upfront capital, making the liquidation market more competitive and efficient.
- **Reserve Factor**: A percentage of interest paid goes to the protocol reserve, building a safety buffer and funding governance-directed activities.
- **Borrow Caps**: Governance-settable maximum borrow amounts per asset to limit protocol risk exposure.
- **Price Oracle Integration**: Asset prices sourced from TWAP oracles (from AMM pools) or external oracle feeds for accurate collateral valuation.
- **Interest Rate Model Upgrades**: Governance can update the interest rate model parameters for each pool without migrating user positions.

## Basalt-Specific Advantages

- **BST-4626 Native Vault Standard**: Basalt's native vault standard means deposit receipts are automatically composable with any BST-4626-aware protocol (yield aggregators, structured products). No adapter contracts needed.
- **AOT-Compiled Interest Accrual**: Interest calculation and accrual logic runs as native AOT-compiled code, making per-block interest updates and multi-position health factor checks efficient even as the number of borrowers scales.
- **ZK Compliance for Institutional Lending**: Institutional lenders and borrowers can participate in regulated lending pools by providing ZK compliance proofs (KYC/AML verification via SchemaRegistry), enabling a parallel compliant lending market without exposing identity data on-chain.
- **Confidential Positions via Pedersen Commitments**: Borrow and deposit amounts can be committed using Pedersen commitments, hiding position sizes from competitors. Health factor verification uses zero-knowledge range proofs to confirm solvency without revealing exact amounts.
- **BST-3525 SFT Debt Positions**: Borrow positions can be represented as BST-3525 semi-fungible tokens with metadata encoding the borrowed asset, interest rate, and health factor, enabling secondary markets for debt positions and sophisticated portfolio management.
- **Ed25519 Signed Oracle Updates**: Oracle price feeds signed with Ed25519 are verified faster than ECDSA-signed feeds, reducing the cost of frequent price updates needed for accurate health factor monitoring.

## Token Standards Used

- **BST-20**: All lendable and borrowable assets are BST-20 tokens.
- **BST-4626 (Vault)**: Deposit receipts are BST-4626 vault shares, automatically accruing interest.
- **BST-3525 (SFT)**: Optional representation of borrow positions with metadata for secondary market trading.

## Integration Points

- **StakingPool (0x0105)**: BST stakers can use stBSLT (liquid staking token) as collateral, and the lending protocol can direct reserve income to StakingPool.
- **Governance (0x0102)**: Controls risk parameters (collateral factors, borrow caps, reserve factors), interest rate model updates, new asset listings, and emergency pause.
- **Escrow (0x0103)**: Flash liquidation settlement uses Escrow for atomicity guarantees.
- **BNS**: Lending pools registered under BNS names (e.g., `lending.basalt`, `bst-pool.lending.basalt`).
- **SchemaRegistry / IssuerRegistry**: ZK compliance verification for regulated lending pools.
- **BridgeETH (0x...1008)**: Bridged assets can be listed as lendable/borrowable assets.
- **AMM DEX (0x0200)**: TWAP price oracle for collateral valuation; flash swap for flash liquidations.

## Technical Sketch

```csharp
// ============================================================
// LendingPool -- Core lending/borrowing contract
// ============================================================

[BasaltContract(TypeId = 0x0220)]
public partial class LendingPool : SdkContract
{
    // --- Storage ---

    // asset address => MarketConfig
    private StorageMap<Address, MarketConfig> _markets;

    // asset address => MarketState (accrued state)
    private StorageMap<Address, MarketState> _marketState;

    // user + asset => UserPosition
    private StorageMap<Hash256, UserPosition> _positions;

    // asset address => vault share token address (BST-4626)
    private StorageMap<Address, Address> _vaultTokens;

    // Oracle contract address
    private StorageValue<Address> _oracle;

    // Protocol reserve per asset
    private StorageMap<Address, UInt256> _reserves;

    // List of assets a user has entered as collateral
    private StorageMap<Address, Address[]> _userCollaterals;

    // --- Structs ---

    public struct MarketConfig
    {
        public Address Asset;
        public UInt256 CollateralFactorBps;   // e.g., 7500 = 75%
        public UInt256 LiquidationBonusBps;   // e.g., 500 = 5%
        public UInt256 ReserveFactorBps;      // e.g., 1000 = 10%
        public UInt256 BorrowCap;
        public bool Active;
        public bool BorrowEnabled;
    }

    public struct MarketState
    {
        public UInt256 TotalDeposits;
        public UInt256 TotalBorrows;
        public UInt256 BorrowIndex;           // accumulates interest over time
        public UInt256 SupplyIndex;
        public ulong LastAccrualBlock;
    }

    public struct UserPosition
    {
        public UInt256 DepositShares;         // vault shares held
        public UInt256 BorrowBalance;         // principal borrowed
        public UInt256 BorrowIndex;           // snapshot at time of borrow
    }

    public struct InterestRateModel
    {
        public UInt256 BaseRatePerBlock;
        public UInt256 MultiplierPerBlock;
        public UInt256 JumpMultiplierPerBlock;
        public UInt256 Kink;                  // utilization threshold for jump
    }

    // --- Admin / Governance ---

    /// <summary>
    /// Add a new asset market. Governance-only.
    /// </summary>
    public void CreateMarket(
        Address asset,
        UInt256 collateralFactorBps,
        UInt256 liquidationBonusBps,
        UInt256 reserveFactorBps,
        UInt256 borrowCap,
        InterestRateModel rateModel)
    {
        RequireGovernance();
        // Validate parameters, deploy BST-4626 vault share token
        // Initialize MarketState with index = 1e18
    }

    /// <summary>
    /// Update risk parameters for an existing market. Governance-only.
    /// </summary>
    public void UpdateMarketConfig(
        Address asset,
        UInt256 collateralFactorBps,
        UInt256 borrowCap)
    {
        RequireGovernance();
        // Update MarketConfig
    }

    // --- Depositing ---

    /// <summary>
    /// Deposit assets into the lending pool. Mints BST-4626 vault shares
    /// representing the deposit plus accrued interest.
    /// </summary>
    public UInt256 Deposit(Address asset, UInt256 amount, Address onBehalfOf)
    {
        AccrueInterest(asset);

        var state = _marketState.Get(asset);
        Require(_markets.Get(asset).Active, "MARKET_INACTIVE");

        // Transfer tokens from sender to pool
        TransferTokenIn(asset, Context.Sender, amount);

        // Calculate shares to mint
        var shares = ConvertToShares(asset, amount, state);

        // Mint BST-4626 vault shares to depositor
        MintVaultShares(_vaultTokens.Get(asset), onBehalfOf, shares);

        // Update position and market state
        var posKey = ComputePositionKey(onBehalfOf, asset);
        var pos = _positions.Get(posKey);
        pos.DepositShares += shares;
        _positions.Set(posKey, pos);

        state.TotalDeposits += amount;
        _marketState.Set(asset, state);

        EmitEvent("Deposit", onBehalfOf, asset, amount, shares);
        return shares;
    }

    /// <summary>
    /// Withdraw assets by burning vault shares.
    /// </summary>
    public UInt256 Withdraw(Address asset, UInt256 shares, Address to)
    {
        AccrueInterest(asset);

        var posKey = ComputePositionKey(Context.Sender, asset);
        var pos = _positions.Get(posKey);
        Require(pos.DepositShares >= shares, "INSUFFICIENT_SHARES");

        var amount = ConvertToAssets(asset, shares, _marketState.Get(asset));

        // Burn vault shares
        BurnVaultShares(_vaultTokens.Get(asset), Context.Sender, shares);

        pos.DepositShares -= shares;
        _positions.Set(posKey, pos);

        // Ensure withdrawal does not make user's position unhealthy
        Require(GetHealthFactor(Context.Sender) >= 1_000_000_000_000_000_000UL,
                "WOULD_BECOME_UNHEALTHY");

        TransferTokenOut(asset, to, amount);

        var state = _marketState.Get(asset);
        state.TotalDeposits -= amount;
        _marketState.Set(asset, state);

        EmitEvent("Withdraw", Context.Sender, asset, amount, shares);
        return amount;
    }

    // --- Borrowing ---

    /// <summary>
    /// Borrow an asset against deposited collateral.
    /// User must have sufficient health factor after borrow.
    /// </summary>
    public void Borrow(Address asset, UInt256 amount)
    {
        AccrueInterest(asset);

        var config = _markets.Get(asset);
        Require(config.BorrowEnabled, "BORROW_DISABLED");

        var state = _marketState.Get(asset);
        Require(state.TotalBorrows + amount <= config.BorrowCap, "BORROW_CAP_REACHED");

        var posKey = ComputePositionKey(Context.Sender, asset);
        var pos = _positions.Get(posKey);

        // Calculate actual borrow balance with accrued interest
        var currentBorrow = (pos.BorrowBalance * state.BorrowIndex) / pos.BorrowIndex;
        pos.BorrowBalance = currentBorrow + amount;
        pos.BorrowIndex = state.BorrowIndex;
        _positions.Set(posKey, pos);

        state.TotalBorrows += amount;
        _marketState.Set(asset, state);

        // Verify health factor remains above 1.0
        Require(GetHealthFactor(Context.Sender) >= 1_000_000_000_000_000_000UL,
                "INSUFFICIENT_COLLATERAL");

        TransferTokenOut(asset, Context.Sender, amount);
        EmitEvent("Borrow", Context.Sender, asset, amount);
    }

    /// <summary>
    /// Repay borrowed assets.
    /// </summary>
    public UInt256 Repay(Address asset, UInt256 amount, Address onBehalfOf)
    {
        AccrueInterest(asset);

        var posKey = ComputePositionKey(onBehalfOf, asset);
        var pos = _positions.Get(posKey);
        var state = _marketState.Get(asset);

        var currentBorrow = (pos.BorrowBalance * state.BorrowIndex) / pos.BorrowIndex;
        var repayAmount = UInt256.Min(amount, currentBorrow);

        TransferTokenIn(asset, Context.Sender, repayAmount);

        pos.BorrowBalance = currentBorrow - repayAmount;
        pos.BorrowIndex = state.BorrowIndex;
        _positions.Set(posKey, pos);

        state.TotalBorrows -= repayAmount;
        _marketState.Set(asset, state);

        EmitEvent("Repay", onBehalfOf, asset, repayAmount);
        return repayAmount;
    }

    // --- Liquidation ---

    /// <summary>
    /// Liquidate an unhealthy position. The liquidator repays a portion
    /// of the borrower's debt and receives equivalent collateral plus
    /// a liquidation bonus.
    /// </summary>
    public void Liquidate(
        Address borrower,
        Address debtAsset,
        UInt256 repayAmount,
        Address collateralAsset)
    {
        AccrueInterest(debtAsset);
        AccrueInterest(collateralAsset);

        var healthFactor = GetHealthFactor(borrower);
        Require(healthFactor < 1_000_000_000_000_000_000UL, "NOT_LIQUIDATABLE");

        // Cap repayment at 50% of borrower's debt (close factor)
        var maxRepay = GetBorrowBalance(borrower, debtAsset) / 2;
        var actualRepay = UInt256.Min(repayAmount, maxRepay);

        // Calculate collateral to seize (including bonus)
        var debtPrice = GetAssetPrice(debtAsset);
        var collPrice = GetAssetPrice(collateralAsset);
        var bonus = _markets.Get(collateralAsset).LiquidationBonusBps;
        var collateralToSeize = (actualRepay * debtPrice * (10000 + bonus))
                              / (collPrice * 10000);

        // Transfer debt repayment from liquidator
        TransferTokenIn(debtAsset, Context.Sender, actualRepay);

        // Reduce borrower's debt
        ReduceBorrowBalance(borrower, debtAsset, actualRepay);

        // Transfer collateral to liquidator
        TransferCollateral(borrower, Context.Sender, collateralAsset, collateralToSeize);

        EmitEvent("Liquidation", borrower, Context.Sender,
                  debtAsset, actualRepay, collateralAsset, collateralToSeize);
    }

    // --- Interest Accrual ---

    /// <summary>
    /// Accrue interest for a market based on blocks elapsed and utilization.
    /// </summary>
    public void AccrueInterest(Address asset)
    {
        var state = _marketState.Get(asset);
        var blocksElapsed = Context.BlockNumber - state.LastAccrualBlock;
        if (blocksElapsed == 0) return;

        var utilization = state.TotalDeposits.IsZero
            ? UInt256.Zero
            : (state.TotalBorrows * 1_000_000_000_000_000_000UL) / state.TotalDeposits;

        var borrowRate = CalculateBorrowRate(asset, utilization);
        var interestAccumulated = state.TotalBorrows * borrowRate * blocksElapsed
                                / 1_000_000_000_000_000_000UL;

        var reserveFactor = _markets.Get(asset).ReserveFactorBps;
        var reserveIncome = interestAccumulated * reserveFactor / 10000;

        state.TotalBorrows += interestAccumulated;
        state.BorrowIndex += (state.BorrowIndex * borrowRate * blocksElapsed)
                           / 1_000_000_000_000_000_000UL;
        state.LastAccrualBlock = Context.BlockNumber;

        _marketState.Set(asset, state);
        _reserves.Set(asset, _reserves.Get(asset) + reserveIncome);
    }

    // --- Health Factor ---

    /// <summary>
    /// Calculate the health factor for a user across all their positions.
    /// Returns fixed-point value (1e18 = 1.0). Below 1e18 = liquidatable.
    /// </summary>
    public UInt256 GetHealthFactor(Address user)
    {
        var collaterals = _userCollaterals.Get(user);
        UInt256 totalCollateralValue = UInt256.Zero;
        UInt256 totalBorrowValue = UInt256.Zero;

        foreach (var asset in collaterals)
        {
            var price = GetAssetPrice(asset);
            var config = _markets.Get(asset);

            var depositValue = GetDepositBalance(user, asset) * price;
            totalCollateralValue += depositValue * config.CollateralFactorBps / 10000;

            var borrowValue = GetBorrowBalance(user, asset) * price;
            totalBorrowValue += borrowValue;
        }

        if (totalBorrowValue.IsZero) return UInt256.MaxValue;
        return (totalCollateralValue * 1_000_000_000_000_000_000UL) / totalBorrowValue;
    }

    // --- Queries ---

    public UInt256 GetDepositBalance(Address user, Address asset) { /* ... */ }
    public UInt256 GetBorrowBalance(Address user, Address asset) { /* ... */ }
    public UInt256 GetUtilizationRate(Address asset) { /* ... */ }
    public UInt256 GetBorrowRate(Address asset) { /* ... */ }
    public UInt256 GetSupplyRate(Address asset) { /* ... */ }

    // --- Internal Helpers ---

    private UInt256 CalculateBorrowRate(Address asset, UInt256 utilization) { /* ... */ }
    private UInt256 ConvertToShares(Address asset, UInt256 amount, MarketState state) { /* ... */ }
    private UInt256 ConvertToAssets(Address asset, UInt256 shares, MarketState state) { /* ... */ }
    private UInt256 GetAssetPrice(Address asset) { /* ... */ }
    private void MintVaultShares(Address vault, Address to, UInt256 shares) { /* ... */ }
    private void BurnVaultShares(Address vault, Address from, UInt256 shares) { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private void ReduceBorrowBalance(Address borrower, Address asset, UInt256 amount) { /* ... */ }
    private void TransferCollateral(Address from, Address to, Address asset, UInt256 amount) { /* ... */ }
    private Hash256 ComputePositionKey(Address user, Address asset) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**High** -- Lending protocols require precise fixed-point arithmetic for interest accrual across variable time periods, correct handling of multi-asset collateral valuation, atomic liquidation mechanics, and careful management of the interaction between deposit/withdraw/borrow/repay operations. Race conditions in liquidation (multiple liquidators, price staleness) add additional complexity. The interest rate model must be parameterized to avoid extreme rates or zero-utilization traps.

## Priority

**P0** -- Lending/borrowing is the second most critical DeFi primitive after swap functionality. It enables leverage, yield generation, and capital efficiency. Most DeFi composability patterns depend on lending markets existing. Should be deployed alongside or immediately after the AMM DEX.
