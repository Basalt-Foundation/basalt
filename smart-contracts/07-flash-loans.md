# Flash Loan Pool

## Category

Decentralized Finance (DeFi) -- Capital Efficiency

## Summary

A flash loan protocol enabling users to borrow any amount of tokens without collateral, provided the loan is repaid (with fee) within the same transaction. The pool is funded by depositors who earn a share of flash loan fees as passive yield. This primitive enables capital-efficient arbitrage, liquidation, collateral swaps, and self-liquidation strategies that would otherwise require significant upfront capital.

## Why It's Useful

- **Zero-Capital Arbitrage**: Traders can exploit price discrepancies across AMM pools, order books, and lending markets without owning any tokens. Borrow, arbitrage, repay with profit -- all in one atomic transaction.
- **Efficient Liquidations**: Liquidation bots can borrow the repayment token via flash loan, liquidate an underwater position, receive collateral, sell collateral for the borrowed token, and repay -- earning the liquidation bonus without capital risk.
- **Collateral Swaps**: Users can swap their lending collateral type (e.g., from BST to stBSLT) in one transaction: flash borrow, repay old debt, withdraw old collateral, deposit new collateral, reborrow, repay flash loan.
- **Self-Liquidation**: Borrowers approaching liquidation can use flash loans to unwind their positions gracefully, avoiding the liquidation penalty.
- **Passive Yield for Depositors**: Depositors earn fees from flash loan usage without any impermanent loss or liquidation risk -- the pool only lends within single transactions, so depositor funds are never at long-term risk.
- **DeFi Composability Testing**: Developers can test complex multi-protocol interactions using flash loans without needing test capital.

## Key Features

- **Multi-Asset Flash Loans**: Flash loans available for any BST-20 token with sufficient pool liquidity.
- **Atomic Execution Guarantee**: If the borrowed amount plus fee is not returned by the end of the transaction, the entire transaction reverts -- the pool is never at risk.
- **Configurable Fee Per Asset**: Each asset pool has its own flash loan fee (e.g., 0.09% default), adjustable via governance.
- **Depositor Yield**: Flash loan fees are distributed pro-rata to depositors. Depositors receive interest-bearing pool shares (BST-4626).
- **Batch Flash Loans**: Borrow multiple assets in a single flash loan call for complex multi-asset strategies.
- **Flash Loan Callback**: The borrower contract must implement an `onFlashLoan` callback where it executes its strategy before repayment.
- **Re-Entrancy Protection**: The pool tracks flash loan state to prevent nested flash loans and re-entrancy attacks.
- **Utilization Tracking**: Historical flash loan utilization data for analytics and fee optimization.
- **Emergency Pause**: Governance can pause flash loans for specific assets or globally in case of discovered vulnerabilities.

## Basalt-Specific Advantages

- **AOT-Compiled Atomic Verification**: The flash loan invariant check (balance after >= balance before + fee) runs as native AOT-compiled code. Combined with Basalt's fast block execution, flash loan transactions complete faster and with lower gas cost than on EVM chains, enabling more complex multi-step strategies within a single transaction.
- **Confidential Flash Loan Amounts**: Pedersen commitments can hide the flash loan amount, preventing competitors from detecting and front-running profitable arbitrage strategies. The pool verifies repayment via commitment arithmetic without revealing the borrowed amount.
- **ZK Compliance for Institutional Flash Loans**: Large institutional flash loan users can provide ZK compliance proofs, enabling regulated entities to participate in flash loan strategies while meeting compliance requirements.
- **Ed25519 Signed Strategies**: Off-chain computed strategies can be signed with Ed25519 and submitted as calldata, providing faster verification for complex strategy execution paths.
- **BST-3525 SFT Deposit Positions**: Depositor positions can be represented as BST-3525 tokens with metadata (deposit date, fee share, asset type), enabling secondary market trading of flash loan pool positions and institutional position tracking.
- **Single-Transaction Finality**: Basalt's BFT consensus provides single-block finality, meaning flash loan transactions are irreversible once included in a finalized block. No risk of chain reorganization undoing a successful flash loan.

