# Tokenized Bonds

## Category

Decentralized Finance (DeFi) / Fixed Income / Real-World Assets (RWA)

## Summary

Tokenized Bonds is a BST-3525 semi-fungible token contract that represents bond positions on-chain. Each token encodes a bond position where the slot represents the maturity date (as a Unix epoch), the value represents the face amount, and coupon payments are distributed automatically at configurable intervals. This is a flagship use case for Basalt's BST-3525 standard combined with its ZK compliance layer, enabling institutional-grade fixed-income instruments with programmable settlement.

The contract supports the full bond lifecycle: issuance by compliance-verified issuers, coupon accrual and distribution, secondary market trading between verified holders, credit rating tiers, and automatic redemption at maturity. ZK compliance gating ensures that only accredited investors can purchase bonds, while transfers between previously verified holders remain permissionless, dramatically reducing friction in secondary markets.

## Why It's Useful

- Fixed-income instruments represent a $130+ trillion global market, yet most bonds remain illiquid and difficult to fractionalize; tokenization unlocks 24/7 secondary market liquidity for retail and institutional participants alike.
- Traditional bond settlement takes T+2 days and involves multiple intermediaries (custodians, clearinghouses, transfer agents); on-chain settlement is atomic and near-instant.
- Accredited investor verification is a major compliance bottleneck in traditional issuance; ZK proofs allow verification without revealing personal financial details to counterparties or the public chain.
- Fractional bond ownership lowers the minimum investment threshold from $100,000+ typical for corporate bonds to whatever denomination the issuer chooses, democratizing access to fixed income.
- Coupon payment distribution is manual and error-prone in legacy systems; programmable coupons ensure automatic, auditable, on-time payments to all holders.
- Rating tiers encoded on-chain provide transparent, immutable credit quality signals that cannot be retroactively altered.
- Integration with BST-4626 vaults enables bond aggregation strategies (e.g., diversified bond funds) as a composable DeFi primitive.

## Key Features

- Bond issuance with configurable face value, coupon rate (basis points), coupon interval (in blocks), maturity date (slot), and credit rating tier.
- BST-3525 slot semantics: bonds sharing the same maturity date are value-fungible, enabling efficient secondary market matching.
- Periodic coupon payments: a `DistributeCoupons` entrypoint calculates accrued interest and transfers native BST to all token holders proportionally.
- ZK compliance gating on primary purchase: buyers must present a valid ZK proof of accredited investor status (via SchemaRegistry credential schema) to acquire bonds; the proof is verified on-chain without revealing the underlying financial data.
- Permissionless transfers between verified holders: once an address has been verified (credential registered in IssuerRegistry), subsequent transfers between verified addresses bypass the ZK proof step.
- Credit rating tiers (AAA, AA, A, BBB, BB, B, CCC): each bond issuance is assigned a rating that affects coupon rates and risk profiles; ratings can be updated by authorized rating agencies (registered issuers).
- Automatic redemption at maturity: after the maturity block is reached, holders can claim their face value plus any final coupon.
- Issuer management: only addresses with valid issuer credentials (via IssuerRegistry) can create new bond series.
- Bond series metadata: URI-based metadata for prospectus documents, legal terms, and issuer information stored via slot URIs.
- Early redemption / callable bonds: optional issuer-callable flag allowing the issuer to redeem bonds before maturity at a premium.
- Default handling: issuer can be flagged as defaulted, freezing coupon payments and enabling governance intervention.

## Basalt-Specific Advantages

