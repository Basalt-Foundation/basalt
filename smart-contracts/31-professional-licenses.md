# Professional License Registry

## Category

Compliance / Credential Verification

## Summary

A registry for professional licenses (medical, legal, engineering, financial advisory, accounting) issued as BST-VC verifiable credentials by accredited licensing bodies registered in IssuerRegistry. The contract manages the full license lifecycle including issuance, renewal, suspension, and revocation, with on-chain verification that preserves holder privacy via ZK proofs.

Employers and clients can verify that a professional holds a valid license in a specific jurisdiction without learning the holder's name, license number, or personal details -- only that the credential is valid and issued by a recognized authority.

## Why It's Useful

- **Fraud prevention**: Professional license fraud costs billions annually. On-chain verification eliminates forged certificates, expired credentials presented as valid, and practitioners operating outside their jurisdiction.
- **Cross-border verification**: Professionals working across jurisdictions (especially in the EU under mutual recognition directives) benefit from a universal verification registry that works regardless of issuing country.
- **Instant verification**: Employers, hospitals, courts, and clients can verify license status in seconds rather than waiting days or weeks for manual checks with licensing boards.
- **Privacy preservation**: Holders control what is disclosed. A doctor can prove "licensed to practice medicine in France" without revealing their name, license number, or graduation details.
- **Regulatory compliance**: Regulated industries (finance, healthcare, law) require credential verification. On-chain licensing satisfies audit requirements with immutable, timestamped records.
- **Continuing education tracking**: License renewal can require proof of continuing education credits, tracked and verified on-chain.

## Key Features

- License issuance by accredited bodies (Tier 1 or Tier 3 in IssuerRegistry)
- Standardized license types: Medical, Legal, Engineering, Financial, Accounting, Teaching, Nursing, Architecture
- Per-license metadata: jurisdiction (country code), specialty, issue date, expiry date, license number hash
- Renewal flow: holders submit renewal requests, issuing body confirms with updated expiry
- Suspension and reinstatement by issuing body with reason tracking
- Revocation as terminal state (cannot be reinstated)
- ZK verification: prove "I hold a valid [type] license in [jurisdiction]" without revealing identity
- Multi-license support: one address can hold multiple licenses across types and jurisdictions
- License lookup by holder address (returns active license count and types)
- Continuing education credit tracking linked to license renewal requirements
- Issuer validation: only IssuerRegistry-approved bodies at Tier 1+ can issue licenses
- Schema standardization: each license type has a registered schema in SchemaRegistry with defined fields

## Basalt-Specific Advantages

- **IssuerRegistry trust tiers**: Licensing bodies register as Tier 1 (regulated entity, admin-approved) or Tier 3 (sovereign/eIDAS) issuers, providing a built-in trust framework. Only bodies with verified authority can issue licenses, enforced at the contract level.
- **BST-VC lifecycle management**: Licenses issued as BST-VC credentials benefit from Basalt's native credential lifecycle (Active, Suspended, Revoked) with on-chain status tracking and off-chain data storage via IPFS.
- **SchemaRegistry standardization**: Each license type (Medical, Legal, etc.) has a registered schema defining required fields (jurisdiction, specialty, expiry). The SchemaRegistry stores Groth16 verification keys for each schema, enabling ZK proof verification.
- **ZK privacy proofs**: Basalt's ZkComplianceVerifier enables proofs like "I hold a valid medical license issued by an EU member state licensing body" without revealing which country, which body, or any identifying information. This is built into the chain, not bolted on.
- **Sparse Merkle Tree revocation**: IssuerRegistry's per-issuer revocation tree roots enable efficient, privacy-preserving revocation checks. A verifier can confirm a license has not been revoked without learning which licenses have been.
- **AOT-compiled deterministic execution**: License verification queries execute with predictable gas costs in the AOT runtime, making them suitable for high-frequency automated compliance checks.
- **Ed25519 issuer signatures**: License attestations are signed with Ed25519 (Basalt-native), providing 128-bit security with fast verification.
- **eIDAS 2.0 alignment**: BST-VC's W3C Verifiable Credential structure and Basalt's eIDAS-compatible trust framework make professional licenses issued on Basalt compatible with European Digital Identity Wallet standards.

