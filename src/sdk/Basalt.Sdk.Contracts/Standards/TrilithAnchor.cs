using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// TrilithAnchor — a minimal, tamper-evident notary for 32-byte Trilith digests.
/// Type ID: 0x0109
/// </summary>
/// <remarks>
/// Anchoring records that a digest existed at a block, by whom, and of what kind, without the chain ever
/// holding the underlying content or any personal data: the digest is an opaque 32-byte hash (a CID root, a
/// crypto-shredding destruction-set SMT root, a signed-blocklist root, or a key-log root). The flagship use
/// is the destruction certificate of prove-then-forget: after erasing a key, the eraser anchors the
/// destruction-set root here, and anyone can later verify the anchor on-chain. First-writer-wins: the first
/// submitter of a digest is recorded permanently, and a later re-anchor is an idempotent no-op that returns
/// the original timestamp, so the record cannot be overwritten or back-dated. Block timestamps are unix
/// milliseconds, stored here as unix seconds.
/// </remarks>
[BasaltContract]
public partial class TrilithAnchor
{
    /// <summary>Digest is the root CID of a piece of content.</summary>
    public const byte KindCidRoot = 0;

    /// <summary>Digest is the root of a crypto-shredding destruction set (proof a key was destroyed).</summary>
    public const byte KindDestructionSet = 1;

    /// <summary>Digest is the root of a signed moderation blocklist.</summary>
    public const byte KindBlocklist = 2;

    /// <summary>Digest is the root of a key-log (key rotation history).</summary>
    public const byte KindKeyLog = 3;

    private const byte MaxKind = KindKeyLog;
    private const int AnchorLabelMaxLength = 64;

    // Parallel maps keyed by the digest hex (no packed struct, so each field stays a primitive value).
    private readonly StorageMap<string, string> _submitters;  // digest hex -> submitter address hex
    private readonly StorageMap<string, string> _labels;      // digest hex -> free-form label
    private readonly StorageMap<string, ulong> _blockNumbers; // digest hex -> block height
    private readonly StorageMap<string, long> _timestamps;    // digest hex -> unix seconds
    private readonly StorageMap<string, byte> _kinds;         // digest hex -> kind
    private readonly StorageMap<string, bool> _exists;        // digest hex -> anchored?
    private readonly StorageValue<ulong> _anchorCount;
    private readonly StorageValue<UInt256> _anchorFee;

    public TrilithAnchor(UInt256 anchorFee = default)
    {
        _submitters = new StorageMap<string, string>("anchor_sub");
        _labels = new StorageMap<string, string>("anchor_lbl");
        _blockNumbers = new StorageMap<string, ulong>("anchor_blk");
        _timestamps = new StorageMap<string, long>("anchor_ts");
        _kinds = new StorageMap<string, byte>("anchor_kind");
        _exists = new StorageMap<string, bool>("anchor_exists");
        _anchorCount = new StorageValue<ulong>("anchor_count");
        _anchorFee = new StorageValue<UInt256>("anchor_fee");
        if (Context.IsDeploying)
            _anchorFee.Set(anchorFee); // default 0 = free anchoring
    }

    private static string Key(byte[] digest) => Convert.ToHexString(digest);

    /// <summary>
    /// Anchor a 32-byte digest with a kind and an optional short label. First-writer-wins: if the digest is
    /// already anchored this is a no-op that returns the original timestamp (no fee, no state change, no
    /// event). Otherwise it records the submitter, block, timestamp, and kind and returns the new timestamp
    /// (unix seconds).
    /// </summary>
    [BasaltEntrypoint]
    public ulong Anchor(byte[] digest, byte kind, string label)
    {
        Context.Require(digest != null && digest.Length == 32, "Anchor: digest must be 32 bytes");
        Context.Require(kind <= MaxKind, "Anchor: unknown kind");
        Context.Require(label == null || label.Length <= AnchorLabelMaxLength, "Anchor: label too long");

        var key = Key(digest!);

        // First-writer-wins: a re-anchor is idempotent and free, returning the original anchor time.
        if (_exists.Get(key))
            return (ulong)_timestamps.Get(key);

        Context.Require(Context.TxValue >= _anchorFee.Get(), "Anchor: insufficient fee");

        var nowSeconds = Context.BlockTimestamp / 1000;
        _submitters.Set(key, Convert.ToHexString(Context.Caller));
        _labels.Set(key, label ?? "");
        _blockNumbers.Set(key, Context.BlockHeight);
        _timestamps.Set(key, nowSeconds);
        _kinds.Set(key, kind);
        _exists.Set(key, true);
        _anchorCount.Set(_anchorCount.Get() + 1);

        Context.Emit(new AnchoredEvent
        {
            Submitter = Context.Caller,
            Digest = digest!,
            Kind = kind,
            Label = label ?? "",
        });
        return (ulong)nowSeconds;
    }

    /// <summary>Whether a digest has been anchored.</summary>
    [BasaltView]
    public bool IsAnchored(byte[] digest) => _exists.Get(Key(digest));

    /// <summary>The unix-seconds timestamp a digest was anchored, or 0 if never anchored.</summary>
    [BasaltView]
    public ulong GetAnchor(byte[] digest) => (ulong)_timestamps.Get(Key(digest));

    /// <summary>The address that first anchored a digest, or the zero address if never anchored.</summary>
    [BasaltView]
    public byte[] GetAnchorSubmitter(byte[] digest)
    {
        var hex = _submitters.Get(Key(digest));
        return string.IsNullOrEmpty(hex) ? new byte[20] : Convert.FromHexString(hex);
    }

    /// <summary>The kind recorded for a digest (0 if never anchored).</summary>
    [BasaltView]
    public byte GetAnchorKind(byte[] digest) => _kinds.Get(Key(digest));

    /// <summary>The block height a digest was anchored at (0 if never anchored).</summary>
    [BasaltView]
    public ulong GetAnchorBlock(byte[] digest) => _blockNumbers.Get(Key(digest));

    /// <summary>The label recorded for a digest (empty if unset or never anchored).</summary>
    [BasaltView]
    public string GetAnchorLabel(byte[] digest) => _labels.Get(Key(digest)) ?? "";

    /// <summary>Total number of distinct digests anchored.</summary>
    [BasaltView]
    public ulong AnchorCount() => _anchorCount.Get();
}

[BasaltEvent]
public class AnchoredEvent
{
    [Indexed] public byte[] Submitter { get; set; } = null!;
    public byte[] Digest { get; set; } = null!;
    public byte Kind { get; set; }
    public string Label { get; set; } = "";
}
