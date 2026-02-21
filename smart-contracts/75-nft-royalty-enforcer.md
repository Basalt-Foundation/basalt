# NFT Royalty Enforcement

## Category

Digital Assets -- Creator Economy Infrastructure

## Summary

A wrapper contract that enforces on-chain royalty payments for BST-721 NFT transfers. All transfers of wrapped tokens must route through the enforcer contract, which automatically deducts royalty payments and distributes them to the creator and optional collaborators. The royalty percentage is immutably set by the creator at wrapping time. Wrapped tokens cannot be transferred without paying royalties, providing a guaranteed revenue stream for creators that cannot be bypassed, unlike optional royalty systems on other chains.

## Why It's Useful

- **Guaranteed Creator Revenue**: Unlike EVM chains where marketplace royalties are optional and increasingly bypassed, this contract enforces royalties at the token transfer level, ensuring creators always receive their fair share.
- **Revenue Splitting**: Collaborators (co-creators, artists, musicians, producers) automatically receive their share of royalties on every transfer, eliminating the need for manual distribution.
- **Secondary Market Value Capture**: Creators participate in the appreciation of their work on secondary markets, aligning incentives between creators and collectors.
- **Ecosystem Sustainability**: Enforced royalties fund ongoing creative work, community development, and project maintenance, making NFT ecosystems more sustainable.
- **Collector Confidence**: Buyers know that their purchase directly supports the creator, increasing willingness to pay and strengthening the creator-collector relationship.
- **Composability**: Other contracts (marketplaces, lending protocols, fractionalization) can integrate royalty enforcement by routing transfers through the enforcer.
- **Transparent Terms**: Royalty percentages and split configurations are visible on-chain, providing transparency absent in traditional licensing agreements.

## Key Features