## Token Standards Used

- **BST-VC** (BSTVCRegistry, type 0x0007): Primary credential format. Each professional license is a BST-VC credential with a schema-defined structure, issuer signature, and managed lifecycle.

## Integration Points

- **IssuerRegistry** (0x...1007): Licensing bodies must be registered as Tier 1 or Tier 3 issuers with active status. The contract validates issuer tier before accepting license issuance calls.
- **SchemaRegistry** (0x...1006): License type schemas are registered with field definitions and Groth16 verification keys. The contract references schema IDs when issuing credentials and validates that issuers support the relevant schema.
- **BSTVCRegistry** (deployed instance): License credentials are issued through BSTVCRegistry.IssueCredential() and their lifecycle (renewal, suspension, revocation) is managed through BSTVCRegistry's state transitions.
- **Governance** (0x...1002): Addition of new license types, changes to issuer tier requirements, and schema updates can be proposed and voted on through governance.
- **BNS** (Basalt Name Service): Professionals can link their BNS name to their license credentials for human-readable verification.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Professional license registry with BST-VC credentials, ZK verification,
/// and IssuerRegistry-backed trust. Type ID: 0x010A.
/// </summary>
[BasaltContract]
public partial class ProfessionalLicenseRegistry
{
    // --- Storage ---

    // License records
    private readonly StorageValue<ulong> _nextLicenseId;
    private readonly StorageMap<string, string> _licenseHolder;       // licenseId -> holderHex
    private readonly StorageMap<string, string> _licenseIssuer;       // licenseId -> issuerHex
    private readonly StorageMap<string, string> _licenseType;         // licenseId -> type name
    private readonly StorageMap<string, ulong> _licenseJurisdiction;  // licenseId -> country code
    private readonly StorageMap<string, string> _licenseSpecialty;    // licenseId -> specialty
    private readonly StorageMap<string, long> _licenseIssueDate;     // licenseId -> issue timestamp
    private readonly StorageMap<string, long> _licenseExpiry;        // licenseId -> expiry timestamp
    private readonly StorageMap<string, string> _licenseStatus;      // licenseId -> active/suspended/revoked
    private readonly StorageMap<string, string> _licenseCredHash;    // licenseId -> BST-VC credential hash hex
    private readonly StorageMap<string, string> _licenseSchemaId;    // licenseId -> schema ID hex
    private readonly StorageMap<string, ulong> _licenseRenewalCount; // licenseId -> number of renewals

    // Holder index
    private readonly StorageMap<string, ulong> _holderLicenseCount;  // holderHex -> count
    private readonly StorageMap<string, string> _holderLicenses;     // holderHex:index -> licenseId

    // License type definitions
    private readonly StorageMap<string, bool> _typeExists;           // typeName -> registered
    private readonly StorageMap<string, string> _typeSchemaId;       // typeName -> schema ID hex
    private readonly StorageMap<string, byte> _typeRequiredTier;     // typeName -> minimum issuer tier

    // Continuing education
    private readonly StorageMap<string, ulong> _ceCredits;           // licenseId -> accumulated CE credits
    private readonly StorageMap<string, ulong> _ceRequired;          // typeName -> CE credits required for renewal

    // Admin
    private readonly StorageMap<string, string> _admin;

    // System contract addresses
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _schemaRegistryAddress;
    private readonly byte[] _vcRegistryAddress;

