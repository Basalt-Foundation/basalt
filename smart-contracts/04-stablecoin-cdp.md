# Collateralized Stablecoin (CDP)

## Category

Decentralized Finance (DeFi) -- Stablecoins

## Summary

A collateralized debt position (CDP) protocol that enables users to lock BST and other approved collateral assets to mint a USD-pegged stablecoin (USDB). The system uses oracle-fed price feeds, overcollateralization ratios, stability fees, and liquidation auctions to maintain the peg. ZK compliance gates minting to KYC-verified users while allowing permissionless holding and transfer of the stablecoin.

## Why It's Useful

- **Native Stable Unit of Account**: A decentralized stablecoin is essential for DeFi to function -- it provides a stable denomination for lending, trading, yield farming, and payments without reliance on centralized stablecoin issuers.
- **Capital Efficiency for BST Holders**: BST holders can unlock liquidity by minting USDB against their holdings without selling, maintaining their long exposure while accessing stable capital.
- **Censorship Resistance**: Unlike centralized stablecoins (USDC, USDT) that can freeze accounts, USDB is governed by code and governance -- no single entity can freeze or blacklist holders.
- **DeFi Composability**: USDB becomes the base quote currency across AMM pools, lending markets, and derivatives, creating deep liquidity and tight spreads.
- **Regulatory Compliance via ZK**: By gating minting through ZK compliance (KYC required to open a CDP) while allowing permissionless transfer, the protocol satisfies regulatory concerns about stablecoin issuance without sacrificing user privacy or fungibility.

## Key Features

- **Multi-Collateral CDPs**: Support for BST and governance-approved BST-20 tokens as collateral. Each collateral type has its own risk parameters (collateralization ratio, stability fee, debt ceiling).
- **Oracle-Fed Price Feeds**: Collateral prices sourced from multiple oracle providers with median aggregation and staleness checks. TWAP from AMM pools serves as a fallback.
- **Overcollateralization Ratio**: Minimum collateral ratio per collateral type (e.g., 150% for BST, 120% for stablecoins). Users must maintain this ratio to avoid liquidation.
- **Stability Fee**: Continuously accruing interest on minted USDB, payable in BST or USDB. Revenue funds the protocol surplus buffer and governance treasury.
- **USDB Savings Rate (USR)**: Holders can deposit USDB into a savings module to earn yield funded by stability fees, creating demand for holding USDB.
- **Liquidation Auctions**: When a CDP falls below the minimum collateral ratio, its collateral is auctioned via a Dutch auction (descending price). Auction proceeds repay the debt; surplus goes to the CDP owner.
- **Emergency Shutdown**: Governance-triggered mechanism to freeze the protocol, allowing all USDB holders to redeem for underlying collateral at a fixed rate.
- **Debt Ceiling**: Per-collateral-type and global caps on USDB minting, preventing overexposure to any single collateral.
- **Surplus Buffer**: Protocol accumulates stability fees in a surplus buffer. When the buffer exceeds a threshold, excess is auctioned for BST, which is burned (deflationary pressure).
- **ZK-Gated Minting**: Opening a CDP requires a valid ZK compliance proof (KYC verification), but USDB itself is a standard BST-20 with no transfer restrictions.

## Basalt-Specific Advantages

- **ZK Compliance for Regulated Minting**: Basalt's built-in ZK compliance infrastructure (SchemaRegistry + IssuerRegistry + ZkComplianceVerifier) enables CDP minting to be gated by KYC without revealing the minter's identity on-chain. This uniquely satisfies emerging stablecoin regulations (e.g., EU MiCA, US stablecoin bills) that require issuer KYC while preserving transactional privacy.
- **Confidential CDP Positions**: Pedersen commitments can hide collateral amounts and debt levels, preventing competitors or liquidation hunters from front-running large CDP adjustments.
- **AOT-Compiled Liquidation Engine**: Dutch auction liquidation logic runs as native AOT-compiled code, enabling faster and cheaper liquidation execution during market crashes when many CDPs become undercollateralized simultaneously.
- **BST-3525 SFT CDP Positions**: CDPs represented as BST-3525 semi-fungible tokens with slot metadata encoding collateral type, ratio, and accrued fees. This enables secondary markets for CDP positions and portfolio management across multiple CDPs.
- **BLS Multi-Oracle Verification**: Oracle price updates can be aggregated via BLS signatures from multiple oracle providers, verifying a single aggregated signature instead of N individual signatures -- reducing on-chain cost for price feed updates.
- **Ed25519 Governance Signatures**: Emergency shutdown proposals and multi-sig governance actions use Ed25519 for faster signature verification.

