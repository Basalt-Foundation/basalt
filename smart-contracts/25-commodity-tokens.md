# Commodity-Backed Tokens

## Category

Decentralized Finance (DeFi) / Commodities / Real-World Assets (RWA) / Synthetic Assets

## Summary

Commodity-Backed Tokens is a BST-20 fungible token contract that represents ownership of physical commodities such as gold, silver, oil, or agricultural products. Each commodity type is deployed as a separate contract instance, with fungible tokens backed 1:1 by physical reserves held in custody. The contract integrates oracle price feeds for real-time valuation, supports physical redemption for holders who wish to take delivery, and maintains proof of reserves through BST-VC attestations from independent auditors. The design supports both fully-backed tokens (physical reserves in custody) and synthetic tokens (collateralized by BST with oracle-based pricing).

The proof-of-reserves system uses BST-VC Verifiable Credentials from registered auditors who periodically attest that the physical reserves match or exceed the circulating token supply. This creates a transparent, on-chain audit trail that eliminates the opacity problems that have plagued existing commodity-backed tokens (e.g., questions about Tether Gold's reserves or PAXG's custody arrangements).

## Why It's Useful

- Commodity exposure is a key portfolio diversification tool, but physical commodity ownership requires storage, insurance, and logistics; tokenization provides exposure without these operational burdens.
- Gold-backed tokens alone represent a $1B+ market (PAXG, XAUT), but existing solutions lack transparent, on-chain proof of reserves; BST-VC attestations from registered auditors provide cryptographically verifiable reserve proofs.
- Physical commodity trading requires warehouse receipts, title transfers, and custodian relationships that are inaccessible to retail investors; tokenization enables fractional ownership and 24/7 trading.
- Agricultural commodity tokens can provide price stability tools for farmers in developing economies who need to hedge against price volatility without access to traditional futures markets.
- Synthetic commodity tokens (collateralized by crypto) enable commodity exposure without physical custody, expanding the addressable market to any tokenizable asset with an oracle price feed.
- Redemption for physical delivery bridges the gap between tokenized and physical markets, ensuring tokens maintain their peg to the underlying commodity.

## Key Features

- BST-20 fungible token representing a specific commodity (e.g., 1 token = 1 troy ounce of gold, or 1 barrel of oil).
- Oracle price feed integration: on-chain price updates from designated oracle operators for real-time valuation in BST or stablecoin terms.
- Proof of reserves via BST-VC: registered auditors periodically submit Verifiable Credential attestations confirming that physical reserves match or exceed circulating supply.
- Reserve ratio tracking: on-chain calculation of reserve ratio (physical reserves / circulating supply) updated with each attestation.
- Physical redemption: token holders can burn tokens and request physical delivery of the underlying commodity, subject to minimum delivery quantities and logistics fees.
- Minting by authorized custodians: only registered custodians (verified via IssuerRegistry) can mint new tokens when new physical reserves are deposited.
- Burning on redemption: tokens are permanently burned when physical delivery is initiated, reducing circulating supply.
- Synthetic mode: optional collateralized minting where users lock BST as collateral and mint synthetic commodity tokens at a configurable collateralization ratio (e.g., 150%).
- Liquidation mechanism: for synthetic tokens, undercollateralized positions are liquidated when the collateral value drops below the maintenance ratio.
- Custody fee deduction: configurable annual custody fee (in basis points) deducted from holder balances or from the reserve.
- Price staleness protection: oracle price feeds include a freshness check; operations requiring price data revert if the price is stale beyond a configured threshold.
- Emergency pause: admin can pause minting and transfers in case of custody issues, with governance oversight.

## Basalt-Specific Advantages