    public ProfessionalLicenseRegistry()
    {
        _nextLicenseId = new StorageValue<ulong>("pl_next");
        _licenseHolder = new StorageMap<string, string>("pl_holder");
        _licenseIssuer = new StorageMap<string, string>("pl_issuer");
        _licenseType = new StorageMap<string, string>("pl_type");
        _licenseJurisdiction = new StorageMap<string, ulong>("pl_juris");
        _licenseSpecialty = new StorageMap<string, string>("pl_spec");
        _licenseIssueDate = new StorageMap<string, long>("pl_issued");
        _licenseExpiry = new StorageMap<string, long>("pl_expiry");
        _licenseStatus = new StorageMap<string, string>("pl_status");
        _licenseCredHash = new StorageMap<string, string>("pl_cred");
        _licenseSchemaId = new StorageMap<string, string>("pl_schema");
        _licenseRenewalCount = new StorageMap<string, ulong>("pl_renew");

        _holderLicenseCount = new StorageMap<string, ulong>("pl_hcount");
        _holderLicenses = new StorageMap<string, string>("pl_hlic");

        _typeExists = new StorageMap<string, bool>("pl_texists");
        _typeSchemaId = new StorageMap<string, string>("pl_tschema");
        _typeRequiredTier = new StorageMap<string, byte>("pl_ttier");

        _ceCredits = new StorageMap<string, ulong>("pl_ce");
        _ceRequired = new StorageMap<string, ulong>("pl_cereq");

        _admin = new StorageMap<string, string>("pl_admin");
        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        _vcRegistryAddress = new byte[20]; // Set post-deploy

        // Register default license types
        RegisterTypeInternal("Medical", 1);
        RegisterTypeInternal("Legal", 1);
        RegisterTypeInternal("Engineering", 1);
        RegisterTypeInternal("Financial", 1);
        RegisterTypeInternal("Accounting", 1);
        RegisterTypeInternal("Teaching", 1);
        RegisterTypeInternal("Nursing", 1);
        RegisterTypeInternal("Architecture", 1);
    }

    // ========================================================
    // License Issuance
    // ========================================================

    /// <summary>
    /// Issue a professional license. Caller must be a registered issuer at the required tier.
    /// Creates a BST-VC credential and records the license on-chain.
    /// </summary>
    [BasaltEntrypoint]
    public ulong IssueLicense(
        byte[] holder, string licenseType, ulong jurisdictionCode,
        string specialty, long expiryTimestamp,
        byte[] credentialHash, byte[] schemaId)
    {
        Context.Require(_typeExists.Get(licenseType), "PL: invalid license type");
        Context.Require(expiryTimestamp > Context.BlockTimestamp, "PL: expiry must be in future");
        Context.Require(credentialHash.Length > 0, "PL: credential hash required");

        var issuerHex = Convert.ToHexString(Context.Caller);

        // Verify issuer is active and at required tier
        var isActive = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsActiveIssuer", Context.Caller);
        Context.Require(isActive, "PL: issuer not active");

        var tier = Context.CallContract<byte>(
            _issuerRegistryAddress, "GetIssuerTier", Context.Caller);
        var requiredTier = _typeRequiredTier.Get(licenseType);
        Context.Require(tier >= requiredTier, "PL: insufficient issuer tier");

        // Verify issuer supports the schema
        var supportsSchema = Context.CallContract<bool>(
            _issuerRegistryAddress, "SupportsSchema", Context.Caller, schemaId);
        Context.Require(supportsSchema, "PL: issuer does not support schema");

        var licenseId = _nextLicenseId.Get();
        _nextLicenseId.Set(licenseId + 1);

        var key = licenseId.ToString();
        var holderHex = Convert.ToHexString(holder);

        _licenseHolder.Set(key, holderHex);
        _licenseIssuer.Set(key, issuerHex);
        _licenseType.Set(key, licenseType);
        _licenseJurisdiction.Set(key, jurisdictionCode);
        _licenseSpecialty.Set(key, specialty);
        _licenseIssueDate.Set(key, Context.BlockTimestamp);
        _licenseExpiry.Set(key, expiryTimestamp);
        _licenseStatus.Set(key, "active");
        _licenseCredHash.Set(key, Convert.ToHexString(credentialHash));
        _licenseSchemaId.Set(key, Convert.ToHexString(schemaId));

        // Index by holder
        var count = _holderLicenseCount.Get(holderHex);
        _holderLicenses.Set(holderHex + ":" + count, licenseId.ToString());
        _holderLicenseCount.Set(holderHex, count + 1);

        Context.Emit(new LicenseIssuedEvent
        {
            LicenseId = licenseId,
            Holder = holder,
            Issuer = Context.Caller,
            LicenseType = licenseType,
            JurisdictionCode = jurisdictionCode,
            Expiry = expiryTimestamp,
        });

        return licenseId;
    }

