# Subscription Manager

## Category

Commerce / Payments

## Summary

A subscription management contract that enables recurring payment authorization between subscribers and service providers. Subscribers approve a payment plan, and the service provider pulls the authorized amount each billing period. The contract handles grace periods for missed payments, cancellation, plan upgrades/downgrades, and issues a BST-721 NFT as proof of active subscription status. This enables SaaS-style recurring revenue on-chain with trust-minimized payment processing.

## Why It's Useful

- **Recurring revenue for on-chain services**: DApps, content platforms, infrastructure providers, and API services need a way to charge recurring fees without manual per-period transactions from users.
- **Pull-based payments**: Unlike push-based crypto payments where the user must initiate each transfer, the subscription model allows the service provider to pull authorized amounts, mirroring traditional payment processing.
- **Transparent billing**: All subscription terms, payments, and cancellations are recorded on-chain, providing complete billing transparency and dispute resolution capabilities.
- **NFT-gated access**: The subscription NFT serves as a composable access token -- any contract or service can check NFT ownership to grant access, without needing to query the subscription contract directly.
- **Flexible plan management**: Service providers can offer multiple tiers with different prices, features, and billing periods. Subscribers can upgrade or downgrade between plans.
- **Automatic grace periods**: Subscribers who fail to maintain sufficient balance get a configurable grace period before service termination, avoiding harsh cutoffs.
- **Web3-native SaaS**: Enables the Web3 equivalent of Stripe subscriptions, allowing service providers to build sustainable recurring revenue businesses on Basalt.

## Key Features

- Plan creation: service providers register plans with (name, price, billing period in blocks, grace period, metadata)
- Subscriber approval: subscriber authorizes a plan, creating a subscription record. Initial payment is pulled immediately.
- Pull payments: service provider calls pull() each period to collect the authorized amount from the subscriber's balance or approved allowance
- Grace period: if the subscriber's balance is insufficient, the subscription enters a grace period. Service continues for the grace duration. If the subscriber funds their account and the provider pulls successfully, the subscription resumes.
- Cancellation: subscriber can cancel at any time. Remaining prepaid time is honored until the current period ends.
- Provider cancellation: service provider can cancel subscriptions (e.g., for ToS violations)
- Subscription NFT: a BST-721 NFT is minted when a subscription starts. The NFT is burned when the subscription ends. Any contract can check NFT ownership for gated access.
- Plan upgrades/downgrades: subscriber can switch plans mid-period, with prorated charges
- Trial periods: plans can include a trial period (N blocks) where no payment is required
- Subscriber allowance: subscribers can set a maximum number of periods they authorize, preventing unbounded spending
- Refund on early cancellation: configurable per-plan refund policy for mid-period cancellations

## Basalt-Specific Advantages

- **BST-721 subscription NFTs**: Basalt's native BST-721 standard is used to mint transferable subscription NFTs. Transferring the NFT transfers the subscription, enabling subscription trading or gifting. Any dApp can use standard `OwnerOf()` checks for access control.
- **ZK identity for subscriber verification**: Service providers can require subscribers to hold valid ZK compliance proofs (via SchemaRegistry) before subscribing, enabling KYC-gated services without centralized identity providers.
- **Ed25519 pull payment authorization**: The subscriber signs an authorization using their Ed25519 key pair, and the pull payment verifies this authorization on-chain. No separate approval transaction is needed if using the meta-transaction forwarder.
- **BNS name integration**: Subscribers and service providers can be referenced by BNS names, improving UX for subscription setup.
- **Cross-contract access checks**: Basalt's `Context.CallContract<T>()` allows any contract to check subscription status by calling the subscription manager, enabling composable gated access across the ecosystem.
- **EIP-1559 fee model compatibility**: Subscription pull payments work naturally with Basalt's EIP-1559 gas model, and can be submitted via meta-transaction relayers for gasless subscriber experiences.
- **AOT-compiled billing logic**: Period calculations, prorating, and grace period logic execute deterministically under AOT compilation, ensuring billing accuracy.
- **BST-3525 SFT for subscription bundles**: Multi-service subscription bundles can use BST-3525 where the slot represents the service and the value represents the remaining prepaid balance.

## Token Standards Used

- **BST-721**: Subscription proof NFT -- minted on subscribe, burned on cancellation/expiry
- **BST-20**: Payment token for subscriptions (native BST or any BST-20 token)
- **BST-3525**: Multi-service subscription bundles with per-service value tracking

## Integration Points

