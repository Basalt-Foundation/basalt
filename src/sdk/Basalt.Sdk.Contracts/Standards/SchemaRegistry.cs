using System.Text;
using Basalt.Crypto;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// On-chain registry of credential schemas.
/// Anyone can register a schema (permissionless). A schema defines WHAT can be proved.
/// SchemaId is derived from BLAKE3(name), first 32 bytes.
/// </summary>
[BasaltContract]
public partial class SchemaRegistry
{
    private readonly StorageMap<string, string> _names;          // schemaIdHex -> schema name
    private readonly StorageMap<string, string> _fields;         // schemaIdHex -> JSON field definitions
    private readonly StorageMap<string, string> _creators;       // schemaIdHex -> creator address hex
    private readonly StorageMap<string, string> _verificationKeys; // schemaIdHex -> hex-encoded Groth16 VK

    public SchemaRegistry()
    {
        _names = new StorageMap<string, string>("scr_name");
        _fields = new StorageMap<string, string>("scr_fields");
        _creators = new StorageMap<string, string>("scr_creator");
        _verificationKeys = new StorageMap<string, string>("scr_vk");
    }

    /// <summary>
    /// Register a new credential schema. SchemaId = BLAKE3(name).
    /// Returns the schema ID as a hex string.
    /// </summary>
    [BasaltEntrypoint]
    public string RegisterSchema(string name, string fieldDefinitions, byte[] verificationKey)
    {
        Context.Require(!string.IsNullOrEmpty(name), "SCHEMA: name required");

        // M-10: Include creator address in schema ID derivation to prevent front-running
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[nameBytes.Length + Context.Caller.Length];
        nameBytes.CopyTo(input, 0);
        Context.Caller.CopyTo(input, nameBytes.Length);
        var hash = Blake3Hasher.Hash(input);
        var schemaIdHex = Convert.ToHexString(hash.ToArray());

        Context.Require(string.IsNullOrEmpty(_names.Get(schemaIdHex)), "SCHEMA: already exists");

        _names.Set(schemaIdHex, name);
        _fields.Set(schemaIdHex, fieldDefinitions);
        _creators.Set(schemaIdHex, Convert.ToHexString(Context.Caller));
        _verificationKeys.Set(schemaIdHex, Convert.ToHexString(verificationKey));

        Context.Emit(new SchemaRegisteredEvent
        {
            SchemaId = hash.ToArray(),
            Creator = Context.Caller,
            Name = name,
        });

        return schemaIdHex;
    }

    /// <summary>
    /// Update the Groth16 verification key for a schema. Creator only.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateVerificationKey(byte[] schemaId, byte[] verificationKey)
    {
        var schemaIdHex = Convert.ToHexString(schemaId);
        var creatorHex = _creators.Get(schemaIdHex);

        Context.Require(!string.IsNullOrEmpty(creatorHex), "SCHEMA: not found");
        Context.Require(Convert.ToHexString(Context.Caller) == creatorHex, "SCHEMA: not creator");

        _verificationKeys.Set(schemaIdHex, Convert.ToHexString(verificationKey));

        Context.Emit(new VerificationKeyUpdatedEvent
        {
            SchemaId = schemaId,
            UpdatedBy = Context.Caller,
        });
    }

    /// <summary>
    /// Get the schema name. Returns empty string if not found.
    /// </summary>
    [BasaltView]
    public string GetSchema(byte[] schemaId)
    {
        return _names.Get(Convert.ToHexString(schemaId)) ?? "";
    }

    /// <summary>
    /// Get the JSON field definitions for a schema.
    /// </summary>
    [BasaltView]
    public string GetFieldDefinitions(byte[] schemaId)
    {
        return _fields.Get(Convert.ToHexString(schemaId)) ?? "";
    }

    /// <summary>
    /// Get the hex-encoded verification key. Returns empty string if not found.
    /// </summary>
    [BasaltView]
    public string GetVerificationKey(byte[] schemaId)
    {
        return _verificationKeys.Get(Convert.ToHexString(schemaId)) ?? "";
    }

    /// <summary>
    /// Check whether a schema exists.
    /// </summary>
    [BasaltView]
    public bool SchemaExists(byte[] schemaId)
    {
        return !string.IsNullOrEmpty(_names.Get(Convert.ToHexString(schemaId)));
    }
}

[BasaltEvent]
public class SchemaRegisteredEvent
{
    [Indexed] public byte[] SchemaId { get; set; } = null!;
    [Indexed] public byte[] Creator { get; set; } = null!;
    public string Name { get; set; } = "";
}

[BasaltEvent]
public class VerificationKeyUpdatedEvent
{
    [Indexed] public byte[] SchemaId { get; set; } = null!;
    [Indexed] public byte[] UpdatedBy { get; set; } = null!;
}