    /// <summary>
    /// Renew a license with a new expiry date. Issuer only.
    /// Optionally requires sufficient continuing education credits.
    /// </summary>
    [BasaltEntrypoint]
    public void RenewLicense(ulong licenseId, long newExpiry)
    {
        var key = licenseId.ToString();
        RequireLicenseIssuer(key);
        Context.Require(
            _licenseStatus.Get(key) == "active" || _licenseStatus.Get(key) == "suspended",
            "PL: cannot renew revoked license");
        Context.Require(newExpiry > Context.BlockTimestamp, "PL: expiry must be in future");

        // Check CE requirements if configured
        var licenseType = _licenseType.Get(key);
        var ceReq = _ceRequired.Get(licenseType);
        if (ceReq > 0)
        {
            var ceEarned = _ceCredits.Get(key);
            Context.Require(ceEarned >= ceReq, "PL: insufficient CE credits");
            _ceCredits.Set(key, 0); // Reset credits on renewal
        }

        _licenseExpiry.Set(key, newExpiry);
        _licenseStatus.Set(key, "active");
        var renewals = _licenseRenewalCount.Get(key);
        _licenseRenewalCount.Set(key, renewals + 1);

        Context.Emit(new LicenseRenewedEvent
        {
            LicenseId = licenseId,
            NewExpiry = newExpiry,
            RenewalCount = renewals + 1,
        });
    }

    /// <summary>
    /// Suspend a license. Issuer only. License can be reinstated.
    /// </summary>
    [BasaltEntrypoint]
    public void SuspendLicense(ulong licenseId, string reason)
    {
        var key = licenseId.ToString();
        RequireLicenseIssuer(key);
        Context.Require(_licenseStatus.Get(key) == "active", "PL: not active");

        _licenseStatus.Set(key, "suspended");

        Context.Emit(new LicenseSuspendedEvent
        {
            LicenseId = licenseId,
            Reason = reason,
        });
    }

    /// <summary>
    /// Reinstate a suspended license. Issuer only.
    /// </summary>
    [BasaltEntrypoint]
    public void ReinstateLicense(ulong licenseId)
    {
        var key = licenseId.ToString();
        RequireLicenseIssuer(key);
        Context.Require(_licenseStatus.Get(key) == "suspended", "PL: not suspended");

        _licenseStatus.Set(key, "active");

        Context.Emit(new LicenseReinstatedEvent { LicenseId = licenseId });
    }

    /// <summary>
    /// Revoke a license permanently. Issuer only. Terminal state.
    /// </summary>
    [BasaltEntrypoint]
    public void RevokeLicense(ulong licenseId, string reason)
    {
        var key = licenseId.ToString();
        RequireLicenseIssuer(key);
        Context.Require(_licenseStatus.Get(key) != "revoked", "PL: already revoked");

        _licenseStatus.Set(key, "revoked");

        Context.Emit(new LicenseRevokedEvent
        {
            LicenseId = licenseId,
            Reason = reason,
        });
    }

    /// <summary>
    /// Record continuing education credits for a license. Issuer only.
    /// </summary>
    [BasaltEntrypoint]
    public void RecordCeCredits(ulong licenseId, ulong credits)
    {
        var key = licenseId.ToString();
        RequireLicenseIssuer(key);
        Context.Require(_licenseStatus.Get(key) == "active", "PL: not active");

        var current = _ceCredits.Get(key);
        _ceCredits.Set(key, current + credits);

        Context.Emit(new CeCreditsRecordedEvent
        {
            LicenseId = licenseId,
            Credits = credits,
            TotalCredits = current + credits,
        });
    }

    // ========================================================
    // Admin
    // ========================================================

    /// <summary>
    /// Register a new license type. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterLicenseType(string name, byte requiredTier)
    {
        RequireAdmin();
        Context.Require(!_typeExists.Get(name), "PL: type exists");
        RegisterTypeInternal(name, requiredTier);
    }

