namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-VC Verifiable Credential Registry — on-chain credential lifecycle management.
/// Full VCs stored off-chain (IPFS), only hashes on-chain.
/// Status: Active → Suspended ↔ Reinstated → Revoked (terminal).
/// Type ID: 0x0007
/// </summary>
[BasaltContract]
public partial class BSTVCRegistry : IBSTVC
{
    private readonly StorageMap<string, byte> _status;
    private readonly StorageMap<string, string> _issuers;
    private readonly StorageMap<string, string> _subjects;
    private readonly StorageMap<string, string> _schemas;
    private readonly StorageMap<string, long> _expiries;
    private readonly StorageMap<string, string> _metadataUris;
    private readonly StorageMap<string, string> _credentialIds;
    private readonly StorageMap<string, ulong> _issuerCounts;
    private readonly StorageMap<string, bool> _issuerCredentials;
    private readonly StorageMap<string, string> _revocationReasons;
    private readonly StorageValue<ulong> _nextCredIndex;

    private const byte StatusUnknown = 0;
    private const byte StatusActive = 1;
    private const byte StatusRevoked = 2;
    private const byte StatusSuspended = 3;

    public BSTVCRegistry()
    {
        _status = new StorageMap<string, byte>("vc_status");
        _issuers = new StorageMap<string, string>("vc_issuer");
        _subjects = new StorageMap<string, string>("vc_subject");
        _schemas = new StorageMap<string, string>("vc_schema");
        _expiries = new StorageMap<string, long>("vc_expiry");
        _metadataUris = new StorageMap<string, string>("vc_meta");
        _credentialIds = new StorageMap<string, string>("vc_id");
        _issuerCounts = new StorageMap<string, ulong>("vc_icount");
        _issuerCredentials = new StorageMap<string, bool>("vc_icred");
        _revocationReasons = new StorageMap<string, string>("vc_reason");
        _nextCredIndex = new StorageValue<ulong>("vc_next");
    }

    [BasaltEntrypoint]
    public string IssueCredential(byte[] credentialHash, byte[] subjectDid,
        byte[] schemaId, long validUntil, string metadataUri)
    {
        Context.Require(credentialHash.Length > 0, "VC: empty credential hash");
        Context.Require(subjectDid.Length > 0, "VC: empty subject");
        Context.Require(schemaId.Length > 0, "VC: empty schema");
        Context.Require(validUntil > Context.BlockTimestamp, "VC: expiry must be in future");

        var credHashHex = Convert.ToHexString(credentialHash);
        Context.Require(_status.Get(credHashHex) == StatusUnknown,
            "VC: credential already exists");

        var issuerHex = Convert.ToHexString(Context.Caller);

        var index = _nextCredIndex.Get();
        _nextCredIndex.Set(index + 1);
        var credentialId = "vc:" + issuerHex + ":" + index.ToString("x16");

        _status.Set(credHashHex, StatusActive);
        _issuers.Set(credHashHex, issuerHex);
        _subjects.Set(credHashHex, Convert.ToHexString(subjectDid));
        _schemas.Set(credHashHex, Convert.ToHexString(schemaId));
        _expiries.Set(credHashHex, validUntil);
        _metadataUris.Set(credHashHex, metadataUri);
        _credentialIds.Set(credHashHex, credentialId);

        var count = _issuerCounts.Get(issuerHex);
        _issuerCounts.Set(issuerHex, count + 1);
        _issuerCredentials.Set(issuerHex + ":" + credHashHex, true);

        Context.Emit(new CredentialIssuedEvent
        {
            CredentialHash = credentialHash,
            Issuer = Context.Caller,
            Subject = subjectDid,
            CredentialId = credentialId,
        });

        return credentialId;
    }

