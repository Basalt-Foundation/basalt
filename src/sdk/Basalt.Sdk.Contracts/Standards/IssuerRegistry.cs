namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// On-chain registry of credential issuers with trust tiers and collateral staking.
/// Tier 0: Self-attestation (anyone, no collateral).
/// Tier 1: Regulated entity (admin-approved, no collateral).
/// Tier 2: Accredited provider (requires BST collateral stake via TxValue).
/// Tier 3: Sovereign/eIDAS (admin-approved, no collateral).
/// </summary>
[BasaltContract]
public partial class IssuerRegistry
{
    private readonly StorageMap<string, string> _admin;      // "admin" -> admin hex
    private readonly StorageMap<string, byte> _tiers;        // issuerHex -> tier (0-3)
    private readonly StorageMap<string, string> _names;      // issuerHex -> display name
    private readonly StorageMap<string, ulong> _stakes;      // issuerHex -> collateral staked
    private readonly StorageMap<string, bool> _active;       // issuerHex -> active flag
    private readonly StorageMap<string, string> _revRoots;   // issuerHex -> revocation tree root hex
    private readonly StorageMap<string, bool> _schemas;      // "issuerHex:schemaIdHex" -> supports schema

    public IssuerRegistry()
    {
        _admin = new StorageMap<string, string>("ir_admin");
        _tiers = new StorageMap<string, byte>("ir_tier");
        _names = new StorageMap<string, string>("ir_name");
        _stakes = new StorageMap<string, ulong>("ir_stake");
        _active = new StorageMap<string, bool>("ir_active");
        _revRoots = new StorageMap<string, string>("ir_revroot");
        _schemas = new StorageMap<string, bool>("ir_schemas");

        // Set deployer as initial admin
        _admin.Set("admin", Convert.ToHexString(Context.Caller));
    }

    /// <summary>
    /// Transfer admin role to a new address. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(callerHex == _admin.Get("admin"), "ISSUER: not admin");

        var oldAdminHex = _admin.Get("admin");
        _admin.Set("admin", Convert.ToHexString(newAdmin));

