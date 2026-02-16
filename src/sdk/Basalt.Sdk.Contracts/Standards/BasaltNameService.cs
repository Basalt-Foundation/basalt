namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Basalt Name Service (BNS) — register human-readable names mapped to addresses.
/// Type ID: 0x0101
/// </summary>
[BasaltContract]
public partial class BasaltNameService
{
    private readonly StorageMap<string, string> _owners;      // name -> owner hex
    private readonly StorageMap<string, string> _addresses;   // name -> target hex
    private readonly StorageMap<string, string> _reverse;     // address hex -> name
    private readonly StorageValue<ulong> _registrationFee;

    public BasaltNameService(ulong registrationFee = 1_000_000_000)
    {
        _owners = new StorageMap<string, string>("bns_owners");
        _addresses = new StorageMap<string, string>("bns_addrs");
        _reverse = new StorageMap<string, string>("bns_rev");
        _registrationFee = new StorageValue<ulong>("bns_fee");
        _registrationFee.Set(registrationFee);
    }

    /// <summary>
    /// Register a name. Requires payment >= registration fee.
    /// </summary>
    [BasaltEntrypoint]
    public void Register(string name)
    {
        Context.Require(!string.IsNullOrEmpty(name), "BNS: name required");
        Context.Require(name.Length <= 64, "BNS: name too long");
        Context.Require(string.IsNullOrEmpty(_owners.Get(name)), "BNS: name taken");
        Context.Require(Context.TxValue >= _registrationFee.Get(), "BNS: insufficient fee");

        var callerHex = Convert.ToHexString(Context.Caller);
        _owners.Set(name, callerHex);
        _addresses.Set(name, callerHex);

        Context.Emit(new NameRegisteredEvent { Name = name, Owner = Context.Caller });
    }

    /// <summary>
    /// Resolve a name to its target address.
    /// </summary>
    [BasaltView]
    public byte[] Resolve(string name)
    {
        var hex = _addresses.Get(name);
        Context.Require(!string.IsNullOrEmpty(hex), "BNS: name not found");
        return Convert.FromHexString(hex);
    }

    /// <summary>
    /// Set the target address for a name you own.
    /// </summary>
    [BasaltEntrypoint]
    public void SetAddress(string name, byte[] target)
    {
        var ownerHex = _owners.Get(name);
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BNS: name not found");
        Context.Require(ownerHex == Convert.ToHexString(Context.Caller), "BNS: not owner");

        _addresses.Set(name, Convert.ToHexString(target));
    }

    /// <summary>
    /// Set a reverse lookup (address -> name) for the caller.
    /// </summary>
    [BasaltEntrypoint]
    public void SetReverse(string name)
    {
        var ownerHex = _owners.Get(name);
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BNS: name not found");
        Context.Require(ownerHex == Convert.ToHexString(Context.Caller), "BNS: not owner");

        _reverse.Set(Convert.ToHexString(Context.Caller), name);
    }

    /// <summary>
    /// Reverse-resolve an address to a name.
    /// </summary>
    [BasaltView]
    public string ReverseLookup(byte[] addr)
    {
        return _reverse.Get(Convert.ToHexString(addr)) ?? "";
    }

    /// <summary>
    /// Transfer name ownership.
    /// </summary>
    [BasaltEntrypoint]
    public void TransferName(string name, byte[] newOwner)
    {
        var ownerHex = _owners.Get(name);
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BNS: name not found");
        Context.Require(ownerHex == Convert.ToHexString(Context.Caller), "BNS: not owner");

        _owners.Set(name, Convert.ToHexString(newOwner));

        Context.Emit(new NameTransferredEvent
        {
            Name = name,
            PreviousOwner = Context.Caller,
            NewOwner = newOwner,
        });
    }

    /// <summary>
    /// Get owner of a name.
    /// </summary>
    [BasaltView]
    public byte[] OwnerOf(string name)
    {
        var hex = _owners.Get(name);
        if (string.IsNullOrEmpty(hex)) return new byte[20];
        return Convert.FromHexString(hex);
    }
}

[BasaltEvent]
public class NameRegisteredEvent
{
    [Indexed] public byte[] Owner { get; set; } = null!;
    public string Name { get; set; } = "";
}

[BasaltEvent]
public class NameTransferredEvent
{
    public string Name { get; set; } = "";
    [Indexed] public byte[] PreviousOwner { get; set; } = null!;
    [Indexed] public byte[] NewOwner { get; set; } = null!;
}