- **BST-VC Verifiable Credentials**: Proof of reserves is the critical trust component for commodity-backed tokens. Basalt's built-in BST-VC standard allows registered auditors to submit W3C Verifiable Credentials attesting to physical reserves. Each attestation is cryptographically signed, timestamped, and permanently recorded on-chain. Unlike centralized proof-of-reserves (e.g., Chainlink PoR on Ethereum), Basalt's approach integrates with the native identity layer -- auditors must be registered in the IssuerRegistry, and their credentials are verifiable by any participant without external dependencies.
- **IssuerRegistry Integration**: Custodians, auditors, and oracle operators must be registered in the protocol-level IssuerRegistry. This creates a curated trust network where adding new participants requires governance approval, preventing unauthorized minting or fraudulent attestations while maintaining decentralization.
- **ZK Compliance Layer**: Large commodity positions may require compliance verification (e.g., precious metals regulations, sanctions screening). Basalt's ZK proof system allows holders to prove compliance without revealing position sizes or trading patterns.
- **AOT-Compiled Execution**: Liquidation calculations for synthetic tokens involve oracle price lookups, collateral ratio computations, and multi-party settlement; AOT compilation provides deterministic gas costs essential for liquidation bots.
- **EIP-1559 Fee Market**: Predictable gas costs for oracle updates and liquidation transactions, critical for time-sensitive operations where fee spikes could delay liquidations and increase bad debt.
- **BridgeETH Integration**: Cross-chain commodity token trading -- holders can bridge commodity tokens to Ethereum for DeFi composability, or bridge ETH to Basalt for commodity exposure.
- **Governance Integration**: Reserve management parameters (custody fees, collateralization ratios, oracle freshness thresholds) are governed through the on-chain governance system.

## Token Standards Used

- **BST-20 (Fungible Token)**: Primary standard. 1 token = 1 unit of the underlying commodity (e.g., 1 troy ounce, 1 barrel).
- **BST-VC (Verifiable Credentials)**: Auditor proof-of-reserves attestations, custodian certifications, and oracle operator credentials.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines credential schemas for "ProofOfReserves" (auditor attestation), "CustodianCertification" (custodian authority), and "OracleOperator" (price feed authorization).
- **IssuerRegistry (0x...1007)**: Verifies that custodians, auditors, and oracle operators are registered and authorized.
- **Governance (0x...1002)**: Governs parameter changes (custody fee, collateralization ratio, oracle freshness), custodian/auditor registration, and emergency pause decisions.
- **Escrow (0x...1003)**: Holds collateral for synthetic positions and facilitates atomic redemption settlements.
- **BridgeETH (0x...1008)**: Cross-chain commodity token transfers between Basalt and Ethereum.
- **BNS (0x...1001)**: Commodity token contracts registered with human-readable names (e.g., "gold.commodity.basalt").

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Commodity-Backed Token (BST-20 fungible).
/// Represents ownership of physical commodities with proof of reserves.
/// Type ID: 0x010C
/// </summary>
[BasaltContract]
public partial class CommodityToken
{
    // --- BST-20 state ---
    private readonly string _name;
    private readonly string _symbol;
    private readonly byte _decimals;
    private readonly StorageValue<UInt256> _totalSupply;
    private readonly StorageMap<string, UInt256> _balances;
    private readonly StorageMap<string, UInt256> _allowances;

    // --- Commodity-specific state ---
    private readonly StorageValue<string> _commodityType;              // "gold"/"silver"/"oil"/"wheat"/etc.
    private readonly StorageValue<string> _unitDescription;            // "troy ounce"/"barrel"/"bushel"

    // --- Oracle state ---
    private readonly StorageMap<string, string> _authorizedOracles;    // oracle address hex -> "1"
    private readonly StorageValue<UInt256> _latestPrice;               // price per unit in BST base units
    private readonly StorageValue<ulong> _priceTimestampBlock;         // block of last price update
    private readonly StorageValue<ulong> _priceFreshnessBlocks;        // max blocks before price is stale

    // --- Reserves state ---
    private readonly StorageValue<UInt256> _verifiedReserves;          // last attested reserve amount
    private readonly StorageValue<ulong> _lastAuditBlock;              // block of last audit attestation
    private readonly StorageValue<string> _lastAuditorHex;             // auditor address of last attestation
    private readonly StorageValue<ulong> _nextAttestationId;
    private readonly StorageMap<string, UInt256> _attestationReserves; // attestationId -> reserve amount
    private readonly StorageMap<string, ulong> _attestationBlocks;    // attestationId -> block number
    private readonly StorageMap<string, string> _attestationAuditors; // attestationId -> auditor hex

