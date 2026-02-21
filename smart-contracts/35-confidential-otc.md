# Confidential OTC Trading Desk

## Category

DeFi / Privacy / Trading

## Summary

A confidential over-the-counter (OTC) trading desk where two parties negotiate trade terms off-chain and settle on-chain with hidden trade amounts via Pedersen commitments. The contract uses Escrow to hold funds until both parties confirm, range proofs to verify amounts are valid without revealing them to observers, and compliance gating for large trades that exceed configurable thresholds.

This enables institutional-grade OTC trading on Basalt where counterparties know their own trade terms but on-chain observers see only that a valid, compliant trade occurred -- not the amounts, price, or exact terms.

## Why It's Useful

- **Trade confidentiality**: OTC trades often involve large amounts where price discovery and trade sizes are market-sensitive information. Revealing trade amounts on a public blockchain causes front-running, copycat trading, and market impact.
- **Institutional adoption**: Professional OTC desks require trade confidentiality as a baseline feature. Without it, institutional traders will not use on-chain settlement.
- **Reduced market manipulation**: When large trades are visible on-chain, arbitrageurs can front-run or sandwich the transaction. Hidden amounts eliminate this attack vector.
- **Compliance for large trades**: Trades above configurable thresholds require compliance proofs (e.g., KYC verification via BST-VC), satisfying regulatory requirements for large-value transfers.
- **Settlement finality**: On-chain settlement provides instant, irrevocable finality that traditional OTC markets lack. No T+2 settlement risk.
- **Counterparty risk elimination**: Escrow-based settlement eliminates the risk of one party defaulting after the other has delivered.

## Key Features

- Trade creation with Pedersen-committed amounts (both parties know the amount, observers do not)
- Off-chain negotiation, on-chain settlement pattern
- Dual-confirmation: both maker and taker must confirm before settlement executes
- Escrow integration: funds locked in Escrow contract until both parties confirm
- Range proofs: ZK proof that committed amount is within valid bounds (0, 2^64) without revealing exact value
- Compliance gating: trades above a threshold require valid KYC credentials (BST-VC)
- Trade cancellation: maker can cancel before taker confirms; mutual cancellation after both confirm
- Partial fills: support for filling part of a trade (commitment arithmetic on Pedersen commitments)
- Trade expiry: automatic cancellation after configurable timeout
- Dispute resolution: if one party claims settlement amount mismatch, admin can mediate
- Fee structure: configurable maker/taker fees deducted at settlement
- Trade history: queryable on-chain record of completed trades (without amounts)
- Multi-asset support: can trade native BST or BST-20 tokens
- Counterparty whitelisting: makers can restrict who can take their trades

## Basalt-Specific Advantages

- **Native Pedersen commitments**: Basalt's cryptographic layer supports Pedersen commitments as a first-class primitive. Trade amounts are committed as `C = amount * G + blinding * H` where G and H are generator points. The commitment is binding (cannot change amount after committing) and hiding (cannot determine amount from commitment alone).
- **Range proofs via ZK compliance**: Basalt's ZkComplianceVerifier verifies Groth16 range proofs that demonstrate `0 < amount < 2^64` without revealing the amount. This prevents negative-amount exploits that plagued early confidential transaction schemes.
- **Escrow system contract**: Basalt's built-in Escrow contract (0x...1003) provides time-locked fund custody with release and refund flows, eliminating the need to build custom escrow logic.
- **BST-VC compliance gating**: For large trades (above threshold), both parties must hold valid KYC credentials (BST-VC). The contract checks credential validity via cross-contract calls to BSTVCRegistry, integrating seamlessly with the KYC marketplace.
- **AOT-compiled settlement**: Settlement logic (commitment verification, escrow release, fee calculation) runs in AOT-compiled code with deterministic gas costs, ensuring predictable transaction costs for professional traders.
- **Ed25519 trade signatures**: Trade terms (amounts, counterparties, expiry) are signed with Ed25519 by both parties off-chain. The contract verifies these signatures on-chain, ensuring non-repudiation.
- **Cross-contract composability**: The OTC desk interacts with Escrow (fund locking), BSTVCRegistry (compliance), and potentially BridgeETH (cross-chain settlement) through Basalt's type-safe cross-contract call mechanism.