    [BasaltEntrypoint]
    public void RevokeCredential(byte[] credentialHash, string reason)
    {
        var credHashHex = Convert.ToHexString(credentialHash);
        RequireIssuer(credHashHex);
        var status = _status.Get(credHashHex);
        Context.Require(status == StatusActive || status == StatusSuspended,
            "VC: cannot revoke");

        _status.Set(credHashHex, StatusRevoked);
        _revocationReasons.Set(credHashHex, reason);

        Context.Emit(new CredentialRevokedEvent
        {
            CredentialHash = credentialHash,
            Issuer = Context.Caller,
            Reason = reason,
        });
    }

    [BasaltEntrypoint]
    public void SuspendCredential(byte[] credentialHash, string reason)
    {
        var credHashHex = Convert.ToHexString(credentialHash);
        RequireIssuer(credHashHex);
        Context.Require(_status.Get(credHashHex) == StatusActive,
            "VC: not active");

        _status.Set(credHashHex, StatusSuspended);
        _revocationReasons.Set(credHashHex, reason);

        Context.Emit(new CredentialSuspendedEvent
        {
            CredentialHash = credentialHash,
            Issuer = Context.Caller,
            Reason = reason,
        });
    }

    [BasaltEntrypoint]
    public void ReinstateCredential(byte[] credentialHash)
    {
        var credHashHex = Convert.ToHexString(credentialHash);
        RequireIssuer(credHashHex);
        Context.Require(_status.Get(credHashHex) == StatusSuspended,
            "VC: not suspended");

        _status.Set(credHashHex, StatusActive);
        _revocationReasons.Delete(credHashHex);

        Context.Emit(new CredentialReinstatedEvent
        {
            CredentialHash = credentialHash,
            Issuer = Context.Caller,
        });
    }

    [BasaltView]
    public byte GetCredentialStatus(byte[] credentialHash)
        => _status.Get(Convert.ToHexString(credentialHash));

    [BasaltView]
    public byte[] GetCredentialIssuer(byte[] credentialHash)
    {
        var hex = _issuers.Get(Convert.ToHexString(credentialHash));
        return string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex);
    }

    [BasaltView]
    public byte[] GetCredentialSubject(byte[] credentialHash)
    {
        var hex = _subjects.Get(Convert.ToHexString(credentialHash));
        return string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex);
    }

    [BasaltView]
    public byte[] GetCredentialSchema(byte[] credentialHash)
    {
        var hex = _schemas.Get(Convert.ToHexString(credentialHash));
        return string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex);
    }

    [BasaltView]
    public long GetCredentialExpiry(byte[] credentialHash)
        => _expiries.Get(Convert.ToHexString(credentialHash));

    [BasaltView]
    public bool IsCredentialValid(byte[] credentialHash)
    {
        var credHashHex = Convert.ToHexString(credentialHash);
        if (_status.Get(credHashHex) != StatusActive)
            return false;
        return _expiries.Get(credHashHex) > Context.BlockTimestamp;
    }

    [BasaltView]
    public string GetCredentialMetadataUri(byte[] credentialHash)
        => _metadataUris.Get(Convert.ToHexString(credentialHash)) ?? "";

    [BasaltView]
    public ulong GetIssuerCredentialCount(byte[] issuer)
        => _issuerCounts.Get(Convert.ToHexString(issuer));

    [BasaltView]
    public bool HasIssuerIssuedCredential(byte[] issuer, byte[] credentialHash)
        => _issuerCredentials.Get(
            Convert.ToHexString(issuer) + ":" + Convert.ToHexString(credentialHash));

    [BasaltView]
    public bool VerifyCredentialSet(byte[] credentialHash)
    {
        if (!IsCredentialValid(credentialHash))
            return false;
        var issuerHex = _issuers.Get(Convert.ToHexString(credentialHash));
        return !string.IsNullOrEmpty(issuerHex);
    }

    private void RequireIssuer(string credHashHex)
    {
        var issuerHex = _issuers.Get(credHashHex);
        Context.Require(!string.IsNullOrEmpty(issuerHex), "VC: credential not found");
        Context.Require(Convert.ToHexString(Context.Caller) == issuerHex,
            "VC: not issuer");
    }
}
