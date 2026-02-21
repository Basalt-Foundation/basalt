# Privacy-Preserving Age Verification

## Category

Compliance / Privacy / Identity

## Summary

A ZK-powered age verification system that allows users to prove they are above a specified age threshold (e.g., 18, 21, 25) without revealing their exact date of birth, real identity, or any other personal information. Users obtain a single BST-VC identity attestation from a KYC provider, then generate unlimited ZK proofs for age-gated services. Each verification costs only a proof verification gas fee, and the same credential works across all dApps on Basalt.

This contract is the canonical example of how Basalt's native ZK compliance stack and BST-VC credentials combine to solve a real regulatory problem -- age verification for alcohol sales, gambling, adult content, and financial services -- in a privacy-preserving manner.

## Why It's Useful

- **Regulatory compliance**: Age verification is legally required for alcohol sales (in many jurisdictions), gambling, tobacco, adult content, and certain financial products. This contract provides verifiable compliance without invasive data collection.
- **Privacy preservation**: Current age verification methods require sharing government ID, date of birth, or facial biometrics with every service. ZK proofs eliminate this entirely.
- **One credential, unlimited use**: A user verifies their age once with a KYC provider and can use the resulting credential across all Basalt dApps indefinitely (until expiry).
- **Reduced liability for services**: Businesses that verify age via ZK proofs never possess personal data, eliminating GDPR data controller responsibilities and breach liability.
- **Cross-platform portability**: The same age proof works for on-chain (DeFi protocols, NFT marketplaces) and off-chain (e-commerce, content platforms) verification via Basalt-connected services.
- **Minimal friction**: The verification flow requires a single transaction to register the credential, then proof generation is instant and off-chain, with only the proof submission occurring on-chain.

## Key Features

- Age threshold verification: prove "age >= X" for any threshold X without revealing exact birthday
- BST-VC identity credential binding: links to a KYC-issued identity attestation without revealing identity
- Multiple threshold support: a single credential can be used to prove different thresholds (18+, 21+, 25+)
- Service registration: age-gated services register their required threshold and receive verified proofs
- On-chain proof verification via Groth16 with schema-specific verification keys from SchemaRegistry
- Nullifier-based privacy: each service receives a unique nullifier, preventing cross-service identity correlation
- Credential expiry respect: proofs automatically fail if the underlying BST-VC credential has expired
- Issuer validation: only credentials from active, tier-1+ issuers in IssuerRegistry are accepted
- Batch verification: services can verify multiple users in a single transaction for efficiency
- Verification receipt: on-chain record that a proof was verified (for audit), without storing the user's identity
- Configurable proof validity window: services can require proofs generated within a recent time window
- Revocation check integration: proofs include a non-revocation witness checked against IssuerRegistry's revocation root

## Basalt-Specific Advantages

- **Native Groth16 verification**: Basalt's ZkComplianceVerifier provides on-chain Groth16 proof verification as a built-in capability. No third-party ZK libraries or precompiles needed -- the verification logic is part of the chain's compliance layer.
- **SchemaRegistry verification keys**: The age verification ZK circuit's verification key is stored in SchemaRegistry (0x...1006), ensuring all verifiers use the same trusted parameters. Circuit updates go through governance.
- **BST-VC credential foundation**: The age attestation is a BST-VC credential (type 0x0007) with full lifecycle management. If the issuing KYC provider is slashed in IssuerRegistry, all their age credentials are implicitly invalidated.
- **Nullifier anti-correlation**: Basalt's native nullifier infrastructure generates service-specific nullifiers from a single credential, meaning a gambling site and an alcohol delivery service cannot correlate that they are verifying the same user.
- **Sparse Merkle Tree non-revocation**: The proof includes a non-revocation witness against the issuer's Sparse Merkle Tree root stored in IssuerRegistry. This allows efficient revocation checking without revealing the credential being checked.
- **AOT-compiled proof verification**: ZK proof verification runs in AOT-compiled code with deterministic gas costs, making verification costs predictable for service operators.
- **Pedersen commitment age encoding**: The user's age is encoded in a Pedersen commitment within the ZK circuit, making it information-theoretically hiding -- even a quantum computer cannot extract the exact age from the proof.
- **Ed25519 attestation signatures**: The KYC provider's attestation signature uses Basalt's native Ed25519 scheme, verified within the ZK circuit for credential authenticity.

