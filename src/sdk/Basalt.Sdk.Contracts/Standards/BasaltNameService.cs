using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Basalt Name Service (BNS) v2 — human-readable names mapped to an address and, for Trilith, to a
/// self-certifying <c>trilith://</c> content name.
/// Type ID: 0x0101
/// </summary>
/// <remarks>
/// Security model: the chain binds a label to a <c>trilith://&lt;key&gt;</c> string only. That string is a
/// self-certifying content name (its authority is the key it contains), so the chain never hosts or vouches
/// for the content itself, it only provides human-readable, expiring indirection to a name the client still
/// verifies end to end. A registration is time-bounded: a name expires and, after a grace period in which
/// only the owner may renew, becomes available to anyone. Block timestamps are unix milliseconds, so all
/// time arithmetic here divides by 1000 to work in unix seconds.
/// </remarks>
[BasaltContract]
public partial class BasaltNameService
{
    /// <summary>Required scheme prefix for a content record value.</summary>
    private const string ContentUriPrefix = "trilith://";

    /// <summary>Maximum length of a content record value.</summary>
    private const int ContentUriMaxLength = 512;

    /// <summary>Seconds after expiry during which only the owner may renew (30 days).</summary>
    private const long GracePeriodSeconds = 2_592_000;

    /// <summary>Default registration/renewal term (365 days).</summary>
    private const long DefaultRegistrationPeriodSeconds = 31_536_000;

    private readonly StorageMap<string, string> _owners;      // name -> owner hex
    private readonly StorageMap<string, string> _addresses;   // name -> target hex
    private readonly StorageMap<string, string> _reverse;     // address hex -> name
    private readonly StorageMap<string, string> _contentRecords; // name -> trilith:// content name
    private readonly StorageMap<string, long> _expiries;      // name -> expiry (unix seconds)
    private readonly StorageValue<UInt256> _registrationFee;
    private readonly StorageValue<long> _registrationPeriodSeconds;

    public BasaltNameService(UInt256 registrationFee = default)
    {
        if (registrationFee.IsZero) registrationFee = new UInt256(1_000_000_000);
        _owners = new StorageMap<string, string>("bns_owners");
        _addresses = new StorageMap<string, string>("bns_addrs");
        _reverse = new StorageMap<string, string>("bns_rev");
        _contentRecords = new StorageMap<string, string>("bns_content");
        _expiries = new StorageMap<string, long>("bns_expiry");
        _registrationFee = new StorageValue<UInt256>("bns_fee");
        _registrationPeriodSeconds = new StorageValue<long>("bns_regperiod");
        if (Context.IsDeploying)
        {
            _registrationFee.Set(registrationFee);
            _registrationPeriodSeconds.Set(DefaultRegistrationPeriodSeconds);
        }
    }

    /// <summary>Current block time in unix seconds (block timestamps are unix milliseconds).</summary>
    private static long NowSeconds() => Context.BlockTimestamp / 1000;

    /// <summary>Whether a name is expired and past its grace period (so anyone may take it).</summary>
    private bool IsExpiredPastGrace(string name)
    {
        var expiry = _expiries.Get(name);
        return expiry != 0 && NowSeconds() > expiry + GracePeriodSeconds;
    }

    /// <summary>Whether a name can be registered: never taken, or expired past its grace period.</summary>
    private bool IsAvailable(string name) =>
        string.IsNullOrEmpty(_owners.Get(name)) || IsExpiredPastGrace(name);

    /// <summary>Requires the caller to be the current, non-past-grace owner of the name.</summary>
    private void RequireLiveOwner(string name)
    {
        var ownerHex = _owners.Get(name);
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BNS: name not found");
        Context.Require(!IsExpiredPastGrace(name), "BNS: name expired");
        Context.Require(ownerHex == Convert.ToHexString(Context.Caller), "BNS: not owner");
    }

    /// <summary>
    /// Register a name for the default term. Requires payment >= registration fee. A name that has expired
    /// past its grace period is available and may be taken here (its prior content record is cleared).
    /// </summary>
    [BasaltEntrypoint]
    public void Register(string name)
    {
        Context.Require(!string.IsNullOrEmpty(name), "BNS: name required");
        Context.Require(name.Length <= 64, "BNS: name too long");
        Context.Require(IsAvailable(name), "BNS: name taken");
        Context.Require(Context.TxValue >= _registrationFee.Get(), "BNS: insufficient fee");

        // Taking over an expired name: clear the previous holder's content record so it cannot linger.
        if (!string.IsNullOrEmpty(_owners.Get(name)))
            _contentRecords.Delete(name);

        var callerHex = Convert.ToHexString(Context.Caller);
        _owners.Set(name, callerHex);
        _addresses.Set(name, callerHex);
        _expiries.Set(name, NowSeconds() + _registrationPeriodSeconds.Get());

        Context.Emit(new NameRegisteredEvent { Name = name, Owner = Context.Caller });
    }

    /// <summary>
    /// Renew a name for another term. Payable and callable by anyone (a name is a public good worth keeping
    /// alive), as long as it has not expired past its grace period. Extends from the later of now or the
    /// current expiry, so early renewals do not lose time.
    /// </summary>
    [BasaltEntrypoint]
    public void Renew(string name)
    {
        Context.Require(!string.IsNullOrEmpty(_owners.Get(name)), "BNS: name not found");
        Context.Require(!IsExpiredPastGrace(name), "BNS: name expired past grace");
        Context.Require(Context.TxValue >= _registrationFee.Get(), "BNS: insufficient fee");

        var now = NowSeconds();
        var current = _expiries.Get(name);
        var baseTime = current > now ? current : now;
        var newExpiry = baseTime + _registrationPeriodSeconds.Get();
        _expiries.Set(name, newExpiry);

        Context.Emit(new NameRenewedEvent { Name = name, NewExpiry = newExpiry });
    }

