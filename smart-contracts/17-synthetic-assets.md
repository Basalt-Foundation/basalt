# Synthetic Asset Protocol

## Category

DeFi / Derivatives / Synthetic Assets

## Summary

A protocol for minting synthetic tokens that track the price of external assets such as stocks, commodities, forex pairs, and indices. Users lock BST as over-collateral, and the protocol mints BST-20 synthetic tokens (synths) whose value is pegged to the oracle-reported price of the tracked asset. A liquidation mechanism ensures solvency when collateral ratios fall below the required threshold. Synthetic tokens are freely tradeable on any Basalt DEX.

## Why It's Useful

- **Global Asset Access**: Synthetic assets allow anyone with BST to gain exposure to traditional financial instruments (Apple stock, gold, EUR/USD) without needing a brokerage account, KYC with traditional finance, or access to specific markets. This is particularly powerful for users in regions with limited financial infrastructure.
- **24/7 Markets**: Unlike traditional markets that close on weekends and holidays, synthetic assets on Basalt trade around the clock. Users can adjust their exposure at any time, not just during NYSE hours.
- **No Custody Risk**: Users maintain self-custody of their synthetic tokens. There is no centralized custodian holding the underlying asset, eliminating counterparty risk from intermediaries.
- **Composable in DeFi**: Synthetic tokens are standard BST-20 tokens, meaning they can be used as collateral in lending protocols, traded on DEXes, included in yield farming strategies, and composed with any other DeFi primitive on Basalt.
- **Hedging and Portfolio Diversification**: Users can hedge their crypto exposure by minting synthetic inverse tokens (e.g., short positions) or diversify into non-correlated assets like commodities and forex.
- **Capital Efficiency via Over-Collateralization**: While over-collateralization requires more capital than centralized systems, it provides trustless solvency guarantees. The protocol remains solvent as long as the oracle functions correctly and liquidations are processed promptly.
- **Liquidation Incentives**: The liquidation mechanism creates profitable opportunities for bot operators who monitor collateral ratios and execute liquidations, further decentralizing the protocol's operations.

## Key Features

- **Multi-Asset Support**: The protocol supports any asset for which a price oracle exists. New synthetic assets can be proposed and approved via governance, specifying the oracle feed, collateral ratio, and liquidation parameters.
- **Over-Collateralized Minting**: Users deposit BST as collateral and mint synthetic tokens up to a maximum determined by the collateral ratio (e.g., 150% means $150 of BST collateral for $100 of synthetic tokens).
- **Oracle Price Feeds**: Asset prices are reported by oracle contracts. The protocol supports multiple oracle sources per asset with a median price aggregation to reduce manipulation risk.
- **Liquidation Mechanism**: When a position's collateral ratio falls below the liquidation threshold (e.g., 120%), anyone can liquidate the position by repaying the synth debt and receiving the collateral at a discount (the liquidation bonus).
- **Partial Liquidation**: Positions can be partially liquidated to bring the collateral ratio back above the safety threshold, rather than requiring full liquidation. This preserves more of the user's position.
- **Dynamic Collateral Ratios**: Different synthetic assets can have different collateral requirements based on their volatility. A synthetic gold token might require 130% collateral, while a synthetic Bitcoin token might require 175%.
- **Debt Tracking via BST-3525**: Each minting position is represented as a BST-3525 semi-fungible token, where the slot represents the synthetic asset type and the value tracks the outstanding debt. Positions can be transferred between users.
- **Global Debt Pool**: All minters share a global debt pool. When the total value of all synthetic assets changes, each minter's share of the debt changes proportionally. This socializes the risk across all participants.
- **Staking Rewards for Minters**: Minters who maintain positions above the safety threshold earn a share of trading fees from synthetic token trades on the DEX, incentivizing collateral provision.
- **Emergency Oracle Pause**: If an oracle is detected as stale or compromised, the admin (or governance) can pause minting and liquidation for that specific asset without affecting other synths.

## Basalt-Specific Advantages

