namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-DID Decentralized Identity Standard.
/// Provides on-chain decentralized identifiers and verifiable credential management.
/// </summary>
public interface IBSTDID
{
    /// <summary>Register a new DID.</summary>
    string RegisterDID(byte[] controller);

    /// <summary>Resolve a DID to its document.</summary>
    DIDDocument? ResolveDID(string did);

    /// <summary>Add an attestation (verifiable credential) to a DID.</summary>
    void AddAttestation(string did, string credentialType, string issuer, long expiresAt, byte[] data);

    /// <summary>Revoke an attestation.</summary>
    void RevokeAttestation(string did, string attestationId);

    /// <summary>Check if a DID has a valid attestation of a given type.</summary>
    bool HasValidAttestation(string did, string credentialType);

    /// <summary>Transfer control of a DID to a new controller.</summary>
    void TransferDID(string did, byte[] newController);

    /// <summary>Deactivate a DID.</summary>
    void DeactivateDID(string did);
}

/// <summary>
/// DID Document stored on-chain.
/// </summary>
public sealed class DIDDocument
{
    public string Id { get; init; } = "";
    public byte[] Controller { get; init; } = [];
    public long CreatedAt { get; init; }
    public long UpdatedAt { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>
/// On-chain attestation (verifiable credential reference).
/// </summary>
public sealed class Attestation
{
    public string Id { get; init; } = "";
    public string CredentialType { get; init; } = "";
    public string Issuer { get; init; } = "";
    public long IssuedAt { get; init; }
    public long ExpiresAt { get; init; }
    public bool Revoked { get; set; }
    public byte[] Data { get; init; } = [];
}

/// <summary>
/// Event emitted when a DID is registered.
/// </summary>
[BasaltEvent]
public sealed class DIDRegisteredEvent
{
    [Indexed] public string DID { get; init; } = "";
    [Indexed] public byte[] Controller { get; init; } = [];
}

/// <summary>
/// Event emitted when an attestation is added.
/// </summary>
[BasaltEvent]
public sealed class AttestationAddedEvent
{
    [Indexed] public string DID { get; init; } = "";
    public string CredentialType { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string AttestationId { get; init; } = "";
}

/// <summary>
/// Event emitted when an attestation is revoked.
/// </summary>
[BasaltEvent]
public sealed class AttestationRevokedEvent
{
    [Indexed] public string DID { get; init; } = "";
    public string AttestationId { get; init; } = "";
}
