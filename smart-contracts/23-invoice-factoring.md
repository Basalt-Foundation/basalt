# Invoice Factoring

## Category

Decentralized Finance (DeFi) / Trade Finance / Real-World Assets (RWA)

## Summary

Invoice Factoring is a BST-3525 semi-fungible token contract that enables businesses to tokenize their accounts receivable (invoices) and sell them to investors at a discount. Each invoice is represented as a BST-3525 token where the slot identifies the debtor (debtor ID), the value represents the invoice face amount, and the token carries metadata about payment terms and due dates. Investors purchase invoice tokens at a discount (e.g., 95% of face value), providing immediate working capital to the business, and receive the full face amount when the debtor pays at maturity.

The contract supports the complete invoice lifecycle: issuance by verified businesses, investor bidding and purchase at discount, payment routing when the debtor settles, and dispute resolution. ZK compliance ensures that businesses can prove their creditworthiness and trade history without revealing sensitive financial details such as revenue figures, profit margins, or customer lists to competing businesses or the public.

## Why It's Useful

- Invoice factoring is a $3+ trillion global market dominated by banks and specialized factors that charge high fees (2-5% per month) and require extensive paperwork; on-chain factoring reduces fees through disintermediation and transparent pricing.
- Small and medium businesses (SMBs) wait an average of 60-90 days for invoice payment, creating cash flow crises that are the #1 cause of business failure; instant tokenization and sale provides same-day liquidity.
- Traditional factoring requires businesses to reveal their full customer list, financial statements, and trade terms to the factor; ZK proofs allow businesses to demonstrate creditworthiness (e.g., "I have > $1M annual revenue and < 5% default rate") without revealing the actual numbers.
- Invoice fraud (double-factoring, fictitious invoices) costs the industry billions annually; on-chain tokenization with unique IDs and debtor verification prevents the same invoice from being sold twice.
- Cross-border invoice factoring involves currency risk, jurisdictional complexity, and trust issues between unknown parties; Basalt's compliance layer and atomic settlement eliminate counterparty risk.
- Institutional investors seeking yield have limited access to trade finance assets; tokenized invoices create a new fixed-income-like asset class with short duration and attractive risk-adjusted returns.

## Key Features

- Invoice tokenization: businesses mint BST-3525 tokens representing verified invoices with face amount, debtor ID (slot), due date, and payment terms.
- Debtor-based fungibility: invoices from the same debtor share a slot, enabling portfolio construction and risk aggregation by debtor exposure.
- Discount bidding: investors place bids to purchase invoices at a discount (expressed in basis points off face value); the business selects the best offer.
- ZK business verification: businesses prove creditworthiness (revenue thresholds, default history, trade volume) via ZK proofs without revealing exact financial data.
- Debtor verification: debtors can optionally confirm invoices on-chain, increasing investor confidence and reducing discount rates.
- Payment routing: when the debtor pays, funds are automatically routed to the current invoice token holder (investor who purchased the factored invoice).
- Partial factoring: businesses can factor a portion of an invoice by splitting the BST-3525 token value, retaining some exposure while selling the rest.
- Maturity tracking: each invoice has a due date (block number); overdue invoices trigger a grace period followed by default classification.
- Default and dispute resolution: overdue invoices enter a dispute phase where the business, debtor, and investor can submit evidence; resolution via governance or arbitration.
- Credit scoring: on-chain track record of payment history builds a transparent, composable credit score for debtors.
- Portfolio construction: investors can build diversified invoice portfolios across multiple debtors and industries, with risk metrics computed from on-chain history.
- Recourse vs. non-recourse: configurable per invoice -- recourse invoices allow investors to claim against the business if the debtor defaults; non-recourse invoices are buyer-beware.

## Basalt-Specific Advantages