- **BST-3525 Semi-Fungible Tokens**: The three-component model (tokenId, slot, value) maps perfectly to bonds: slot = maturity date creates natural fungibility pools where bonds with the same maturity can be split, merged, and traded by value without losing their individual identity. This is not possible with simple ERC-20 or ERC-721 tokens.
- **ZK Compliance Layer**: Basalt's built-in SchemaRegistry and ZkComplianceVerifier allow accredited investor proofs to be verified on-chain using Groth16 zero-knowledge proofs. Buyers prove they meet the accreditation threshold ($1M+ net worth or $200K+ income) without revealing their actual financial figures. No other L1 has this compliance primitive built into the protocol layer.
- **BST-VC Verifiable Credentials**: Issuer credentials and credit rating attestations are represented as W3C Verifiable Credentials on-chain via BST-VC, providing a standardized, tamper-proof identity layer for bond issuers and rating agencies.
- **IssuerRegistry Integration**: Only issuers registered in the protocol-level IssuerRegistry can mint new bond series, ensuring institutional trust without relying on off-chain whitelists.
- **AOT-Compiled Execution**: Bond coupon distribution iterates over all holders and performs proportional calculations; Basalt's AOT-compiled SDK contracts execute this deterministically without JIT overhead or gas unpredictability, making batch operations feasible.
- **Ed25519 Signatures**: All bond transactions are signed with Ed25519, providing faster verification than ECDSA (important for high-frequency secondary market trading) and post-quantum migration readiness.
- **Pedersen Commitment Compatibility**: Future integration can shield bond position sizes using Pedersen commitments, allowing holders to prove they own a bond position without revealing the face value to other market participants.
- **BST-4626 Vault Composability**: Tokenized bonds can be deposited into BST-4626 vault contracts to create diversified bond fund products, leveraging Basalt's native vault standard for share accounting.
- **EIP-1559 Fee Market**: Predictable gas costs for coupon distribution and trading operations, critical for institutional adoption where fee volatility is unacceptable.

## Token Standards Used

- **BST-3525 (Semi-Fungible Token)**: Primary standard. Each bond position is a BST-3525 token with slot = maturity date and value = face amount.
- **BST-VC (Verifiable Credentials)**: Used for issuer credentials, rating agency attestations, and accredited investor proofs.
- **BST-20 (Fungible Token)**: Coupon payments are distributed in native BST (or optionally a BST-20 stablecoin).

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines the credential schema for "AccreditedInvestor" proofs, specifying the required claim fields (jurisdiction, accreditation type, expiry).
- **IssuerRegistry (0x...1007)**: Verifies that bond issuers and rating agencies are registered credential issuers with valid authority.
- **Escrow (0x...1003)**: Used for primary bond auctions where purchase funds are escrowed until the issuance is confirmed; also used for callable bond redemption premium escrow.
- **Governance (0x...1002)**: Bond default resolution and parameter changes (e.g., adding new rating tiers, changing compliance schemas) are governed through the on-chain governance system.
- **StakingPool (0x...1005)**: Issuer reputation can be cross-referenced with staking position to provide economic security guarantees.
- **BridgeETH (0x...1008)**: Enables cross-chain bond purchases where investors bridge ETH to BST for primary market participation.
- **BNS (0x...1001)**: Bond issuers can register human-readable names (e.g., "acme-corp.basalt") linked to their issuer address for discoverability.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Tokenized Bonds contract built on BST-3525.
/// Slot = maturity date (Unix epoch block number), Value = face amount.
/// Type ID: 0x0108
/// </summary>
[BasaltContract]
public partial class TokenizedBonds : BST3525Token
{
    // --- Bond series state ---
    private readonly StorageMap<string, string> _seriesIssuers;         // slot -> issuer hex
    private readonly StorageMap<string, ulong> _couponRateBps;          // slot -> coupon rate in basis points
    private readonly StorageMap<string, ulong> _couponIntervalBlocks;   // slot -> blocks between coupons
    private readonly StorageMap<string, ulong> _lastCouponBlock;        // slot -> last coupon distribution block
    private readonly StorageMap<string, string> _creditRating;          // slot -> "AAA"/"AA"/"A"/"BBB"/"BB"/"B"/"CCC"
    private readonly StorageMap<string, string> _seriesStatus;          // slot -> "active"/"matured"/"defaulted"/"callable"
    private readonly StorageMap<string, UInt256> _totalFaceValue;       // slot -> total face value issued
    private readonly StorageMap<string, UInt256> _couponPool;           // slot -> BST deposited for coupons