- **ZK Compliance for Security Tokens**: Some synthetic assets may represent securities (e.g., synthetic stocks). Basalt's ZK compliance layer allows the protocol to restrict minting and trading of these synths to users who have verified credentials (e.g., accredited investor status) without revealing their identity on-chain. This is a unique capability that EVM chains lack natively.
- **BST-3525 Debt Positions**: Minting positions as BST-3525 SFTs provide rich metadata (slot = asset type, value = debt amount) and enable position transfer, splitting, and merging. On EVM chains, this requires custom NFT implementations; on Basalt, it is a first-class token standard.
- **AOT-Compiled Liquidation Scans**: The liquidation detection logic, which must iterate through positions and check collateral ratios against oracle prices, executes as native AOT-compiled code. This makes real-time position scanning feasible on-chain, whereas EVM chains typically rely on off-chain bots for position monitoring.
- **Pedersen Commitment Privacy for Positions**: Users can optionally hide their position size using Pedersen commitments. The protocol verifies via range proofs that the collateral ratio is sufficient without revealing the exact amounts. This prevents other market participants from targeting large positions for liquidation sniping.
- **BLAKE3 Oracle Feed Hashing**: Oracle price updates are authenticated using BLAKE3 hashes for fast verification. The median aggregation across multiple oracle sources benefits from BLAKE3's speed when processing many price reports per block.
- **Ed25519 Oracle Signatures**: Oracle operators sign price reports with Ed25519, which is faster to verify than ECDSA. This reduces the per-update overhead for high-frequency price feeds.
- **BST-4626 Collateral Vaults**: Deposited BST collateral can be wrapped in a BST-4626 vault to earn staking yield while serving as collateral. The yield accrues to the minter, improving capital efficiency. The vault's share price is used for collateral valuation.
- **UInt256 Precision**: Price calculations involving oracle feeds, collateral ratios, and liquidation math all benefit from Basalt's native UInt256, avoiding the precision loss that plagues fixed-point arithmetic in Solidity.

## Token Standards Used

- **BST-20**: Synthetic tokens are BST-20 fungible tokens, immediately compatible with all Basalt DEXes, wallets, and DeFi protocols.
- **BST-3525 (SFT)**: Minting positions are BST-3525 semi-fungible tokens. The slot identifies the synthetic asset type, and the value represents the outstanding debt amount. Positions are transferable and composable.
- **BST-4626 (Vault)**: Collateral can be deposited into a BST-4626 vault to earn yield while locked, improving capital efficiency for minters.

## Integration Points

- **Governance (0x...1002)**: New synthetic asset types, collateral ratios, liquidation parameters, and oracle configurations are proposed and approved via Governance. Emergency oracle pauses can also be triggered through governance.
- **StakingPool (0x...1005)**: Collateral BST is staked via StakingPool (through a BST-4626 vault) to earn yield for minters.
- **BNS (0x...1001)**: Synthetic tokens register BNS names (e.g., "sAAPL.synth.bst", "sGOLD.synth.bst") for user-friendly identification.
- **Escrow (0x...1003)**: Liquidation proceeds can be held in Escrow for a challenge period, allowing liquidated users to verify the oracle price and collateral calculation before the liquidator receives the collateral.
- **SchemaRegistry (0x...1006)**: Credential schemas for accredited investor verification and other compliance requirements are stored in the SchemaRegistry.
- **IssuerRegistry (0x...1007)**: Validates credential issuers for ZK compliance proofs required to mint or trade compliance-gated synthetic assets.
- **BridgeETH (0x...1008)**: Synthetic tokens on Basalt can be bridged to Ethereum for trading on EVM DEXes, expanding liquidity and market access. Collateral bridged from Ethereum can also be used for minting.

## Technical Sketch