## Token Standards Used

- **BST-20**: All borrowable assets are BST-20. Pool share tokens are BST-20 (via BST-4626).
- **BST-4626 (Vault)**: Depositor pool shares follow the BST-4626 vault standard for composability with yield aggregators.
- **BST-3525 (SFT)**: Optional per-depositor position tokens with metadata.

## Integration Points

- **AMM DEX (0x0200)**: Flash loans commonly used for AMM arbitrage. Flash swap on the AMM is a related but distinct mechanism.
- **Lending Protocol (0x0220)**: Flash loans used for liquidation execution and collateral swaps within lending markets.
- **Stablecoin CDP (0x0230)**: Flash loans used for CDP self-liquidation and collateral migration.
- **Order Book DEX (0x0210)**: Flash loans used for cross-venue arbitrage between order book and AMM prices.
- **Governance (0x0102)**: Controls fee parameters, asset listing, and emergency pause.
- **BNS**: Registered as `flash.basalt`.
- **Escrow (0x0103)**: Optional escrow integration for complex multi-step strategies.

## Technical Sketch

```csharp
// ============================================================
// FlashLoanPool -- Multi-asset flash loan provider
// ============================================================

[BasaltContract(TypeId = 0x0260)]
public partial class FlashLoanPool : SdkContract
{
    // --- Storage ---

    // asset => pool configuration
    private StorageMap<Address, PoolConfig> _pools;

    // asset => total deposits
    private StorageMap<Address, UInt256> _totalDeposits;

    // asset => total fees earned (cumulative)
    private StorageMap<Address, UInt256> _totalFeesEarned;

    // asset + depositor => deposit shares
    private StorageMap<Hash256, UInt256> _depositShares;

    // asset => total shares
    private StorageMap<Address, UInt256> _totalShares;

    // Re-entrancy guard: active flash loan flag
    private StorageValue<bool> _flashLoanActive;

    // --- Structs ---

    public struct PoolConfig
    {
        public Address Asset;
        public uint FeeBps;         // flash loan fee (e.g., 9 = 0.09%)
        public bool Active;
        public bool Paused;
    }

    public struct FlashLoanParams
    {
        public Address Asset;
        public UInt256 Amount;
    }

    // --- Flash Loan Callback Interface ---

    /// <summary>
    /// Interface that borrowers must implement to receive flash loans.
    /// </summary>
    // public void OnFlashLoan(
    //     Address initiator,
    //     Address asset,
    //     UInt256 amount,
    //     UInt256 fee,
    //     byte[] data
    // );

    // --- Pool Management ---

    /// <summary>
    /// Initialize a flash loan pool for an asset. Governance-only.
    /// </summary>
    public void CreatePool(Address asset, uint feeBps)
    {
        RequireGovernance();
        Require(_pools.Get(asset).Asset == Address.Zero, "POOL_EXISTS");

        _pools.Set(asset, new PoolConfig
        {
            Asset = asset,
            FeeBps = feeBps,
            Active = true,
            Paused = false
        });

        EmitEvent("PoolCreated", asset, feeBps);
    }

    /// <summary>
    /// Update flash loan fee for an asset pool. Governance-only.
    /// </summary>
    public void UpdateFee(Address asset, uint newFeeBps)
    {
        RequireGovernance();
        var pool = _pools.Get(asset);
        Require(pool.Active, "POOL_INACTIVE");
        pool.FeeBps = newFeeBps;
        _pools.Set(asset, pool);
        EmitEvent("FeeUpdated", asset, newFeeBps);
    }

    /// <summary>
    /// Pause/unpause flash loans for an asset. Governance-only.
    /// </summary>
    public void SetPaused(Address asset, bool paused)
    {
        RequireGovernance();
        var pool = _pools.Get(asset);
        pool.Paused = paused;
        _pools.Set(asset, pool);
        EmitEvent("PauseUpdated", asset, paused);
    }

    // --- Depositing / Withdrawing ---

    /// <summary>
    /// Deposit tokens into the flash loan pool to earn fees.
    /// Receives pool shares (BST-4626).
    /// </summary>
    public UInt256 Deposit(Address asset, UInt256 amount)
    {
        var pool = _pools.Get(asset);
        Require(pool.Active, "POOL_INACTIVE");
        Require(amount > UInt256.Zero, "ZERO_AMOUNT");

        TransferTokenIn(asset, Context.Sender, amount);

        var totalShares = _totalShares.Get(asset);
        var totalDeposit = _totalDeposits.Get(asset);

        UInt256 shares;
        if (totalShares.IsZero)
        {
            shares = amount;
        }
        else
        {
            shares = (amount * totalShares) / totalDeposit;
        }

        var key = ComputeDepositKey(asset, Context.Sender);
        _depositShares.Set(key, _depositShares.Get(key) + shares);
        _totalShares.Set(asset, totalShares + shares);
        _totalDeposits.Set(asset, totalDeposit + amount);

        EmitEvent("Deposited", Context.Sender, asset, amount, shares);
        return shares;
    }

    /// <summary>
    /// Withdraw tokens by burning pool shares.
    /// Share value includes accumulated flash loan fees.
    /// </summary>
    public UInt256 Withdraw(Address asset, UInt256 shares)
    {
        Require(!_flashLoanActive.Get(), "DURING_FLASH_LOAN");

        var key = ComputeDepositKey(asset, Context.Sender);
        var userShares = _depositShares.Get(key);
        Require(userShares >= shares, "INSUFFICIENT_SHARES");

        var totalShares = _totalShares.Get(asset);
        var totalDeposit = _totalDeposits.Get(asset);

        var amount = (shares * totalDeposit) / totalShares;

        _depositShares.Set(key, userShares - shares);
        _totalShares.Set(asset, totalShares - shares);
        _totalDeposits.Set(asset, totalDeposit - amount);

        TransferTokenOut(asset, Context.Sender, amount);

        EmitEvent("Withdrawn", Context.Sender, asset, amount, shares);
        return amount;
    }

    // --- Flash Loan Execution ---

    /// <summary>
    /// Execute a single-asset flash loan. The borrower's OnFlashLoan
    /// callback is invoked, and the loan + fee must be repaid before
    /// the function returns.
    /// </summary>
    public void FlashLoan(
        Address borrower,
        Address asset,
        UInt256 amount,
        byte[] data)
    {
        Require(!_flashLoanActive.Get(), "REENTRANCY");
        var pool = _pools.Get(asset);
        Require(pool.Active && !pool.Paused, "POOL_UNAVAILABLE");

        var totalDeposit = _totalDeposits.Get(asset);
        Require(amount <= totalDeposit, "INSUFFICIENT_LIQUIDITY");

        var fee = (amount * pool.FeeBps) / 10000;
        if (fee.IsZero && amount > UInt256.Zero)
            fee = UInt256.One; // minimum fee of 1 unit

        // Record pre-loan balance
        var balanceBefore = GetTokenBalance(asset);

        // Set re-entrancy guard
        _flashLoanActive.Set(true);

        // Transfer tokens to borrower
        TransferTokenOut(asset, borrower, amount);

        // Invoke borrower callback
        Context.Call(borrower, "OnFlashLoan",
            Context.Sender,  // initiator
            asset,
            amount,
            fee,
            data);

        // Verify repayment
        var balanceAfter = GetTokenBalance(asset);
        Require(balanceAfter >= balanceBefore + fee, "FLASH_LOAN_NOT_REPAID");

        // Clear re-entrancy guard
        _flashLoanActive.Set(false);

        // Update pool accounting with fee income
        _totalDeposits.Set(asset, _totalDeposits.Get(asset) + fee);
        _totalFeesEarned.Set(asset, _totalFeesEarned.Get(asset) + fee);

        EmitEvent("FlashLoan", borrower, asset, amount, fee);
    }

    /// <summary>
    /// Execute a batch flash loan across multiple assets.
    /// All loans must be repaid (with fees) within the same transaction.
    /// </summary>
    public void BatchFlashLoan(
        Address borrower,
        FlashLoanParams[] loans,
        byte[] data)
    {
        Require(!_flashLoanActive.Get(), "REENTRANCY");

        // Record pre-loan balances and calculate fees
        var balancesBefore = new UInt256[loans.Length];
        var fees = new UInt256[loans.Length];

        for (int i = 0; i < loans.Length; i++)
        {
            var pool = _pools.Get(loans[i].Asset);
            Require(pool.Active && !pool.Paused, "POOL_UNAVAILABLE");
            Require(loans[i].Amount <= _totalDeposits.Get(loans[i].Asset),
                    "INSUFFICIENT_LIQUIDITY");

            fees[i] = (loans[i].Amount * pool.FeeBps) / 10000;
            if (fees[i].IsZero && loans[i].Amount > UInt256.Zero)
                fees[i] = UInt256.One;

            balancesBefore[i] = GetTokenBalance(loans[i].Asset);
        }

        _flashLoanActive.Set(true);

        // Transfer all borrowed tokens
        for (int i = 0; i < loans.Length; i++)
            TransferTokenOut(loans[i].Asset, borrower, loans[i].Amount);

        // Invoke borrower callback (single callback for batch)
        Context.Call(borrower, "OnBatchFlashLoan",
            Context.Sender, loans, fees, data);

        // Verify all repayments
        for (int i = 0; i < loans.Length; i++)
        {
            var balanceAfter = GetTokenBalance(loans[i].Asset);
            Require(balanceAfter >= balancesBefore[i] + fees[i],
                    "FLASH_LOAN_NOT_REPAID");

            _totalDeposits.Set(loans[i].Asset,
                _totalDeposits.Get(loans[i].Asset) + fees[i]);
            _totalFeesEarned.Set(loans[i].Asset,
                _totalFeesEarned.Get(loans[i].Asset) + fees[i]);
        }

        _flashLoanActive.Set(false);

        EmitEvent("BatchFlashLoan", borrower, (ulong)loans.Length);
    }

    // --- Queries ---

    /// <summary>
    /// Get the maximum amount available for flash loan of an asset.
    /// </summary>
    public UInt256 MaxFlashLoan(Address asset)
        => _totalDeposits.Get(asset);

    /// <summary>
    /// Calculate the fee for a flash loan of a given amount.
    /// </summary>
    public UInt256 FlashFee(Address asset, UInt256 amount)
    {
        var pool = _pools.Get(asset);
        var fee = (amount * pool.FeeBps) / 10000;
        return fee.IsZero && amount > UInt256.Zero ? UInt256.One : fee;
    }

    /// <summary>
    /// Get depositor's share balance and current value for an asset pool.
    /// </summary>
    public (UInt256 shares, UInt256 value) GetDepositInfo(Address depositor, Address asset)
    {
        var key = ComputeDepositKey(asset, depositor);
        var shares = _depositShares.Get(key);
        var totalShares = _totalShares.Get(asset);
        if (totalShares.IsZero) return (shares, UInt256.Zero);
        var value = (shares * _totalDeposits.Get(asset)) / totalShares;
        return (shares, value);
    }

    public UInt256 GetTotalDeposits(Address asset) => _totalDeposits.Get(asset);
    public UInt256 GetTotalFeesEarned(Address asset) => _totalFeesEarned.Get(asset);

    // --- Internal Helpers ---

    private Hash256 ComputeDepositKey(Address asset, Address depositor) { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private UInt256 GetTokenBalance(Address token) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**Low** -- Flash loans are conceptually simple: lend, callback, verify repayment. The core invariant (balance after >= balance before + fee) is straightforward to check. The main complexity is in re-entrancy protection, batch loan accounting, and the depositor share/fee distribution model. The callback pattern requires careful gas estimation to ensure the borrower's strategy completes within block gas limits.

## Priority

**P1** -- Flash loans are a critical DeFi primitive that enables capital-efficient liquidations (keeping lending protocols solvent), arbitrage (keeping prices aligned across venues), and sophisticated position management. They depend on the lending protocol and AMM being live (as primary use cases) but should be deployed alongside them for maximum ecosystem benefit.
