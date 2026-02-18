namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-VC Verifiable Credential Registry Standard.
/// On-chain registry for verifiable credential lifecycle: issuance records,
/// status tracking, revocation. Full VCs stored off-chain, only hashes on-chain.
/// </summary>
public interface IBSTVC
{
    string IssueCredential(byte[] credentialHash, byte[] subjectDid, byte[] schemaId,
        long validUntil, string metadataUri);
    void RevokeCredential(byte[] credentialHash, string reason);
    void SuspendCredential(byte[] credentialHash, string reason);
    void ReinstateCredential(byte[] credentialHash);
    byte GetCredentialStatus(byte[] credentialHash);
    byte[] GetCredentialIssuer(byte[] credentialHash);
    byte[] GetCredentialSubject(byte[] credentialHash);
    byte[] GetCredentialSchema(byte[] credentialHash);
    long GetCredentialExpiry(byte[] credentialHash);
    bool IsCredentialValid(byte[] credentialHash);
    string GetCredentialMetadataUri(byte[] credentialHash);
    ulong GetIssuerCredentialCount(byte[] issuer);
    bool HasIssuerIssuedCredential(byte[] issuer, byte[] credentialHash);
    bool VerifyCredentialSet(byte[] credentialHash);
}

[BasaltEvent]
public sealed class CredentialIssuedEvent
{
    [Indexed] public byte[] CredentialHash { get; init; } = [];
    [Indexed] public byte[] Issuer { get; init; } = [];
    [Indexed] public byte[] Subject { get; init; } = [];
    public string CredentialId { get; init; } = "";
}

[BasaltEvent]
public sealed class CredentialRevokedEvent
{
    [Indexed] public byte[] CredentialHash { get; init; } = [];
    [Indexed] public byte[] Issuer { get; init; } = [];
    public string Reason { get; init; } = "";
}

[BasaltEvent]
public sealed class CredentialSuspendedEvent
{
    [Indexed] public byte[] CredentialHash { get; init; } = [];
    [Indexed] public byte[] Issuer { get; init; } = [];
    public string Reason { get; init; } = "";
}

[BasaltEvent]
public sealed class CredentialReinstatedEvent
{
    [Indexed] public byte[] CredentialHash { get; init; } = [];
    [Indexed] public byte[] Issuer { get; init; } = [];
}