## Token Standards Used

- **BST-20** (BST20Token, type 0x0001): For OTC trades involving BST-20 tokens rather than native BST.
- **BST-VC** (BSTVCRegistry, type 0x0007): For compliance gating on large trades. Both parties must hold valid KYC credentials.

## Integration Points

- **Escrow** (0x...1003): Trade deposits are held in Escrow with release contingent on both-party confirmation. Escrow provides timeout-based refund if the counterparty does not confirm.
- **BSTVCRegistry** (deployed instance): Large trade compliance checks query BSTVCRegistry to verify that both parties hold valid, non-expired KYC credentials.
- **IssuerRegistry** (0x...1007): Validates that KYC credential issuers are active and at sufficient trust tiers.
- **SchemaRegistry** (0x...1006): Range proof verification keys are stored in SchemaRegistry for the commitment validation circuit.
- **BridgeETH** (0x...1008): Cross-chain OTC: one leg of the trade can settle on Ethereum via the bridge, enabling cross-chain atomic OTC swaps.
- **Governance** (0x...1002): Fee changes, compliance thresholds, and circuit updates are governed through proposals.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Confidential OTC trading desk with Pedersen-committed amounts,
/// range proofs, escrow settlement, and compliance gating.
/// Type ID: 0x010E.
/// </summary>
[BasaltContract]
public partial class ConfidentialOtc
{
    // --- Storage ---

    // Trade records
    private readonly StorageValue<ulong> _nextTradeId;
    private readonly StorageMap<string, string> _tradeMaker;          // tradeId -> makerHex
    private readonly StorageMap<string, string> _tradeTaker;          // tradeId -> takerHex (empty until filled)
    private readonly StorageMap<string, string> _tradeCommitment;     // tradeId -> Pedersen commitment hex
    private readonly StorageMap<string, string> _tradeStatus;         // tradeId -> open/filled/settled/cancelled/expired
    private readonly StorageMap<string, long> _tradeExpiry;           // tradeId -> expiry timestamp
    private readonly StorageMap<string, ulong> _tradeEscrowId;        // tradeId -> Escrow contract escrow ID
    private readonly StorageMap<string, bool> _tradeMakerConfirmed;   // tradeId -> maker confirmed
    private readonly StorageMap<string, bool> _tradeTakerConfirmed;   // tradeId -> taker confirmed
    private readonly StorageMap<string, string> _tradeRangeProof;     // tradeId -> range proof hex
    private readonly StorageMap<string, bool> _tradeComplianceRequired; // tradeId -> requires KYC
    private readonly StorageMap<string, string> _tradeWhitelist;      // tradeId:takerHex -> whitelisted

    // Fee configuration
    private readonly StorageValue<ulong> _makerFeeBps;                // maker fee in basis points
    private readonly StorageValue<ulong> _takerFeeBps;                // taker fee in basis points
    private readonly StorageValue<UInt256> _complianceThreshold;      // amount above which KYC required
    private readonly StorageValue<UInt256> _protocolFeeBalance;

    // Admin
    private readonly StorageMap<string, string> _admin;

    // System contract addresses
    private readonly byte[] _escrowAddress;
    private readonly byte[] _vcRegistryAddress;
    private readonly byte[] _schemaRegistryAddress;

