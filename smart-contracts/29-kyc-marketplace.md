# Decentralized KYC Marketplace

## Category

Compliance / Identity Infrastructure

## Summary

A decentralized marketplace where KYC providers stake collateral via IssuerRegistry, issue BST-VC verifiable credentials to users, and compete on price and quality. Users pay once for KYC verification and reuse the resulting ZK proof across every dApp on Basalt without re-verifying. Revenue is split between providers and the protocol, with slashing for fraudulent attestations providing economic security guarantees.

This contract is the **flagship compliance use case** for Basalt, demonstrating how native ZK compliance, verifiable credentials, and collateral-backed trust tiers create a KYC experience that is superior to anything possible on chains without built-in identity infrastructure.

## Why It's Useful

- **Eliminates redundant KYC**: Users in traditional finance and DeFi must re-verify identity for every new platform. A single KYC credential reusable across all Basalt dApps saves users time and providers money.
- **Creates a competitive provider market**: Multiple KYC providers compete on verification speed, coverage, cost, and quality, driving prices down and service quality up.
- **Enables regulatory compliance for DeFi**: Protocols that need to comply with AML/CFT regulations can gate access via KYC marketplace credentials without building their own identity stack.
- **Protects user privacy**: ZK proofs allow users to prove "I am KYC-verified by an accredited provider" without revealing their personal data to every dApp.
- **Economic accountability**: Providers stake collateral that can be slashed for issuing fraudulent credentials, aligning incentives and discouraging bad actors.
- **Protocol revenue**: A portion of every KYC fee flows to the protocol treasury, creating sustainable revenue for the Basalt ecosystem.

## Key Features

- Provider registration with tiered collateral requirements (minimum stake scales with tier level)
- Per-provider service listings with pricing, supported jurisdictions, verification levels (Basic, Enhanced, Full)
- User-initiated KYC purchase flow: select provider, pay fee, receive BST-VC credential
- ZK proof generation helper: prove KYC status without revealing provider, date, or identity details
- Revenue splitting: configurable protocol fee (default 10%) deducted from provider payments
- Dispute resolution: users or third parties can file disputes against credentials with evidence
- Slashing mechanism: governance-triggered or automated slashing for proven fraudulent attestations
- Provider reputation tracking: success rate, dispute history, average verification time
- Credential renewal: time-limited credentials with on-chain renewal flow
- Multi-jurisdiction support: providers declare supported country codes and verification standards
- Bulk verification discounts: providers can offer tiered pricing for enterprise clients
- Provider deactivation and graceful wind-down with pending credential fulfillment

## Basalt-Specific Advantages

- **Native IssuerRegistry integration**: Providers register directly in Basalt's on-chain IssuerRegistry (0x...1007) with trust tiers (0-3) and collateral staking, eliminating the need for a separate trust framework.
- **BST-VC credentials**: KYC attestations are issued as BST-VC verifiable credentials with full lifecycle management (active, suspended, revoked), stored off-chain with on-chain hash anchoring.
- **ZK compliance layer**: Basalt's built-in ZkComplianceVerifier and Groth16 proof verification allow users to prove KYC status via ZK proofs without any additional infrastructure. The SchemaRegistry stores verification keys for KYC schemas.
- **Nullifier-based anti-correlation**: Users can prove they hold a valid KYC credential without linking their identity across different dApps, using Basalt's native nullifier infrastructure to prevent double-use tracking.
- **AOT-compiled execution**: The marketplace contract runs as a source-generated, AOT-compiled SDK contract with zero reflection overhead, ensuring deterministic gas costs and fast execution.
- **Ed25519 signature verification**: Provider attestations are signed with Ed25519, which is Basalt's native signature scheme, requiring no additional cryptographic libraries.
- **Cross-contract composability**: The marketplace interacts with IssuerRegistry, SchemaRegistry, BSTVCRegistry, StakingPool, and Escrow through Basalt's type-safe cross-contract call mechanism.
- **Pedersen commitment support**: For premium KYC tiers, payment amounts can be hidden via Pedersen commitments, protecting commercial terms from competitors.

## Token Standards Used

- **BST-VC** (BSTVCRegistry, type 0x0007): Core credential format for KYC attestations. Each verification produces a BST-VC credential with schema-defined fields, issuer signature, and expiry.
- **BST-20** (optional): KYC marketplace could accept BST-20 stablecoin payments in addition to native BST.