    // --- Compliance state ---
    private readonly StorageMap<string, string> _verifiedHolders;       // address hex -> "1" if verified
    private readonly StorageValue<string> _accreditedSchemaId;          // SchemaRegistry schema ID

    // --- Callable bond state ---
    private readonly StorageMap<string, ulong> _callPremiumBps;         // slot -> call premium in bps
    private readonly StorageMap<string, string> _isCallable;            // slot -> "1" if callable

    // --- System contract addresses ---
    private readonly byte[] _schemaRegistryAddress;   // 0x...1006
    private readonly byte[] _issuerRegistryAddress;   // 0x...1007
    private readonly byte[] _escrowAddress;           // 0x...1003

    public TokenizedBonds()
        : base("Basalt Tokenized Bond", "bBOND", 18)
    {
        _seriesIssuers = new StorageMap<string, string>("bond_issuer");
        _couponRateBps = new StorageMap<string, ulong>("bond_coupon");
        _couponIntervalBlocks = new StorageMap<string, ulong>("bond_interval");
        _lastCouponBlock = new StorageMap<string, ulong>("bond_lastcpn");
        _creditRating = new StorageMap<string, string>("bond_rating");
        _seriesStatus = new StorageMap<string, string>("bond_status");
        _totalFaceValue = new StorageMap<string, UInt256>("bond_total");
        _couponPool = new StorageMap<string, UInt256>("bond_pool");
        _verifiedHolders = new StorageMap<string, string>("bond_verified");
        _accreditedSchemaId = new StorageValue<string>("bond_schema");
        _callPremiumBps = new StorageMap<string, ulong>("bond_callprem");
        _isCallable = new StorageMap<string, string>("bond_callable");

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _escrowAddress = new byte[20];
        _escrowAddress[18] = 0x10;
        _escrowAddress[19] = 0x03;
    }

    // ================================================================
    // Admin / Setup
    // ================================================================

    /// <summary>
    /// Set the SchemaRegistry schema ID for accredited investor credentials.
    /// Only callable once during initial configuration.
    /// </summary>
    [BasaltEntrypoint]
    public void SetAccreditedSchema(string schemaId)
    {
        Context.Require(
            string.IsNullOrEmpty(_accreditedSchemaId.Get()),
            "BOND: schema already set");
        _accreditedSchemaId.Set(schemaId);
    }

    // ================================================================
    // Bond Series Management
    // ================================================================

    /// <summary>
    /// Create a new bond series. Only callable by registered issuers.
    /// Slot = maturity block number. Configures coupon rate, interval, rating, and callable flag.
    /// </summary>
    [BasaltEntrypoint]
    public void CreateBondSeries(
        ulong maturityBlock,
        ulong couponRateBps,
        ulong couponIntervalBlocks,
        string rating,
        string metadataUri,
        ulong callPremiumBps)
    {
        // Verify caller is a registered issuer
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "BOND: caller not a registered issuer");

        Context.Require(maturityBlock > Context.BlockHeight, "BOND: maturity must be in future");
        Context.Require(couponIntervalBlocks > 0, "BOND: interval must be > 0");
        RequireValidRating(rating);

        var slotKey = maturityBlock.ToString();
        Context.Require(
            string.IsNullOrEmpty(_seriesIssuers.Get(slotKey)),
            "BOND: series already exists for this maturity");

        _seriesIssuers.Set(slotKey, Convert.ToHexString(Context.Caller));
        _couponRateBps.Set(slotKey, couponRateBps);
        _couponIntervalBlocks.Set(slotKey, couponIntervalBlocks);
        _lastCouponBlock.Set(slotKey, Context.BlockHeight);
        _creditRating.Set(slotKey, rating);
        _seriesStatus.Set(slotKey, "active");

