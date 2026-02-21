# Academic Credential Registry

## Category

Compliance / Education / Credential Verification

## Summary

A registry where accredited universities and educational institutions issue BST-VC verifiable credentials for degrees, certifications, diplomas, and transcripts. The contract manages credential issuance, verification, and revocation while preserving graduate privacy through ZK proofs. An employer can verify that a candidate "holds a Computer Science degree from an accredited university" without learning which university, the graduation year, or the candidate's GPA.

Schema standardization via SchemaRegistry ensures interoperability across institutions and jurisdictions, while IssuerRegistry's trust tiers guarantee that only recognized educational bodies can issue credentials.

## Why It's Useful

- **Eliminates credential fraud**: Academic credential fraud is a global problem. The World Economic Forum estimates that fake degrees cost the global economy tens of billions annually. On-chain verification makes forgery impossible.
- **Instant employer verification**: Background checks on academic credentials currently take days to weeks. On-chain verification is instant and costs only gas.
- **Graduate privacy**: Candidates control what they disclose. They can prove qualification without revealing their alma mater (useful for reducing institutional bias in hiring), exact GPA, or graduation year.
- **Cross-border recognition**: International credential recognition is complex and slow. Standardized on-chain credentials with ZK proofs simplify verification across borders, particularly within the EU's Bologna Process framework.
- **Lifelong learning records**: As micro-credentials and continuing education grow, individuals need a portable, verifiable record of all their learning achievements.
- **Institutional efficiency**: Universities spend significant resources responding to verification requests. An on-chain registry automates this entirely.

## Key Features

- Credential issuance by accredited institutions registered in IssuerRegistry (Tier 1 or Tier 3)
- Credential types: Bachelor, Master, Doctorate, Professional Certificate, Micro-Credential, Diploma, Transcript
- Standardized schemas per credential type registered in SchemaRegistry
- Selective disclosure via ZK proofs: prove field of study, degree level, or accreditation status without revealing institution
- Multi-credential support: one address can hold credentials from multiple institutions
- Batch issuance: institutions can issue credentials to graduating classes efficiently
- Credential equivalency declarations: admin can declare equivalency between credential types across jurisdictions
- Accreditation status tracking: link credential validity to issuing institution's continued accreditation
- Transcript hashing: full transcript stored off-chain (IPFS), hash anchored on-chain
- Transfer prohibition: academic credentials are non-transferable (soulbound)
- Expiry support: some credentials (e.g., professional certificates) have expiry dates
- Revocation with reason codes: degree revocation (e.g., academic misconduct discovery) is recorded permanently

## Basalt-Specific Advantages

- **SchemaRegistry standardization**: Each credential type (Bachelor, Master, etc.) has a registered schema in Basalt's SchemaRegistry (0x...1006) with Groth16 verification keys. This ensures all institutions issue credentials in a compatible format, enabling cross-institutional ZK proofs.
- **IssuerRegistry accreditation**: Educational institutions register as Tier 1 (regulated) or Tier 3 (sovereign) issuers. Their accreditation status is verifiable on-chain, and credentials automatically lose validity if the issuing institution is deactivated or slashed.
- **BST-VC with W3C compatibility**: Credentials follow the W3C Verifiable Credential specification via Basalt's BST-VC standard, making them interoperable with emerging European Digital Identity Wallet (EUDI) standards and the European Blockchain Services Infrastructure (EBSI).
- **ZK selective disclosure**: Basalt's native Groth16 proof verification allows graduates to construct proofs like "I have a STEM degree at Master level or above from an institution accredited in the EU" -- a single proof statement that would require multiple verification calls on other chains.
- **Sparse Merkle Tree revocation**: Efficient privacy-preserving revocation checks. Verifiers can confirm a credential has not been revoked without learning about other revocations by the same institution.
- **AOT-compiled batch processing**: Batch credential issuance (e.g., 500 graduates at commencement) benefits from AOT-compiled contract execution with predictable per-operation gas costs.
- **BLS aggregate signatures**: When institutions issue credentials in batch, BLS aggregate signatures can compress multiple issuer attestations into a single signature for efficient on-chain storage.