- **Token Wrapping**: Creators deposit BST-721 tokens into the enforcer contract and receive wrapped tokens with enforced royalties. Wrapping is irreversible by default (configurable).
- **Configurable Royalty Rate**: Creator sets royalty percentage (basis points, e.g., 500 = 5%) at wrapping time. Rate is immutable after wrapping to protect collector expectations.
- **Revenue Split**: Royalties are distributed among multiple recipients (creator, collaborators, treasury) according to configurable split percentages.
- **Transfer Hook**: All transfers of wrapped tokens trigger the royalty payment. The transfer reverts if royalty payment is insufficient.
- **Payment in Any BST-20**: Royalties can be paid in BST, WBSLT, or any approved BST-20 token, with automatic price conversion via AMM.
- **Minimum Sale Price**: Optional minimum transfer price to prevent wash trading at negligible prices to minimize royalty payments.
- **Royalty Escrow**: Royalties are accumulated in escrow and distributable on-demand, reducing gas costs for recipients.
- **Collection-Level Enforcement**: Wrap an entire collection under a single enforcer instance with uniform royalty terms.
- **Creator Verification**: Optional BST-VC credential verification to prove original creator identity.
- **Royalty Analytics**: On-chain tracking of total royalties paid, transfers per token, and per-recipient earnings.
- **Allowlist Exemptions**: Creator can exempt specific addresses (e.g., the creator's own marketplace, lending protocols) from royalty payments.
- **Batch Wrapping**: Wrap multiple tokens from the same collection in a single transaction.
- **Unwrap Mechanism**: Optional unwrap functionality (disabled by default) that burns the wrapper and returns the original BST-721, subject to governance approval.
- **EIP-2981 Compatibility**: Implements a `royaltyInfo` query method compatible with ERC-2981, enabling external systems to discover royalty parameters.

## Basalt-Specific Advantages

- **AOT-Compiled Transfer Hooks**: Royalty calculation and distribution logic executes in AOT-compiled native code on every transfer, ensuring minimal overhead. On EVM chains, transfer hooks add significant gas costs; on Basalt, native execution makes enforcement nearly free.
- **BST-VC Creator Verification**: Creators can attach BST-VC credentials proving their identity (artist, musician, developer), verified via the IssuerRegistry. This prevents impersonation and counterfeit collections, a major problem on EVM chains.
- **Confidential Sales via Pedersen Commitments**: Sale prices can be hidden using Pedersen commitments while still proving the royalty payment is correct (royalty >= price * rate). This prevents price manipulation and enables private sales with enforced royalties.
- **ZK Royalty Compliance**: Marketplaces can prove they correctly paid royalties via ZK proofs without revealing the exact sale price, providing privacy while maintaining enforcement.
- **BST-3525 SFT Royalty Positions**: Royalty recipient shares can be tokenized as BST-3525 semi-fungible tokens, enabling secondary market trading of future royalty income (receivables factoring for creators).
- **Ed25519 Signature Speed**: High-volume NFT trading generates many royalty-checked transfers; Ed25519's fast signature verification keeps transaction costs low even during peak trading.
- **BLS Aggregate Approvals**: Multi-party royalty split changes (requiring approval from all collaborators) can be approved with a single BLS-aggregated signature.

## Token Standards Used

- **BST-721**: The underlying NFT being wrapped and the wrapped token itself (extends BST-721 with transfer restrictions).
- **BST-20**: Royalty payment denomination. WBSLT and other BST-20 tokens supported for royalty payments.
- **BST-3525 (SFT)**: Royalty income positions tokenized as semi-fungible tokens with metadata (recipient share, total earned, pending claims).
- **BST-VC (Verifiable Credentials)**: Creator identity credentials for verification and provenance.

## Integration Points

- **Governance (0x0102)**: Governs approved payment tokens, minimum royalty floor rates, and unwrap policy. Also handles disputes about creator identity.
- **Escrow (0x0103)**: Accumulated royalties held in escrow until claimed by recipients.
- **BNS (0x0101)**: Collections and enforcer contracts registered under BNS names (e.g., `collection-name.royalty.basalt`).
- **SchemaRegistry (0x...1006)**: Creator identity credential schemas.
- **IssuerRegistry (0x...1007)**: Trusted issuers for creator credentials (art institutions, music labels, verified identity providers).
- **WBSLT (0x0100)**: Default royalty payment token.
- **StakingPool (0x0105)**: Protocol fees from royalty enforcement directed to staking rewards.

## Technical Sketch

```csharp
// ============================================================
// NftRoyaltyEnforcer -- Mandatory royalty enforcement for BST-721
// ============================================================

[BasaltContract(TypeId = 0x030B)]
public partial class NftRoyaltyEnforcer : SdkContract
{
    // --- Storage ---

    // Wrapped collection: original collection address
    private StorageValue<Address> _originalCollection;

    // Royalty configuration (immutable after initialization)
    private StorageValue<uint> _royaltyBps;                     // e.g., 500 = 5%
    private StorageValue<bool> _initialized;

    // Revenue split: recipient index => SplitRecipient
    private StorageMap<uint, SplitRecipient> _splitRecipients;
    private StorageValue<uint> _recipientCount;

    // Token mapping: wrappedTokenId => originalTokenId
    private StorageMap<ulong, ulong> _wrappedToOriginal;
    private StorageMap<ulong, ulong> _originalToWrapped;
    private StorageMap<ulong, bool> _isWrapped;

    // Token ownership: wrappedTokenId => owner
    private StorageMap<ulong, Address> _owners;

    // Token approvals: wrappedTokenId => approved address
    private StorageMap<ulong, Address> _approvals;

    // Operator approvals: owner => operator => approved
    private StorageMap<Address, StorageMap<Address, bool>> _operatorApprovals;

    // Royalty accounting: recipient => accumulated royalties
    private StorageMap<Address, UInt256> _pendingRoyalties;

    // Total royalties ever paid
    private StorageValue<UInt256> _totalRoyaltiesPaid;

    // Per-token transfer count and royalties earned
    private StorageMap<ulong, ulong> _transferCount;
    private StorageMap<ulong, UInt256> _tokenRoyalties;

    // Exempted addresses (no royalty on transfers to/from these)
    private StorageMap<Address, bool> _exemptAddresses;

    // Minimum sale price (anti-wash-trading)
    private StorageValue<UInt256> _minimumPrice;

    // Payment token (BST-20 address, or Address.Zero for native BST)
    private StorageValue<Address> _paymentToken;

    // Approved payment tokens
    private StorageMap<Address, bool> _approvedPaymentTokens;

    // Creator address
    private StorageValue<Address> _creator;

    // Unwrap enabled
    private StorageValue<bool> _unwrapEnabled;

    // Token supply tracking
    private StorageValue<ulong> _totalWrapped;

    // --- Data Structures ---

    public struct SplitRecipient
    {
        public Address Recipient;
        public uint ShareBps;          // Share of royalties in basis points
        public string Role;            // "creator", "collaborator", "treasury", etc.
    }

    // --- Initialization ---

    /// <summary>
    /// Initialize the royalty enforcer for a specific NFT collection.
    /// Can only be called once.
    /// </summary>
    public void Initialize(
        Address originalCollection,
        uint royaltyBps,
        Address[] recipients,
        uint[] shareBps,
        string[] roles,
        Address paymentToken,
        UInt256 minimumPrice,
        bool unwrapEnabled)
    {
        Require(!_initialized.Get(), "ALREADY_INITIALIZED");
        Require(royaltyBps > 0 && royaltyBps <= 5000, "INVALID_ROYALTY"); // Max 50%
        Require(recipients.Length == shareBps.Length, "LENGTH_MISMATCH");
        Require(recipients.Length == roles.Length, "LENGTH_MISMATCH");

        // Verify shares sum to 10000
        uint totalShares = 0;
        for (int i = 0; i < shareBps.Length; i++)
        {
            totalShares += shareBps[i];
        }
        Require(totalShares == 10000, "SHARES_MUST_SUM_TO_10000");

        _originalCollection.Set(originalCollection);
        _royaltyBps.Set(royaltyBps);
        _creator.Set(Context.Sender);
        _paymentToken.Set(paymentToken);
        _minimumPrice.Set(minimumPrice);
        _unwrapEnabled.Set(unwrapEnabled);

        for (int i = 0; i < recipients.Length; i++)
        {
            _splitRecipients.Set((uint)i, new SplitRecipient
            {
                Recipient = recipients[i],
                ShareBps = shareBps[i],
                Role = roles[i]
            });
        }
        _recipientCount.Set((uint)recipients.Length);

        _initialized.Set(true);
        EmitEvent("EnforcerInitialized", originalCollection, royaltyBps, Context.Sender);
    }

    // --- Wrapping ---

    /// <summary>
    /// Wrap a BST-721 token to enforce royalties on all future transfers.
    /// The original token is locked in this contract.
    /// </summary>
    public ulong Wrap(ulong originalTokenId)
    {
        Require(_initialized.Get(), "NOT_INITIALIZED");

        // Transfer original NFT from sender to this contract
        Address collection = _originalCollection.Get();
        TransferNftIn(collection, Context.Sender, originalTokenId);

        // Mint wrapped token (same ID for simplicity)
        ulong wrappedTokenId = originalTokenId;
        _wrappedToOriginal.Set(wrappedTokenId, originalTokenId);
        _originalToWrapped.Set(originalTokenId, wrappedTokenId);
        _isWrapped.Set(wrappedTokenId, true);
        _owners.Set(wrappedTokenId, Context.Sender);
        _totalWrapped.Set(_totalWrapped.Get() + 1);

        EmitEvent("TokenWrapped", originalTokenId, wrappedTokenId, Context.Sender);
        return wrappedTokenId;
    }

    /// <summary>
    /// Batch wrap multiple tokens from the same collection.
    /// </summary>
    public ulong[] WrapBatch(ulong[] originalTokenIds)
    {
        ulong[] wrappedIds = new ulong[originalTokenIds.Length];
        for (int i = 0; i < originalTokenIds.Length; i++)
        {
            wrappedIds[i] = Wrap(originalTokenIds[i]);
        }
        return wrappedIds;
    }

    /// <summary>
    /// Unwrap a token (returns original BST-721). Only if unwrap is enabled.
    /// </summary>
    public void Unwrap(ulong wrappedTokenId)
    {
        Require(_unwrapEnabled.Get(), "UNWRAP_DISABLED");
        Require(_owners.Get(wrappedTokenId) == Context.Sender, "NOT_OWNER");

        ulong originalTokenId = _wrappedToOriginal.Get(wrappedTokenId);

        // Burn wrapped token
        _isWrapped.Set(wrappedTokenId, false);
        _owners.Set(wrappedTokenId, Address.Zero);
        _totalWrapped.Set(_totalWrapped.Get() - 1);

        // Return original NFT
        Address collection = _originalCollection.Get();
        TransferNftOut(collection, Context.Sender, originalTokenId);

        EmitEvent("TokenUnwrapped", wrappedTokenId, originalTokenId, Context.Sender);
    }

    // --- Royalty-Enforced Transfers ---

    /// <summary>
    /// Transfer a wrapped token with mandatory royalty payment.
    /// The caller must include sufficient payment for the royalty.
    /// </summary>
    public void TransferWithRoyalty(
        ulong wrappedTokenId,
        Address from,
        Address to,
        UInt256 salePrice)
    {
        Require(_isWrapped.Get(wrappedTokenId), "NOT_WRAPPED");
        Require(_owners.Get(wrappedTokenId) == from, "NOT_OWNER");
        Require(
            Context.Sender == from ||
            _approvals.Get(wrappedTokenId) == Context.Sender ||
            _operatorApprovals.Get(from).Get(Context.Sender),
            "NOT_AUTHORIZED");

        // Check minimum price (anti-wash-trading)
        UInt256 minPrice = _minimumPrice.Get();
        Require(salePrice >= minPrice, "BELOW_MINIMUM_PRICE");

        // Check exemptions
        bool fromExempt = _exemptAddresses.Get(from);
        bool toExempt = _exemptAddresses.Get(to);

        if (!fromExempt && !toExempt)
        {
            // Calculate royalty
            uint royaltyBps = _royaltyBps.Get();
            UInt256 royaltyAmount = (salePrice * royaltyBps) / 10000;

            // Collect royalty payment
            Address paymentToken = _paymentToken.Get();
            if (paymentToken == Address.Zero)
            {
                Require(Context.TxValue >= royaltyAmount, "INSUFFICIENT_ROYALTY");
            }
            else
            {
                TransferTokenIn(paymentToken, Context.Sender, royaltyAmount);
            }

            // Distribute to split recipients
            DistributeRoyalties(royaltyAmount);

            // Update analytics
            _totalRoyaltiesPaid.Set(_totalRoyaltiesPaid.Get() + royaltyAmount);
            _tokenRoyalties.Set(wrappedTokenId,
                _tokenRoyalties.Get(wrappedTokenId) + royaltyAmount);
        }

        // Execute transfer
        _owners.Set(wrappedTokenId, to);
        _approvals.Set(wrappedTokenId, Address.Zero); // Clear approval
        _transferCount.Set(wrappedTokenId, _transferCount.Get(wrappedTokenId) + 1);

        EmitEvent("TransferWithRoyalty", wrappedTokenId, from, to, salePrice);
    }

    /// <summary>
    /// Transfer a wrapped token when sender/receiver is exempt.
    /// Still records the transfer but skips royalty.
    /// </summary>
    public void ExemptTransfer(ulong wrappedTokenId, Address from, Address to)
    {
        Require(_isWrapped.Get(wrappedTokenId), "NOT_WRAPPED");
        Require(_owners.Get(wrappedTokenId) == from, "NOT_OWNER");
        Require(
            Context.Sender == from ||
            _approvals.Get(wrappedTokenId) == Context.Sender ||
            _operatorApprovals.Get(from).Get(Context.Sender),
            "NOT_AUTHORIZED");
        Require(_exemptAddresses.Get(from) || _exemptAddresses.Get(to),
                "NOT_EXEMPT");

        _owners.Set(wrappedTokenId, to);
        _approvals.Set(wrappedTokenId, Address.Zero);
        _transferCount.Set(wrappedTokenId, _transferCount.Get(wrappedTokenId) + 1);

        EmitEvent("ExemptTransfer", wrappedTokenId, from, to);
    }

    // --- Royalty Distribution ---

    /// <summary>
    /// Claim accumulated royalties. Any recipient can call.
    /// </summary>
    public UInt256 ClaimRoyalties()
    {
        UInt256 pending = _pendingRoyalties.Get(Context.Sender);
        Require(!pending.IsZero, "NO_PENDING_ROYALTIES");

        _pendingRoyalties.Set(Context.Sender, UInt256.Zero);

        Address paymentToken = _paymentToken.Get();
        if (paymentToken == Address.Zero)
        {
            Context.TransferNative(Context.Sender, pending);
        }
        else
        {
            TransferTokenOut(paymentToken, Context.Sender, pending);
        }

        EmitEvent("RoyaltiesClaimed", Context.Sender, pending);
        return pending;
    }

    // --- BST-721 Interface ---

    public Address OwnerOf(ulong tokenId) => _owners.Get(tokenId);

    public void Approve(Address to, ulong tokenId)
    {
        Require(_owners.Get(tokenId) == Context.Sender, "NOT_OWNER");
        _approvals.Set(tokenId, to);
        EmitEvent("Approval", Context.Sender, to, tokenId);
    }

    public void SetApprovalForAll(Address operator_, bool approved)
    {
        _operatorApprovals.Get(Context.Sender).Set(operator_, approved);
        EmitEvent("ApprovalForAll", Context.Sender, operator_, approved);
    }

    public Address GetApproved(ulong tokenId) => _approvals.Get(tokenId);

    public bool IsApprovedForAll(Address owner, Address operator_)
        => _operatorApprovals.Get(owner).Get(operator_);

    // --- EIP-2981 Compatible Interface ---

    /// <summary>
    /// Returns royalty information for a given sale price.
    /// Compatible with ERC-2981 royaltyInfo.
    /// </summary>
    public (Address receiver, UInt256 royaltyAmount) RoyaltyInfo(
        ulong tokenId, UInt256 salePrice)
    {
        UInt256 royalty = (salePrice * _royaltyBps.Get()) / 10000;
        // Primary recipient is the first split recipient (usually the creator)
        var primaryRecipient = _splitRecipients.Get(0);
        return (primaryRecipient.Recipient, royalty);
    }

    // --- Exemption Management ---

    /// <summary>
    /// Add or remove an exempted address. Creator-only.
    /// </summary>
    public void SetExemption(Address addr, bool exempt)
    {
        Require(Context.Sender == _creator.Get(), "NOT_CREATOR");
        _exemptAddresses.Set(addr, exempt);
        EmitEvent("ExemptionUpdated", addr, exempt);
    }

    // --- Query Methods ---

    public uint GetRoyaltyBps() => _royaltyBps.Get();
    public Address GetCreator() => _creator.Get();
    public Address GetOriginalCollection() => _originalCollection.Get();
    public ulong GetTotalWrapped() => _totalWrapped.Get();
    public UInt256 GetTotalRoyaltiesPaid() => _totalRoyaltiesPaid.Get();
    public UInt256 GetPendingRoyalties(Address recipient) => _pendingRoyalties.Get(recipient);
    public ulong GetTransferCount(ulong tokenId) => _transferCount.Get(tokenId);
    public UInt256 GetTokenRoyalties(ulong tokenId) => _tokenRoyalties.Get(tokenId);
    public uint GetRecipientCount() => _recipientCount.Get();
    public SplitRecipient GetRecipient(uint index) => _splitRecipients.Get(index);
    public bool IsExempt(Address addr) => _exemptAddresses.Get(addr);

    // --- Internal Helpers ---

    private void DistributeRoyalties(UInt256 totalRoyalty)
    {
        uint count = _recipientCount.Get();
        for (uint i = 0; i < count; i++)
        {
            var recipient = _splitRecipients.Get(i);
            UInt256 share = (totalRoyalty * recipient.ShareBps) / 10000;
            _pendingRoyalties.Set(recipient.Recipient,
                _pendingRoyalties.Get(recipient.Recipient) + share);
        }
    }

    private void TransferNftIn(Address collection, Address from, ulong tokenId) { /* ... */ }
    private void TransferNftOut(Address collection, Address to, ulong tokenId) { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
}
```

## Complexity

**Medium** -- The core wrapping/transfer mechanism is straightforward: wrap an NFT, enforce royalty payment on every transfer, distribute to split recipients. The primary complexity lies in: ensuring the transfer hook cannot be bypassed (all transfer paths must route through `TransferWithRoyalty`), handling edge cases around exemptions and minimum prices, correct split distribution math with no rounding losses, and interaction with marketplace contracts. The optional unwrap mechanism and multi-payment-token support add moderate complexity. Anti-wash-trading (minimum price enforcement) requires careful consideration to avoid legitimate use case blockage.

## Priority

**P1** -- Royalty enforcement is critical creator economy infrastructure. The NFT space has been plagued by royalty evasion on EVM chains, driving creators away. Basalt's enforced royalty system is a significant competitive advantage and must be available alongside the NFT marketplace. It directly impacts creator adoption and the sustainability of the NFT ecosystem on Basalt.