```csharp
// Contract type ID: 0x010C
[BasaltContract(0x010C)]
public partial class SyntheticAsset : SdkContract, IDispatchable
{
    // --- Storage ---

    private StorageValue<Address> _admin;
    private StorageValue<ulong> _nextPositionId;
    private StorageValue<UInt256> _globalDebt;             // total value of all synth debt (in BST)
    private StorageValue<bool> _paused;

    // Asset configuration (keyed by assetId)
    private StorageMap<ulong, bool> _assetActive;
    private StorageMap<ulong, Address> _assetSynthToken;   // BST-20 address of the synthetic token
    private StorageMap<ulong, Address> _assetOracle;       // oracle contract address
    private StorageMap<ulong, ulong> _assetCollateralRatioBps;   // e.g., 15000 = 150%
    private StorageMap<ulong, ulong> _assetLiquidationRatioBps;  // e.g., 12000 = 120%
    private StorageMap<ulong, ulong> _assetLiquidationBonusBps;  // e.g., 500 = 5%
    private StorageMap<ulong, bool> _assetComplianceRequired;
    private StorageValue<ulong> _nextAssetId;

    // Position data (keyed by positionId)
    private StorageMap<ulong, Address> _positionOwner;
    private StorageMap<ulong, ulong> _positionAssetId;
    private StorageMap<ulong, UInt256> _positionCollateral;     // BST locked
    private StorageMap<ulong, UInt256> _positionDebt;           // synth tokens minted
    private StorageMap<ulong, bool> _positionActive;

    // Global debt share tracking
    private StorageMap<ulong, UInt256> _positionDebtShare;      // share of global debt pool
    private StorageValue<UInt256> _totalDebtShares;

    // --- Constructor ---

    public void Initialize(Address admin)
    {
        _admin.Set(admin);
        _nextPositionId.Set(1);
        _nextAssetId.Set(1);
    }

    // --- Asset Management ---

    public ulong RegisterAsset(
        Address synthToken,
        Address oracle,
        ulong collateralRatioBps,
        ulong liquidationRatioBps,
        ulong liquidationBonusBps,
        bool complianceRequired)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        Require(collateralRatioBps > liquidationRatioBps, "Invalid ratios");
        Require(liquidationRatioBps > 10000, "Liq ratio must be > 100%");

        ulong assetId = _nextAssetId.Get();
        _nextAssetId.Set(assetId + 1);

        _assetActive.Set(assetId, true);
        _assetSynthToken.Set(assetId, synthToken);
        _assetOracle.Set(assetId, oracle);
        _assetCollateralRatioBps.Set(assetId, collateralRatioBps);
        _assetLiquidationRatioBps.Set(assetId, liquidationRatioBps);
        _assetLiquidationBonusBps.Set(assetId, liquidationBonusBps);
        _assetComplianceRequired.Set(assetId, complianceRequired);

        return assetId;
    }

    public void PauseAsset(ulong assetId)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _assetActive.Set(assetId, false);
    }

    public void UnpauseAsset(ulong assetId)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _assetActive.Set(assetId, true);
    }

    // --- Minting ---

    public ulong OpenPosition(ulong assetId, UInt256 synthAmount)
    {
        Require(_assetActive.Get(assetId), "Asset not active");
        Require(!_paused.Get(), "Protocol paused");

        if (_assetComplianceRequired.Get(assetId))
            RequireCompliance(Context.Caller);

        UInt256 collateral = Context.TxValue;
        Require(!collateral.IsZero, "Must deposit collateral");

        // Get oracle price: how much BST is 1 unit of the synthetic asset worth
        UInt256 assetPrice = GetOraclePrice(assetId);
        Require(!assetPrice.IsZero, "Oracle price unavailable");

        // Verify collateral ratio
        UInt256 debtValue = synthAmount * assetPrice / Precision;
        UInt256 requiredCollateral = debtValue
            * _assetCollateralRatioBps.Get(assetId) / 10000;
        Require(collateral >= requiredCollateral, "Insufficient collateral");

        ulong positionId = _nextPositionId.Get();
        _nextPositionId.Set(positionId + 1);

        _positionOwner.Set(positionId, Context.Caller);
        _positionAssetId.Set(positionId, assetId);
        _positionCollateral.Set(positionId, collateral);
        _positionDebt.Set(positionId, synthAmount);
        _positionActive.Set(positionId, true);

        // Update global debt
        UInt256 debtShares = _totalDebtShares.Get().IsZero
            ? debtValue
            : debtValue * _totalDebtShares.Get() / _globalDebt.Get();
        _positionDebtShare.Set(positionId, debtShares);
        _totalDebtShares.Set(_totalDebtShares.Get() + debtShares);
        _globalDebt.Set(_globalDebt.Get() + debtValue);

        // Mint synthetic tokens to user
        MintSynthTokens(_assetSynthToken.Get(assetId), Context.Caller, synthAmount);

        return positionId;
    }

    // --- Collateral Management ---

    public void AddCollateral(ulong positionId)
    {
        Require(_positionOwner.Get(positionId) == Context.Caller, "Not position owner");
        Require(_positionActive.Get(positionId), "Position not active");

        UInt256 additionalCollateral = Context.TxValue;
        Require(!additionalCollateral.IsZero, "Must deposit collateral");

        _positionCollateral.Set(positionId,
            _positionCollateral.Get(positionId) + additionalCollateral);
    }

    public void WithdrawCollateral(ulong positionId, UInt256 amount)
    {
        Require(_positionOwner.Get(positionId) == Context.Caller, "Not position owner");
        Require(_positionActive.Get(positionId), "Position not active");

        UInt256 currentCollateral = _positionCollateral.Get(positionId);
        Require(currentCollateral >= amount, "Insufficient collateral");

        UInt256 remainingCollateral = currentCollateral - amount;

        // Verify collateral ratio remains sufficient after withdrawal
        ulong assetId = _positionAssetId.Get(positionId);
        UInt256 assetPrice = GetOraclePrice(assetId);
        UInt256 debtValue = _positionDebt.Get(positionId) * assetPrice / Precision;
        UInt256 requiredCollateral = debtValue
            * _assetCollateralRatioBps.Get(assetId) / 10000;
        Require(remainingCollateral >= requiredCollateral, "Would breach collateral ratio");

        _positionCollateral.Set(positionId, remainingCollateral);
        Context.TransferNative(Context.Caller, amount);
    }

    // --- Repayment ---

    public void RepayDebt(ulong positionId, UInt256 synthAmount)
    {
        Require(_positionOwner.Get(positionId) == Context.Caller, "Not position owner");
        Require(_positionActive.Get(positionId), "Position not active");

        UInt256 currentDebt = _positionDebt.Get(positionId);
        Require(synthAmount <= currentDebt, "Repay exceeds debt");

        // Burn synthetic tokens from caller
        ulong assetId = _positionAssetId.Get(positionId);
        BurnSynthTokens(_assetSynthToken.Get(assetId), Context.Caller, synthAmount);

        _positionDebt.Set(positionId, currentDebt - synthAmount);

        // Update global debt
        UInt256 assetPrice = GetOraclePrice(assetId);
        UInt256 debtValueReduced = synthAmount * assetPrice / Precision;
        UInt256 shareReduction = debtValueReduced * _totalDebtShares.Get() / _globalDebt.Get();
        _positionDebtShare.Set(positionId,
            _positionDebtShare.Get(positionId) - shareReduction);
        _totalDebtShares.Set(_totalDebtShares.Get() - shareReduction);
        _globalDebt.Set(_globalDebt.Get() - debtValueReduced);

        // Close position if fully repaid
        if (_positionDebt.Get(positionId).IsZero)
        {
            _positionActive.Set(positionId, false);
            UInt256 collateral = _positionCollateral.Get(positionId);
            _positionCollateral.Set(positionId, UInt256.Zero);
            Context.TransferNative(Context.Caller, collateral);
        }
    }

    // --- Liquidation ---

    public UInt256 Liquidate(ulong positionId, UInt256 synthAmount)
    {
        Require(_positionActive.Get(positionId), "Position not active");

        ulong assetId = _positionAssetId.Get(positionId);
        Require(_assetActive.Get(assetId), "Asset paused");

        UInt256 assetPrice = GetOraclePrice(assetId);
        UInt256 debtValue = _positionDebt.Get(positionId) * assetPrice / Precision;
        UInt256 collateral = _positionCollateral.Get(positionId);

        // Check if position is below liquidation threshold
        UInt256 currentRatioBps = collateral * 10000 / debtValue;
        ulong liquidationRatio = _assetLiquidationRatioBps.Get(assetId);
        Require(currentRatioBps < liquidationRatio, "Position is healthy");

        UInt256 currentDebt = _positionDebt.Get(positionId);
        UInt256 liquidateAmount = synthAmount <= currentDebt ? synthAmount : currentDebt;

        // Burn liquidator's synthetic tokens
        BurnSynthTokens(_assetSynthToken.Get(assetId), Context.Caller, liquidateAmount);

        // Calculate collateral to seize (debt value + liquidation bonus)
        UInt256 debtPortionValue = liquidateAmount * assetPrice / Precision;
        ulong bonusBps = _assetLiquidationBonusBps.Get(assetId);
        UInt256 collateralSeized = debtPortionValue + (debtPortionValue * bonusBps / 10000);

        if (collateralSeized > collateral)
            collateralSeized = collateral; // cap at available collateral

        // Update position
        _positionDebt.Set(positionId, currentDebt - liquidateAmount);
        _positionCollateral.Set(positionId, collateral - collateralSeized);

        // Update global debt
        UInt256 shareReduction = debtPortionValue * _totalDebtShares.Get() / _globalDebt.Get();
        _positionDebtShare.Set(positionId,
            _positionDebtShare.Get(positionId) - shareReduction);
        _totalDebtShares.Set(_totalDebtShares.Get() - shareReduction);
        _globalDebt.Set(_globalDebt.Get() - debtPortionValue);

        // Close position if fully liquidated
        if (_positionDebt.Get(positionId).IsZero)
        {
            _positionActive.Set(positionId, false);
            UInt256 remainingCollateral = _positionCollateral.Get(positionId);
            if (!remainingCollateral.IsZero)
            {
                _positionCollateral.Set(positionId, UInt256.Zero);
                Context.TransferNative(_positionOwner.Get(positionId), remainingCollateral);
            }
        }

        // Transfer seized collateral to liquidator
        Context.TransferNative(Context.Caller, collateralSeized);

        return collateralSeized;
    }

    // --- Query ---

    public UInt256 GetCollateralRatio(ulong positionId)
    {
        ulong assetId = _positionAssetId.Get(positionId);
        UInt256 assetPrice = GetOraclePrice(assetId);
        UInt256 debtValue = _positionDebt.Get(positionId) * assetPrice / Precision;
        if (debtValue.IsZero) return UInt256.Zero;
        UInt256 collateral = _positionCollateral.Get(positionId);
        return collateral * 10000 / debtValue; // returns basis points
    }

    public bool IsLiquidatable(ulong positionId)
    {
        if (!_positionActive.Get(positionId)) return false;
        ulong assetId = _positionAssetId.Get(positionId);
        UInt256 ratio = GetCollateralRatio(positionId);
        return ratio < _assetLiquidationRatioBps.Get(assetId);
    }

    public UInt256 GetPositionCollateral(ulong positionId) => _positionCollateral.Get(positionId);
    public UInt256 GetPositionDebt(ulong positionId) => _positionDebt.Get(positionId);
    public Address GetPositionOwner(ulong positionId) => _positionOwner.Get(positionId);
    public bool IsPositionActive(ulong positionId) => _positionActive.Get(positionId);
    public UInt256 GlobalDebt() => _globalDebt.Get();

    public UInt256 GetMaxMintable(ulong assetId, UInt256 collateralAmount)
    {
        UInt256 assetPrice = GetOraclePrice(assetId);
        if (assetPrice.IsZero) return UInt256.Zero;
        ulong ratioRequired = _assetCollateralRatioBps.Get(assetId);
        UInt256 maxDebtValue = collateralAmount * 10000 / ratioRequired;
        return maxDebtValue * Precision / assetPrice;
    }

    // --- Internal ---

    private UInt256 GetOraclePrice(ulong assetId)
    {
        // Cross-contract call to the oracle contract for this asset
        // Returns: price of 1 synth unit in BST (scaled by Precision)
        Address oracle = _assetOracle.Get(assetId);
        // oracle.GetPrice() -> UInt256
        return UInt256.Zero; // placeholder
    }

    private void MintSynthTokens(Address synthToken, Address to, UInt256 amount)
    {
        // Cross-contract call to BST-20 synthToken.Mint(to, amount)
        // The synthetic asset protocol must be the minting authority
    }

    private void BurnSynthTokens(Address synthToken, Address from, UInt256 amount)
    {
        // Cross-contract call to BST-20 synthToken.Burn(from, amount)
    }

    private void RequireCompliance(Address account)
    {
        // Validate ZK compliance proof via SchemaRegistry + IssuerRegistry
    }

    private static readonly UInt256 Precision = new UInt256(1_000_000_000_000_000_000UL);
}
```

## Complexity

**High** -- Synthetic asset protocols are among the most complex DeFi primitives. The complexity stems from multiple interacting systems: oracle integration with staleness and manipulation detection, dynamic collateral ratio management across multiple asset types, a global debt pool with proportional share tracking, partial and full liquidation mechanics with bonus calculations, and compliance integration for security-type synthetics. Each component has subtle edge cases: oracle price feed delays can create brief arbitrage windows, rounding in debt share calculations can lead to dust accumulation, and liquidation cascades during rapid price movements must be handled gracefully. The dependency on external oracle infrastructure adds operational complexity beyond the smart contract itself.

## Priority

**P2** -- Synthetic assets are a powerful ecosystem differentiator and represent one of the most ambitious DeFi primitives. However, they depend on reliable oracle infrastructure (which must be built or integrated separately), a functioning DEX for trading synthetic tokens, and sufficient BST liquidity for collateralization. The compliance integration for synthetic securities is a unique selling point for Basalt but adds regulatory complexity. This contract should be prioritized after the core DeFi stack (DEX, lending, staking) is established and oracle infrastructure is operational.