## Integration Points

- **IssuerRegistry** (0x...1007): Providers must be registered as Tier 2 (accredited) or Tier 3 (sovereign) issuers with active status and sufficient collateral. The marketplace validates provider status before allowing service listings.
- **SchemaRegistry** (0x...1006): KYC credential schemas (BasicKYC, EnhancedKYC, FullKYC) are registered here with Groth16 verification keys. The marketplace references schema IDs when creating credentials.
- **BSTVCRegistry** (deployed instance): Credentials are issued through BSTVCRegistry.IssueCredential(), and the marketplace tracks credential hashes for dispute resolution.
- **Escrow** (0x...1003): User payments are held in escrow during the verification window. Released to the provider upon successful credential issuance, refunded on timeout or dispute.
- **Governance** (0x...1002): Protocol fee changes, slashing thresholds, and provider tier requirements can be modified through governance proposals.
- **StakingPool** (0x...1005): Provider reputation scores can serve as multipliers for staking rewards, incentivizing high-quality KYC services.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Decentralized KYC marketplace: providers stake collateral, users purchase
/// KYC credentials, ZK proofs enable cross-dApp reuse. Type ID: 0x0108.
/// </summary>
[BasaltContract]
public partial class KycMarketplace
{
    // --- Storage ---

    // Admin
    private readonly StorageMap<string, string> _admin;  // "admin" -> admin hex

    // Provider listings
    private readonly StorageMap<string, bool> _providerActive;        // providerHex -> active
    private readonly StorageMap<string, UInt256> _providerFeeBasic;   // providerHex -> fee for basic KYC
    private readonly StorageMap<string, UInt256> _providerFeeEnhanced;
    private readonly StorageMap<string, UInt256> _providerFeeFull;
    private readonly StorageMap<string, string> _providerJurisdictions; // providerHex -> comma-separated country codes
    private readonly StorageMap<string, ulong> _providerSuccessCount;
    private readonly StorageMap<string, ulong> _providerDisputeCount;
    private readonly StorageMap<string, ulong> _providerTotalRequests;

    // KYC requests
    private readonly StorageValue<ulong> _nextRequestId;
    private readonly StorageMap<string, string> _requestUser;         // requestId -> userHex
    private readonly StorageMap<string, string> _requestProvider;     // requestId -> providerHex
    private readonly StorageMap<string, byte> _requestLevel;          // requestId -> 1=Basic,2=Enhanced,3=Full
    private readonly StorageMap<string, UInt256> _requestFee;         // requestId -> fee paid
    private readonly StorageMap<string, string> _requestStatus;       // requestId -> pending/completed/disputed/refunded
    private readonly StorageMap<string, ulong> _requestEscrowId;      // requestId -> Escrow contract escrow ID
    private readonly StorageMap<string, long> _requestDeadline;       // requestId -> completion deadline timestamp
    private readonly StorageMap<string, string> _requestCredHash;     // requestId -> credential hash hex (after completion)

    // Disputes
    private readonly StorageValue<ulong> _nextDisputeId;
    private readonly StorageMap<string, ulong> _disputeRequestId;     // disputeId -> requestId
    private readonly StorageMap<string, string> _disputeReason;       // disputeId -> reason
    private readonly StorageMap<string, string> _disputeStatus;       // disputeId -> open/resolved/slashed
    private readonly StorageMap<string, string> _disputeFiler;        // disputeId -> filerHex

    // Protocol configuration
    private readonly StorageValue<ulong> _protocolFeeBps;             // basis points (default 1000 = 10%)
    private readonly StorageValue<UInt256> _minProviderStake;         // minimum collateral to list
    private readonly StorageValue<long> _defaultDeadlineSeconds;      // default verification window

    // Revenue tracking
    private readonly StorageValue<UInt256> _protocolRevenue;
    private readonly StorageMap<string, UInt256> _providerRevenue;    // providerHex -> accumulated revenue

    // System contract addresses
    private readonly byte[] _issuerRegistryAddress;   // 0x...1007
    private readonly byte[] _escrowAddress;           // 0x...1003
    private readonly byte[] _vcRegistryAddress;       // deployed BSTVCRegistry instance