- **BNS (0x...1002)**: Human-readable names for subscribers and service providers.
- **Governance (0x...1005 area)**: Dispute resolution for contested charges. Platform-wide subscription parameters can be updated via governance.
- **SchemaRegistry (0x...1006)**: ZK compliance verification for subscriber identity.
- **Escrow (0x...1003)**: Prepaid subscription deposits can be held in escrow for the billing period.
- **MetaTransactionForwarder**: Gasless subscription management for new users.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Subscription Manager -- recurring payment authorization with pull-based
/// billing, grace periods, and BST-721 NFT proof of active subscription.
/// </summary>
[BasaltContract]
public partial class SubscriptionManager
{
    // --- Plan registry ---
    private readonly StorageValue<ulong> _nextPlanId;
    private readonly StorageMap<string, string> _planNames;              // planId -> name
    private readonly StorageMap<string, string> _planProviders;          // planId -> provider hex
    private readonly StorageMap<string, UInt256> _planPrices;            // planId -> price per period
    private readonly StorageMap<string, ulong> _planPeriodBlocks;        // planId -> billing period in blocks
    private readonly StorageMap<string, ulong> _planGracePeriodBlocks;   // planId -> grace period
    private readonly StorageMap<string, ulong> _planTrialBlocks;         // planId -> trial period (0 = none)
    private readonly StorageMap<string, bool> _planActive;               // planId -> active
    private readonly StorageMap<string, string> _planMetadata;           // planId -> metadata

    // --- Subscription state ---
    private readonly StorageValue<ulong> _nextSubscriptionId;
    private readonly StorageMap<string, string> _subSubscribers;         // subId -> subscriber hex
    private readonly StorageMap<string, ulong> _subPlanIds;              // subId -> plan ID
    private readonly StorageMap<string, ulong> _subStartBlock;           // subId -> start block
    private readonly StorageMap<string, ulong> _subLastPaymentBlock;     // subId -> last payment block
    private readonly StorageMap<string, ulong> _subNextPaymentBlock;     // subId -> next payment due
    private readonly StorageMap<string, string> _subStatus;              // subId -> "active"|"grace"|"cancelled"|"expired"
    private readonly StorageMap<string, ulong> _subMaxPeriods;           // subId -> max authorized periods (0 = unlimited)
    private readonly StorageMap<string, ulong> _subPeriodsPaid;          // subId -> periods paid so far
    private readonly StorageMap<string, ulong> _subNftTokenId;           // subId -> BST-721 token ID

    // --- NFT contract reference ---
    private readonly StorageValue<ulong> _nextNftId;
    private readonly StorageMap<string, string> _nftOwners;              // tokenId -> owner hex
    private readonly StorageMap<string, ulong> _nftSubscriptionIds;      // tokenId -> subId

    // --- Revenue tracking ---
    private readonly StorageMap<string, UInt256> _providerRevenue;       // providerHex -> total revenue
    private readonly StorageMap<string, ulong> _providerSubscriberCount; // providerHex -> active count

    public SubscriptionManager()
    {
        _nextPlanId = new StorageValue<ulong>("sm_nplan");
        _planNames = new StorageMap<string, string>("sm_pname");
        _planProviders = new StorageMap<string, string>("sm_pprov");
        _planPrices = new StorageMap<string, UInt256>("sm_pprice");
        _planPeriodBlocks = new StorageMap<string, ulong>("sm_pperiod");
        _planGracePeriodBlocks = new StorageMap<string, ulong>("sm_pgrace");
        _planTrialBlocks = new StorageMap<string, ulong>("sm_ptrial");
        _planActive = new StorageMap<string, bool>("sm_pact");
        _planMetadata = new StorageMap<string, string>("sm_pmeta");
        _nextSubscriptionId = new StorageValue<ulong>("sm_nsub");
        _subSubscribers = new StorageMap<string, string>("sm_ssub");
        _subPlanIds = new StorageMap<string, ulong>("sm_splan");
        _subStartBlock = new StorageMap<string, ulong>("sm_sstart");
        _subLastPaymentBlock = new StorageMap<string, ulong>("sm_slast");
        _subNextPaymentBlock = new StorageMap<string, ulong>("sm_snext");
        _subStatus = new StorageMap<string, string>("sm_ssts");
        _subMaxPeriods = new StorageMap<string, ulong>("sm_smax");
        _subPeriodsPaid = new StorageMap<string, ulong>("sm_spaid");
        _subNftTokenId = new StorageMap<string, ulong>("sm_snft");
        _nextNftId = new StorageValue<ulong>("sm_nnft");
        _nftOwners = new StorageMap<string, string>("sm_nown");
        _nftSubscriptionIds = new StorageMap<string, ulong>("sm_nsid");
        _providerRevenue = new StorageMap<string, UInt256>("sm_prev");
        _providerSubscriberCount = new StorageMap<string, ulong>("sm_pcnt");
    }