- **BST-3525 Semi-Fungible Tokens**: The slot = debtor ID model creates natural risk pools. Invoices from the same debtor are fungible by value, allowing investors to aggregate or split positions. An investor holding multiple invoices from the same debtor can merge them into a single token for portfolio simplicity, or split a large invoice into smaller tranches for resale. This debtor-level fungibility is unique to BST-3525.
- **ZK Compliance Layer**: Businesses prove their creditworthiness without revealing sensitive financial data. A business can generate a ZK proof showing "annual revenue > $1M AND default rate < 3% AND years in operation > 5" without disclosing the actual revenue figure, customer names, or detailed financials. This is critical for competitive markets where revealing financial details to potential investors (who may also be competitors' investors) is unacceptable.
- **BST-VC Verifiable Credentials**: Business registration, trade licenses, and debtor verification are represented as W3C Verifiable Credentials, providing standardized identity attestations that integrate with existing compliance workflows.
- **Escrow Integration**: Invoice purchase funds and debtor payments flow through the protocol-level Escrow contract, ensuring atomic settlement: the business receives funds only when the invoice token is transferred, and the investor receives payment only when the debtor settles.
- **IssuerRegistry Integration**: Only verified factoring platforms and business registrars can authorize invoice tokenization, preventing fictitious invoice creation.
- **AOT-Compiled Execution**: Discount auction matching and payment distribution are computationally intensive for large invoice portfolios; AOT compilation ensures predictable gas costs.
- **Ed25519 Signatures**: Invoice confirmation by debtors uses Ed25519 signatures, providing fast verification and non-repudiation of payment obligations.
- **Pedersen Commitments**: Future integration can shield invoice amounts on-chain, allowing businesses to factor invoices without competitors learning their contract sizes.

## Token Standards Used

- **BST-3525 (Semi-Fungible Token)**: Primary standard. Slot = debtor ID, Value = invoice face amount in base units.
- **BST-VC (Verifiable Credentials)**: Business registration credentials, debtor verification, and creditworthiness attestations.
- **BST-20 (Fungible Token)**: Payments can be settled in native BST or BST-20 stablecoins.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines credential schemas for "BusinessCredit" (revenue/default rate proofs), "DebtorVerification" (debtor identity confirmation), and "TradeFinanceLicense" (factoring platform authorization).
- **IssuerRegistry (0x...1007)**: Verifies that businesses and factoring platforms are registered credential issuers.
- **Escrow (0x...1003)**: Holds purchase funds during the bidding process and debtor payments pending distribution to invoice holders.
- **Governance (0x...1002)**: Dispute resolution for defaulted invoices; parameter governance (e.g., maximum discount rates, grace period duration).
- **BNS (0x...1001)**: Businesses and debtors can register human-readable names for invoice discoverability and reputation building.
- **BridgeETH (0x...1008)**: Cross-chain invoice factoring where international investors bridge ETH to participate in Basalt-native invoice markets.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Invoice Factoring contract built on BST-3525.
/// Slot = debtor ID (hash of debtor identity), Value = invoice face amount.
/// Type ID: 0x010A
/// </summary>
[BasaltContract]
public partial class InvoiceFactoring : BST3525Token
{
    // --- Invoice metadata ---
    private readonly StorageMap<string, string> _invoiceIssuers;       // tokenId -> business address hex
    private readonly StorageMap<string, ulong> _dueDateBlocks;         // tokenId -> due date block number
    private readonly StorageMap<string, string> _invoiceStatus;        // tokenId -> "open"/"factored"/"paid"/"overdue"/"defaulted"/"disputed"
    private readonly StorageMap<string, string> _invoiceRecourse;      // tokenId -> "recourse"/"non-recourse"
    private readonly StorageMap<string, string> _debtorConfirmed;      // tokenId -> "1" if debtor confirmed

    // --- Bidding state ---
    private readonly StorageMap<string, ulong> _nextBidId;             // tokenId -> next bid counter
    private readonly StorageMap<string, string> _bidders;              // tokenId:bidId -> bidder hex
    private readonly StorageMap<string, ulong> _bidDiscountBps;        // tokenId:bidId -> discount in bps
    private readonly StorageMap<string, string> _bidStatus;            // tokenId:bidId -> "active"/"accepted"/"rejected"/"withdrawn"

    // --- Debtor credit tracking ---
    private readonly StorageMap<string, ulong> _debtorTotalInvoices;   // debtorSlot -> count of invoices
    private readonly StorageMap<string, ulong> _debtorPaidOnTime;      // debtorSlot -> count paid on time
    private readonly StorageMap<string, ulong> _debtorDefaulted;       // debtorSlot -> count defaulted

    // --- Configuration ---
    private readonly StorageValue<ulong> _gracePeriodBlocks;           // blocks after due date before default
    private readonly StorageValue<ulong> _maxDiscountBps;              // maximum discount allowed

    // --- Compliance ---
    private readonly StorageMap<string, string> _verifiedBusinesses;   // address hex -> "1"

    // --- System contract addresses ---
    private readonly byte[] _schemaRegistryAddress;
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _escrowAddress;

    public InvoiceFactoring(ulong gracePeriodBlocks = 43200, ulong maxDiscountBps = 2000)
        : base("Basalt Invoice Token", "bINV", 18)
    {
        _invoiceIssuers = new StorageMap<string, string>("inv_issuer");
        _dueDateBlocks = new StorageMap<string, ulong>("inv_due");
        _invoiceStatus = new StorageMap<string, string>("inv_status");
        _invoiceRecourse = new StorageMap<string, string>("inv_recourse");
        _debtorConfirmed = new StorageMap<string, string>("inv_confirm");
        _nextBidId = new StorageMap<string, ulong>("inv_nbid");
        _bidders = new StorageMap<string, string>("inv_bidder");
        _bidDiscountBps = new StorageMap<string, ulong>("inv_disc");
        _bidStatus = new StorageMap<string, string>("inv_bstat");
        _debtorTotalInvoices = new StorageMap<string, ulong>("inv_dtotal");
        _debtorPaidOnTime = new StorageMap<string, ulong>("inv_dpaid");
        _debtorDefaulted = new StorageMap<string, ulong>("inv_ddef");
        _gracePeriodBlocks = new StorageValue<ulong>("inv_grace");
        _maxDiscountBps = new StorageValue<ulong>("inv_maxdisc");
        _verifiedBusinesses = new StorageMap<string, string>("inv_vbus");

        _gracePeriodBlocks.Set(gracePeriodBlocks);
        _maxDiscountBps.Set(maxDiscountBps);

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
    // Invoice Creation
    // ================================================================

    /// <summary>
    /// Tokenize an invoice. Business must be verified via ZK proof of creditworthiness.
    /// Slot = debtor ID, Value = invoice face amount.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateInvoice(
        ulong debtorId,
        UInt256 faceAmount,
        ulong dueDateBlock,
        string recourseType,
        string metadataUri,
        byte[] businessProof)
    {
        Context.Require(dueDateBlock > Context.BlockHeight, "INV: due date must be in future");
        Context.Require(
            recourseType == "recourse" || recourseType == "non-recourse",
            "INV: invalid recourse type");
        Context.Require(!faceAmount.IsZero, "INV: face amount must be > 0");

        // Verify business credentials
        VerifyBusiness(Context.Caller, businessProof);

        var tokenId = Mint(Context.Caller, debtorId, faceAmount);
        var tokenKey = tokenId.ToString();

        _invoiceIssuers.Set(tokenKey, Convert.ToHexString(Context.Caller));
        _dueDateBlocks.Set(tokenKey, dueDateBlock);
        _invoiceStatus.Set(tokenKey, "open");
        _invoiceRecourse.Set(tokenKey, recourseType);

        // Track debtor statistics
        var slotKey = debtorId.ToString();
        _debtorTotalInvoices.Set(slotKey, _debtorTotalInvoices.Get(slotKey) + 1);

        SetTokenUri(tokenId, metadataUri);

        Context.Emit(new InvoiceCreatedEvent
        {
            TokenId = tokenId,
            Business = Context.Caller,
            DebtorId = debtorId,
            FaceAmount = faceAmount,
            DueDateBlock = dueDateBlock,
            RecourseType = recourseType,
        });

        return tokenId;
    }

    /// <summary>
    /// Debtor confirms an invoice is valid. Increases investor confidence.
    /// </summary>
    [BasaltEntrypoint]
    public void ConfirmInvoice(ulong tokenId)
    {
        Context.Require(
            _invoiceStatus.Get(tokenId.ToString()) == "open",
            "INV: invoice not open");
        _debtorConfirmed.Set(tokenId.ToString(), "1");

        Context.Emit(new InvoiceConfirmedEvent
        {
            TokenId = tokenId,
            Debtor = Context.Caller,
        });
    }

    // ================================================================
    // Discount Bidding
    // ================================================================

    /// <summary>
    /// Place a bid to purchase an invoice at a discount.
    /// Sends the discounted amount as BST value; held until accepted or withdrawn.
    /// </summary>
    [BasaltEntrypoint]
    public ulong PlaceBid(ulong tokenId, ulong discountBps)
    {
        var tokenKey = tokenId.ToString();
        Context.Require(_invoiceStatus.Get(tokenKey) == "open", "INV: invoice not open");
        Context.Require(discountBps <= _maxDiscountBps.Get(), "INV: discount exceeds maximum");
        Context.Require(discountBps > 0, "INV: discount must be > 0");

        var faceAmount = BalanceOf(tokenId);
        var purchasePrice = faceAmount - (faceAmount * new UInt256(discountBps) / new UInt256(10000));
        Context.Require(Context.TxValue >= purchasePrice, "INV: insufficient bid amount");

        var bidId = _nextBidId.Get(tokenKey);
        _nextBidId.Set(tokenKey, bidId + 1);

        var bidKey = tokenKey + ":" + bidId.ToString();
        _bidders.Set(bidKey, Convert.ToHexString(Context.Caller));
        _bidDiscountBps.Set(bidKey, discountBps);
        _bidStatus.Set(bidKey, "active");

        Context.Emit(new BidPlacedEvent
        {
            TokenId = tokenId,
            BidId = bidId,
            Bidder = Context.Caller,
            DiscountBps = discountBps,
            PurchasePrice = purchasePrice,
        });

        return bidId;
    }

    /// <summary>
    /// Business accepts a bid. Transfers invoice token to bidder, receives discounted payment.
    /// </summary>
    [BasaltEntrypoint]
    public void AcceptBid(ulong tokenId, ulong bidId)
    {
        var tokenKey = tokenId.ToString();
        Context.Require(
            Convert.ToHexString(Context.Caller) == _invoiceIssuers.Get(tokenKey),
            "INV: only invoice issuer");
        Context.Require(_invoiceStatus.Get(tokenKey) == "open", "INV: not open");

        var bidKey = tokenKey + ":" + bidId.ToString();
        Context.Require(_bidStatus.Get(bidKey) == "active", "INV: bid not active");

        var bidderHex = _bidders.Get(bidKey);
        var discountBps = _bidDiscountBps.Get(bidKey);
        var faceAmount = BalanceOf(tokenId);
        var purchasePrice = faceAmount - (faceAmount * new UInt256(discountBps) / new UInt256(10000));

        _bidStatus.Set(bidKey, "accepted");
        _invoiceStatus.Set(tokenKey, "factored");

        // Transfer invoice token to bidder (investor)
        var bidder = Convert.FromHexString(bidderHex);
        TransferToken(bidder, tokenId);

        // Transfer purchase price to business
        Context.TransferNative(Context.Caller, purchasePrice);

        Context.Emit(new InvoiceFactoredEvent
        {
            TokenId = tokenId,
            Business = Context.Caller,
            Investor = bidder,
            DiscountBps = discountBps,
            PurchasePrice = purchasePrice,
        });
    }

    /// <summary>
    /// Withdraw an active bid. Returns escrowed funds to bidder.
    /// </summary>
    [BasaltEntrypoint]
    public void WithdrawBid(ulong tokenId, ulong bidId)
    {
        var bidKey = tokenId.ToString() + ":" + bidId.ToString();
        Context.Require(
            Convert.ToHexString(Context.Caller) == _bidders.Get(bidKey),
            "INV: not bidder");
        Context.Require(_bidStatus.Get(bidKey) == "active", "INV: bid not active");

        _bidStatus.Set(bidKey, "withdrawn");

        var faceAmount = BalanceOf(tokenId);
        var discountBps = _bidDiscountBps.Get(bidKey);
        var refundAmount = faceAmount - (faceAmount * new UInt256(discountBps) / new UInt256(10000));
        Context.TransferNative(Context.Caller, refundAmount);
    }

    // ================================================================
    // Payment Settlement
    // ================================================================

    /// <summary>
    /// Debtor pays the invoice. Funds are routed to the current token holder.
    /// </summary>
    [BasaltEntrypoint]
    public void PayInvoice(ulong tokenId)
    {
        var tokenKey = tokenId.ToString();
        var status = _invoiceStatus.Get(tokenKey);
        Context.Require(
            status == "factored" || status == "open" || status == "overdue",
            "INV: cannot pay this invoice");

        var faceAmount = BalanceOf(tokenId);
        Context.Require(Context.TxValue >= faceAmount, "INV: insufficient payment");

        var holder = OwnerOf(tokenId);
        _invoiceStatus.Set(tokenKey, "paid");

        // Update debtor credit score
        var slot = SlotOf(tokenId);
        var slotKey = slot.ToString();
        var dueDate = _dueDateBlocks.Get(tokenKey);
        if (Context.BlockHeight <= dueDate)
            _debtorPaidOnTime.Set(slotKey, _debtorPaidOnTime.Get(slotKey) + 1);

        // Transfer face amount to current holder
        Context.TransferNative(holder, faceAmount);

        Context.Emit(new InvoicePaidEvent
        {
            TokenId = tokenId,
            Debtor = Context.Caller,
            Holder = holder,
            Amount = faceAmount,
        });
    }

    /// <summary>
    /// Mark an invoice as overdue. Anyone can call after due date + grace period.
    /// </summary>
    [BasaltEntrypoint]
    public void MarkOverdue(ulong tokenId)
    {
        var tokenKey = tokenId.ToString();
        var status = _invoiceStatus.Get(tokenKey);
        Context.Require(
            status == "factored" || status == "open",
            "INV: cannot mark overdue");

        var dueDate = _dueDateBlocks.Get(tokenKey);
        Context.Require(Context.BlockHeight > dueDate, "INV: not yet overdue");

        _invoiceStatus.Set(tokenKey, "overdue");

        Context.Emit(new InvoiceOverdueEvent { TokenId = tokenId });
    }

    /// <summary>
    /// Mark an overdue invoice as defaulted after the grace period.
    /// For recourse invoices, the business is liable.
    /// </summary>
    [BasaltEntrypoint]
    public void MarkDefaulted(ulong tokenId)
    {
        var tokenKey = tokenId.ToString();
        Context.Require(_invoiceStatus.Get(tokenKey) == "overdue", "INV: not overdue");

        var dueDate = _dueDateBlocks.Get(tokenKey);
        var grace = _gracePeriodBlocks.Get();
        Context.Require(Context.BlockHeight > dueDate + grace, "INV: grace period not expired");

        _invoiceStatus.Set(tokenKey, "defaulted");

        var slot = SlotOf(tokenId);
        _debtorDefaulted.Set(slot.ToString(), _debtorDefaulted.Get(slot.ToString()) + 1);

        Context.Emit(new InvoiceDefaultedEvent
        {
            TokenId = tokenId,
            RecourseType = _invoiceRecourse.Get(tokenKey),
        });
    }

    /// <summary>
    /// Claim recourse against the business for a defaulted recourse invoice.
    /// Only the current token holder can claim.
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimRecourse(ulong tokenId)
    {
        var tokenKey = tokenId.ToString();
        Context.Require(_invoiceStatus.Get(tokenKey) == "defaulted", "INV: not defaulted");
        Context.Require(_invoiceRecourse.Get(tokenKey) == "recourse", "INV: non-recourse invoice");

        var holder = OwnerOf(tokenId);
        Context.Require(
            Convert.ToHexString(Context.Caller) == Convert.ToHexString(holder),
            "INV: not token holder");

        _invoiceStatus.Set(tokenKey, "disputed");

        Context.Emit(new RecourseClaimedEvent
        {
            TokenId = tokenId,
            Claimant = Context.Caller,
            Business = Convert.FromHexString(_invoiceIssuers.Get(tokenKey)),
        });
    }

    // ================================================================
    // Views
    // ================================================================

    [BasaltView]
    public string GetInvoiceStatus(ulong tokenId)
        => _invoiceStatus.Get(tokenId.ToString()) ?? "unknown";

    [BasaltView]
    public ulong GetDueDateBlock(ulong tokenId)
        => _dueDateBlocks.Get(tokenId.ToString());

    [BasaltView]
    public bool IsDebtorConfirmed(ulong tokenId)
        => _debtorConfirmed.Get(tokenId.ToString()) == "1";

    [BasaltView]
    public string GetRecourseType(ulong tokenId)
        => _invoiceRecourse.Get(tokenId.ToString()) ?? "";

    [BasaltView]
    public ulong GetDebtorPaidOnTimeCount(ulong debtorId)
        => _debtorPaidOnTime.Get(debtorId.ToString());

    [BasaltView]
    public ulong GetDebtorDefaultCount(ulong debtorId)
        => _debtorDefaulted.Get(debtorId.ToString());

    [BasaltView]
    public ulong GetDebtorTotalInvoices(ulong debtorId)
        => _debtorTotalInvoices.Get(debtorId.ToString());

    [BasaltView]
    public ulong GetBidDiscountBps(ulong tokenId, ulong bidId)
        => _bidDiscountBps.Get(tokenId.ToString() + ":" + bidId.ToString());

    [BasaltView]
    public string GetBidStatus(ulong tokenId, ulong bidId)
        => _bidStatus.Get(tokenId.ToString() + ":" + bidId.ToString()) ?? "unknown";

    // ================================================================
    // Internal helpers
    // ================================================================

    private void VerifyBusiness(byte[] business, byte[] proof)
    {
        var hex = Convert.ToHexString(business);
        if (_verifiedBusinesses.Get(hex) == "1") return;

        var valid = Context.CallContract<bool>(
            _schemaRegistryAddress, "VerifyProof", "BusinessCredit", business, proof);
        Context.Require(valid, "INV: invalid business credit proof");

        _verifiedBusinesses.Set(hex, "1");
    }
}

// ================================================================
// Events
// ================================================================

[BasaltEvent]
public sealed class InvoiceCreatedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public byte[] Business { get; init; } = [];
    public ulong DebtorId { get; init; }
    public UInt256 FaceAmount { get; init; }
    public ulong DueDateBlock { get; init; }
    public string RecourseType { get; init; } = "";
}

