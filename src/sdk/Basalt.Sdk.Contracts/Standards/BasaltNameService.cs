using Basalt.Crypto;
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
    /// <summary>The one top-level domain this registry serves.</summary>
    private const string Tld = "bslt";

    /// <summary>Longest a single label may be, matching DNS.</summary>
    private const int MaxLabelLength = 63;

    /// <summary>Longest a full name may be, excluding the TLD.</summary>
    private const int MaxNameLength = 64;

    /// <summary>Required scheme prefix for a content record value.</summary>
    private const string ContentUriPrefix = "trilith://";

    /// <summary>Maximum length of a content record value.</summary>
    private const int ContentUriMaxLength = 512;

    /// <summary>Seconds after expiry during which only the owner may renew (30 days).</summary>
    private const long GracePeriodSeconds = 2_592_000;

    /// <summary>Default registration/renewal term (365 days).</summary>
    private const long DefaultRegistrationPeriodSeconds = 31_536_000;

    /// <summary>
    /// Names one account may register per year unless governance votes otherwise.
    ///
    /// A hundred is high enough that no ordinary user will ever notice it and low enough that taking a
    /// meaningful share of the namespace needs many accounts rather than one loop. It is a default, not
    /// a ceiling on the design: Governance is a real vote, with proposals and delegation, so changing it
    /// is a decision the network makes rather than one an operator makes alone.
    /// </summary>
    private const int DefaultMaxRegistrationsPerWindow = 100;

    private readonly StorageMap<string, string> _owners;      // name -> owner hex
    private readonly StorageMap<string, string> _addresses;   // name -> target hex
    private readonly StorageMap<string, string> _reverse;     // address hex -> name
    private readonly StorageMap<string, string> _contentRecords; // name -> trilith:// content name
    private readonly StorageMap<string, long> _expiries;      // name -> expiry (unix seconds)
    private readonly StorageMap<string, string> _subOwners;   // full subdomain -> delegated owner hex
    private readonly StorageMap<string, bool> _reserved;      // label -> reserved at launch
    private readonly StorageValue<long> _reservationDeadline; // unix seconds; reservations lapse after
    private readonly StorageMap<string, long> _commitments;   // commitment hex -> block second it was made
    private readonly StorageMap<string, bool> _attesters;     // attester hex -> authorised
    private readonly StorageValue<int> _attesterCount;
    private readonly StorageValue<int> _attestationThreshold; // how many must agree
    private readonly StorageMap<string, bool> _attested;      // "label|owner|attester" -> has attested
    private readonly StorageMap<string, int> _attestationTally; // "label|owner" -> agreeing attesters
    private readonly StorageMap<string, int> _windowCount;    // account hex -> registrations this window
    private readonly StorageMap<string, long> _windowStart;   // account hex -> when its window opened
    private readonly StorageValue<int> _maxPerWindow;         // 0 disables the limit
    private readonly StorageValue<long> _windowSeconds;
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
        _subOwners = new StorageMap<string, string>("bns_subowners");
        _reserved = new StorageMap<string, bool>("bns_reserved");
        _reservationDeadline = new StorageValue<long>("bns_resdeadline");
        _commitments = new StorageMap<string, long>("bns_commits");
        _attesters = new StorageMap<string, bool>("bns_attesters");
        _attesterCount = new StorageValue<int>("bns_attcount");
        _attestationThreshold = new StorageValue<int>("bns_attthresh");
        _attested = new StorageMap<string, bool>("bns_attested");
        _attestationTally = new StorageMap<string, int>("bns_atttally");
        _windowCount = new StorageMap<string, int>("bns_wcount");
        _windowStart = new StorageMap<string, long>("bns_wstart");
        _maxPerWindow = new StorageValue<int>("bns_wmax");
        _windowSeconds = new StorageValue<long>("bns_wsecs");
        _registrationFee = new StorageValue<UInt256>("bns_fee");
        _registrationPeriodSeconds = new StorageValue<long>("bns_regperiod");
        if (Context.IsDeploying)
        {
            _registrationFee.Set(registrationFee);
            _registrationPeriodSeconds.Set(DefaultRegistrationPeriodSeconds);
        }
    }


    /// <summary>
    /// Puts a name into the single form everything else uses: lowercase, no <c>.bslt</c> suffix.
    ///
    /// Both <c>alice</c> and <c>alice.bslt</c> are accepted and mean the same registration, because a
    /// registry that treats them as two names guarantees someone eventually owns one and not the other.
    /// The suffix is presentation, so it is not stored: keys stay short and <c>alice.bslt.bslt</c> cannot
    /// exist.
    ///
    /// Rejects anything that is not a valid name outright rather than normalising it into something the
    /// caller did not ask for. Silent coercion in a naming system is how people end up owning a name they
    /// did not mean to buy.
    /// </summary>
    private static string NormaliseName(string input)
    {
        Context.Require(!string.IsNullOrEmpty(input), "BNS: name required");

        var lowered = ToLowerAscii(input);

        // Strip one trailing ".bslt", and only one.
        var suffix = "." + Tld;
        if (lowered.Length > suffix.Length && lowered.EndsWith(suffix, StringComparison.Ordinal))
            lowered = lowered.Substring(0, lowered.Length - suffix.Length);

        Context.Require(lowered.Length > 0, "BNS: name required");
        Context.Require(lowered.Length <= MaxNameLength, "BNS: name too long");
        ValidateLabels(lowered);
        return lowered;
    }

    /// <summary>Lowercases ASCII letters. Names are ASCII by construction, checked in ValidateLabels.</summary>
    private static string ToLowerAscii(string value)
    {
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] >= 'A' && chars[i] <= 'Z')
                chars[i] = (char)(chars[i] + 32);
        }

        return new string(chars);
    }

    /// <summary>
    /// Every label must be ASCII letters, digits or hyphens, must not start or end with a hyphen, and must
    /// be non-empty. This is the DNS rule, kept deliberately: a name that cannot be written in a URL or
    /// read aloud is not a human-readable name, and mixed scripts are how homograph attacks start.
    /// </summary>
    private static void ValidateLabels(string name)
    {
        var start = 0;
        for (int i = 0; i <= name.Length; i++)
        {
            if (i != name.Length && name[i] != '.')
                continue;

            var length = i - start;
            Context.Require(length > 0, "BNS: empty label");
            Context.Require(length <= MaxLabelLength, "BNS: label too long");
            Context.Require(name[start] != '-', "BNS: label starts with hyphen");
            Context.Require(name[i - 1] != '-', "BNS: label ends with hyphen");

            for (int j = start; j < i; j++)
            {
                var c = name[j];
                var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                Context.Require(ok, "BNS: label has an invalid character");
            }

            start = i + 1;
        }
    }

    /// <summary>
    /// The registrable part of a name, meaning its rightmost label. Ownership of <c>blog.alice</c> is
    /// ownership of <c>alice</c>: only the second level is bought, everything under it comes with it.
    /// </summary>
    private static string RegistrableLabel(string normalised)
    {
        var lastDot = normalised.LastIndexOf('.');
        return lastDot < 0 ? normalised : normalised.Substring(lastDot + 1);
    }

    /// <summary>Whether a normalised name names a subdomain rather than a registrable label.</summary>
    private static bool IsSubdomain(string normalised) => normalised.IndexOf('.') >= 0;

    /// <summary>
    /// The form a person sees and types, which is the normalised name with the TLD put back.
    ///
    /// Storage is canonical without it and everything user-facing is canonical with it. Emitting the
    /// stored form instead would leak an implementation detail into every event and reverse lookup, and
    /// leave callers unsure which of the two spellings is the real name.
    /// </summary>
    private static string DisplayName(string normalised) => normalised + "." + Tld;

    /// <summary>Current block time in unix seconds (block timestamps are unix milliseconds).</summary>
    private static long NowSeconds() => Context.BlockTimestamp / 1000;

    /// <summary>
    /// The registration term in seconds, falling back to the default when unset. The genesis deploy path
    /// constructs system contracts with <c>Context.IsDeploying == false</c>, so the constructor's init block
    /// does not run at genesis and the stored value is 0 there; the fallback keeps a genesis-deployed BNS
    /// working (a governance setter could still store a non-default term later).
    /// </summary>
    private long RegistrationPeriodSeconds()
    {
        var stored = _registrationPeriodSeconds.Get();
        return stored > 0 ? stored : DefaultRegistrationPeriodSeconds;
    }

    /// <summary>Whether a name is expired and past its grace period (so anyone may take it).</summary>
    private bool IsExpiredPastGrace(string name)
    {
        // A subdomain has no life of its own: it lives and dies with the label that was bought.
        var expiry = _expiries.Get(RegistrableLabel(name));
        return expiry != 0 && NowSeconds() > expiry + GracePeriodSeconds;
    }

    /// <summary>Whether a name can be registered: never taken, or expired past its grace period.</summary>
    private bool IsAvailable(string name) =>
        string.IsNullOrEmpty(_owners.Get(RegistrableLabel(name))) || IsExpiredPastGrace(name);

    /// <summary>
    /// Requires the caller to control this name. For a registrable label that means owning it. For a
    /// subdomain it means owning the label above it, or holding an explicit delegation for exactly this
    /// subdomain. Either way the parent's expiry governs, so a lapsed registration takes its whole tree
    /// with it and a delegate cannot outlive the person who delegated.
    /// </summary>
    private void RequireLiveOwner(string name)
    {
        var label = RegistrableLabel(name);
        var ownerHex = _owners.Get(label);
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BNS: name not found");
        Context.Require(!IsExpiredPastGrace(name), "BNS: name expired");

        var callerHex = Convert.ToHexString(Context.Caller);
        if (ownerHex == callerHex)
            return;

        if (IsSubdomain(name))
        {
            var delegated = _subOwners.Get(name);
            if (!string.IsNullOrEmpty(delegated) && delegated == callerHex)
                return;
        }

        Context.Require(false, "BNS: not owner");
    }

    /// <summary>
    /// Register a name for the default term. Requires payment >= registration fee. A name that has expired
    /// past its grace period is available and may be taken here (its prior content record is cleared).
    /// </summary>
    [BasaltEntrypoint]
    public void Register(string name)
    {
        name = NormaliseName(name);
        // Only the second level is for sale. Everything under a label comes with it, so registering
        // blog.alice separately would let two parties hold overlapping claims on the same tree.
        Context.Require(!IsSubdomain(name), "BNS: register the label, subdomains come with it");
        Context.Require(!IsReserved(name), "BNS: name is reserved");
        Context.Require(IsAvailable(name), "BNS: name taken");
        Context.Require(Context.TxValue >= _registrationFee.Get(), "BNS: insufficient fee");

        // Taking over an expired name: clear the previous holder's content record so it cannot linger.
        if (!string.IsNullOrEmpty(_owners.Get(name)))
            _contentRecords.Delete(name);

        CountRegistration();

        var callerHex = Convert.ToHexString(Context.Caller);
        _owners.Set(name, callerHex);
        _addresses.Set(name, callerHex);
        _expiries.Set(name, NowSeconds() + RegistrationPeriodSeconds());

        Context.Emit(new NameRegisteredEvent { Name = DisplayName(name), Owner = Context.Caller });
    }

    /// <summary>
    /// Renew a name for another term. Payable and callable by anyone (a name is a public good worth keeping
    /// alive), as long as it has not expired past its grace period. Extends from the later of now or the
    /// current expiry, so early renewals do not lose time.
    /// </summary>
    [BasaltEntrypoint]
    public void Renew(string name)
    {
        name = NormaliseName(name);
        Context.Require(!string.IsNullOrEmpty(_owners.Get(name)), "BNS: name not found");
        Context.Require(!IsExpiredPastGrace(name), "BNS: name expired past grace");
        Context.Require(Context.TxValue >= _registrationFee.Get(), "BNS: insufficient fee");

        var now = NowSeconds();
        var current = _expiries.Get(name);
        var baseTime = current > now ? current : now;
        var newExpiry = baseTime + RegistrationPeriodSeconds();
        _expiries.Set(name, newExpiry);

        Context.Emit(new NameRenewedEvent { Name = DisplayName(name), NewExpiry = newExpiry });
    }

    /// <summary>
    /// Release a name that has expired past its grace period, clearing every record (owner, target, content,
    /// expiry, reverse). Callable by anyone, so stale names can be garbage-collected before re-registration.
    /// </summary>
    [BasaltEntrypoint]
    public void Reclaim(string name)
    {
        name = NormaliseName(name);
        Context.Require(!IsSubdomain(name), "BNS: reclaim the label, not a subdomain");
        Context.Require(IsExpiredPastGrace(name), "BNS: name not expired past grace");

        var oldOwnerHex = _owners.Get(name);
        _owners.Delete(name);
        _addresses.Delete(name);
        _contentRecords.Delete(name);
        _expiries.Delete(name);
        if (!string.IsNullOrEmpty(oldOwnerHex) && _reverse.Get(oldOwnerHex) == name)
            _reverse.Delete(oldOwnerHex);

        Context.Emit(new NameReclaimedEvent { Name = DisplayName(name) });
    }

    /// <summary>
    /// Set the <c>trilith://</c> content name a label resolves to. Owner-only, must carry the trilith scheme
    /// and be within the length bound.
    /// </summary>
    [BasaltEntrypoint]
    public void SetContentRecord(string name, string contentUri)
    {
        name = NormaliseName(name);
        RequireLiveOwner(name);
        Context.Require(!string.IsNullOrEmpty(contentUri), "BNS: content uri required");
        Context.Require(contentUri.Length <= ContentUriMaxLength, "BNS: content uri too long");
        Context.Require(
            contentUri.StartsWith(ContentUriPrefix, StringComparison.Ordinal), "BNS: content uri must be trilith://");

        _contentRecords.Set(name, contentUri);
        Context.Emit(new ContentRecordSetEvent { Name = DisplayName(name), ContentUri = contentUri });
    }

    /// <summary>Clear the content record for a name you own.</summary>
    [BasaltEntrypoint]
    public void ClearContentRecord(string name)
    {
        name = NormaliseName(name);
        RequireLiveOwner(name);
        _contentRecords.Delete(name);
        Context.Emit(new ContentRecordSetEvent { Name = DisplayName(name), ContentUri = "" });
    }

    /// <summary>Resolve a name to its <c>trilith://</c> content name, or empty if unset or expired past grace.</summary>
    [BasaltView]
    public string ResolveContent(string name)
    {
        name = NormaliseName(name);
        if (IsExpiredPastGrace(name)) return "";
        return _contentRecords.Get(name) ?? "";
    }


    /// <summary>
    /// Hand control of one subdomain to another account, revocably.
    ///
    /// Without this, "subdomains are supported" only means the owner can set more records under their own
    /// name. Delegation is what makes a subdomain useful to someone else, and it is deliberately narrow:
    /// exactly one subdomain, no inheritance to deeper levels, revocable at any time by the label's owner,
    /// and dead the moment the parent registration lapses. A delegate can publish, never sell or outlive.
    /// </summary>
    [BasaltEntrypoint]
    public void SetSubdomainOwner(string name, byte[] newOwner)
    {
        name = NormaliseName(name);
        Context.Require(IsSubdomain(name), "BNS: not a subdomain");
        Context.Require(newOwner is { Length: 20 }, "BNS: owner must be a 20-byte address");

        // Only the label's owner delegates. A delegate cannot re-delegate, which would otherwise let a
        // subdomain escape the person who created it.
        var ownerHex = _owners.Get(RegistrableLabel(name));
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BNS: name not found");
        Context.Require(!IsExpiredPastGrace(name), "BNS: name expired");
        Context.Require(ownerHex == Convert.ToHexString(Context.Caller), "BNS: not owner");

        _subOwners.Set(name, Convert.ToHexString(newOwner));
        Context.Emit(new SubdomainDelegatedEvent { Name = DisplayName(name), Owner = new Address(newOwner) });
    }

    /// <summary>Take a delegated subdomain back. Owner-only, and it takes effect immediately.</summary>
    [BasaltEntrypoint]
    public void ClearSubdomainOwner(string name)
    {
        name = NormaliseName(name);
        Context.Require(IsSubdomain(name), "BNS: not a subdomain");

        var ownerHex = _owners.Get(RegistrableLabel(name));
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BNS: name not found");
        Context.Require(ownerHex == Convert.ToHexString(Context.Caller), "BNS: not owner");

        _subOwners.Delete(name);
        Context.Emit(new SubdomainDelegatedEvent { Name = DisplayName(name), Owner = Address.Zero });
    }

    /// <summary>The account a subdomain is delegated to, or empty when it is not delegated.</summary>
    [BasaltView]
    public byte[] SubdomainOwner(string name)
    {
        name = NormaliseName(name);
        var hex = _subOwners.Get(name);
        return string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex);
    }

    /// <summary>The registrable label a name belongs to, with the TLD stripped and case normalised.</summary>
    [BasaltView]
    public string LabelOf(string name) => RegistrableLabel(NormaliseName(name));


    /// <summary>
    /// Governance, the only account allowed to reserve names or hand a reserved name to its claimant.
    ///
    /// Hardcoded rather than taken as a constructor argument because BNS is registered at genesis under
    /// type 0x0101 with a fixed constructor arity, and changing that would invalidate the registration.
    /// It matches GenesisContractDeployer.Addresses.Governance (0x1003).
    /// </summary>
    private static string GovernanceHex()
    {
        var bytes = new byte[20];
        bytes[18] = 0x10;
        bytes[19] = 0x03;
        return Convert.ToHexString(bytes);
    }

    private void RequireGovernance()
        => Context.Require(Convert.ToHexString(Context.Caller) == GovernanceHex(), "BNS: governance only");

    /// <summary>
    /// Whether a label is currently reserved, meaning nobody may register it.
    ///
    /// Reservations are deliberately temporary. A registry that holds well-known names forever is a
    /// registry with a permanently empty shelf, so once the deadline passes an unclaimed reservation
    /// simply stops applying and the name becomes ordinary.
    /// </summary>
    [BasaltView]
    public bool IsReserved(string name)
    {
        var label = RegistrableLabel(NormaliseName(name));
        if (!_reserved.Get(label))
            return false;

        var deadline = _reservationDeadline.Get();
        return deadline == 0 || NowSeconds() <= deadline;
    }

    /// <summary>The unix second at which unclaimed reservations lapse (0 when unset).</summary>
    [BasaltView]
    public long ReservationDeadline() => _reservationDeadline.Get();

    /// <summary>
    /// Sets the moment reservations lapse. Governance only, and it must be set before any name can be
    /// reserved, so an operator cannot accidentally create a permanent hold by forgetting this call.
    /// </summary>
    [BasaltEntrypoint]
    public void SetReservationDeadline(long unixSeconds)
    {
        RequireGovernance();
        Context.Require(unixSeconds > NowSeconds(), "BNS: deadline must be in the future");
        _reservationDeadline.Set(unixSeconds);
        Context.Emit(new ReservationDeadlineSetEvent { Deadline = unixSeconds });
    }

    /// <summary>
    /// Reserves one label so no one can register it before the deadline.
    ///
    /// This exists so that a launch does not hand every recognisable name to whoever scripts the fastest
    /// transaction. Reserved names are held by the registry and by nobody: they are not owned, not for
    /// sale, and released free to whoever proves they should have them.
    /// </summary>
    [BasaltEntrypoint]
    public void ReserveName(string name)
    {
        RequireGovernance();
        Context.Require(_reservationDeadline.Get() > 0, "BNS: set the reservation deadline first");

        var label = RegistrableLabel(NormaliseName(name));
        Context.Require(IsAvailable(label), "BNS: name already registered");
        _reserved.Set(label, true);
        Context.Emit(new NameReservedEvent { Name = DisplayName(label) });
    }

    /// <summary>
    /// Hands a reserved name to the account that proved it should have it, free of charge.
    ///
    /// The proof is not made here and is not a trademark judgement, which nobody running a registry is
    /// equipped to render. It is control of the matching DNS name, shown by a TXT record naming the
    /// claiming address, which anyone can re-check for themselves. <paramref name="evidence"/> records
    /// where that proof lives, so the assignment is auditable after the fact rather than taken on trust.
    ///
    /// Governance can therefore witness a proof that exists. It cannot invent one, because the record it
    /// points at is public and someone will look.
    /// </summary>
    /// <summary>
    /// Rejects an owner nobody could hold the name with.
    ///
    /// The zero address is the case that matters, and it has to be refused here rather than tidied up
    /// later: assignment deletes the reservation, so a name handed to an address with no key is gone for
    /// good. A placeholder left in a DNS template must not be able to burn a name forever.
    /// </summary>
    private static void RequireAssignableOwner(byte[] owner)
    {
        Context.Require(owner is { Length: 20 }, "BNS: owner must be a 20-byte address");

        var isZero = true;
        for (int i = 0; i < owner.Length; i++)
        {
            if (owner[i] != 0)
            {
                isZero = false;
                break;
            }
        }

        Context.Require(!isZero, "BNS: owner must not be the zero address");
    }

    [BasaltEntrypoint]
    public void ClaimReserved(string name, byte[] owner, string evidence)
    {
        RequireGovernance();
        RequireAssignableOwner(owner);
        Context.Require(!string.IsNullOrEmpty(evidence), "BNS: evidence required");
        Context.Require(evidence.Length <= 256, "BNS: evidence too long");

        var label = RegistrableLabel(NormaliseName(name));
        Context.Require(_reserved.Get(label), "BNS: name is not reserved");
        Context.Require(IsAvailable(label), "BNS: name already registered");

        var ownerHex = Convert.ToHexString(owner);
        _owners.Set(label, ownerHex);
        _addresses.Set(label, ownerHex);
        _expiries.Set(label, NowSeconds() + RegistrationPeriodSeconds());
        _reserved.Delete(label);

        Context.Emit(new ReservedNameClaimedEvent
        {
            Name = DisplayName(label),
            Owner = new Address(owner),
            Evidence = evidence,
        });
    }


    /// <summary>Shortest a commitment must sit before it can be revealed.</summary>
    private const long MinCommitmentAgeSeconds = 60;

    /// <summary>Longest a commitment stays usable, after which it must be made again.</summary>
    private const long MaxCommitmentAgeSeconds = 86_400;

    /// <summary>
    /// The commitment for a registration, which is what a client sends first.
    ///
    /// It binds the name, the account that will own it, and a secret only the registrant knows. Binding
    /// the owner is the part that matters: without it, someone watching the reveal could replay the same
    /// name and secret from their own account and take the name in the same block.
    /// </summary>
    [BasaltView]
    public byte[] MakeCommitment(string name, byte[] owner, byte[] secret)
    {
        var normalised = NormaliseName(name);
        Context.Require(owner is { Length: 20 }, "BNS: owner must be a 20-byte address");
        Context.Require(secret is { Length: 32 }, "BNS: secret must be 32 bytes");

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(normalised);
        var buffer = new byte[nameBytes.Length + 20 + 32];
        nameBytes.CopyTo(buffer, 0);
        owner.CopyTo(buffer, nameBytes.Length);
        secret.CopyTo(buffer, nameBytes.Length + 20);
        return Blake3Hasher.Hash(buffer).ToArray();
    }

    /// <summary>
    /// Records a commitment, which says nothing about which name is being registered.
    ///
    /// Registering in one step is a race anyone can win by watching the mempool: the name is visible in
    /// the pending transaction, and whoever pays more gas takes it. Committing first and revealing later
    /// removes the information a front-runner needs, because the commitment is a hash and the name only
    /// appears once the claim is already anchored to an earlier block.
    /// </summary>
    [BasaltEntrypoint]
    public void Commit(byte[] commitment)
    {
        Context.Require(commitment is { Length: 32 }, "BNS: commitment must be 32 bytes");

        var key = Convert.ToHexString(commitment);
        var existing = _commitments.Get(key);
        // Re-committing an unexpired commitment would reset its clock, which would let someone keep a
        // claim alive indefinitely without ever revealing it.
        Context.Require(existing == 0 || NowSeconds() > existing + MaxCommitmentAgeSeconds,
            "BNS: commitment already pending");

        _commitments.Set(key, NowSeconds());
    }

    /// <summary>
    /// Registers a name previously committed to, proving the commitment by revealing its secret.
    ///
    /// The commitment must be old enough that it could not have been made in reaction to this
    /// transaction, and young enough that stale commitments do not accumulate forever. It is consumed
    /// either way, so a secret is good for exactly one registration.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterCommitted(string name, byte[] secret)
    {
        var normalised = NormaliseName(name);
        Context.Require(!IsSubdomain(normalised), "BNS: register the label, subdomains come with it");
        Context.Require(!IsReserved(normalised), "BNS: name is reserved");
        Context.Require(IsAvailable(normalised), "BNS: name taken");
        Context.Require(Context.TxValue >= _registrationFee.Get(), "BNS: insufficient fee");

        var commitment = MakeCommitment(normalised, Context.Caller, secret);
        var key = Convert.ToHexString(commitment);
        var madeAt = _commitments.Get(key);
        Context.Require(madeAt != 0, "BNS: no matching commitment");

        var age = NowSeconds() - madeAt;
        Context.Require(age >= MinCommitmentAgeSeconds, "BNS: commitment too new");
        Context.Require(age <= MaxCommitmentAgeSeconds, "BNS: commitment expired");

        _commitments.Delete(key);
        CountRegistration();

        if (!string.IsNullOrEmpty(_owners.Get(normalised)))
            _contentRecords.Delete(normalised);

        var callerHex = Convert.ToHexString(Context.Caller);
        _owners.Set(normalised, callerHex);
        _addresses.Set(normalised, callerHex);
        _expiries.Set(normalised, NowSeconds() + RegistrationPeriodSeconds());

        Context.Emit(new NameRegisteredEvent { Name = DisplayName(normalised), Owner = Context.Caller });
    }


    /// <summary>
    /// Authorises an account to attest that a DNS record exists. Governance only.
    ///
    /// An attester's job is narrow and mechanical: resolve a TXT record and report what is there. It is
    /// not a judgement, which is the point, because a fact that several independent parties can check
    /// the same way does not need anyone's opinion.
    /// </summary>
    [BasaltEntrypoint]
    public void SetAttester(byte[] attester, bool authorised)
    {
        RequireGovernance();
        Context.Require(attester is { Length: 20 }, "BNS: attester must be a 20-byte address");

        var key = Convert.ToHexString(attester);
        var current = _attesters.Get(key);
        if (current == authorised)
            return;

        _attesters.Set(key, authorised);
        _attesterCount.Set(_attesterCount.Get() + (authorised ? 1 : -1));
        Context.Emit(new AttesterSetEvent { Attester = new Address(attester), Authorised = authorised });
    }

    /// <summary>
    /// How many attesters must independently report the same record before a name is handed over.
    ///
    /// Governance sets the bar and then cannot reach past it: it can say how many must agree, but not
    /// what they see. A threshold above the number of attesters is refused, since it would make every
    /// claim permanently unsatisfiable rather than merely strict.
    /// </summary>
    [BasaltEntrypoint]
    public void SetAttestationThreshold(int threshold)
    {
        RequireGovernance();
        Context.Require(threshold > 0, "BNS: threshold must be positive");
        Context.Require(threshold <= _attesterCount.Get(), "BNS: threshold exceeds attester count");
        _attestationThreshold.Set(threshold);
    }

    /// <summary>Number of attesters that have agreed on a claim so far.</summary>
    [BasaltView]
    public int AttestationCount(string name, byte[] owner)
    {
        var label = RegistrableLabel(NormaliseName(name));
        return _attestationTally.Get(label + "|" + Convert.ToHexString(owner));
    }

    /// <summary>The number of agreeing attesters a claim needs (0 when unset).</summary>
    [BasaltView]
    public int AttestationThreshold() => _attestationThreshold.Get();

    /// <summary>
    /// Reports that the DNS record proving control of this name names this address.
    ///
    /// The name is assigned automatically once enough attesters agree, so no one decides: the record
    /// either exists and independent observers see it, or it does not. Each attester counts once per
    /// claim, so a single compromised attester cannot reach the threshold alone.
    /// </summary>
    [BasaltEntrypoint]
    public void AttestClaim(string name, byte[] owner, string evidence)
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        Context.Require(_attesters.Get(callerHex), "BNS: not an attester");
        RequireAssignableOwner(owner);
        Context.Require(!string.IsNullOrEmpty(evidence), "BNS: evidence required");
        Context.Require(evidence.Length <= 256, "BNS: evidence too long");

        var threshold = _attestationThreshold.Get();
        Context.Require(threshold > 0, "BNS: attestation threshold not set");

        var label = RegistrableLabel(NormaliseName(name));
        Context.Require(_reserved.Get(label), "BNS: name is not reserved");
        Context.Require(IsAvailable(label), "BNS: name already registered");

        var ownerHex = Convert.ToHexString(owner);
        var claimKey = label + "|" + ownerHex;
        var attesterKey = claimKey + "|" + callerHex;
        Context.Require(!_attested.Get(attesterKey), "BNS: already attested");

        _attested.Set(attesterKey, true);
        var tally = _attestationTally.Get(claimKey) + 1;
        _attestationTally.Set(claimKey, tally);

        Context.Emit(new ClaimAttestedEvent
        {
            Name = DisplayName(label),
            Owner = new Address(owner),
            Attester = new Address(Context.Caller),
            Evidence = evidence,
            Tally = tally,
        });

        if (tally < threshold)
            return;

        _owners.Set(label, ownerHex);
        _addresses.Set(label, ownerHex);
        _expiries.Set(label, NowSeconds() + RegistrationPeriodSeconds());
        _reserved.Delete(label);
        _attestationTally.Delete(claimKey);

        Context.Emit(new ReservedNameClaimedEvent
        {
            Name = DisplayName(label),
            Owner = new Address(owner),
            Evidence = evidence,
        });
    }


    /// <summary>
    /// Caps how many names one account may register per window. Zero, the default, means no cap.
    ///
    /// Worth being honest about what this buys. It does not stop bulk registration, because an attacker
    /// can use more accounts. It changes the cost from one script on one account to many accounts each
    /// funded separately, which on a network where funds are rate-limited at the source is the
    /// difference between minutes and days. It is a speed bump, and the reservation list is the wall.
    /// </summary>
    [BasaltEntrypoint]
    public void SetRegistrationLimit(int maxPerWindow, long windowSeconds)
    {
        RequireGovernance();
        // -1 removes the cap. 0 is not "no cap" because 0 is also what unset storage reads as, and a
        // cap that silently disappears when nobody set it is the failure mode worth designing out.
        Context.Require(maxPerWindow == -1 || maxPerWindow > 0, "BNS: limit must be positive, or -1 to remove it");
        Context.Require(maxPerWindow == -1 || windowSeconds > 0, "BNS: window must be positive");
        _maxPerWindow.Set(maxPerWindow);
        _windowSeconds.Set(windowSeconds);
        Context.Emit(new RegistrationLimitSetEvent { MaxPerWindow = maxPerWindow, WindowSeconds = windowSeconds });
    }

    /// <summary>
    /// The cap in force. Falls back to the default when unset, because the genesis deploy path builds
    /// system contracts with Context.IsDeploying false, so a constructor default would never be stored
    /// and the namespace would open uncapped without anyone choosing that.
    /// </summary>
    private int MaxRegistrationsPerWindow()
    {
        var stored = _maxPerWindow.Get();
        return stored != 0 ? stored : DefaultMaxRegistrationsPerWindow;
    }

    private long RegistrationWindowSeconds()
    {
        var stored = _windowSeconds.Get();
        return stored > 0 ? stored : DefaultRegistrationPeriodSeconds;
    }

    /// <summary>How many registrations the caller has left in the current window (-1 when uncapped).</summary>
    [BasaltView]
    public int RegistrationsRemaining(byte[] account)
    {
        var max = MaxRegistrationsPerWindow();
        if (max < 0)
            return -1;

        var key = Convert.ToHexString(account);
        var started = _windowStart.Get(key);
        if (started == 0 || NowSeconds() >= started + RegistrationWindowSeconds())
            return max;

        var used = _windowCount.Get(key);
        return used >= max ? 0 : max - used;
    }

    /// <summary>
    /// Counts one registration against the caller's window, rolling the window over when it has elapsed.
    /// Assignments of reserved names do not pass through here: a claimant receiving the name they proved
    /// they control is not registering names in bulk.
    /// </summary>
    private void CountRegistration()
    {
        var max = MaxRegistrationsPerWindow();
        if (max < 0)
            return;

        var key = Convert.ToHexString(Context.Caller);
        var window = RegistrationWindowSeconds();
        var started = _windowStart.Get(key);
        var now = NowSeconds();

        if (started == 0 || now >= started + window)
        {
            _windowStart.Set(key, now);
            _windowCount.Set(key, 1);
            return;
        }

        var used = _windowCount.Get(key);
        Context.Require(used < max, "BNS: registration limit reached for this window");
        _windowCount.Set(key, used + 1);
    }

    /// <summary>The name's expiry in unix seconds (0 if never registered).</summary>
    [BasaltView]
    public long ExpiryOf(string name) => _expiries.Get(RegistrableLabel(NormaliseName(name)));

    /// <summary>Resolve a name to its target address.</summary>
    [BasaltView]
    public byte[] Resolve(string name)
    {
        name = NormaliseName(name);
        Context.Require(!IsExpiredPastGrace(name), "BNS: name expired");
        var hex = _addresses.Get(name);
        Context.Require(!string.IsNullOrEmpty(hex), "BNS: name not found");
        return Convert.FromHexString(hex);
    }

    /// <summary>Set the target address for a name you own.</summary>
    [BasaltEntrypoint]
    public void SetAddress(string name, byte[] target)
    {
        name = NormaliseName(name);
        RequireLiveOwner(name);
        _addresses.Set(name, Convert.ToHexString(target));
    }

    /// <summary>Set a reverse lookup (address -> name) for the caller.</summary>
    [BasaltEntrypoint]
    public void SetReverse(string name)
    {
        name = NormaliseName(name);
        RequireLiveOwner(name);
        _reverse.Set(Convert.ToHexString(Context.Caller), DisplayName(name));
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
        name = NormaliseName(name);
        Context.Require(!IsSubdomain(name), "BNS: transfer the label, subdomains follow it");
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
            Name = DisplayName(name),
            PreviousOwner = Context.Caller,
            NewOwner = newOwner,
        });
    }

    /// <summary>Get owner of a name (empty if unregistered or expired past grace).</summary>
    [BasaltView]
    public byte[] OwnerOf(string name)
    {
        name = NormaliseName(name);
        if (IsExpiredPastGrace(name)) return new byte[20];
        var hex = _owners.Get(RegistrableLabel(name));
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

/// <summary>A subdomain was delegated to an account, or the delegation was cleared (Owner is zero).</summary>
public sealed class SubdomainDelegatedEvent
{
    public string Name { get; set; } = "";
    public Address Owner { get; set; }
}

/// <summary>A label was reserved so it cannot be registered before the deadline.</summary>
[BasaltEvent]
public class NameReservedEvent
{
    public string Name { get; set; } = "";
}

/// <summary>The moment unclaimed reservations lapse was set.</summary>
[BasaltEvent]
public class ReservationDeadlineSetEvent
{
    public long Deadline { get; set; }
}

/// <summary>
/// A reserved name was handed to its claimant. Evidence points at the public proof of control, so the
/// assignment can be checked by anyone rather than believed.
/// </summary>
[BasaltEvent]
public class ReservedNameClaimedEvent
{
    [Indexed] public Address Owner { get; set; }
    public string Name { get; set; } = "";
    public string Evidence { get; set; } = "";
}

/// <summary>An account was authorised to attest DNS records, or had that authorisation withdrawn.</summary>
[BasaltEvent]
public class AttesterSetEvent
{
    [Indexed] public Address Attester { get; set; }
    public bool Authorised { get; set; }
}

/// <summary>One attester reported the record proving a claim. Tally is how many now agree.</summary>
[BasaltEvent]
public class ClaimAttestedEvent
{
    [Indexed] public Address Owner { get; set; }
    public string Name { get; set; } = "";
    public Address Attester { get; set; }
    public string Evidence { get; set; } = "";
    public int Tally { get; set; }
}

/// <summary>The per-account registration cap was changed.</summary>
[BasaltEvent]
public class RegistrationLimitSetEvent
{
    public int MaxPerWindow { get; set; }
    public long WindowSeconds { get; set; }
}