## Token Standards Used

- **BST-VC** (BSTVCRegistry, type 0x0007): Primary credential format. Each academic credential is a BST-VC with schema-defined fields, issuer signature, and lifecycle management (active/suspended/revoked).

## Integration Points

- **IssuerRegistry** (0x...1007): Educational institutions must be registered as Tier 1+ issuers with active status. The contract validates issuer status and tier before accepting credential issuance.
- **SchemaRegistry** (0x...1006): Credential schemas (BachelorDegree, MasterDegree, Doctorate, etc.) are registered with field definitions and Groth16 verification keys. Each credential references its schema ID.
- **BSTVCRegistry** (deployed instance): Credentials are issued through BSTVCRegistry.IssueCredential() and lifecycle events (suspension, revocation) are propagated to BSTVCRegistry.
- **Governance** (0x...1002): Addition of new credential types, schema updates, and equivalency declarations can be governed through proposals.
- **BNS** (Basalt Name Service): Institutions can register their BNS names for human-readable identification in verification flows.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Academic credential registry with BST-VC issuance, ZK privacy proofs,
/// and IssuerRegistry-backed accreditation. Type ID: 0x010B.
/// </summary>
[BasaltContract]
public partial class AcademicCredentialRegistry
{
    // --- Storage ---

    // Credential records
    private readonly StorageValue<ulong> _nextCredentialId;
    private readonly StorageMap<string, string> _credHolder;         // credId -> holderHex
    private readonly StorageMap<string, string> _credIssuer;         // credId -> issuerHex (institution)
    private readonly StorageMap<string, string> _credType;           // credId -> credential type name
    private readonly StorageMap<string, string> _credFieldOfStudy;   // credId -> field of study
    private readonly StorageMap<string, long> _credIssueDate;        // credId -> issue timestamp
    private readonly StorageMap<string, long> _credExpiry;           // credId -> expiry (0 = no expiry)
    private readonly StorageMap<string, string> _credStatus;         // credId -> active/suspended/revoked
    private readonly StorageMap<string, string> _credVcHash;         // credId -> BST-VC hash hex
    private readonly StorageMap<string, string> _credSchemaId;       // credId -> schema ID hex
    private readonly StorageMap<string, string> _credIpfsUri;        // credId -> IPFS URI for full credential
    private readonly StorageMap<string, ulong> _credJurisdiction;    // credId -> country code
    private readonly StorageMap<string, string> _credRevocationReason;

    // Holder index
    private readonly StorageMap<string, ulong> _holderCredCount;     // holderHex -> count
    private readonly StorageMap<string, string> _holderCredentials;   // holderHex:index -> credId

    // Credential type definitions
    private readonly StorageMap<string, bool> _typeExists;
    private readonly StorageMap<string, string> _typeSchemaId;       // typeName -> schema ID hex
    private readonly StorageMap<string, byte> _typeRequiredTier;     // typeName -> minimum issuer tier
    private readonly StorageMap<string, bool> _typeHasExpiry;        // typeName -> requires expiry

    // Credential equivalency
    private readonly StorageMap<string, bool> _equivalencies;        // "typeA:jurisA:typeB:jurisB" -> equivalent

    // Admin
    private readonly StorageMap<string, string> _admin;

    // System contract addresses
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _schemaRegistryAddress;