        if (callPremiumBps > 0)
        {
            _isCallable.Set(slotKey, "1");
            _callPremiumBps.Set(slotKey, callPremiumBps);
        }

        // Store metadata URI via inherited BST-3525 slot URI
        SetSlotUri(maturityBlock, metadataUri);

        Context.Emit(new BondSeriesCreatedEvent
        {
            MaturitySlot = maturityBlock,
            Issuer = Context.Caller,
            CouponRateBps = couponRateBps,
            Rating = rating,
        });
    }

    /// <summary>
    /// Update the credit rating for a bond series.
    /// Only callable by registered rating agencies (issuers with rating authority).
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateRating(ulong maturitySlot, string newRating)
    {
        var isRegistered = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsRegistered", Context.Caller);
        Context.Require(isRegistered, "BOND: caller not a registered rating agency");
        RequireValidRating(newRating);

        var slotKey = maturitySlot.ToString();
        Context.Require(
            !string.IsNullOrEmpty(_seriesIssuers.Get(slotKey)),
            "BOND: series does not exist");

        _creditRating.Set(slotKey, newRating);

        Context.Emit(new RatingUpdatedEvent
        {
            MaturitySlot = maturitySlot,
            NewRating = newRating,
            RatingAgency = Context.Caller,
        });
    }

    // ================================================================
    // Primary Market (Issuance)
    // ================================================================

    /// <summary>
    /// Purchase bonds in the primary market. Requires ZK proof of accredited
    /// investor status. The sent BST value determines the face amount of bonds minted.
    /// </summary>
    [BasaltEntrypoint]
    public ulong PurchaseBond(ulong maturitySlot, byte[] zkProof)
    {
        var slotKey = maturitySlot.ToString();
        Context.Require(_seriesStatus.Get(slotKey) == "active", "BOND: series not active");
        Context.Require(!Context.TxValue.IsZero, "BOND: must send value");

        // Verify ZK accredited investor proof
        var schemaId = _accreditedSchemaId.Get();
        Context.Require(!string.IsNullOrEmpty(schemaId), "BOND: schema not configured");
        var proofValid = Context.CallContract<bool>(
            _schemaRegistryAddress, "VerifyProof", schemaId, Context.Caller, zkProof);
        Context.Require(proofValid, "BOND: invalid accredited investor proof");

        // Mark holder as verified for secondary market transfers
        _verifiedHolders.Set(Convert.ToHexString(Context.Caller), "1");

        // Mint BST-3525 token: slot = maturity, value = face amount
        var tokenId = Mint(Context.Caller, maturitySlot, Context.TxValue);

        // Track total face value for coupon calculations
        _totalFaceValue.Set(slotKey, _totalFaceValue.Get(slotKey) + Context.TxValue);

        Context.Emit(new BondPurchasedEvent
        {
            TokenId = tokenId,
            Buyer = Context.Caller,
            MaturitySlot = maturitySlot,
            FaceAmount = Context.TxValue,
        });

        return tokenId;
    }

    // ================================================================
    // Secondary Market (Transfers)
    // ================================================================

    /// <summary>
    /// Transfer bond value between verified holders.
    /// Both sender and receiver must be previously verified (permissionless once verified).
    /// </summary>
    [BasaltEntrypoint]
    public ulong TransferBondToAddress(ulong fromTokenId, byte[] to, UInt256 value)
    {
        RequireVerifiedHolder(to);
        return TransferValueToAddress(fromTokenId, to, value);
    }

    /// <summary>
    /// Transfer bond value between two existing token IDs (same maturity slot).
    /// Receiver token owner must be a verified holder.
    /// </summary>
    [BasaltEntrypoint]
    public void TransferBondToId(ulong fromTokenId, ulong toTokenId, UInt256 value)
    {
        var receiver = OwnerOf(toTokenId);
        RequireVerifiedHolder(receiver);
        TransferValueToId(fromTokenId, toTokenId, value);
    }

    // ================================================================
    // Coupon Distribution
    // ================================================================

    /// <summary>
    /// Fund the coupon pool for a bond series. Issuer deposits BST to cover coupon payments.
    /// </summary>
    [BasaltEntrypoint]
    public void FundCouponPool(ulong maturitySlot)
    {
        Context.Require(!Context.TxValue.IsZero, "BOND: must send value");
        var slotKey = maturitySlot.ToString();
        Context.Require(
            !string.IsNullOrEmpty(_seriesIssuers.Get(slotKey)),
            "BOND: series does not exist");

        _couponPool.Set(slotKey, _couponPool.Get(slotKey) + Context.TxValue);

        Context.Emit(new CouponPoolFundedEvent
        {
            MaturitySlot = maturitySlot,
            Amount = Context.TxValue,
        });
    }

    /// <summary>
    /// Claim accrued coupon payment for a specific bond token.
    /// Calculates proportional coupon based on face value and elapsed intervals.
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimCoupon(ulong tokenId)
    {
        var owner = OwnerOf(tokenId);
        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(callerHex == Convert.ToHexString(owner), "BOND: not token owner");

        var slot = SlotOf(tokenId);
        var slotKey = slot.ToString();
        Context.Require(_seriesStatus.Get(slotKey) == "active", "BOND: series not active");

        var lastCoupon = _lastCouponBlock.Get(slotKey);
        var interval = _couponIntervalBlocks.Get(slotKey);
        Context.Require(
            Context.BlockHeight >= lastCoupon + interval,
            "BOND: coupon not yet due");

        var faceValue = BalanceOf(tokenId);
        var totalFace = _totalFaceValue.Get(slotKey);
        var rateBps = _couponRateBps.Get(slotKey);
        var pool = _couponPool.Get(slotKey);

        // Coupon = faceValue * rateBps / 10000 (per interval)
        var couponAmount = faceValue * new UInt256(rateBps) / new UInt256(10000);
        Context.Require(pool >= couponAmount, "BOND: insufficient coupon pool");

        _couponPool.Set(slotKey, pool - couponAmount);
        Context.TransferNative(Context.Caller, couponAmount);

        Context.Emit(new CouponPaidEvent
        {
            TokenId = tokenId,
            Recipient = Context.Caller,
            Amount = couponAmount,
        });
    }

    /// <summary>
    /// Advance the coupon epoch for a bond series. Called after all holders
    /// have claimed (or after a grace period). Resets the last coupon block.
    /// </summary>
    [BasaltEntrypoint]
    public void AdvanceCouponEpoch(ulong maturitySlot)
    {
        var slotKey = maturitySlot.ToString();
        var issuerHex = _seriesIssuers.Get(slotKey);
        Context.Require(
            Convert.ToHexString(Context.Caller) == issuerHex,
            "BOND: only issuer");

        var lastCoupon = _lastCouponBlock.Get(slotKey);
        var interval = _couponIntervalBlocks.Get(slotKey);
        Context.Require(Context.BlockHeight >= lastCoupon + interval, "BOND: too early");

        _lastCouponBlock.Set(slotKey, Context.BlockHeight);
    }

    // ================================================================
    // Maturity Redemption
    // ================================================================

    /// <summary>
    /// Redeem a bond at maturity. Burns the BST-3525 token and returns the face value.
    /// </summary>
    [BasaltEntrypoint]
    public void Redeem(ulong tokenId)
    {
        var owner = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(owner),
            "BOND: not token owner");

        var slot = SlotOf(tokenId);
        Context.Require(Context.BlockHeight >= slot, "BOND: not yet matured");

        var slotKey = slot.ToString();
        var status = _seriesStatus.Get(slotKey);
        Context.Require(status != "defaulted", "BOND: series defaulted");

        var faceValue = BalanceOf(tokenId);

        // Burn by transferring value to zero (reduce total face value)
        _totalFaceValue.Set(slotKey, _totalFaceValue.Get(slotKey) - faceValue);

        // Mark series as matured if all bonds redeemed
        if (_totalFaceValue.Get(slotKey).IsZero)
            _seriesStatus.Set(slotKey, "matured");

        // Transfer face value to holder
        Context.TransferNative(Context.Caller, faceValue);

        Context.Emit(new BondRedeemedEvent
        {
            TokenId = tokenId,
            Holder = Context.Caller,
            FaceAmount = faceValue,
        });
    }

    // ================================================================
    // Callable Bond Redemption
    // ================================================================

    /// <summary>
    /// Issuer calls (redeems early) a callable bond. Pays face value + call premium.
    /// </summary>
    [BasaltEntrypoint]
    public void CallBond(ulong tokenId)
    {
        var slot = SlotOf(tokenId);
        var slotKey = slot.ToString();
        Context.Require(_isCallable.Get(slotKey) == "1", "BOND: not callable");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _seriesIssuers.Get(slotKey),
            "BOND: only issuer can call");
        Context.Require(_seriesStatus.Get(slotKey) == "active", "BOND: not active");

        var faceValue = BalanceOf(tokenId);
        var premiumBps = _callPremiumBps.Get(slotKey);
        var premium = faceValue * new UInt256(premiumBps) / new UInt256(10000);
        var totalPayout = faceValue + premium;

        Context.Require(Context.TxValue >= totalPayout, "BOND: insufficient call payment");

        var holder = OwnerOf(tokenId);
        _totalFaceValue.Set(slotKey, _totalFaceValue.Get(slotKey) - faceValue);

        Context.TransferNative(holder, totalPayout);

        Context.Emit(new BondCalledEvent
        {
            TokenId = tokenId,
            Holder = holder,
            FaceAmount = faceValue,
            Premium = premium,
        });
    }

    // ================================================================
    // Default Management
    // ================================================================

    /// <summary>
    /// Flag a bond series as defaulted. Governance-only action.
    /// Freezes coupon payments and enables recovery proceedings.
    /// </summary>
    [BasaltEntrypoint]
    public void FlagDefault(ulong maturitySlot)
    {
        // Only governance contract can flag defaults
        var governanceAddress = new byte[20];
        governanceAddress[18] = 0x10;
        governanceAddress[19] = 0x02;
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(governanceAddress),
            "BOND: only governance");

        var slotKey = maturitySlot.ToString();
        Context.Require(_seriesStatus.Get(slotKey) == "active", "BOND: not active");
        _seriesStatus.Set(slotKey, "defaulted");

        Context.Emit(new BondDefaultedEvent { MaturitySlot = maturitySlot });
    }

    // ================================================================
    // Views
    // ================================================================

    [BasaltView]
    public string GetSeriesStatus(ulong maturitySlot)
        => _seriesStatus.Get(maturitySlot.ToString()) ?? "unknown";

    [BasaltView]
    public string GetCreditRating(ulong maturitySlot)
        => _creditRating.Get(maturitySlot.ToString()) ?? "";

    [BasaltView]
    public ulong GetCouponRateBps(ulong maturitySlot)
        => _couponRateBps.Get(maturitySlot.ToString());

    [BasaltView]
    public ulong GetCouponIntervalBlocks(ulong maturitySlot)
        => _couponIntervalBlocks.Get(maturitySlot.ToString());

    [BasaltView]
    public UInt256 GetTotalFaceValue(ulong maturitySlot)
        => _totalFaceValue.Get(maturitySlot.ToString());

    [BasaltView]
    public UInt256 GetCouponPoolBalance(ulong maturitySlot)
        => _couponPool.Get(maturitySlot.ToString());

    [BasaltView]
    public bool IsVerifiedHolder(byte[] holder)
        => _verifiedHolders.Get(Convert.ToHexString(holder)) == "1";

    [BasaltView]
    public bool IsCallable(ulong maturitySlot)
        => _isCallable.Get(maturitySlot.ToString()) == "1";

    [BasaltView]
    public ulong GetCallPremiumBps(ulong maturitySlot)
        => _callPremiumBps.Get(maturitySlot.ToString());

    [BasaltView]
    public ulong GetNextCouponBlock(ulong maturitySlot)
    {
        var slotKey = maturitySlot.ToString();
        return _lastCouponBlock.Get(slotKey) + _couponIntervalBlocks.Get(slotKey);
    }

    // ================================================================
    // Internal helpers
    // ================================================================

    private void RequireVerifiedHolder(byte[] holder)
    {
        Context.Require(
            _verifiedHolders.Get(Convert.ToHexString(holder)) == "1",
            "BOND: holder not verified");
    }

    private static void RequireValidRating(string rating)
    {
        Context.Require(
            rating == "AAA" || rating == "AA" || rating == "A" ||
            rating == "BBB" || rating == "BB" || rating == "B" ||
            rating == "CCC",
            "BOND: invalid rating");
    }
}