        Context.Emit(new AdminTransferredEvent
        {
            OldAdmin = Convert.FromHexString(oldAdminHex!),
            NewAdmin = newAdmin,
        });
    }

    /// <summary>
    /// Register an issuer at a given tier.
    /// Tier 0: anyone can register self. Tier 1/3: admin only. Tier 2: anyone but requires collateral later.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterIssuer(string name, byte tier)
    {
        Context.Require(tier <= 3, "ISSUER: invalid tier");
        Context.Require(!string.IsNullOrEmpty(name), "ISSUER: name required");

        var callerHex = Convert.ToHexString(Context.Caller);
        var adminHex = _admin.Get("admin");

        if (tier == 1 || tier == 3)
        {
            Context.Require(callerHex == adminHex, "ISSUER: admin only for tier 1/3");
        }

        // For tier 0, the issuer is the caller themselves
        // For tier 1/3, admin registers the issuer (also the caller for simplicity)
        // For tier 2, anyone can register but must stake collateral via StakeCollateral
        var issuerHex = callerHex;

        _tiers.Set(issuerHex, tier);
        _names.Set(issuerHex, name);
        _active.Set(issuerHex, true);

        Context.Emit(new IssuerRegisteredEvent
        {
            Issuer = Context.Caller,
            Name = name,
            Tier = tier,
        });
    }

    /// <summary>
    /// Tier 2 issuers deposit BST collateral. Uses Context.TxValue.
    /// Adds to existing stake.
    /// </summary>
    [BasaltEntrypoint]
    public void StakeCollateral()
    {
        Context.Require(Context.TxValue > 0, "ISSUER: must send value");

        var issuerHex = Convert.ToHexString(Context.Caller);
        Context.Require(!string.IsNullOrEmpty(_names.Get(issuerHex)), "ISSUER: not registered");
        Context.Require(_active.Get(issuerHex), "ISSUER: not active");

        var currentStake = _stakes.Get(issuerHex);
        var newStake = currentStake + Context.TxValue;
        _stakes.Set(issuerHex, newStake);

        Context.Emit(new CollateralStakedEvent
        {
            Issuer = Context.Caller,
            Amount = Context.TxValue,
            TotalStake = newStake,
        });
    }

    /// <summary>
    /// Issuer updates their Sparse Merkle Tree revocation root.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateRevocationRoot(byte[] newRoot)
    {
        var issuerHex = Convert.ToHexString(Context.Caller);
        Context.Require(!string.IsNullOrEmpty(_names.Get(issuerHex)), "ISSUER: not registered");
        Context.Require(_active.Get(issuerHex), "ISSUER: not active");

        _revRoots.Set(issuerHex, Convert.ToHexString(newRoot));

        Context.Emit(new RevocationRootUpdatedEvent
        {
            Issuer = Context.Caller,
        });
    }

    /// <summary>
    /// Issuer declares support for a credential schema.
    /// </summary>
    [BasaltEntrypoint]
    public void AddSchemaSupport(byte[] schemaId)
    {
        var issuerHex = Convert.ToHexString(Context.Caller);
        Context.Require(!string.IsNullOrEmpty(_names.Get(issuerHex)), "ISSUER: not registered");
        Context.Require(_active.Get(issuerHex), "ISSUER: not active");

        var compositeKey = issuerHex + ":" + Convert.ToHexString(schemaId);
        _schemas.Set(compositeKey, true);
    }

    /// <summary>
    /// Issuer removes support for a credential schema.
    /// </summary>
    [BasaltEntrypoint]
    public void RemoveSchemaSupport(byte[] schemaId)
    {
        var issuerHex = Convert.ToHexString(Context.Caller);
        Context.Require(!string.IsNullOrEmpty(_names.Get(issuerHex)), "ISSUER: not registered");

        var compositeKey = issuerHex + ":" + Convert.ToHexString(schemaId);
        _schemas.Set(compositeKey, false);
    }

    /// <summary>
    /// Admin slashes an issuer: burns collateral (set stake to 0) and deactivates.
    /// </summary>
    [BasaltEntrypoint]
    public void SlashIssuer(byte[] issuerAddress, string reason)
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(callerHex == _admin.Get("admin"), "ISSUER: not admin");

        var issuerHex = Convert.ToHexString(issuerAddress);
        Context.Require(!string.IsNullOrEmpty(_names.Get(issuerHex)), "ISSUER: not registered");

        var slashedAmount = _stakes.Get(issuerHex);
        _stakes.Set(issuerHex, 0);
        _active.Set(issuerHex, false);

        Context.Emit(new IssuerSlashedEvent
        {
            Issuer = issuerAddress,
            Reason = reason,
            SlashedAmount = slashedAmount,
        });
    }

    /// <summary>
    /// Deactivate an issuer. Admin or self only.
    /// </summary>
    [BasaltEntrypoint]
    public void DeactivateIssuer(byte[] issuerAddress)
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        var issuerHex = Convert.ToHexString(issuerAddress);
        var adminHex = _admin.Get("admin");

        Context.Require(callerHex == adminHex || callerHex == issuerHex, "ISSUER: not authorized");
        Context.Require(!string.IsNullOrEmpty(_names.Get(issuerHex)), "ISSUER: not registered");

        _active.Set(issuerHex, false);

        Context.Emit(new IssuerDeactivatedEvent
        {
            Issuer = issuerAddress,
        });
    }

    /// <summary>
    /// Reactivate an issuer. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void ReactivateIssuer(byte[] issuerAddress)
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(callerHex == _admin.Get("admin"), "ISSUER: not admin");

        var issuerHex = Convert.ToHexString(issuerAddress);
        Context.Require(!string.IsNullOrEmpty(_names.Get(issuerHex)), "ISSUER: not registered");

        _active.Set(issuerHex, true);

        Context.Emit(new IssuerReactivatedEvent
        {
            Issuer = issuerAddress,
        });
    }

    /// <summary>
    /// Get the tier of an issuer.
    /// </summary>
    [BasaltView]
    public byte GetIssuerTier(byte[] issuerAddress)
    {
        return _tiers.Get(Convert.ToHexString(issuerAddress));
    }

    /// <summary>
    /// Check if an issuer is active.
    /// </summary>
    [BasaltView]
    public bool IsActiveIssuer(byte[] issuerAddress)
    {
        return _active.Get(Convert.ToHexString(issuerAddress));
    }

    /// <summary>
    /// Get the revocation tree root for an issuer.
    /// </summary>
    [BasaltView]
    public string GetRevocationRoot(byte[] issuerAddress)
    {
        return _revRoots.Get(Convert.ToHexString(issuerAddress)) ?? "";
    }

    /// <summary>
    /// Get the collateral stake amount for an issuer.
    /// </summary>
    [BasaltView]
    public ulong GetCollateralStake(byte[] issuerAddress)
    {
        return _stakes.Get(Convert.ToHexString(issuerAddress));
    }

    /// <summary>
    /// Check if an issuer supports a specific schema.
    /// </summary>
    [BasaltView]
    public bool SupportsSchema(byte[] issuerAddress, byte[] schemaId)
    {
        var compositeKey = Convert.ToHexString(issuerAddress) + ":" + Convert.ToHexString(schemaId);
        return _schemas.Get(compositeKey);
    }

    /// <summary>
    /// Get the display name of an issuer.
    /// </summary>
    [BasaltView]
    public string GetIssuerName(byte[] issuerAddress)
    {
        return _names.Get(Convert.ToHexString(issuerAddress)) ?? "";
    }

    /// <summary>
    /// Get the current admin address as hex.
    /// </summary>
    [BasaltView]
    public string GetAdmin()
    {
        return _admin.Get("admin") ?? "";
    }
}

[BasaltEvent]
public class IssuerRegisteredEvent
{
    [Indexed] public byte[] Issuer { get; set; } = null!;
    public string Name { get; set; } = "";
    public byte Tier { get; set; }
}

[BasaltEvent]
public class CollateralStakedEvent
{
    [Indexed] public byte[] Issuer { get; set; } = null!;
    public ulong Amount { get; set; }
    public ulong TotalStake { get; set; }
}

[BasaltEvent]
public class RevocationRootUpdatedEvent
{
    [Indexed] public byte[] Issuer { get; set; } = null!;
}

[BasaltEvent]
public class IssuerSlashedEvent
{
    [Indexed] public byte[] Issuer { get; set; } = null!;
    public string Reason { get; set; } = "";
    public ulong SlashedAmount { get; set; }
}

[BasaltEvent]
public class IssuerDeactivatedEvent
{
    [Indexed] public byte[] Issuer { get; set; } = null!;
}

[BasaltEvent]
public class IssuerReactivatedEvent
{
    [Indexed] public byte[] Issuer { get; set; } = null!;
}

[BasaltEvent]
public class AdminTransferredEvent
{
    [Indexed] public byte[] OldAdmin { get; set; } = null!;
    [Indexed] public byte[] NewAdmin { get; set; } = null!;
}