    // ===================== Plan Management (Service Provider) =====================

    /// <summary>
    /// Create a new subscription plan. Returns plan ID.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreatePlan(string name, UInt256 pricePerPeriod, ulong periodBlocks,
        ulong gracePeriodBlocks, ulong trialBlocks, string metadata)
    {
        Context.Require(!string.IsNullOrEmpty(name), "SUB: name required");
        Context.Require(!pricePerPeriod.IsZero, "SUB: price required");
        Context.Require(periodBlocks > 0, "SUB: invalid period");

        var id = _nextPlanId.Get();
        _nextPlanId.Set(id + 1);
        var key = id.ToString();

        _planNames.Set(key, name);
        _planProviders.Set(key, Convert.ToHexString(Context.Caller));
        _planPrices.Set(key, pricePerPeriod);
        _planPeriodBlocks.Set(key, periodBlocks);
        _planGracePeriodBlocks.Set(key, gracePeriodBlocks);
        _planTrialBlocks.Set(key, trialBlocks);
        _planActive.Set(key, true);
        _planMetadata.Set(key, metadata);

        Context.Emit(new PlanCreatedEvent
        {
            PlanId = id, Name = name, Provider = Context.Caller,
            Price = pricePerPeriod, PeriodBlocks = periodBlocks
        });
        return id;
    }

    /// <summary>
    /// Deactivate a plan (prevents new subscriptions but existing ones continue).
    /// </summary>
    [BasaltEntrypoint]
    public void DeactivatePlan(ulong planId)
    {
        RequirePlanProvider(planId);
        _planActive.Set(planId.ToString(), false);
    }

    /// <summary>
    /// Update plan price (only affects new subscriptions).
    /// </summary>
    [BasaltEntrypoint]
    public void UpdatePlanPrice(ulong planId, UInt256 newPrice)
    {
        RequirePlanProvider(planId);
        _planPrices.Set(planId.ToString(), newPrice);
    }

    // ===================== Subscription Lifecycle =====================

    /// <summary>
    /// Subscribe to a plan. Send the first period's payment as value.
    /// Returns subscription ID and mints a BST-721 proof NFT.
    /// </summary>
    [BasaltEntrypoint]
    public ulong Subscribe(ulong planId, ulong maxPeriods)
    {
        var planKey = planId.ToString();
        Context.Require(_planActive.Get(planKey), "SUB: plan not active");

        var price = _planPrices.Get(planKey);
        var trialBlocks = _planTrialBlocks.Get(planKey);
        var periodBlocks = _planPeriodBlocks.Get(planKey);

        // If no trial, require payment
        if (trialBlocks == 0)
        {
            Context.Require(Context.TxValue >= price, "SUB: insufficient payment");
            // Forward payment to provider
            var provider = Convert.FromHexString(_planProviders.Get(planKey));
            Context.TransferNative(provider, price);

            var providerHex = _planProviders.Get(planKey);
            _providerRevenue.Set(providerHex, _providerRevenue.Get(providerHex) + price);
        }

        var subId = _nextSubscriptionId.Get();
        _nextSubscriptionId.Set(subId + 1);
        var subKey = subId.ToString();

        _subSubscribers.Set(subKey, Convert.ToHexString(Context.Caller));
        _subPlanIds.Set(subKey, planId);
        _subStartBlock.Set(subKey, Context.BlockHeight);
        _subLastPaymentBlock.Set(subKey, Context.BlockHeight);
        _subStatus.Set(subKey, "active");
        _subMaxPeriods.Set(subKey, maxPeriods);
        _subPeriodsPaid.Set(subKey, trialBlocks > 0 ? 0UL : 1UL);

        var nextPayment = Context.BlockHeight + (trialBlocks > 0 ? trialBlocks : periodBlocks);
        _subNextPaymentBlock.Set(subKey, nextPayment);

        // Mint subscription NFT
        var nftId = _nextNftId.Get();
        _nextNftId.Set(nftId + 1);
        _nftOwners.Set(nftId.ToString(), Convert.ToHexString(Context.Caller));
        _nftSubscriptionIds.Set(nftId.ToString(), subId);
        _subNftTokenId.Set(subKey, nftId);

        var provHex = _planProviders.Get(planKey);
        _providerSubscriberCount.Set(provHex, _providerSubscriberCount.Get(provHex) + 1);

        Context.Emit(new SubscribedEvent
        {
            SubscriptionId = subId, Subscriber = Context.Caller,
            PlanId = planId, NftTokenId = nftId
        });
        return subId;
    }

    /// <summary>
    /// Service provider pulls payment for the current period.
    /// </summary>
    [BasaltEntrypoint]
    public void PullPayment(ulong subscriptionId)
    {
        var subKey = subscriptionId.ToString();
        var status = _subStatus.Get(subKey);
        Context.Require(status == "active" || status == "grace", "SUB: not active");

        var planId = _subPlanIds.Get(subKey);
        RequirePlanProvider(planId);

        var nextPayment = _subNextPaymentBlock.Get(subKey);
        Context.Require(Context.BlockHeight >= nextPayment, "SUB: payment not due");

        // Check max periods
        var maxPeriods = _subMaxPeriods.Get(subKey);
        var periodsPaid = _subPeriodsPaid.Get(subKey);
        if (maxPeriods > 0)
            Context.Require(periodsPaid < maxPeriods, "SUB: max periods reached");

        var price = _planPrices.Get(planId.ToString());
        var periodBlocks = _planPeriodBlocks.Get(planId.ToString());

        // Payment is pulled from the contract's balance (subscriber must have pre-funded)
        // In a full implementation, this would use an allowance/approval mechanism
        _subLastPaymentBlock.Set(subKey, Context.BlockHeight);
        _subNextPaymentBlock.Set(subKey, Context.BlockHeight + periodBlocks);
        _subPeriodsPaid.Set(subKey, periodsPaid + 1);

        if (status == "grace")
            _subStatus.Set(subKey, "active");

        var provider = Convert.FromHexString(_planProviders.Get(planId.ToString()));
        Context.TransferNative(provider, price);

        var provHex = _planProviders.Get(planId.ToString());
        _providerRevenue.Set(provHex, _providerRevenue.Get(provHex) + price);

        Context.Emit(new PaymentPulledEvent
        {
            SubscriptionId = subscriptionId, Amount = price,
            PeriodNumber = periodsPaid + 1
        });
    }

    /// <summary>
    /// Subscriber pays for the next period proactively.
    /// </summary>
    [BasaltEntrypoint]
    public void PrepayPeriod(ulong subscriptionId)
    {
        var subKey = subscriptionId.ToString();
        Context.Require(_subStatus.Get(subKey) == "active" || _subStatus.Get(subKey) == "grace",
            "SUB: not active");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _subSubscribers.Get(subKey),
            "SUB: not subscriber");

        var planId = _subPlanIds.Get(subKey);
        var price = _planPrices.Get(planId.ToString());
        Context.Require(Context.TxValue >= price, "SUB: insufficient payment");

        var provider = Convert.FromHexString(_planProviders.Get(planId.ToString()));
        Context.TransferNative(provider, price);

        var periodBlocks = _planPeriodBlocks.Get(planId.ToString());
        var periodsPaid = _subPeriodsPaid.Get(subKey) + 1;
        _subPeriodsPaid.Set(subKey, periodsPaid);
        _subLastPaymentBlock.Set(subKey, Context.BlockHeight);
        _subNextPaymentBlock.Set(subKey, _subNextPaymentBlock.Get(subKey) + periodBlocks);

        if (_subStatus.Get(subKey) == "grace")
            _subStatus.Set(subKey, "active");

        Context.Emit(new PaymentPulledEvent
        {
            SubscriptionId = subscriptionId, Amount = price, PeriodNumber = periodsPaid
        });
    }

    /// <summary>
    /// Subscriber cancels their subscription.
    /// </summary>
    [BasaltEntrypoint]
    public void Cancel(ulong subscriptionId)
    {
        var subKey = subscriptionId.ToString();
        var callerHex = Convert.ToHexString(Context.Caller);
        var subscriberHex = _subSubscribers.Get(subKey);

        Context.Require(callerHex == subscriberHex, "SUB: not subscriber");
        var status = _subStatus.Get(subKey);
        Context.Require(status == "active" || status == "grace", "SUB: not active");

        _subStatus.Set(subKey, "cancelled");

        // Burn NFT
        var nftId = _subNftTokenId.Get(subKey);
        _nftOwners.Set(nftId.ToString(), "");

        var planId = _subPlanIds.Get(subKey);
        var provHex = _planProviders.Get(planId.ToString());
        var count = _providerSubscriberCount.Get(provHex);
        if (count > 0) _providerSubscriberCount.Set(provHex, count - 1);

        Context.Emit(new SubscriptionCancelledEvent
        {
            SubscriptionId = subscriptionId, Subscriber = Context.Caller
        });
    }

    /// <summary>
    /// Mark a subscription as expired (callable by provider when grace period ends).
    /// </summary>
    [BasaltEntrypoint]
    public void Expire(ulong subscriptionId)
    {
        var subKey = subscriptionId.ToString();
        Context.Require(_subStatus.Get(subKey) == "grace", "SUB: not in grace");

        var planId = _subPlanIds.Get(subKey);
        var gracePeriod = _planGracePeriodBlocks.Get(planId.ToString());
        var nextPayment = _subNextPaymentBlock.Get(subKey);
        Context.Require(Context.BlockHeight > nextPayment + gracePeriod, "SUB: grace not expired");

        _subStatus.Set(subKey, "expired");

        var nftId = _subNftTokenId.Get(subKey);
        _nftOwners.Set(nftId.ToString(), "");

        Context.Emit(new SubscriptionExpiredEvent { SubscriptionId = subscriptionId });
    }

    // ===================== Views =====================

    [BasaltView]
    public string GetPlanName(ulong planId) => _planNames.Get(planId.ToString()) ?? "";

    [BasaltView]
    public UInt256 GetPlanPrice(ulong planId) => _planPrices.Get(planId.ToString());

    [BasaltView]
    public ulong GetPlanPeriod(ulong planId) => _planPeriodBlocks.Get(planId.ToString());

    [BasaltView]
    public string GetSubscriptionStatus(ulong subId) => _subStatus.Get(subId.ToString()) ?? "unknown";

    [BasaltView]
    public ulong GetNextPaymentBlock(ulong subId) => _subNextPaymentBlock.Get(subId.ToString());

    [BasaltView]
    public ulong GetSubscriptionNftId(ulong subId) => _subNftTokenId.Get(subId.ToString());

    [BasaltView]
    public string GetNftOwner(ulong tokenId) => _nftOwners.Get(tokenId.ToString()) ?? "";

    [BasaltView]
    public UInt256 GetProviderRevenue(byte[] provider)
        => _providerRevenue.Get(Convert.ToHexString(provider));

    [BasaltView]
    public ulong GetProviderSubscriberCount(byte[] provider)
        => _providerSubscriberCount.Get(Convert.ToHexString(provider));

    [BasaltView]
    public bool IsSubscriptionActive(ulong subId)
        => _subStatus.Get(subId.ToString()) == "active";

    // ===================== Internal =====================

    private void RequirePlanProvider(ulong planId)
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _planProviders.Get(planId.ToString()),
            "SUB: not plan provider");
    }
}