    public KycMarketplace(
        ulong protocolFeeBps = 1000,
        UInt256 minProviderStake = default,
        long defaultDeadlineSeconds = 86400)
    {
        if (minProviderStake.IsZero) minProviderStake = new UInt256(10000);

        _admin = new StorageMap<string, string>("kyc_admin");
        _providerActive = new StorageMap<string, bool>("kyc_pactive");
        _providerFeeBasic = new StorageMap<string, UInt256>("kyc_pfee_b");
        _providerFeeEnhanced = new StorageMap<string, UInt256>("kyc_pfee_e");
        _providerFeeFull = new StorageMap<string, UInt256>("kyc_pfee_f");
        _providerJurisdictions = new StorageMap<string, string>("kyc_pjuris");
        _providerSuccessCount = new StorageMap<string, ulong>("kyc_psuccess");
        _providerDisputeCount = new StorageMap<string, ulong>("kyc_pdispute");
        _providerTotalRequests = new StorageMap<string, ulong>("kyc_ptotal");

        _nextRequestId = new StorageValue<ulong>("kyc_nextreq");
        _requestUser = new StorageMap<string, string>("kyc_ruser");
        _requestProvider = new StorageMap<string, string>("kyc_rprov");
        _requestLevel = new StorageMap<string, byte>("kyc_rlevel");
        _requestFee = new StorageMap<string, UInt256>("kyc_rfee");
        _requestStatus = new StorageMap<string, string>("kyc_rstatus");
        _requestEscrowId = new StorageMap<string, ulong>("kyc_rescrow");
        _requestDeadline = new StorageMap<string, long>("kyc_rdeadline");
        _requestCredHash = new StorageMap<string, string>("kyc_rcred");

        _nextDisputeId = new StorageValue<ulong>("kyc_nextdisp");
        _disputeRequestId = new StorageMap<string, ulong>("kyc_dreq");
        _disputeReason = new StorageMap<string, string>("kyc_dreason");
        _disputeStatus = new StorageMap<string, string>("kyc_dstatus");
        _disputeFiler = new StorageMap<string, string>("kyc_dfiler");

        _protocolFeeBps = new StorageValue<ulong>("kyc_pfee_bps");
        _minProviderStake = new StorageValue<UInt256>("kyc_minstake");
        _defaultDeadlineSeconds = new StorageValue<long>("kyc_deadline_s");
        _protocolRevenue = new StorageValue<UInt256>("kyc_prevenue");
        _providerRevenue = new StorageMap<string, UInt256>("kyc_provrev");

        _protocolFeeBps.Set(protocolFeeBps);
        _minProviderStake.Set(minProviderStake);
        _defaultDeadlineSeconds.Set(defaultDeadlineSeconds);
        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _escrowAddress = new byte[20];
        _escrowAddress[18] = 0x10;
        _escrowAddress[19] = 0x03;

        _vcRegistryAddress = new byte[20]; // Set via SetVcRegistry after deploy
    }

    // ========================================================
    // Provider Management
    // ========================================================