    public ConfidentialOtc(
        ulong makerFeeBps = 10,
        ulong takerFeeBps = 20,
        UInt256 complianceThreshold = default)
    {
        if (complianceThreshold.IsZero) complianceThreshold = new UInt256(100000);

        _nextTradeId = new StorageValue<ulong>("otc_next");
        _tradeMaker = new StorageMap<string, string>("otc_maker");
        _tradeTaker = new StorageMap<string, string>("otc_taker");
        _tradeCommitment = new StorageMap<string, string>("otc_commit");
        _tradeStatus = new StorageMap<string, string>("otc_status");
        _tradeExpiry = new StorageMap<string, long>("otc_expiry");
        _tradeEscrowId = new StorageMap<string, ulong>("otc_escrow");
        _tradeMakerConfirmed = new StorageMap<string, bool>("otc_mconf");
        _tradeTakerConfirmed = new StorageMap<string, bool>("otc_tconf");
        _tradeRangeProof = new StorageMap<string, string>("otc_rproof");
        _tradeComplianceRequired = new StorageMap<string, bool>("otc_compliance");
        _tradeWhitelist = new StorageMap<string, string>("otc_wl");

        _makerFeeBps = new StorageValue<ulong>("otc_mfee");
        _takerFeeBps = new StorageValue<ulong>("otc_tfee");
        _complianceThreshold = new StorageValue<UInt256>("otc_cthresh");
        _protocolFeeBalance = new StorageValue<UInt256>("otc_pfees");

        _admin = new StorageMap<string, string>("otc_admin");
        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        _makerFeeBps.Set(makerFeeBps);
        _takerFeeBps.Set(takerFeeBps);
        _complianceThreshold.Set(complianceThreshold);

        _escrowAddress = new byte[20];
        _escrowAddress[18] = 0x10;
        _escrowAddress[19] = 0x03;

        _vcRegistryAddress = new byte[20]; // Set post-deploy
        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;
    }

    // ========================================================
    // Trade Lifecycle
    // ========================================================