    // --- Custodian state ---
    private readonly StorageMap<string, string> _authorizedCustodians; // custodian address hex -> "1"

    // --- Synthetic mode state ---
    private readonly StorageValue<string> _isSyntheticMode;            // "1" if synthetic
    private readonly StorageValue<ulong> _collateralRatioBps;          // e.g., 15000 = 150%
    private readonly StorageValue<ulong> _maintenanceRatioBps;         // e.g., 12000 = 120%
    private readonly StorageMap<string, UInt256> _collateralDeposits;   // user hex -> BST locked
    private readonly StorageMap<string, UInt256> _syntheticMinted;      // user hex -> synthetic tokens minted

    // --- Redemption state ---
    private readonly StorageValue<ulong> _nextRedemptionId;
    private readonly StorageMap<string, string> _redemptionRequesters;  // redemptionId -> requester hex
    private readonly StorageMap<string, UInt256> _redemptionAmounts;    // redemptionId -> amount
    private readonly StorageMap<string, string> _redemptionStatus;     // redemptionId -> "pending"/"fulfilled"/"cancelled"

    // --- Configuration ---
    private readonly StorageValue<ulong> _custodyFeeBps;               // annual custody fee in bps
    private readonly StorageValue<UInt256> _minRedemptionAmount;        // minimum redemption quantity
    private readonly StorageValue<string> _paused;                     // "1" if paused

    // --- System contract addresses ---
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _schemaRegistryAddress;
    private readonly byte[] _governanceAddress;

    public CommodityToken(
        string name,
        string symbol,
        string commodityType,
        string unitDescription,
        byte decimals = 18,
        ulong priceFreshnessBlocks = 1000,
        ulong custodyFeeBps = 25,
        ulong collateralRatioBps = 15000,
        ulong maintenanceRatioBps = 12000)
    {
        _name = name;
        _symbol = symbol;
        _decimals = decimals;
        _totalSupply = new StorageValue<UInt256>("ct_supply");
        _balances = new StorageMap<string, UInt256>("ct_bal");
        _allowances = new StorageMap<string, UInt256>("ct_allow");
        _commodityType = new StorageValue<string>("ct_type");
        _unitDescription = new StorageValue<string>("ct_unit");
        _authorizedOracles = new StorageMap<string, string>("ct_oracle");
        _latestPrice = new StorageValue<UInt256>("ct_price");
        _priceTimestampBlock = new StorageValue<ulong>("ct_pblock");
        _priceFreshnessBlocks = new StorageValue<ulong>("ct_pfresh");
        _verifiedReserves = new StorageValue<UInt256>("ct_reserves");
        _lastAuditBlock = new StorageValue<ulong>("ct_ablock");
        _lastAuditorHex = new StorageValue<string>("ct_aaddr");
        _nextAttestationId = new StorageValue<ulong>("ct_anext");
        _attestationReserves = new StorageMap<string, UInt256>("ct_ares");
        _attestationBlocks = new StorageMap<string, ulong>("ct_ablk");
        _attestationAuditors = new StorageMap<string, string>("ct_aaud");
        _authorizedCustodians = new StorageMap<string, string>("ct_cust");
        _isSyntheticMode = new StorageValue<string>("ct_synth");
        _collateralRatioBps = new StorageValue<ulong>("ct_cratio");
        _maintenanceRatioBps = new StorageValue<ulong>("ct_mratio");
        _collateralDeposits = new StorageMap<string, UInt256>("ct_cdep");
        _syntheticMinted = new StorageMap<string, UInt256>("ct_smint");
        _nextRedemptionId = new StorageValue<ulong>("ct_rnext");
        _redemptionRequesters = new StorageMap<string, string>("ct_rreq");
        _redemptionAmounts = new StorageMap<string, UInt256>("ct_ramt");
        _redemptionStatus = new StorageMap<string, string>("ct_rstat");
        _custodyFeeBps = new StorageValue<ulong>("ct_cfee");
        _minRedemptionAmount = new StorageValue<UInt256>("ct_minred");
        _paused = new StorageValue<string>("ct_paused");

        _commodityType.Set(commodityType);
        _unitDescription.Set(unitDescription);
        _priceFreshnessBlocks.Set(priceFreshnessBlocks);
        _custodyFeeBps.Set(custodyFeeBps);
        _collateralRatioBps.Set(collateralRatioBps);
        _maintenanceRatioBps.Set(maintenanceRatioBps);

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        _governanceAddress = new byte[20];
        _governanceAddress[18] = 0x10;
        _governanceAddress[19] = 0x02;
    }