    public AcademicCredentialRegistry()
    {
        _nextCredentialId = new StorageValue<ulong>("ac_next");
        _credHolder = new StorageMap<string, string>("ac_holder");
        _credIssuer = new StorageMap<string, string>("ac_issuer");
        _credType = new StorageMap<string, string>("ac_type");
        _credFieldOfStudy = new StorageMap<string, string>("ac_field");
        _credIssueDate = new StorageMap<string, long>("ac_issued");
        _credExpiry = new StorageMap<string, long>("ac_expiry");
        _credStatus = new StorageMap<string, string>("ac_status");
        _credVcHash = new StorageMap<string, string>("ac_vchash");
        _credSchemaId = new StorageMap<string, string>("ac_schema");
        _credIpfsUri = new StorageMap<string, string>("ac_ipfs");
        _credJurisdiction = new StorageMap<string, ulong>("ac_juris");
        _credRevocationReason = new StorageMap<string, string>("ac_revreason");

        _holderCredCount = new StorageMap<string, ulong>("ac_hcount");
        _holderCredentials = new StorageMap<string, string>("ac_hcred");

        _typeExists = new StorageMap<string, bool>("ac_texists");
        _typeSchemaId = new StorageMap<string, string>("ac_tschema");
        _typeRequiredTier = new StorageMap<string, byte>("ac_ttier");
        _typeHasExpiry = new StorageMap<string, bool>("ac_texpiry");

        _equivalencies = new StorageMap<string, bool>("ac_equiv");

        _admin = new StorageMap<string, string>("ac_admin");
        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        // Register default credential types
        RegisterTypeInternal("Bachelor", 1, false);
        RegisterTypeInternal("Master", 1, false);
        RegisterTypeInternal("Doctorate", 1, false);
        RegisterTypeInternal("ProfessionalCertificate", 1, true);
        RegisterTypeInternal("MicroCredential", 1, true);
        RegisterTypeInternal("Diploma", 1, false);
        RegisterTypeInternal("Transcript", 1, false);
    }

    // ========================================================
    // Credential Issuance
    // ========================================================

    /// <summary>
    /// Issue an academic credential. Caller must be an accredited institution
    /// registered in IssuerRegistry at the required tier.
    /// </summary>
    [BasaltEntrypoint]
    public ulong IssueCredential(
        byte[] holder, string credentialType, string fieldOfStudy,
        ulong jurisdictionCode, long expiryTimestamp,
        byte[] vcCredentialHash, byte[] schemaId, string ipfsUri)
    {
        Context.Require(_typeExists.Get(credentialType), "AC: invalid credential type");
        Context.Require(!string.IsNullOrEmpty(fieldOfStudy), "AC: field of study required");
        Context.Require(vcCredentialHash.Length > 0, "AC: VC hash required");

        // If type requires expiry, validate it
        if (_typeHasExpiry.Get(credentialType))
        {
            Context.Require(expiryTimestamp > Context.BlockTimestamp, "AC: expiry required and must be in future");
        }

        // Validate issuer in IssuerRegistry
        var isActive = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsActiveIssuer", Context.Caller);
        Context.Require(isActive, "AC: institution not active in IssuerRegistry");

        var tier = Context.CallContract<byte>(
            _issuerRegistryAddress, "GetIssuerTier", Context.Caller);
        var requiredTier = _typeRequiredTier.Get(credentialType);
        Context.Require(tier >= requiredTier, "AC: insufficient issuer tier");

        var credId = _nextCredentialId.Get();
        _nextCredentialId.Set(credId + 1);

        var key = credId.ToString();
        var holderHex = Convert.ToHexString(holder);
        var issuerHex = Convert.ToHexString(Context.Caller);

        _credHolder.Set(key, holderHex);
        _credIssuer.Set(key, issuerHex);
        _credType.Set(key, credentialType);
        _credFieldOfStudy.Set(key, fieldOfStudy);
        _credIssueDate.Set(key, Context.BlockTimestamp);
        _credExpiry.Set(key, expiryTimestamp);
        _credStatus.Set(key, "active");
        _credVcHash.Set(key, Convert.ToHexString(vcCredentialHash));
        _credSchemaId.Set(key, Convert.ToHexString(schemaId));
        _credIpfsUri.Set(key, ipfsUri);
        _credJurisdiction.Set(key, jurisdictionCode);

        // Index by holder
        var count = _holderCredCount.Get(holderHex);
        _holderCredentials.Set(holderHex + ":" + count, credId.ToString());
        _holderCredCount.Set(holderHex, count + 1);

        Context.Emit(new AcademicCredentialIssuedEvent
        {
            CredentialId = credId,
            Holder = holder,
            Issuer = Context.Caller,
            CredentialType = credentialType,
            FieldOfStudy = fieldOfStudy,
            JurisdictionCode = jurisdictionCode,
        });

        return credId;
    }