    /// <summary>
    /// Release a name that has expired past its grace period, clearing every record (owner, target, content,
    /// expiry, reverse). Callable by anyone, so stale names can be garbage-collected before re-registration.
    /// </summary>
    [BasaltEntrypoint]
    public void Reclaim(string name)
    {
        Context.Require(IsExpiredPastGrace(name), "BNS: name not expired past grace");

        var oldOwnerHex = _owners.Get(name);
        _owners.Delete(name);
        _addresses.Delete(name);
        _contentRecords.Delete(name);
        _expiries.Delete(name);
        if (!string.IsNullOrEmpty(oldOwnerHex) && _reverse.Get(oldOwnerHex) == name)
            _reverse.Delete(oldOwnerHex);

        Context.Emit(new NameReclaimedEvent { Name = name });
    }

    /// <summary>
    /// Set the <c>trilith://</c> content name a label resolves to. Owner-only, must carry the trilith scheme
    /// and be within the length bound.
    /// </summary>
    [BasaltEntrypoint]
    public void SetContentRecord(string name, string contentUri)
    {
        RequireLiveOwner(name);
        Context.Require(!string.IsNullOrEmpty(contentUri), "BNS: content uri required");
        Context.Require(contentUri.Length <= ContentUriMaxLength, "BNS: content uri too long");
        Context.Require(
            contentUri.StartsWith(ContentUriPrefix, StringComparison.Ordinal), "BNS: content uri must be trilith://");

        _contentRecords.Set(name, contentUri);
        Context.Emit(new ContentRecordSetEvent { Name = name, ContentUri = contentUri });
    }

    /// <summary>Clear the content record for a name you own.</summary>
    [BasaltEntrypoint]
    public void ClearContentRecord(string name)
    {
        RequireLiveOwner(name);
        _contentRecords.Delete(name);
        Context.Emit(new ContentRecordSetEvent { Name = name, ContentUri = "" });
    }

    /// <summary>Resolve a name to its <c>trilith://</c> content name, or empty if unset or expired past grace.</summary>
    [BasaltView]
    public string ResolveContent(string name)
    {
        if (IsExpiredPastGrace(name)) return "";
        return _contentRecords.Get(name) ?? "";
    }

    /// <summary>The name's expiry in unix seconds (0 if never registered).</summary>
    [BasaltView]
    public long ExpiryOf(string name) => _expiries.Get(name);

    /// <summary>Resolve a name to its target address.</summary>
    [BasaltView]
    public byte[] Resolve(string name)
    {
        Context.Require(!IsExpiredPastGrace(name), "BNS: name expired");
        var hex = _addresses.Get(name);
        Context.Require(!string.IsNullOrEmpty(hex), "BNS: name not found");
        return Convert.FromHexString(hex);
    }

    /// <summary>Set the target address for a name you own.</summary>
    [BasaltEntrypoint]
    public void SetAddress(string name, byte[] target)
    {
        RequireLiveOwner(name);
        _addresses.Set(name, Convert.ToHexString(target));
    }

    /// <summary>Set a reverse lookup (address -> name) for the caller.</summary>
    [BasaltEntrypoint]
    public void SetReverse(string name)
    {
        RequireLiveOwner(name);
        _reverse.Set(Convert.ToHexString(Context.Caller), name);
    }

    /// <summary>Reverse-resolve an address to a name.</summary>
    [BasaltView]
    public string ReverseLookup(byte[] addr)
    {
        return _reverse.Get(Convert.ToHexString(addr)) ?? "";
    }

    /// <summary>Transfer name ownership.</summary>
    [BasaltEntrypoint]
    public void TransferName(string name, byte[] newOwner)
    {
        RequireLiveOwner(name);
        var ownerHex = _owners.Get(name);

        var newOwnerHex = Convert.ToHexString(newOwner);
        _owners.Set(name, newOwnerHex);
        // M-5: Update address mapping so Resolve(name) returns the new owner
        _addresses.Set(name, newOwnerHex);
        // M-5: Clear old owner's reverse lookup if it pointed to this name
        var oldReverse = _reverse.Get(ownerHex);
        if (oldReverse == name)
            _reverse.Delete(ownerHex);

        Context.Emit(new NameTransferredEvent
        {
            Name = name,
            PreviousOwner = Context.Caller,
            NewOwner = newOwner,
        });
    }

    /// <summary>Get owner of a name (empty if unregistered or expired past grace).</summary>
    [BasaltView]
    public byte[] OwnerOf(string name)
    {
        if (IsExpiredPastGrace(name)) return new byte[20];
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

[BasaltEvent]
public class ContentRecordSetEvent
{
    public string Name { get; set; } = "";
    public string ContentUri { get; set; } = "";
}

[BasaltEvent]
public class NameRenewedEvent
{
    public string Name { get; set; } = "";
    public long NewExpiry { get; set; }
}

[BasaltEvent]
public class NameReclaimedEvent
{
    public string Name { get; set; } = "";
}