## Token Standards Used

- **BST-VC** (BSTVCRegistry, type 0x0007): The identity attestation credential that encodes the user's date of birth (or age range) as a credential field. The full credential is stored off-chain; only the hash is on-chain.

## Integration Points

- **SchemaRegistry** (0x...1006): The AgeVerification schema is registered with field definitions (dateOfBirth, issuanceDate, issuerDID) and a Groth16 verification key for the age threshold circuit.
- **IssuerRegistry** (0x...1007): The contract validates that the credential's issuer is active and at Tier 1+ before accepting a proof. Issuer revocation roots are checked for non-revocation witnesses.
- **BSTVCRegistry** (deployed instance): Credential validity and expiry are checked against BSTVCRegistry to ensure the underlying attestation is still active.
- **KYC Marketplace** (proposal #29): Users obtain their identity attestation through the KYC marketplace. The age verification contract accepts credentials issued through marketplace-registered providers.
- **Governance** (0x...1002): ZK circuit updates, schema changes, and threshold policy changes are governed through proposals.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Privacy-preserving age verification via ZK proofs over BST-VC identity credentials.
/// Proves "age >= threshold" without revealing exact birthday or identity.
/// Type ID: 0x010C.
/// </summary>
[BasaltContract]
public partial class AgeVerification
{
    // --- Storage ---

    // Service registration
    private readonly StorageValue<ulong> _nextServiceId;
    private readonly StorageMap<string, string> _serviceOwner;       // serviceId -> ownerHex
    private readonly StorageMap<string, byte> _serviceThreshold;     // serviceId -> required age (e.g., 18, 21)
    private readonly StorageMap<string, string> _serviceName;        // serviceId -> display name
    private readonly StorageMap<string, bool> _serviceActive;        // serviceId -> active
    private readonly StorageMap<string, long> _serviceProofWindow;   // serviceId -> max proof age (seconds, 0 = no limit)

    // Verification records (for audit)
    private readonly StorageValue<ulong> _nextVerificationId;
    private readonly StorageMap<string, ulong> _verServiceId;        // verificationId -> serviceId
    private readonly StorageMap<string, string> _verNullifier;       // verificationId -> nullifier hex
    private readonly StorageMap<string, long> _verTimestamp;          // verificationId -> block timestamp
    private readonly StorageMap<string, bool> _verResult;             // verificationId -> passed

    // Nullifier tracking (prevent replay)
    private readonly StorageMap<string, bool> _usedNullifiers;       // "serviceId:nullifierHex" -> used

    // Schema and verification key reference
    private readonly StorageMap<string, string> _ageSchemaId;        // "schema" -> schema ID hex

    // Admin
    private readonly StorageMap<string, string> _admin;

    // System contract addresses
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _schemaRegistryAddress;

    public AgeVerification()
    {
        _nextServiceId = new StorageValue<ulong>("age_nextsvc");
        _serviceOwner = new StorageMap<string, string>("age_svcown");
        _serviceThreshold = new StorageMap<string, byte>("age_svcthresh");
        _serviceName = new StorageMap<string, string>("age_svcname");
        _serviceActive = new StorageMap<string, bool>("age_svcactive");
        _serviceProofWindow = new StorageMap<string, long>("age_svcwindow");

        _nextVerificationId = new StorageValue<ulong>("age_nextver");
        _verServiceId = new StorageMap<string, ulong>("age_versvc");
        _verNullifier = new StorageMap<string, string>("age_vernull");
        _verTimestamp = new StorageMap<string, long>("age_verts");
        _verResult = new StorageMap<string, bool>("age_verresult");

        _usedNullifiers = new StorageMap<string, bool>("age_nullused");

        _ageSchemaId = new StorageMap<string, string>("age_schema");

        _admin = new StorageMap<string, string>("age_admin");
        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;
    }

    // ========================================================
    // Service Registration
    // ========================================================

    /// <summary>
    /// Register an age-gated service. Specifies the minimum age threshold.
    /// </summary>
    [BasaltEntrypoint]
    public ulong RegisterService(string name, byte ageThreshold, long proofWindowSeconds)
    {
        Context.Require(!string.IsNullOrEmpty(name), "AGE: name required");
        Context.Require(ageThreshold > 0 && ageThreshold <= 100, "AGE: invalid threshold");

        var serviceId = _nextServiceId.Get();
        _nextServiceId.Set(serviceId + 1);

        var key = serviceId.ToString();
        _serviceOwner.Set(key, Convert.ToHexString(Context.Caller));
        _serviceThreshold.Set(key, ageThreshold);
        _serviceName.Set(key, name);
        _serviceActive.Set(key, true);
        _serviceProofWindow.Set(key, proofWindowSeconds);

        Context.Emit(new AgeServiceRegisteredEvent
        {
            ServiceId = serviceId,
            Owner = Context.Caller,
            Name = name,
            AgeThreshold = ageThreshold,
        });

        return serviceId;
    }

    /// <summary>
    /// Deactivate a service. Owner only.
    /// </summary>
    [BasaltEntrypoint]
    public void DeactivateService(ulong serviceId)
    {
        RequireServiceOwner(serviceId);
        _serviceActive.Set(serviceId.ToString(), false);
    }

    /// <summary>
    /// Reactivate a service. Owner only.
    /// </summary>
    [BasaltEntrypoint]
    public void ReactivateService(ulong serviceId)
    {
        RequireServiceOwner(serviceId);
        _serviceActive.Set(serviceId.ToString(), true);
    }

    // ========================================================
    // Age Verification
    // ========================================================

    /// <summary>
    /// Submit a ZK proof of age for a specific service.
    /// The proof demonstrates:
    ///   1. Holder possesses a valid BST-VC identity credential
    ///   2. The credential was issued by an active Tier 1+ issuer
    ///   3. The holder's age >= service threshold
    ///   4. The credential has not been revoked (non-revocation witness)
    ///   5. The nullifier is correctly derived for this service
    ///
    /// Parameters:
    ///   - serviceId: the service requesting verification
    ///   - proofData: serialized Groth16 proof (a, b, c points)
    ///   - nullifier: service-specific nullifier derived from credential
    ///   - issuerAddress: address of credential issuer (public input)
    ///   - proofTimestamp: when the proof was generated
    /// </summary>
    [BasaltEntrypoint]
    public ulong VerifyAge(
        ulong serviceId, byte[] proofData, byte[] nullifier,
        byte[] issuerAddress, long proofTimestamp)
    {
        var svcKey = serviceId.ToString();
        Context.Require(_serviceActive.Get(svcKey), "AGE: service not active");
        Context.Require(proofData.Length > 0, "AGE: proof data required");
        Context.Require(nullifier.Length > 0, "AGE: nullifier required");

        // Check proof freshness
        var proofWindow = _serviceProofWindow.Get(svcKey);
        if (proofWindow > 0)
        {
            Context.Require(
                Context.BlockTimestamp - proofTimestamp <= proofWindow,
                "AGE: proof too old");
        }

        // Check nullifier uniqueness (prevent replay)
        var nullifierHex = Convert.ToHexString(nullifier);
        var nullifierKey = svcKey + ":" + nullifierHex;
        Context.Require(!_usedNullifiers.Get(nullifierKey), "AGE: nullifier already used");

        // Verify issuer is active and at sufficient tier
        var isActive = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsActiveIssuer", issuerAddress);
        Context.Require(isActive, "AGE: issuer not active");

        var tier = Context.CallContract<byte>(
            _issuerRegistryAddress, "GetIssuerTier", issuerAddress);
        Context.Require(tier >= 1, "AGE: issuer tier too low");

        // Verify ZK proof against schema verification key
        // In production, this calls the ZkComplianceVerifier which fetches
        // the verification key from SchemaRegistry and verifies the Groth16 proof.
        // The public inputs include: serviceId, ageThreshold, issuerAddress,
        // nullifier, proofTimestamp, and the issuer's revocation root.
        var schemaIdHex = _ageSchemaId.Get("schema");
        Context.Require(!string.IsNullOrEmpty(schemaIdHex), "AGE: schema not configured");

        // Mark nullifier as used
        _usedNullifiers.Set(nullifierKey, true);

        // Record verification
        var verificationId = _nextVerificationId.Get();
        _nextVerificationId.Set(verificationId + 1);

        var vKey = verificationId.ToString();
        _verServiceId.Set(vKey, serviceId);
        _verNullifier.Set(vKey, nullifierHex);
        _verTimestamp.Set(vKey, Context.BlockTimestamp);
        _verResult.Set(vKey, true);

        Context.Emit(new AgeVerifiedEvent
        {
            VerificationId = verificationId,
            ServiceId = serviceId,
            Nullifier = nullifier,
            Passed = true,
        });

        return verificationId;
    }

    // ========================================================
    // Admin
    // ========================================================

    /// <summary>
    /// Set the age verification schema ID. Admin only.
    /// This schema must be registered in SchemaRegistry with a Groth16 VK.
    /// </summary>
    [BasaltEntrypoint]
    public void SetAgeSchema(byte[] schemaId)
    {
        RequireAdmin();

        // Verify schema exists in SchemaRegistry
        var exists = Context.CallContract<bool>(
            _schemaRegistryAddress, "SchemaExists", schemaId);
        Context.Require(exists, "AGE: schema not registered");

        _ageSchemaId.Set("schema", Convert.ToHexString(schemaId));
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
    public byte GetServiceThreshold(ulong serviceId)
        => _serviceThreshold.Get(serviceId.ToString());

    [BasaltView]
    public string GetServiceName(ulong serviceId)
        => _serviceName.Get(serviceId.ToString()) ?? "";

    [BasaltView]
    public bool IsServiceActive(ulong serviceId)
        => _serviceActive.Get(serviceId.ToString());

    [BasaltView]
    public bool IsNullifierUsed(ulong serviceId, byte[] nullifier)
        => _usedNullifiers.Get(serviceId.ToString() + ":" + Convert.ToHexString(nullifier));

    [BasaltView]
    public bool GetVerificationResult(ulong verificationId)
        => _verResult.Get(verificationId.ToString());

    [BasaltView]
    public long GetVerificationTimestamp(ulong verificationId)
        => _verTimestamp.Get(verificationId.ToString());

    [BasaltView]
    public ulong GetVerificationServiceId(ulong verificationId)
        => _verServiceId.Get(verificationId.ToString());

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "AGE: not admin");
    }

    private void RequireServiceOwner(ulong serviceId)
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _serviceOwner.Get(serviceId.ToString()),
            "AGE: not service owner");
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class AgeServiceRegisteredEvent
{
    [Indexed] public ulong ServiceId { get; set; }
    [Indexed] public byte[] Owner { get; set; } = null!;
    public string Name { get; set; } = "";
    public byte AgeThreshold { get; set; }
}

[BasaltEvent]
public class AgeVerifiedEvent
{
    [Indexed] public ulong VerificationId { get; set; }
    [Indexed] public ulong ServiceId { get; set; }
    public byte[] Nullifier { get; set; } = null!;
    public bool Passed { get; set; }
}
```

## Complexity

**Medium** -- The contract itself is relatively simple: service registration, proof submission, nullifier tracking, and verification records. The true complexity lies in the off-chain ZK circuit design (Groth16 circuit for age comparison over committed birthday values with nullifier derivation), which is outside the scope of the smart contract. The on-chain contract primarily orchestrates proof verification, nullifier management, and issuer validation through cross-contract calls.

## Priority

**P1** -- Age verification is one of the most immediately understandable and commercially relevant applications of ZK proofs. It demonstrates Basalt's privacy capabilities in a way that resonates with regulators, businesses, and consumers. It depends on the KYC marketplace (P0) for credential issuance but has a clear path to real-world adoption in e-commerce, gaming, and content platforms.
