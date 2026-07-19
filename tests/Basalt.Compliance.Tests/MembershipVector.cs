using Basalt.Core;

namespace Basalt.Compliance.Tests;

/// <summary>
/// The golden compliance-circuit v2 vector shared by the circuit tests and the COMPL-C03 enforcement
/// tests. From <c>tools/basalt-prove membership</c>: gnark v0.15, depth-4 issuer tree, credential
/// commitment MiMC(secret=42, expiry=1893456000, tier=3) at leaf index 5, a revocation non-membership
/// proof against an empty revocation tree, and the v2 request binding (cid "trilith://cid-under-test" and
/// a fixed X25519 recipient key), with nullifier = MiMC(secret, cidField, recipientKeyHash). All seven
/// public inputs are bound: issuerRoot (membership), expiry and tier (in the leaf), nullifier, revocationRoot
/// (non-membership), and cidField/recipientKeyHash (folded into the nullifier). Tampering any of them fails
/// verification.
/// </summary>
internal static class MembershipVector
{
    public const string VkHex =
        "b5eaf146bf7abd9ff9784242980895f623b0b9ab18baf0f3972a79c17377b285d742c7b1b88ebc9697469091a1f5e9ce" +
        "a335c7c4d155d160c0f9177d10f5659a7fe34681f86788b832690f71a81a03d7c76a66aa38cede33afd826734fe39fbd" +
        "05530a57d38b64bd6eec8b239db3c9f272021e919846116ebfa30155f425e74922985577e3e363a8829939d0b36bcf63" +
        "a0fd9858ef7669048ccbf4ba806da3c9b7dd1eea6297eed344fd2b92ad29f132306314a074d49792f08e07391c7da481" +
        "008c973a8024b33877be3a79b4fd2fa9b4e91b41ad7740a8754e1bdbeeb0bdf77e136783e0673a31e1ad7c673c15426b" +
        "812b8ffcadc2dedbfe0f6809bfd7188f950d2d5bb83600156e3fa1938efd0a97bfc4cd669d3c44c38be3ca9d85926f19" +
        "19c287bb11c65fd4ac81d9870fd1b3aad495eccec6cd6df02f314c71a659168911f0da3c012c160e86524cf946745ee5" +
        "08000000b03633dd8fb246ef726168d93fb2fda4c2d3d8ad27c5572dd0fa5d72b2f16598442e074d45a93d809b9556a8" +
        "015351608adcaf98cb1eb19d7fe1cbc4312ce3a05cb725bc649caad4b519159eae6cd117c927293d1b67bcc96152f1ac" +
        "d7d3a41597e36551e3946ac46be20e89fcbc21b4b88dea2375aad4905c01329fab4700cace92fed42626f2d941e7f769" +
        "3c66929093c6cfdc6577349bbb92fe856f6b37ebe187fbda5a97075c998964e8ef4d73366c3c6561c7c4d96eb4101b52" +
        "28140b46a9705b680f7bf1ab970ead8946a7d3156324371f1186b3206351441382e693eca95c30db20d24f5f016df10d" +
        "215ce2938d9c05a1a7f3ba2d4d0b5067808e7037ae94f5e7b5b4cde3d79d61d1bd5c344cdcf48dbc9ac8428faa1ab010" +
        "3e0668ce86b04bb5f7c24b26e49365f28f8f6bf90c78893be05ef030f917edd85b9db8f103ff0ee49c47dbbe6826e846" +
        "15f85f5a95853e5fd69ccfa033c850a3f3654511c8fb726ce326c49070d94e226d97a9200f6298942ad317bdbd30c220" +
        "cdd49caf";

    public const string ProofHex =
        "80315bf653934e7a9efea042f737792e611d3715f02dec4aead11e2b5ed707f1ddb3cd7bf9198127e2ce9081ea81dc0b" +
        "8cb23726b09636765ecea907344ae0832b795c307cc41cce4b3071a2bc7a0b8784b2b732d148749a309bfdfef062e75f" +
        "0c4a125d765df47c5f45a059d640569a7b58858735b6bdeb502d2c1fd29fc3d5963a713645e77483c92711379a8b2b21" +
        "aa9282341fdd4cc94224133ba6f98749694dfa588fa49be28287d05eac8af18617d86d3116920d64042cc5370cc143bd";

    // v2 layout order: issuerRoot, nullifier, expiry (1893456000), tier (3), revocationRoot (empty tree),
    // cidField ("trilith://cid-under-test"), recipientKeyHash (fixed X25519 key 87b6fc9b...).
    public static readonly string[] PublicInputHex =
    {
        "17e1ee6c2f7d633c138faaabc0eb014f127bc2c8c5290929e293034c590c07ab",
        "3d1e7cb96b9cc3cc7304c2a483f20da0677e92edbe45541a58d7edb818555a73",
        "0000000000000000000000000000000000000000000000000000000070dbd880",
        "0000000000000000000000000000000000000000000000000000000000000003",
        "5cf842758b8b17627ab0e9144945946eb38940bb715085ac0e71754dacd72018",
        "21c83cf61c8b0d108ba7f498d73528006cd1bfe55013e231f3c0605aaddc120f",
        "596be643f4863751015ca898adf1b43f6e28c304d96417278b3752c5ad05603e",
    };

    public const ulong ExpirySeconds = 1893456000; // 2030-01-01
    public const ulong Tier = 3;

    public static byte[] VkBytes() => Convert.FromHexString(VkHex);
    public static byte[] ProofBytes() => Convert.FromHexString(ProofHex);

    public static byte[][] PublicInputs()
    {
        var r = new byte[PublicInputHex.Length][];
        for (int i = 0; i < r.Length; i++)
            r[i] = Convert.FromHexString(PublicInputHex[i]);
        return r;
    }

    /// <summary>The seven public inputs concatenated, as a transaction carries them.</summary>
    public static byte[] FlatPublicInputs()
    {
        var flat = new byte[PublicInputHex.Length * 32];
        var pis = PublicInputs();
        for (int i = 0; i < pis.Length; i++)
            Array.Copy(pis[i], 0, flat, i * 32, 32);
        return flat;
    }

    /// <summary>The nullifier the circuit proved (public input index 1), as a Hash256.</summary>
    public static Hash256 CircuitNullifier() => new(PublicInputs()[CircuitV2Layout.Nullifier]);
}