    /// <summary>
    /// Register as a KYC provider. Caller must be an active issuer in IssuerRegistry
    /// with collateral >= minProviderStake.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterProvider(
        UInt256 feeBasic, UInt256 feeEnhanced, UInt256 feeFull,
        string jurisdictions)
    {
        var providerHex = Convert.ToHexString(Context.Caller);

        // Verify provider is active in IssuerRegistry
        var isActive = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsActiveIssuer", Context.Caller);
        Context.Require(isActive, "KYC: not an active issuer");

        // Verify collateral meets minimum
        var collateral = Context.CallContract<UInt256>(
            _issuerRegistryAddress, "GetCollateralStake", Context.Caller);
        Context.Require(collateral >= _minProviderStake.Get(), "KYC: insufficient collateral");

        Context.Require(!feeBasic.IsZero, "KYC: basic fee required");

        _providerActive.Set(providerHex, true);
        _providerFeeBasic.Set(providerHex, feeBasic);
        _providerFeeEnhanced.Set(providerHex, feeEnhanced);
        _providerFeeFull.Set(providerHex, feeFull);
        _providerJurisdictions.Set(providerHex, jurisdictions);

        Context.Emit(new KycProviderRegisteredEvent
        {
            Provider = Context.Caller,
            FeeBasic = feeBasic,
            Jurisdictions = jurisdictions,
        });
    }

    /// <summary>
    /// Update provider listing fees.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateFees(UInt256 feeBasic, UInt256 feeEnhanced, UInt256 feeFull)
    {
        var providerHex = Convert.ToHexString(Context.Caller);
        Context.Require(_providerActive.Get(providerHex), "KYC: not an active provider");

        _providerFeeBasic.Set(providerHex, feeBasic);
        _providerFeeEnhanced.Set(providerHex, feeEnhanced);
        _providerFeeFull.Set(providerHex, feeFull);
    }

    /// <summary>
    /// Deactivate provider listing. Cannot accept new requests.
    /// </summary>
    [BasaltEntrypoint]
    public void DeactivateProvider()
    {
        var providerHex = Convert.ToHexString(Context.Caller);
        Context.Require(_providerActive.Get(providerHex), "KYC: not active");
        _providerActive.Set(providerHex, false);
    }

    // ========================================================
    // KYC Request Flow
    // ========================================================

    /// <summary>
    /// User requests KYC from a specific provider. Pays fee via TxValue.
    /// Fee is held in Escrow until provider completes verification.
    /// </summary>
    [BasaltEntrypoint]
    public ulong RequestKyc(byte[] provider, byte level)
    {
        Context.Require(level >= 1 && level <= 3, "KYC: invalid level (1-3)");
        var providerHex = Convert.ToHexString(provider);
        Context.Require(_providerActive.Get(providerHex), "KYC: provider not active");

        // Determine required fee
        UInt256 requiredFee = level switch
        {
            1 => _providerFeeBasic.Get(providerHex),
            2 => _providerFeeEnhanced.Get(providerHex),
            3 => _providerFeeFull.Get(providerHex),
            _ => UInt256.Zero
        };
        Context.Require(!requiredFee.IsZero, "KYC: level not offered by provider");
        Context.Require(Context.TxValue >= requiredFee, "KYC: insufficient payment");

        // Create escrow for the payment
        var releaseBlock = Context.BlockHeight + 50000; // ~7 days at 12s blocks
        var escrowId = Context.CallContract<ulong>(
            _escrowAddress, "Create", provider, releaseBlock);

        var requestId = _nextRequestId.Get();
        _nextRequestId.Set(requestId + 1);

        var key = requestId.ToString();
        _requestUser.Set(key, Convert.ToHexString(Context.Caller));
        _requestProvider.Set(key, providerHex);
        _requestLevel.Set(key, level);
        _requestFee.Set(key, Context.TxValue);
        _requestStatus.Set(key, "pending");
        _requestEscrowId.Set(key, escrowId);
        _requestDeadline.Set(key, Context.BlockTimestamp + _defaultDeadlineSeconds.Get());

        var total = _providerTotalRequests.Get(providerHex);
        _providerTotalRequests.Set(providerHex, total + 1);

        Context.Emit(new KycRequestedEvent
        {
            RequestId = requestId,
            User = Context.Caller,
            Provider = provider,
            Level = level,
            Fee = Context.TxValue,
        });

        return requestId;
    }

    /// <summary>
    /// Provider fulfills a KYC request by issuing a BST-VC credential.
    /// Payment is released from escrow minus protocol fee.
    /// </summary>
    [BasaltEntrypoint]
    public void FulfillRequest(ulong requestId, byte[] credentialHash)
    {
        var key = requestId.ToString();
        Context.Require(_requestStatus.Get(key) == "pending", "KYC: not pending");

        var providerHex = Convert.ToHexString(Context.Caller);
        Context.Require(providerHex == _requestProvider.Get(key), "KYC: not assigned provider");

        // Store credential reference
        _requestCredHash.Set(key, Convert.ToHexString(credentialHash));
        _requestStatus.Set(key, "completed");

        // Calculate revenue split
        var fee = _requestFee.Get(key);
        var protocolBps = _protocolFeeBps.Get();
        var protocolCut = fee * new UInt256(protocolBps) / new UInt256(10000);
        var providerCut = fee - protocolCut;

        // Track revenue
        var currentProtocol = _protocolRevenue.Get();
        _protocolRevenue.Set(currentProtocol + protocolCut);
        var currentProvider = _providerRevenue.Get(providerHex);
        _providerRevenue.Set(providerHex, currentProvider + providerCut);

        // Release escrow to provider
        var escrowId = _requestEscrowId.Get(key);
        Context.CallContract(_escrowAddress, "Release", escrowId);

        // Update success count
        var success = _providerSuccessCount.Get(providerHex);
        _providerSuccessCount.Set(providerHex, success + 1);

        Context.Emit(new KycFulfilledEvent
        {
            RequestId = requestId,
            Provider = Context.Caller,
            CredentialHash = credentialHash,
            ProviderRevenue = providerCut,
            ProtocolRevenue = protocolCut,
        });
    }

    /// <summary>
    /// User claims refund if provider missed the deadline.
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimRefund(ulong requestId)
    {
        var key = requestId.ToString();
        Context.Require(_requestStatus.Get(key) == "pending", "KYC: not pending");

        var userHex = Convert.ToHexString(Context.Caller);
        Context.Require(userHex == _requestUser.Get(key), "KYC: not requester");
        Context.Require(Context.BlockTimestamp > _requestDeadline.Get(key), "KYC: deadline not passed");

        _requestStatus.Set(key, "refunded");

        // Refund from escrow
        var escrowId = _requestEscrowId.Get(key);
        Context.CallContract(_escrowAddress, "Refund", escrowId);

        Context.Emit(new KycRefundedEvent { RequestId = requestId });
    }

    // ========================================================
    // Dispute Resolution
    // ========================================================

    /// <summary>
    /// File a dispute against a completed KYC credential.
    /// </summary>
    [BasaltEntrypoint]
    public ulong FileDispute(ulong requestId, string reason)
    {
        var key = requestId.ToString();
        Context.Require(_requestStatus.Get(key) == "completed", "KYC: not completed");
        Context.Require(!string.IsNullOrEmpty(reason), "KYC: reason required");

        var disputeId = _nextDisputeId.Get();
        _nextDisputeId.Set(disputeId + 1);

        var dKey = disputeId.ToString();
        _disputeRequestId.Set(dKey, requestId);
        _disputeReason.Set(dKey, reason);
        _disputeStatus.Set(dKey, "open");
        _disputeFiler.Set(dKey, Convert.ToHexString(Context.Caller));

        _requestStatus.Set(key, "disputed");

        var providerHex = _requestProvider.Get(key);
        var disputes = _providerDisputeCount.Get(providerHex);
        _providerDisputeCount.Set(providerHex, disputes + 1);

        Context.Emit(new KycDisputeFiledEvent
        {
            DisputeId = disputeId,
            RequestId = requestId,
            Filer = Context.Caller,
            Reason = reason,
        });

        return disputeId;
    }

    /// <summary>
    /// Admin resolves a dispute. If upheld, triggers issuer slashing via IssuerRegistry.
    /// </summary>
    [BasaltEntrypoint]
    public void ResolveDispute(ulong disputeId, bool upheld)
    {
        RequireAdmin();
        var dKey = disputeId.ToString();
        Context.Require(_disputeStatus.Get(dKey) == "open", "KYC: dispute not open");

        if (upheld)
        {
            _disputeStatus.Set(dKey, "slashed");
            var requestId = _disputeRequestId.Get(dKey);
            var providerHex = _requestProvider.Get(requestId.ToString());

            // Slash provider in IssuerRegistry
            Context.CallContract(
                _issuerRegistryAddress, "SlashIssuer",
                Convert.FromHexString(providerHex),
                "KYC marketplace dispute upheld: dispute #" + disputeId);
        }
        else
        {
            _disputeStatus.Set(dKey, "resolved");
        }

        Context.Emit(new KycDisputeResolvedEvent
        {
            DisputeId = disputeId,
            Upheld = upheld,
        });
    }

    // ========================================================
    // Admin
    // ========================================================

    /// <summary>
    /// Withdraw accumulated protocol revenue to a destination address.
    /// </summary>
    [BasaltEntrypoint]
    public void WithdrawProtocolRevenue(byte[] destination)
    {
        RequireAdmin();
        var revenue = _protocolRevenue.Get();
        Context.Require(!revenue.IsZero, "KYC: no revenue");

        _protocolRevenue.Set(UInt256.Zero);
        Context.TransferNative(destination, revenue);
    }

    /// <summary>
    /// Update protocol fee basis points. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetProtocolFee(ulong feeBps)
    {
        RequireAdmin();
        Context.Require(feeBps <= 5000, "KYC: fee too high"); // max 50%
        _protocolFeeBps.Set(feeBps);
    }

    /// <summary>
    /// Set the BSTVCRegistry contract address. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetVcRegistry(byte[] vcRegistryAddr)
    {
        RequireAdmin();
        Array.Copy(vcRegistryAddr, _vcRegistryAddress, 20);
    }

    // ========================================================
    // Views
    // ========================================================

    [BasaltView]
    public bool IsProviderActive(byte[] provider)
        => _providerActive.Get(Convert.ToHexString(provider));

    [BasaltView]
    public UInt256 GetProviderFee(byte[] provider, byte level)
    {
        var hex = Convert.ToHexString(provider);
        return level switch
        {
            1 => _providerFeeBasic.Get(hex),
            2 => _providerFeeEnhanced.Get(hex),
            3 => _providerFeeFull.Get(hex),
            _ => UInt256.Zero
        };
    }

    [BasaltView]
    public string GetRequestStatus(ulong requestId)
        => _requestStatus.Get(requestId.ToString()) ?? "unknown";

    [BasaltView]
    public ulong GetProviderSuccessCount(byte[] provider)
        => _providerSuccessCount.Get(Convert.ToHexString(provider));

    [BasaltView]
    public ulong GetProviderDisputeCount(byte[] provider)
        => _providerDisputeCount.Get(Convert.ToHexString(provider));

    [BasaltView]
    public UInt256 GetProviderRevenue(byte[] provider)
        => _providerRevenue.Get(Convert.ToHexString(provider));

    [BasaltView]
    public UInt256 GetProtocolRevenue()
        => _protocolRevenue.Get();

    [BasaltView]
    public ulong GetProtocolFeeBps()
        => _protocolFeeBps.Get();

    [BasaltView]
    public string GetDisputeStatus(ulong disputeId)
        => _disputeStatus.Get(disputeId.ToString()) ?? "unknown";

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void RequireAdmin()
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(callerHex == _admin.Get("admin"), "KYC: not admin");
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class KycProviderRegisteredEvent
{
    [Indexed] public byte[] Provider { get; set; } = null!;
    public UInt256 FeeBasic { get; set; }
    public string Jurisdictions { get; set; } = "";
}