[BasaltEvent]
public sealed class InvoiceConfirmedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    public byte[] Debtor { get; init; } = [];
}

[BasaltEvent]
public sealed class BidPlacedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    [Indexed] public ulong BidId { get; init; }
    public byte[] Bidder { get; init; } = [];
    public ulong DiscountBps { get; init; }
    public UInt256 PurchasePrice { get; init; }
}

[BasaltEvent]
public sealed class InvoiceFactoredEvent
{
    [Indexed] public ulong TokenId { get; init; }
    public byte[] Business { get; init; } = [];
    public byte[] Investor { get; init; } = [];
    public ulong DiscountBps { get; init; }
    public UInt256 PurchasePrice { get; init; }
}

[BasaltEvent]
public sealed class InvoicePaidEvent
{
    [Indexed] public ulong TokenId { get; init; }
    public byte[] Debtor { get; init; } = [];
    public byte[] Holder { get; init; } = [];
    public UInt256 Amount { get; init; }
}

[BasaltEvent]
public sealed class InvoiceOverdueEvent
{
    [Indexed] public ulong TokenId { get; init; }
}

[BasaltEvent]
public sealed class InvoiceDefaultedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    public string RecourseType { get; init; } = "";
}

[BasaltEvent]
public sealed class RecourseClaimedEvent
{
    [Indexed] public ulong TokenId { get; init; }
    public byte[] Claimant { get; init; } = [];
    public byte[] Business { get; init; } = [];
}
```

## Complexity

**High** -- The contract manages a multi-phase lifecycle (creation, bidding, factoring, payment, overdue, default, recourse) with numerous state transitions. The bidding system requires fund escrow and matching logic. ZK business verification adds cryptographic complexity. Debtor credit scoring accumulates on-chain history across multiple invoices. The recourse/non-recourse distinction creates branching settlement paths. Cross-contract calls to SchemaRegistry and Escrow are required for compliance and fund management.

## Priority

**P1** -- Invoice factoring is a high-impact trade finance use case with immediate market demand from SMBs globally. It demonstrates BST-3525's versatility beyond securities (debtor-based fungibility is a novel application). Combined with ZK business verification, this contract showcases a unique value proposition: businesses get liquidity without revealing sensitive financials. Slightly behind tokenized bonds (P0) and real estate (P1) because the legal infrastructure for tokenized invoices is more nascent.