    // ================================================================
    // BST-20 Standard Methods
    // ================================================================

    [BasaltView]
    public string Name() => _name;

    [BasaltView]
    public string Symbol() => _symbol;

    [BasaltView]
    public byte Decimals() => _decimals;

    [BasaltView]
    public UInt256 TotalSupply() => _totalSupply.Get();

    [BasaltView]
    public UInt256 BalanceOf(byte[] account)
        => _balances.Get(Convert.ToHexString(account));

    [BasaltEntrypoint]
    public void Transfer(byte[] to, UInt256 amount)
    {
        RequireNotPaused();
        var fromHex = Convert.ToHexString(Context.Caller);
        var toHex = Convert.ToHexString(to);
        var balance = _balances.Get(fromHex);
        Context.Require(balance >= amount, "CT: insufficient balance");

        _balances.Set(fromHex, balance - amount);
        _balances.Set(toHex, _balances.Get(toHex) + amount);
    }

    [BasaltEntrypoint]
    public void Approve(byte[] spender, UInt256 amount)
    {
        var key = Convert.ToHexString(Context.Caller) + ":" + Convert.ToHexString(spender);
        _allowances.Set(key, amount);
    }

    [BasaltEntrypoint]
    public void TransferFrom(byte[] from, byte[] to, UInt256 amount)
    {
        RequireNotPaused();
        var fromHex = Convert.ToHexString(from);
        var toHex = Convert.ToHexString(to);
        var callerHex = Convert.ToHexString(Context.Caller);
        var allowKey = fromHex + ":" + callerHex;

        var allowance = _allowances.Get(allowKey);
        Context.Require(allowance >= amount, "CT: insufficient allowance");
        _allowances.Set(allowKey, allowance - amount);

        var balance = _balances.Get(fromHex);
        Context.Require(balance >= amount, "CT: insufficient balance");

        _balances.Set(fromHex, balance - amount);
        _balances.Set(toHex, _balances.Get(toHex) + amount);
    }

    // ================================================================
    // Oracle Price Feed
    // ================================================================

    /// <summary>
    /// Submit a price update. Only authorized oracle operators.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdatePrice(UInt256 pricePerUnit)
    {
        Context.Require(
            _authorizedOracles.Get(Convert.ToHexString(Context.Caller)) == "1",
            "CT: not authorized oracle");
        Context.Require(!pricePerUnit.IsZero, "CT: price must be > 0");

        _latestPrice.Set(pricePerUnit);
        _priceTimestampBlock.Set(Context.BlockHeight);

        Context.Emit(new PriceUpdatedEvent
        {
            Oracle = Context.Caller,
            Price = pricePerUnit,
            Block = Context.BlockHeight,
        });
    }

    /// <summary>
    /// Register an authorized oracle operator. Governance-only.
    /// </summary>
    [BasaltEntrypoint]
    public void AuthorizeOracle(byte[] oracle)
    {
        RequireGovernance();
        _authorizedOracles.Set(Convert.ToHexString(oracle), "1");
    }

    // ================================================================
    // Proof of Reserves
    // ================================================================

    /// <summary>
    /// Submit a proof-of-reserves attestation. Only registered auditors.
    /// </summary>
    [BasaltEntrypoint]
    public ulong SubmitReservesAttestation(UInt256 verifiedReserveAmount)
    {
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "CT: caller not a registered auditor");