    /// <summary>
    /// Create a new OTC trade. Maker deposits funds and provides a Pedersen commitment
    /// to the trade amount. The actual amount is hidden from observers.
    /// A range proof must be provided to demonstrate the committed amount is valid.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateTrade(
        byte[] amountCommitment, byte[] rangeProof,
        long expiryTimestamp, bool requireCompliance)
    {
        Context.Require(!Context.TxValue.IsZero, "OTC: must deposit funds");
        Context.Require(amountCommitment.Length == 32, "OTC: invalid commitment");
        Context.Require(rangeProof.Length > 0, "OTC: range proof required");
        Context.Require(expiryTimestamp > Context.BlockTimestamp, "OTC: expiry must be in future");

        var tradeId = _nextTradeId.Get();
        _nextTradeId.Set(tradeId + 1);

        var key = tradeId.ToString();
        var makerHex = Convert.ToHexString(Context.Caller);

        // Deposit into escrow
        var releaseBlock = Context.BlockHeight + 100000; // generous timeout
        var escrowId = Context.CallContract<ulong>(
            _escrowAddress, "Create", Context.Caller, releaseBlock);

        _tradeMaker.Set(key, makerHex);
        _tradeCommitment.Set(key, Convert.ToHexString(amountCommitment));
        _tradeStatus.Set(key, "open");
        _tradeExpiry.Set(key, expiryTimestamp);
        _tradeEscrowId.Set(key, escrowId);
        _tradeRangeProof.Set(key, Convert.ToHexString(rangeProof));
        _tradeComplianceRequired.Set(key, requireCompliance);

        Context.Emit(new TradeCreatedEvent
        {
            TradeId = tradeId,
            Maker = Context.Caller,
            Commitment = amountCommitment,
            Expiry = expiryTimestamp,
            ComplianceRequired = requireCompliance,
        });

        return tradeId;
    }

    /// <summary>
    /// Taker accepts a trade. Deposits matching funds.
    /// If compliance is required, taker must hold valid KYC credential.
    /// </summary>
    [BasaltEntrypoint]
    public void AcceptTrade(ulong tradeId, byte[] kycCredentialHash)
    {
        var key = tradeId.ToString();
        Context.Require(_tradeStatus.Get(key) == "open", "OTC: trade not open");
        Context.Require(Context.BlockTimestamp < _tradeExpiry.Get(key), "OTC: trade expired");
        Context.Require(!Context.TxValue.IsZero, "OTC: must deposit funds");

        var takerHex = Convert.ToHexString(Context.Caller);
        var makerHex = _tradeMaker.Get(key);
        Context.Require(takerHex != makerHex, "OTC: cannot self-trade");

        // Check whitelist if set
        var wlKey = key + ":" + takerHex;
        // Whitelist is optional -- only enforced if maker set specific takers
        // (simplified: no whitelist enforcement for brevity)

        // Check compliance if required
        if (_tradeComplianceRequired.Get(key))
        {
            Context.Require(kycCredentialHash.Length > 0, "OTC: KYC credential required");
            // Verify credential is valid in BSTVCRegistry (simplified)
        }

        _tradeTaker.Set(key, takerHex);
        _tradeStatus.Set(key, "filled");

        Context.Emit(new TradeAcceptedEvent
        {
            TradeId = tradeId,
            Taker = Context.Caller,
        });
    }

    /// <summary>
    /// Maker confirms settlement. Both parties must confirm for settlement to execute.
    /// </summary>
    [BasaltEntrypoint]
    public void ConfirmSettlement(ulong tradeId)
    {
        var key = tradeId.ToString();
        Context.Require(_tradeStatus.Get(key) == "filled", "OTC: trade not filled");

        var callerHex = Convert.ToHexString(Context.Caller);
        var makerHex = _tradeMaker.Get(key);
        var takerHex = _tradeTaker.Get(key);

        if (callerHex == makerHex)
        {
            _tradeMakerConfirmed.Set(key, true);
        }
        else if (callerHex == takerHex)
        {
            _tradeTakerConfirmed.Set(key, true);
        }
        else
        {
            Context.Revert("OTC: not a trade party");
        }

        // If both confirmed, execute settlement
        if (_tradeMakerConfirmed.Get(key) && _tradeTakerConfirmed.Get(key))
        {
            ExecuteSettlement(tradeId, key, makerHex, takerHex);
        }
    }

    /// <summary>
    /// Cancel an open trade. Maker only, before taker accepts.
    /// </summary>
    [BasaltEntrypoint]
    public void CancelTrade(ulong tradeId)
    {
        var key = tradeId.ToString();
        var status = _tradeStatus.Get(key);
        Context.Require(status == "open", "OTC: can only cancel open trades");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _tradeMaker.Get(key),
            "OTC: not maker");

        _tradeStatus.Set(key, "cancelled");

        // Refund escrow
        var escrowId = _tradeEscrowId.Get(key);
        Context.CallContract(_escrowAddress, "Refund", escrowId);

        Context.Emit(new TradeCancelledEvent { TradeId = tradeId });
    }

    /// <summary>
    /// Claim expiry: anyone can trigger expiry cleanup after deadline.
    /// Refunds maker's deposit.
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimExpiry(ulong tradeId)
    {
        var key = tradeId.ToString();
        var status = _tradeStatus.Get(key);
        Context.Require(status == "open" || status == "filled", "OTC: not expirable");
        Context.Require(Context.BlockTimestamp >= _tradeExpiry.Get(key), "OTC: not expired");

        _tradeStatus.Set(key, "expired");

        var escrowId = _tradeEscrowId.Get(key);
        Context.CallContract(_escrowAddress, "Refund", escrowId);

        Context.Emit(new TradeExpiredEvent { TradeId = tradeId });
    }

    /// <summary>
    /// Add an address to a trade's taker whitelist. Maker only, while trade is open.
    /// </summary>
    [BasaltEntrypoint]
    public void WhitelistTaker(ulong tradeId, byte[] takerAddress)
    {
        var key = tradeId.ToString();
        Context.Require(_tradeStatus.Get(key) == "open", "OTC: trade not open");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _tradeMaker.Get(key),
            "OTC: not maker");

        var wlKey = key + ":" + Convert.ToHexString(takerAddress);
        _tradeWhitelist.Set(wlKey, "1");
    }

    // ========================================================
    // Admin
    // ========================================================

    /// <summary>
    /// Update fee structure. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetFees(ulong makerFeeBps, ulong takerFeeBps)
    {
        RequireAdmin();
        Context.Require(makerFeeBps <= 500, "OTC: maker fee too high");
        Context.Require(takerFeeBps <= 500, "OTC: taker fee too high");
        _makerFeeBps.Set(makerFeeBps);
        _takerFeeBps.Set(takerFeeBps);
    }

    /// <summary>
    /// Update compliance threshold. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetComplianceThreshold(UInt256 threshold)
    {
        RequireAdmin();
        _complianceThreshold.Set(threshold);
    }

    /// <summary>
    /// Withdraw accumulated protocol fees. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void WithdrawFees(byte[] destination)
    {
        RequireAdmin();
        var fees = _protocolFeeBalance.Get();
        Context.Require(!fees.IsZero, "OTC: no fees");
        _protocolFeeBalance.Set(UInt256.Zero);
        Context.TransferNative(destination, fees);
    }

    /// <summary>
    /// Set BSTVCRegistry address. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetVcRegistry(byte[] addr)
    {
        RequireAdmin();
        Array.Copy(addr, _vcRegistryAddress, 20);
    }

    /// <summary>
    /// Transfer admin role. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        RequireAdmin();
        _admin.Set("admin", Convert.ToHexString(newAdmin));
    }

    // ========================================================
    // Views
    // ========================================================

    [BasaltView]
    public string GetTradeStatus(ulong tradeId)
        => _tradeStatus.Get(tradeId.ToString()) ?? "unknown";

    [BasaltView]
    public string GetTradeCommitment(ulong tradeId)
        => _tradeCommitment.Get(tradeId.ToString()) ?? "";

    [BasaltView]
    public long GetTradeExpiry(ulong tradeId)
        => _tradeExpiry.Get(tradeId.ToString());

    [BasaltView]
    public bool IsMakerConfirmed(ulong tradeId)
        => _tradeMakerConfirmed.Get(tradeId.ToString());

    [BasaltView]
    public bool IsTakerConfirmed(ulong tradeId)
        => _tradeTakerConfirmed.Get(tradeId.ToString());

    [BasaltView]
    public ulong GetMakerFeeBps() => _makerFeeBps.Get();

    [BasaltView]
    public ulong GetTakerFeeBps() => _takerFeeBps.Get();

    [BasaltView]
    public UInt256 GetComplianceThreshold() => _complianceThreshold.Get();

    [BasaltView]
    public UInt256 GetProtocolFeeBalance() => _protocolFeeBalance.Get();

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void ExecuteSettlement(
        ulong tradeId, string key, string makerHex, string takerHex)
    {
        _tradeStatus.Set(key, "settled");

        // Release escrow to taker
        var escrowId = _tradeEscrowId.Get(key);
        Context.CallContract(_escrowAddress, "Release", escrowId);

        Context.Emit(new TradeSettledEvent
        {
            TradeId = tradeId,
            Maker = Convert.FromHexString(makerHex),
            Taker = Convert.FromHexString(takerHex),
        });
    }

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "OTC: not admin");
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class TradeCreatedEvent
{
    [Indexed] public ulong TradeId { get; set; }
    [Indexed] public byte[] Maker { get; set; } = null!;
    public byte[] Commitment { get; set; } = null!;
    public long Expiry { get; set; }
    public bool ComplianceRequired { get; set; }
}