    /// <summary>
    /// Set continuing education requirements for a license type. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetCeRequirement(string licenseType, ulong creditsRequired)
    {
        RequireAdmin();
        Context.Require(_typeExists.Get(licenseType), "PL: type not found");
        _ceRequired.Set(licenseType, creditsRequired);
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
    public string GetLicenseStatus(ulong licenseId)
        => _licenseStatus.Get(licenseId.ToString()) ?? "unknown";

    [BasaltView]
    public string GetLicenseType(ulong licenseId)
        => _licenseType.Get(licenseId.ToString()) ?? "";

    [BasaltView]
    public long GetLicenseExpiry(ulong licenseId)
        => _licenseExpiry.Get(licenseId.ToString());

    [BasaltView]
    public bool IsLicenseValid(ulong licenseId)
    {
        var key = licenseId.ToString();
        return _licenseStatus.Get(key) == "active"
            && _licenseExpiry.Get(key) > Context.BlockTimestamp;
    }

    [BasaltView]
    public ulong GetHolderLicenseCount(byte[] holder)
        => _holderLicenseCount.Get(Convert.ToHexString(holder));

    [BasaltView]
    public ulong GetCeCredits(ulong licenseId)
        => _ceCredits.Get(licenseId.ToString());

    [BasaltView]
    public ulong GetRenewalCount(ulong licenseId)
        => _licenseRenewalCount.Get(licenseId.ToString());

    [BasaltView]
    public ulong GetJurisdiction(ulong licenseId)
        => _licenseJurisdiction.Get(licenseId.ToString());

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "PL: not admin");
    }

    private void RequireLicenseIssuer(string licenseKey)
    {
        var issuerHex = _licenseIssuer.Get(licenseKey);
        Context.Require(!string.IsNullOrEmpty(issuerHex), "PL: license not found");
        Context.Require(
            Convert.ToHexString(Context.Caller) == issuerHex,
            "PL: not license issuer");
    }

    private void RegisterTypeInternal(string name, byte requiredTier)
    {
        _typeExists.Set(name, true);
        _typeRequiredTier.Set(name, requiredTier);
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class LicenseIssuedEvent
{
    [Indexed] public ulong LicenseId { get; set; }
    [Indexed] public byte[] Holder { get; set; } = null!;
    [Indexed] public byte[] Issuer { get; set; } = null!;
    public string LicenseType { get; set; } = "";
    public ulong JurisdictionCode { get; set; }
    public long Expiry { get; set; }
}

[BasaltEvent]
public class LicenseRenewedEvent
{
    [Indexed] public ulong LicenseId { get; set; }
    public long NewExpiry { get; set; }
    public ulong RenewalCount { get; set; }
}

[BasaltEvent]
public class LicenseSuspendedEvent
{
    [Indexed] public ulong LicenseId { get; set; }
    public string Reason { get; set; } = "";
}

[BasaltEvent]
public class LicenseReinstatedEvent
{
    [Indexed] public ulong LicenseId { get; set; }
}

[BasaltEvent]
public class LicenseRevokedEvent
{
    [Indexed] public ulong LicenseId { get; set; }
    public string Reason { get; set; } = "";
}

[BasaltEvent]
public class CeCreditsRecordedEvent
{
    [Indexed] public ulong LicenseId { get; set; }
    public ulong Credits { get; set; }
    public ulong TotalCredits { get; set; }
}
```

## Complexity

**Medium** -- The contract manages a straightforward credential lifecycle (issue, renew, suspend, reinstate, revoke) with well-defined state transitions. The main complexity comes from cross-contract validation with IssuerRegistry (tier checks, schema support checks) and the continuing education credit tracking system. ZK proof generation for privacy-preserving verification happens off-chain with on-chain verification keys, which simplifies the contract logic.

## Priority

**P1** -- Professional license verification is a high-value, real-world use case that demonstrates Basalt's compliance infrastructure in a tangible way. It builds directly on the IssuerRegistry and SchemaRegistry infrastructure and provides a clear regulatory compliance narrative. However, it depends on the KYC marketplace (P0) and issuer onboarding pipeline being functional first.