    /// <summary>
    /// Suspend a credential. Issuer only. Can be reinstated.
    /// </summary>
    [BasaltEntrypoint]
    public void SuspendCredential(ulong credentialId, string reason)
    {
        var key = credentialId.ToString();
        RequireCredentialIssuer(key);
        Context.Require(_credStatus.Get(key) == "active", "AC: not active");

        _credStatus.Set(key, "suspended");
        _credRevocationReason.Set(key, reason);

        Context.Emit(new AcademicCredentialSuspendedEvent
        {
            CredentialId = credentialId,
            Reason = reason,
        });
    }

    /// <summary>
    /// Reinstate a suspended credential. Issuer only.
    /// </summary>
    [BasaltEntrypoint]
    public void ReinstateCredential(ulong credentialId)
    {
        var key = credentialId.ToString();
        RequireCredentialIssuer(key);
        Context.Require(_credStatus.Get(key) == "suspended", "AC: not suspended");

        _credStatus.Set(key, "active");
        _credRevocationReason.Delete(key);

        Context.Emit(new AcademicCredentialReinstatedEvent
        {
            CredentialId = credentialId,
        });
    }

    /// <summary>
    /// Revoke a credential permanently (e.g., academic misconduct). Terminal state.
    /// </summary>
    [BasaltEntrypoint]
    public void RevokeCredential(ulong credentialId, string reason)
    {
        var key = credentialId.ToString();
        RequireCredentialIssuer(key);
        Context.Require(_credStatus.Get(key) != "revoked", "AC: already revoked");

        _credStatus.Set(key, "revoked");
        _credRevocationReason.Set(key, reason);

        Context.Emit(new AcademicCredentialRevokedEvent
        {
            CredentialId = credentialId,
            Reason = reason,
        });
    }

    // ========================================================
    // Admin Operations
    // ========================================================

    /// <summary>
    /// Register a new credential type. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterCredentialType(string name, byte requiredTier, bool hasExpiry)
    {
        RequireAdmin();
        Context.Require(!_typeExists.Get(name), "AC: type exists");
        RegisterTypeInternal(name, requiredTier, hasExpiry);
    }

