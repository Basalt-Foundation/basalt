namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Reference implementation of the BST-DID Decentralized Identity Standard.
/// </summary>
[BasaltContract]
public class BSTDIDRegistry : IBSTDID
{
    private readonly StorageMap<string, string> _controllers;       // did -> controller (hex)
    private readonly StorageMap<string, string> _documents;          // did -> serialized document
    private readonly StorageMap<string, string> _attestations;       // "did:attId" -> attestation data
    private readonly StorageMap<string, string> _attestationTypes;   // "did:type" -> attestation ID
    private readonly StorageMap<string, string> _attestationStatus;  // "did:attId:revoked" -> "1"/"0"
    private readonly StorageValue<ulong> _nextDIDIndex;
    private readonly string _chainPrefix;

    public BSTDIDRegistry(string chainPrefix = "did:basalt:")
    {
        _chainPrefix = chainPrefix;
        _controllers = new StorageMap<string, string>("did_ctrl");
        _documents = new StorageMap<string, string>("did_doc");
        _attestations = new StorageMap<string, string>("did_att");
        _attestationTypes = new StorageMap<string, string>("did_att_type");
        _attestationStatus = new StorageMap<string, string>("did_att_status");
        _nextDIDIndex = new StorageValue<ulong>("did_next");
    }

    [BasaltEntrypoint]
    public string RegisterDID(byte[] controller)
    {
        var index = _nextDIDIndex.Get();
        _nextDIDIndex.Set(index + 1);

        var did = $"{_chainPrefix}{index:x16}";
        var controllerHex = Convert.ToHexString(controller);

        _controllers.Set(did, controllerHex);

        Context.Emit(new DIDRegisteredEvent
        {
            DID = did,
            Controller = controller,
        });

        return did;
    }

    [BasaltView]
    public DIDDocument? ResolveDID(string did)
    {
        var controllerHex = _controllers.Get(did);
        if (string.IsNullOrEmpty(controllerHex))
            return null;

        return new DIDDocument
        {
            Id = did,
            Controller = Convert.FromHexString(controllerHex),
            Active = _attestationStatus.Get($"{did}:deactivated") != "1",
        };
    }

    [BasaltEntrypoint]
    public void AddAttestation(string did, string credentialType, string issuer, long expiresAt, byte[] data)
    {
        var controllerHex = _controllers.Get(did);
        Context.Require(!string.IsNullOrEmpty(controllerHex), "BST-DID: DID not found");

        // Only controller or issuer can add attestations
        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(callerHex == controllerHex || callerHex == issuer,
            "BST-DID: not authorized");

        var attId = $"{credentialType}:{Context.BlockHeight}";
        _attestations.Set($"{did}:{attId}", Convert.ToHexString(data));
        _attestationTypes.Set($"{did}:{credentialType}", attId);
        _attestationStatus.Set($"{did}:{attId}:revoked", "0");

        Context.Emit(new AttestationAddedEvent
        {
            DID = did,
            CredentialType = credentialType,
            Issuer = issuer,
            AttestationId = attId,
        });
    }

    [BasaltEntrypoint]
    public void RevokeAttestation(string did, string attestationId)
    {
        var controllerHex = _controllers.Get(did);
        Context.Require(!string.IsNullOrEmpty(controllerHex), "BST-DID: DID not found");

        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(callerHex == controllerHex, "BST-DID: not controller");

        _attestationStatus.Set($"{did}:{attestationId}:revoked", "1");

        Context.Emit(new AttestationRevokedEvent
        {
            DID = did,
            AttestationId = attestationId,
        });
    }

    [BasaltView]
    public bool HasValidAttestation(string did, string credentialType)
    {
        var attId = _attestationTypes.Get($"{did}:{credentialType}");
        if (string.IsNullOrEmpty(attId))
            return false;

        var revoked = _attestationStatus.Get($"{did}:{attId}:revoked");
        return revoked != "1";
    }

    [BasaltEntrypoint]
    public void TransferDID(string did, byte[] newController)
    {
        var controllerHex = _controllers.Get(did);
        Context.Require(!string.IsNullOrEmpty(controllerHex), "BST-DID: DID not found");

        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(callerHex == controllerHex, "BST-DID: not controller");

        _controllers.Set(did, Convert.ToHexString(newController));
    }

    [BasaltEntrypoint]
    public void DeactivateDID(string did)
    {
        var controllerHex = _controllers.Get(did);
        Context.Require(!string.IsNullOrEmpty(controllerHex), "BST-DID: DID not found");

        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(callerHex == controllerHex, "BST-DID: not controller");

        _attestationStatus.Set($"{did}:deactivated", "1");
    }
}