## Token Standards Used

- **BST-20**: USDB stablecoin is a BST-20 token. All collateral assets are BST-20.
- **BST-3525 (SFT)**: CDP positions represented as semi-fungible tokens with collateral metadata.
- **BST-4626 (Vault)**: USDB Savings Rate module uses BST-4626 vault interface for yield-bearing USDB deposits.

## Integration Points

- **SchemaRegistry (0x...1006) / IssuerRegistry (0x...1007)**: ZK compliance proof verification for CDP minting. Verifies KYC credential schemas and trusted issuers.
- **Governance (0x0102)**: Controls all risk parameters (collateralization ratios, stability fees, debt ceilings), emergency shutdown, new collateral listings, and surplus buffer management.
- **AMM DEX (0x0200)**: TWAP oracle fallback for collateral pricing; USDB liquidity pools for peg stability.
- **BNS**: Registered as `usdb.basalt` and `cdp.basalt`.
- **StakingPool (0x0105)**: Stability fee surplus can fund staking rewards; stBSLT can serve as collateral.
- **BridgeETH (0x...1008)**: Bridged ETH or wrapped assets can be used as CDP collateral.

## Technical Sketch

```csharp
// ============================================================
// StablecoinCDP -- Collateralized Debt Position Manager
// ============================================================

[BasaltContract(TypeId = 0x0230)]
public partial class StablecoinCDP : SdkContract
{
    // --- Storage ---

    // cdpId => CDP data
    private StorageMap<ulong, CDP> _cdps;
    private StorageValue<ulong> _nextCdpId;

    // collateral address => CollateralConfig
    private StorageMap<Address, CollateralConfig> _collateralConfigs;

    // collateral address => total debt minted against this collateral
    private StorageMap<Address, UInt256> _totalDebtPerCollateral;

    // Global total USDB supply
    private StorageValue<UInt256> _globalDebt;
    private StorageValue<UInt256> _globalDebtCeiling;

    // Surplus buffer (stability fee revenue)
    private StorageValue<UInt256> _surplusBuffer;
    private StorageValue<UInt256> _surplusThreshold;

    // USDB token address
    private StorageValue<Address> _usdbToken;

    // Oracle aggregator address
    private StorageValue<Address> _oracle;

    // Savings rate (per-block, 1e27 scale)
    private StorageValue<UInt256> _savingsRate;

    // Emergency shutdown flag
    private StorageValue<bool> _shutdown;

    // --- Structs ---

    public struct CDP
    {
        public ulong Id;
        public Address Owner;
        public Address CollateralAsset;
        public UInt256 CollateralAmount;
        public UInt256 DebtAmount;          // USDB owed
        public UInt256 AccruedFees;         // accumulated stability fee
        public ulong LastFeeAccrualBlock;
        public bool Active;
    }

    public struct CollateralConfig
    {
        public Address Asset;
        public UInt256 MinCollateralRatioBps;   // e.g., 15000 = 150%
        public UInt256 LiquidationRatioBps;     // e.g., 13000 = 130%
        public UInt256 StabilityFeePerBlock;    // per-block rate (1e27 scale)
        public UInt256 DebtCeiling;
        public UInt256 LiquidationPenaltyBps;   // e.g., 1300 = 13%
        public UInt256 DustThreshold;            // minimum debt size
        public bool Active;
    }

    public struct LiquidationAuction
    {
        public ulong CdpId;
        public UInt256 CollateralForSale;
        public UInt256 DebtToRaise;
        public UInt256 StartPrice;
        public ulong StartBlock;
        public ulong Duration;          // blocks until price reaches floor
    }

    // --- CDP Management ---

    /// <summary>
    /// Open a new CDP by depositing collateral.
    /// Requires ZK compliance proof for KYC verification.
    /// </summary>
    public ulong OpenCDP(Address collateralAsset, UInt256 collateralAmount)
    {
        Require(!_shutdown.Get(), "SYSTEM_SHUTDOWN");

        // Verify KYC compliance via ZK proof
        RequireZkCompliance(Context.Sender);

        var config = _collateralConfigs.Get(collateralAsset);
        Require(config.Active, "COLLATERAL_NOT_SUPPORTED");

        TransferTokenIn(collateralAsset, Context.Sender, collateralAmount);

        var cdpId = _nextCdpId.Get();
        _nextCdpId.Set(cdpId + 1);

        var cdp = new CDP
        {
            Id = cdpId,
            Owner = Context.Sender,
            CollateralAsset = collateralAsset,
            CollateralAmount = collateralAmount,
            DebtAmount = UInt256.Zero,
            AccruedFees = UInt256.Zero,
            LastFeeAccrualBlock = Context.BlockNumber,
            Active = true
        };

        _cdps.Set(cdpId, cdp);
        EmitEvent("CDPOpened", cdpId, Context.Sender, collateralAsset, collateralAmount);
        return cdpId;
    }

    /// <summary>
    /// Deposit additional collateral into an existing CDP.
    /// </summary>
    public void DepositCollateral(ulong cdpId, UInt256 amount)
    {
        var cdp = _cdps.Get(cdpId);
        Require(cdp.Owner == Context.Sender, "NOT_OWNER");
        Require(cdp.Active, "CDP_CLOSED");

        AccrueStabilityFee(ref cdp);
        TransferTokenIn(cdp.CollateralAsset, Context.Sender, amount);

        cdp.CollateralAmount += amount;
        _cdps.Set(cdpId, cdp);
        EmitEvent("CollateralDeposited", cdpId, amount);
    }

    /// <summary>
    /// Withdraw collateral from a CDP. Must maintain minimum collateral ratio.
    /// </summary>
    public void WithdrawCollateral(ulong cdpId, UInt256 amount)
    {
        var cdp = _cdps.Get(cdpId);
        Require(cdp.Owner == Context.Sender, "NOT_OWNER");
        Require(cdp.Active, "CDP_CLOSED");

        AccrueStabilityFee(ref cdp);
        cdp.CollateralAmount -= amount;

        // Check collateral ratio remains above minimum
        if (cdp.DebtAmount > UInt256.Zero)
        {
            var ratio = GetCollateralRatio(cdp);
            var config = _collateralConfigs.Get(cdp.CollateralAsset);
            Require(ratio >= config.MinCollateralRatioBps, "BELOW_MIN_RATIO");
        }

        _cdps.Set(cdpId, cdp);
        TransferTokenOut(cdp.CollateralAsset, Context.Sender, amount);
        EmitEvent("CollateralWithdrawn", cdpId, amount);
    }

    /// <summary>
    /// Mint USDB against CDP collateral.
    /// Requires ZK compliance proof (KYC) for minting.
    /// </summary>
    public void MintUSDB(ulong cdpId, UInt256 amount)
    {
        Require(!_shutdown.Get(), "SYSTEM_SHUTDOWN");
        RequireZkCompliance(Context.Sender);

        var cdp = _cdps.Get(cdpId);
        Require(cdp.Owner == Context.Sender, "NOT_OWNER");
        Require(cdp.Active, "CDP_CLOSED");

        AccrueStabilityFee(ref cdp);
        cdp.DebtAmount += amount;

        // Check debt ceiling
        var config = _collateralConfigs.Get(cdp.CollateralAsset);
        var totalDebt = _totalDebtPerCollateral.Get(cdp.CollateralAsset);
        Require(totalDebt + amount <= config.DebtCeiling, "DEBT_CEILING_REACHED");
        Require(_globalDebt.Get() + amount <= _globalDebtCeiling.Get(), "GLOBAL_CEILING");

        // Check dust threshold
        Require(cdp.DebtAmount >= config.DustThreshold, "BELOW_DUST");

        // Check collateral ratio
        var ratio = GetCollateralRatio(cdp);
        Require(ratio >= config.MinCollateralRatioBps, "INSUFFICIENT_COLLATERAL");

        _cdps.Set(cdpId, cdp);
        _totalDebtPerCollateral.Set(cdp.CollateralAsset, totalDebt + amount);
        _globalDebt.Set(_globalDebt.Get() + amount);

        // Mint USDB to CDP owner
        MintToken(_usdbToken.Get(), Context.Sender, amount);
        EmitEvent("USDBMinted", cdpId, amount);
    }

    /// <summary>
    /// Repay USDB debt to free collateral. Burns USDB.
    /// Anyone can repay (no KYC required for repayment).
    /// </summary>
    public void RepayUSDB(ulong cdpId, UInt256 amount)
    {
        var cdp = _cdps.Get(cdpId);
        Require(cdp.Active, "CDP_CLOSED");

        AccrueStabilityFee(ref cdp);

        var totalOwed = cdp.DebtAmount + cdp.AccruedFees;
        var repayAmount = UInt256.Min(amount, totalOwed);

        // Apply to fees first, then principal
        if (repayAmount <= cdp.AccruedFees)
        {
            cdp.AccruedFees -= repayAmount;
        }
        else
        {
            var debtRepay = repayAmount - cdp.AccruedFees;
            cdp.AccruedFees = UInt256.Zero;
            cdp.DebtAmount -= debtRepay;
        }

        // Burn USDB from sender
        BurnToken(_usdbToken.Get(), Context.Sender, repayAmount);

        _cdps.Set(cdpId, cdp);
        _globalDebt.Set(_globalDebt.Get() - repayAmount);
        EmitEvent("USDBRepaid", cdpId, repayAmount);
    }

    /// <summary>
    /// Close a CDP entirely. Repays all debt + fees, returns all collateral.
    /// </summary>
    public void CloseCDP(ulong cdpId)
    {
        var cdp = _cdps.Get(cdpId);
        Require(cdp.Owner == Context.Sender, "NOT_OWNER");

        AccrueStabilityFee(ref cdp);
        var totalOwed = cdp.DebtAmount + cdp.AccruedFees;

        if (totalOwed > UInt256.Zero)
            BurnToken(_usdbToken.Get(), Context.Sender, totalOwed);

        TransferTokenOut(cdp.CollateralAsset, Context.Sender, cdp.CollateralAmount);

        cdp.Active = false;
        cdp.CollateralAmount = UInt256.Zero;
        cdp.DebtAmount = UInt256.Zero;
        cdp.AccruedFees = UInt256.Zero;
        _cdps.Set(cdpId, cdp);

        EmitEvent("CDPClosed", cdpId);
    }

    // --- Liquidation ---

    /// <summary>
    /// Initiate liquidation of an undercollateralized CDP via Dutch auction.
    /// </summary>
    public ulong Liquidate(ulong cdpId)
    {
        var cdp = _cdps.Get(cdpId);
        Require(cdp.Active, "CDP_CLOSED");

        AccrueStabilityFee(ref cdp);
        var ratio = GetCollateralRatio(cdp);
        var config = _collateralConfigs.Get(cdp.CollateralAsset);
        Require(ratio < config.LiquidationRatioBps, "NOT_LIQUIDATABLE");

        // Calculate penalty
        var penalty = cdp.DebtAmount * config.LiquidationPenaltyBps / 10000;
        var debtToRaise = cdp.DebtAmount + cdp.AccruedFees + penalty;

        var startPrice = GetCollateralPrice(cdp.CollateralAsset) * 2; // 2x market for Dutch auction

        // Create auction
        var auction = new LiquidationAuction
        {
            CdpId = cdpId,
            CollateralForSale = cdp.CollateralAmount,
            DebtToRaise = debtToRaise,
            StartPrice = startPrice,
            StartBlock = Context.BlockNumber,
            Duration = 600 // ~1 hour in blocks
        };

        // Mark CDP as under liquidation
        cdp.Active = false;
        _cdps.Set(cdpId, cdp);

        EmitEvent("LiquidationStarted", cdpId, debtToRaise, cdp.CollateralAmount);
        return StoreAuction(auction);
    }

    /// <summary>
    /// Bid on a Dutch auction. Price decreases over time from startPrice.
    /// Bidder pays USDB, receives collateral at current auction price.
    /// </summary>
    public void BidOnAuction(ulong auctionId, UInt256 maxCollateralAmount)
    {
        var auction = GetAuction(auctionId);
        var blocksElapsed = Context.BlockNumber - auction.StartBlock;
        Require(blocksElapsed <= auction.Duration, "AUCTION_EXPIRED");

        // Dutch auction: price decreases linearly
        var currentPrice = auction.StartPrice
            - (auction.StartPrice * blocksElapsed / auction.Duration);

        var collateralToBuy = UInt256.Min(maxCollateralAmount, auction.CollateralForSale);
        var cost = collateralToBuy * currentPrice / 1_000_000_000_000_000_000UL;

        BurnToken(_usdbToken.Get(), Context.Sender, cost);
        TransferTokenOut(GetCdp(auction.CdpId).CollateralAsset, Context.Sender, collateralToBuy);

        // Update auction state
        auction.CollateralForSale -= collateralToBuy;
        auction.DebtToRaise -= UInt256.Min(cost, auction.DebtToRaise);

        EmitEvent("AuctionBid", auctionId, Context.Sender, collateralToBuy, cost);
    }

    // --- Stability Fee Accrual ---

    private void AccrueStabilityFee(ref CDP cdp)
    {
        var blocksElapsed = Context.BlockNumber - cdp.LastFeeAccrualBlock;
        if (blocksElapsed == 0) return;

        var config = _collateralConfigs.Get(cdp.CollateralAsset);
        var feeAccrued = cdp.DebtAmount * config.StabilityFeePerBlock * blocksElapsed
                       / 1_000_000_000_000_000_000_000_000_000UL;

        cdp.AccruedFees += feeAccrued;
        cdp.LastFeeAccrualBlock = Context.BlockNumber;

        _surplusBuffer.Set(_surplusBuffer.Get() + feeAccrued);
    }

    // --- Emergency Shutdown ---

    /// <summary>
    /// Governance-only: trigger emergency shutdown. Freezes all minting.
    /// USDB holders can redeem for proportional collateral.
    /// </summary>
    public void EmergencyShutdown()
    {
        RequireGovernance();
        _shutdown.Set(true);
        EmitEvent("EmergencyShutdown", Context.BlockNumber);
    }

    /// <summary>
    /// After shutdown: redeem USDB for proportional collateral.
    /// </summary>
    public void RedeemAfterShutdown(UInt256 usdbAmount, Address collateralAsset)
    {
        Require(_shutdown.Get(), "NOT_SHUTDOWN");
        // Calculate proportional collateral based on total debt vs total collateral
        // Burn USDB, transfer collateral
    }

    // --- Helpers ---

    private UInt256 GetCollateralRatio(CDP cdp)
    {
        var collateralValue = cdp.CollateralAmount * GetCollateralPrice(cdp.CollateralAsset);
        var debtValue = (cdp.DebtAmount + cdp.AccruedFees) * 1_000_000_000_000_000_000UL;
        if (debtValue.IsZero) return UInt256.MaxValue;
        return (collateralValue * 10000) / debtValue;
    }

    private UInt256 GetCollateralPrice(Address asset) { /* Oracle query */ }
    private void RequireZkCompliance(Address user) { /* ZK proof check via SchemaRegistry */ }
    private void MintToken(Address token, Address to, UInt256 amount) { /* ... */ }
    private void BurnToken(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private ulong StoreAuction(LiquidationAuction auction) { /* ... */ }
    private LiquidationAuction GetAuction(ulong id) { /* ... */ }
    private CDP GetCdp(ulong id) => _cdps.Get(id);
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**High** -- CDP systems are among the most complex DeFi contracts, requiring: precise stability fee accrual with arbitrary time intervals, multi-collateral risk parameterization, robust oracle integration with staleness/manipulation protection, Dutch auction liquidation mechanics, emergency shutdown with proportional redemption, and careful debt accounting across open/close/liquidation paths. The ZK compliance layer adds verification complexity for minting operations.

## Priority

**P0** -- A native stablecoin is essential for the Basalt DeFi ecosystem. Without a stable denomination, lending rates, derivatives, and payments all suffer from excessive volatility. USDB should launch alongside or shortly after the AMM and lending protocol. The ZK-gated minting provides a unique regulatory compliance narrative.