    /// <summary>
    /// Declare equivalency between two credential types across jurisdictions.
    /// Admin only. Used for cross-border recognition.
    /// </summary>
    [BasaltEntrypoint]
    public void DeclareEquivalency(
        string typeA, ulong jurisdictionA,
        string typeB, ulong jurisdictionB)
    {
        RequireAdmin();
        Context.Require(_typeExists.Get(typeA), "AC: typeA not found");
        Context.Require(_typeExists.Get(typeB), "AC: typeB not found");

        var equivKey = typeA + ":" + jurisdictionA + ":" + typeB + ":" + jurisdictionB;
        var reverseKey = typeB + ":" + jurisdictionB + ":" + typeA + ":" + jurisdictionA;

        _equivalencies.Set(equivKey, true);
        _equivalencies.Set(reverseKey, true);

        Context.Emit(new EquivalencyDeclaredEvent
        {
            TypeA = typeA,
            JurisdictionA = jurisdictionA,
            TypeB = typeB,
            JurisdictionB = jurisdictionB,
        });
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
    public string GetCredentialStatus(ulong credentialId)
        => _credStatus.Get(credentialId.ToString()) ?? "unknown";

    [BasaltView]
    public string GetCredentialType(ulong credentialId)
        => _credType.Get(credentialId.ToString()) ?? "";

    [BasaltView]
    public string GetFieldOfStudy(ulong credentialId)
        => _credFieldOfStudy.Get(credentialId.ToString()) ?? "";

    [BasaltView]
    public bool IsCredentialValid(ulong credentialId)
    {
        var key = credentialId.ToString();
        var status = _credStatus.Get(key);
        if (status != "active") return false;

        var expiry = _credExpiry.Get(key);
        if (expiry > 0 && expiry <= Context.BlockTimestamp) return false;

        return true;
    }

    [BasaltView]
    public ulong GetHolderCredentialCount(byte[] holder)
        => _holderCredCount.Get(Convert.ToHexString(holder));

    [BasaltView]
    public long GetCredentialExpiry(ulong credentialId)
        => _credExpiry.Get(credentialId.ToString());

    [BasaltView]
    public string GetCredentialIpfsUri(ulong credentialId)
        => _credIpfsUri.Get(credentialId.ToString()) ?? "";

    [BasaltView]
    public ulong GetCredentialJurisdiction(ulong credentialId)
        => _credJurisdiction.Get(credentialId.ToString());

    [BasaltView]
    public bool AreTypesEquivalent(
        string typeA, ulong jurisdictionA,
        string typeB, ulong jurisdictionB)
    {
        var equivKey = typeA + ":" + jurisdictionA + ":" + typeB + ":" + jurisdictionB;
        return _equivalencies.Get(equivKey);
    }

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "AC: not admin");
    }

    private void RequireCredentialIssuer(string credKey)
    {
        var issuerHex = _credIssuer.Get(credKey);
        Context.Require(!string.IsNullOrEmpty(issuerHex), "AC: credential not found");
        Context.Require(
            Convert.ToHexString(Context.Caller) == issuerHex,
            "AC: not credential issuer");
    }

    private void RegisterTypeInternal(string name, byte requiredTier, bool hasExpiry)
    {
        _typeExists.Set(name, true);
        _typeRequiredTier.Set(name, requiredTier);
        _typeHasExpiry.Set(name, hasExpiry);
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class AcademicCredentialIssuedEvent
{
    [Indexed] public ulong CredentialId { get; set; }
    [Indexed] public byte[] Holder { get; set; } = null!;
    [Indexed] public byte[] Issuer { get; set; } = null!;
    public string CredentialType { get; set; } = "";
    public string FieldOfStudy { get; set; } = "";
    public ulong JurisdictionCode { get; set; }
}

[BasaltEvent]
public class AcademicCredentialSuspendedEvent
{
    [Indexed] public ulong CredentialId { get; set; }
    public string Reason { get; set; } = "";
}

[BasaltEvent]
public class AcademicCredentialReinstatedEvent
{
    [Indexed] public ulong CredentialId { get; set; }
}

[BasaltEvent]
public class AcademicCredentialRevokedEvent
{
    [Indexed] public ulong CredentialId { get; set; }
    public string Reason { get; set; } = "";
}

[BasaltEvent]
public class EquivalencyDeclaredEvent
{
    public string TypeA { get; set; } = "";
    public ulong JurisdictionA { get; set; }
    public string TypeB { get; set; } = "";
    public ulong JurisdictionB { get; set; }
}
```

## Complexity

**Medium** -- The contract manages a credential lifecycle similar to the Professional License Registry, with additional complexity from cross-border equivalency declarations and batch issuance patterns. The ZK proof construction for selective disclosure (e.g., "has CS degree from accredited EU university" without revealing which one) relies on the SchemaRegistry's Groth16 verification keys and is handled off-chain, keeping the on-chain contract relatively straightforward.

## Priority

**P1** -- Academic credential verification is a high-impact use case that resonates with a broad audience (students, universities, employers) and demonstrates Basalt's compliance infrastructure. It shares architectural patterns with the Professional License Registry and can be built on the same IssuerRegistry/SchemaRegistry foundation. The equivalency declaration feature is particularly relevant for EU markets where mutual credential recognition is mandated.