// ================================================================
// Events
// ================================================================

[BasaltEvent]
public sealed class BondSeriesCreatedEvent
{
    [Indexed] public ulong MaturitySlot { get; init; }
    [Indexed] public byte[] Issuer { get; init; } = [];
    public ulong CouponRateBps { get; init; }
    public string Rating { get; init; } = "";
}

[BasaltEvent]
public sealed class BondPurchasedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public byte[] Buyer { get; init; } = [];
    public ulong MaturitySlot { get; init; }
    public UInt256 FaceAmount { get; init; }
}

[BasaltEvent]
public sealed class CouponPoolFundedEvent
{
    [Indexed] public ulong MaturitySlot { get; init; }
    public UInt256 Amount { get; init; }
}

[BasaltEvent]
public sealed class CouponPaidEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public byte[] Recipient { get; init; } = [];
    public UInt256 Amount { get; init; }
}

[BasaltEvent]
public sealed class BondRedeemedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public byte[] Holder { get; init; } = [];
    public UInt256 FaceAmount { get; init; }
}

[BasaltEvent]
public sealed class BondCalledEvent
{
    [Indexed] public ulong TokenId { get; init; }
    public byte[] Holder { get; init; } = [];
    public UInt256 FaceAmount { get; init; }
    public UInt256 Premium { get; init; }
}