        _verifiedReserves.Set(verifiedReserveAmount);
        _lastAuditBlock.Set(Context.BlockHeight);
        _lastAuditorHex.Set(Convert.ToHexString(Context.Caller));

        var attestationId = _nextAttestationId.Get();
        _nextAttestationId.Set(attestationId + 1);

        var key = attestationId.ToString();
        _attestationReserves.Set(key, verifiedReserveAmount);
        _attestationBlocks.Set(key, Context.BlockHeight);
        _attestationAuditors.Set(key, Convert.ToHexString(Context.Caller));

        Context.Emit(new ReservesAttestedEvent
        {
            AttestationId = attestationId,
            Auditor = Context.Caller,
            ReserveAmount = verifiedReserveAmount,
            TotalSupply = _totalSupply.Get(),
        });

        return attestationId;
    }

    // ================================================================
    // Custodian Minting (Backed Mode)
    // ================================================================

    /// <summary>
    /// Mint tokens when new physical reserves are deposited. Only authorized custodians.
    /// </summary>
    [BasaltEntrypoint]
    public void MintBacked(byte[] recipient, UInt256 amount)
    {
        RequireNotPaused();
        Context.Require(
            _authorizedCustodians.Get(Convert.ToHexString(Context.Caller)) == "1",
            "CT: not authorized custodian");

        var recipientHex = Convert.ToHexString(recipient);
        _balances.Set(recipientHex, _balances.Get(recipientHex) + amount);
        _totalSupply.Set(_totalSupply.Get() + amount);

        Context.Emit(new CommodityMintedEvent
        {
            Recipient = recipient,
            Amount = amount,
            Mode = "backed",
        });
    }

    /// <summary>
    /// Register an authorized custodian. Governance-only.
    /// </summary>
    [BasaltEntrypoint]
    public void AuthorizeCustodian(byte[] custodian)
    {
        RequireGovernance();
        _authorizedCustodians.Set(Convert.ToHexString(custodian), "1");
    }

    // ================================================================
    // Synthetic Minting (Collateralized Mode)
    // ================================================================

    /// <summary>
    /// Mint synthetic commodity tokens by depositing BST as collateral.
    /// Requires a fresh oracle price. Collateral must meet the collateralization ratio.
    /// </summary>
    [BasaltEntrypoint]
    public void MintSynthetic(UInt256 tokenAmount)
    {
        RequireNotPaused();
        Context.Require(_isSyntheticMode.Get() == "1", "CT: not in synthetic mode");
        RequireFreshPrice();

        var price = _latestPrice.Get();
        var collateralRequired = tokenAmount * price * new UInt256(_collateralRatioBps.Get())
            / new UInt256(10000);
        Context.Require(Context.TxValue >= collateralRequired, "CT: insufficient collateral");

        var callerHex = Convert.ToHexString(Context.Caller);
        _collateralDeposits.Set(callerHex, _collateralDeposits.Get(callerHex) + Context.TxValue);
        _syntheticMinted.Set(callerHex, _syntheticMinted.Get(callerHex) + tokenAmount);

        _balances.Set(callerHex, _balances.Get(callerHex) + tokenAmount);
        _totalSupply.Set(_totalSupply.Get() + tokenAmount);

        Context.Emit(new CommodityMintedEvent
        {
            Recipient = Context.Caller,
            Amount = tokenAmount,
            Mode = "synthetic",
        });
    }

    /// <summary>
    /// Liquidate an undercollateralized synthetic position.
    /// Anyone can call. Liquidator receives a discount on the collateral.
    /// </summary>
    [BasaltEntrypoint]
    public void Liquidate(byte[] positionOwner)
    {
        RequireFreshPrice();
        var ownerHex = Convert.ToHexString(positionOwner);
        var collateral = _collateralDeposits.Get(ownerHex);
        var minted = _syntheticMinted.Get(ownerHex);
        Context.Require(!minted.IsZero, "CT: no position");

        var price = _latestPrice.Get();
        var debtValue = minted * price;
        var maintenanceRatio = _maintenanceRatioBps.Get();
        var requiredCollateral = debtValue * new UInt256(maintenanceRatio) / new UInt256(10000);

        Context.Require(collateral < requiredCollateral, "CT: position not undercollateralized");

        // Liquidator burns the debt and receives collateral at a 5% discount
        var liquidatorHex = Convert.ToHexString(Context.Caller);
        var liquidatorBalance = _balances.Get(liquidatorHex);
        Context.Require(liquidatorBalance >= minted, "CT: liquidator has insufficient tokens");

        _balances.Set(liquidatorHex, liquidatorBalance - minted);
        _totalSupply.Set(_totalSupply.Get() - minted);

        // Transfer collateral to liquidator (minus 5% penalty to protocol)
        var penalty = collateral * new UInt256(500) / new UInt256(10000);
        var liquidatorReward = collateral - penalty;

        _collateralDeposits.Set(ownerHex, UInt256.Zero);
        _syntheticMinted.Set(ownerHex, UInt256.Zero);

        Context.TransferNative(Context.Caller, liquidatorReward);

        Context.Emit(new PositionLiquidatedEvent
        {
            Owner = positionOwner,
            Liquidator = Context.Caller,
            DebtBurned = minted,
            CollateralSeized = liquidatorReward,
        });
    }

    // ================================================================
    // Physical Redemption
    // ================================================================

    /// <summary>
    /// Request physical redemption of commodity tokens.
    /// Burns tokens and creates a redemption request for the custodian.
    /// </summary>
    [BasaltEntrypoint]
    public ulong RequestRedemption(UInt256 amount)
    {
        Context.Require(amount >= _minRedemptionAmount.Get(), "CT: below minimum redemption");
        var callerHex = Convert.ToHexString(Context.Caller);
        var balance = _balances.Get(callerHex);
        Context.Require(balance >= amount, "CT: insufficient balance");

        // Burn tokens
        _balances.Set(callerHex, balance - amount);
        _totalSupply.Set(_totalSupply.Get() - amount);

        var redemptionId = _nextRedemptionId.Get();
        _nextRedemptionId.Set(redemptionId + 1);

        var key = redemptionId.ToString();
        _redemptionRequesters.Set(key, callerHex);
        _redemptionAmounts.Set(key, amount);
        _redemptionStatus.Set(key, "pending");

        Context.Emit(new RedemptionRequestedEvent
        {
            RedemptionId = redemptionId,
            Requester = Context.Caller,
            Amount = amount,
        });

        return redemptionId;
    }

    /// <summary>
    /// Custodian fulfills a physical redemption request.
    /// </summary>
    [BasaltEntrypoint]
    public void FulfillRedemption(ulong redemptionId)
    {
        Context.Require(
            _authorizedCustodians.Get(Convert.ToHexString(Context.Caller)) == "1",
            "CT: not authorized custodian");

        var key = redemptionId.ToString();
        Context.Require(_redemptionStatus.Get(key) == "pending", "CT: not pending");
        _redemptionStatus.Set(key, "fulfilled");

        Context.Emit(new RedemptionFulfilledEvent
        {
            RedemptionId = redemptionId,
            Custodian = Context.Caller,
        });
    }

    // ================================================================
    // Admin / Governance
    // ================================================================

    [BasaltEntrypoint]
    public void Pause()
    {
        RequireGovernance();
        _paused.Set("1");
    }

    [BasaltEntrypoint]
    public void Unpause()
    {
        RequireGovernance();
        _paused.Set("");
    }

    [BasaltEntrypoint]
    public void EnableSyntheticMode()
    {
        RequireGovernance();
        _isSyntheticMode.Set("1");
    }

    // ================================================================
    // Views
    // ================================================================

    [BasaltView]
    public string CommodityType() => _commodityType.Get() ?? "";

    [BasaltView]
    public UInt256 LatestPrice() => _latestPrice.Get();

    [BasaltView]
    public ulong PriceBlock() => _priceTimestampBlock.Get();

    [BasaltView]
    public UInt256 VerifiedReserves() => _verifiedReserves.Get();

    [BasaltView]
    public ulong LastAuditBlock() => _lastAuditBlock.Get();

    [BasaltView]
    public UInt256 ReserveRatioBps()
    {
        var supply = _totalSupply.Get();
        if (supply.IsZero) return new UInt256(10000);
        return _verifiedReserves.Get() * new UInt256(10000) / supply;
    }

    [BasaltView]
    public UInt256 GetCollateral(byte[] owner)
        => _collateralDeposits.Get(Convert.ToHexString(owner));

    [BasaltView]
    public UInt256 GetSyntheticDebt(byte[] owner)
        => _syntheticMinted.Get(Convert.ToHexString(owner));

    [BasaltView]
    public string GetRedemptionStatus(ulong redemptionId)
        => _redemptionStatus.Get(redemptionId.ToString()) ?? "unknown";

    [BasaltView]
    public bool IsPaused() => _paused.Get() == "1";

    // ================================================================
    // Internal helpers
    // ================================================================

    private void RequireNotPaused()
    {
        Context.Require(_paused.Get() != "1", "CT: contract paused");
    }

    private void RequireGovernance()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(_governanceAddress),
            "CT: only governance");
    }

    private void RequireFreshPrice()
    {
        var lastUpdate = _priceTimestampBlock.Get();
        var freshness = _priceFreshnessBlocks.Get();
        Context.Require(
            Context.BlockHeight <= lastUpdate + freshness,
            "CT: price is stale");
    }
}