[BasaltEvent]
public class KycRequestedEvent
{
    [Indexed] public ulong RequestId { get; set; }
    [Indexed] public byte[] User { get; set; } = null!;
    [Indexed] public byte[] Provider { get; set; } = null!;
    public byte Level { get; set; }
    public UInt256 Fee { get; set; }
}

[BasaltEvent]
public class KycFulfilledEvent
{
    [Indexed] public ulong RequestId { get; set; }
    [Indexed] public byte[] Provider { get; set; } = null!;
    public byte[] CredentialHash { get; set; } = null!;
    public UInt256 ProviderRevenue { get; set; }
    public UInt256 ProtocolRevenue { get; set; }
}

[BasaltEvent]
public class KycRefundedEvent
{
    [Indexed] public ulong RequestId { get; set; }
}

[BasaltEvent]
public class KycDisputeFiledEvent
{
    [Indexed] public ulong DisputeId { get; set; }
    [Indexed] public ulong RequestId { get; set; }
    [Indexed] public byte[] Filer { get; set; } = null!;
    public string Reason { get; set; } = "";
}

[BasaltEvent]
public class KycDisputeResolvedEvent
{
    [Indexed] public ulong DisputeId { get; set; }
    public bool Upheld { get; set; }
}
```

## Complexity

**High** -- This contract coordinates across five system contracts (IssuerRegistry, SchemaRegistry, BSTVCRegistry, Escrow, Governance), manages a multi-party marketplace with payment splitting, escrow flows, dispute resolution, and slashing. The state model is complex with interrelated request, provider, and dispute lifecycles. ZK proof integration for credential reuse adds additional design considerations around schema management and proof verification.

## Priority

**P0** -- This is the flagship compliance use case for Basalt. The entire ZK compliance stack (IssuerRegistry, SchemaRegistry, BSTVCRegistry, ZkComplianceVerifier, ComplianceEngine) was built to enable exactly this kind of application. A working KYC marketplace demonstrates Basalt's unique value proposition against competitors and is a prerequisite for any regulated DeFi activity on the network.