[BasaltEvent]
public class TradeAcceptedEvent
{
    [Indexed] public ulong TradeId { get; set; }
    [Indexed] public byte[] Taker { get; set; } = null!;
}

[BasaltEvent]
public class TradeSettledEvent
{
    [Indexed] public ulong TradeId { get; set; }
    [Indexed] public byte[] Maker { get; set; } = null!;
    [Indexed] public byte[] Taker { get; set; } = null!;
}

[BasaltEvent]
public class TradeCancelledEvent
{
    [Indexed] public ulong TradeId { get; set; }
}

[BasaltEvent]
public class TradeExpiredEvent
{
    [Indexed] public ulong TradeId { get; set; }
}
```

## Complexity

**High** -- The contract combines Pedersen commitment cryptography, range proof verification, escrow coordination, compliance gating, and a multi-phase settlement protocol (create, accept, confirm, settle). The interaction between hidden amounts and fee calculation requires careful design -- fees must be computed on actual amounts known to both parties while remaining hidden from observers. Partial fill support with commitment arithmetic adds further complexity. The off-chain negotiation and on-chain settlement pattern also requires careful replay protection and signature verification.

## Priority

**P2** -- While confidential OTC trading is a compelling use case for institutional adoption, it depends on several prerequisite systems (Pedersen commitment infrastructure, range proof circuits, Escrow, KYC marketplace) and targets a smaller initial user base than the compliance-focused proposals. It should be prioritized after the core compliance and privacy pool infrastructure is proven.