// ================================================================
// Events
// ================================================================

[BasaltEvent]
public sealed class PriceUpdatedEvent
{
    [Indexed] public byte[] Oracle { get; init; } = [];
    public UInt256 Price { get; init; }
    public ulong Block { get; init; }
}

[BasaltEvent]
public sealed class ReservesAttestedEvent
{
    [Indexed] public ulong AttestationId { get; init; }
    [Indexed] public byte[] Auditor { get; init; } = [];
    public UInt256 ReserveAmount { get; init; }
    public UInt256 TotalSupply { get; init; }
}

[BasaltEvent]
public sealed class CommodityMintedEvent
{
    [Indexed] public byte[] Recipient { get; init; } = [];
    public UInt256 Amount { get; init; }
    public string Mode { get; init; } = "";
}

[BasaltEvent]
public sealed class PositionLiquidatedEvent
{
    [Indexed] public byte[] Owner { get; init; } = [];
    [Indexed] public byte[] Liquidator { get; init; } = [];
    public UInt256 DebtBurned { get; init; }
    public UInt256 CollateralSeized { get; init; }
}

[BasaltEvent]
public sealed class RedemptionRequestedEvent
{
    [Indexed] public ulong RedemptionId { get; init; }
    [Indexed] public byte[] Requester { get; init; } = [];
    public UInt256 Amount { get; init; }
}

[BasaltEvent]
public sealed class RedemptionFulfilledEvent
{
    [Indexed] public ulong RedemptionId { get; init; }
    public byte[] Custodian { get; init; } = [];
}
```

## Complexity

**High** -- This contract implements a full BST-20 token standard plus three distinct operational modes (backed minting, synthetic minting with collateralization/liquidation, and physical redemption). Oracle price feed management with freshness checks adds temporal complexity. The proof-of-reserves attestation system requires cross-contract calls to IssuerRegistry. The liquidation mechanism involves collateral ratio calculations with oracle dependency. Emergency pause and governance integration add administrative complexity.

## Priority

**P2** -- Commodity-backed tokens are a well-understood use case with existing market demand (PAXG, XAUT), but the differentiation on Basalt (BST-VC proof of reserves, ZK compliance) is incremental rather than transformative. The synthetic mode adds DeFi composability but competes with established platforms (Synthetix, Mirror). Higher priority than niche use cases due to the broad market appeal of gold/commodity tokenization, but lower than the RWA instruments that uniquely leverage BST-3525.