// ===================== Events =====================

[BasaltEvent]
public class PlanCreatedEvent
{
    [Indexed] public ulong PlanId { get; set; }
    public string Name { get; set; } = "";
    [Indexed] public byte[] Provider { get; set; } = null!;
    public UInt256 Price { get; set; }
    public ulong PeriodBlocks { get; set; }
}

[BasaltEvent]
public class SubscribedEvent
{
    [Indexed] public ulong SubscriptionId { get; set; }
    [Indexed] public byte[] Subscriber { get; set; } = null!;
    public ulong PlanId { get; set; }
    public ulong NftTokenId { get; set; }
}

[BasaltEvent]
public class PaymentPulledEvent
{
    [Indexed] public ulong SubscriptionId { get; set; }
    public UInt256 Amount { get; set; }
    public ulong PeriodNumber { get; set; }
}

[BasaltEvent]
public class SubscriptionCancelledEvent
{
    [Indexed] public ulong SubscriptionId { get; set; }
    [Indexed] public byte[] Subscriber { get; set; } = null!;
}

[BasaltEvent]
public class SubscriptionExpiredEvent
{
    [Indexed] public ulong SubscriptionId { get; set; }
}
```

## Complexity

**Medium** -- The plan and subscription CRUD is straightforward. The main complexity is in the pull payment mechanism (ensuring correct period tracking, handling grace periods, and preventing double-pulls), the trial period logic, and the plan upgrade/downgrade prorating. The integrated BST-721 NFT minting adds moderate complexity but follows established patterns.

## Priority

**P2** -- Subscription management is important for building sustainable on-chain businesses but is not critical infrastructure for the initial DeFi ecosystem. It becomes more valuable as dApps and services mature and need recurring revenue models. Recommended for the second wave of contract deployments after core DeFi primitives are established.