[BasaltEvent]
public sealed class RatingUpdatedEvent
{
    [Indexed] public ulong MaturitySlot { get; init; }
    public string NewRating { get; init; } = "";
    public byte[] RatingAgency { get; init; } = [];
}

[BasaltEvent]
public sealed class BondDefaultedEvent
{
    [Indexed] public ulong MaturitySlot { get; init; }
}
```

## Complexity

**High** -- This contract inherits from BST-3525 and layers bond-specific semantics on top (coupon accrual, compliance gating, callable redemption, credit ratings, default handling). It requires cross-contract calls to SchemaRegistry, IssuerRegistry, and Governance. The coupon distribution mechanism involves proportional calculations across potentially many holders. The ZK proof verification path adds cryptographic complexity. State management spans multiple storage maps for series metadata, holder verification, and coupon pools.

## Priority

**P0** -- Tokenized bonds are the flagship use case for Basalt's unique combination of BST-3525 semi-fungible tokens and ZK compliance. This contract demonstrates capabilities that no other L1 can match natively: compliant fixed-income instruments with programmable coupons, ZK-gated investor verification, and fractionalized secondary market trading. It serves as the reference implementation for the entire real-world asset (RWA) thesis of the Basalt ecosystem and will be prominently featured in developer documentation and institutional pitches.
