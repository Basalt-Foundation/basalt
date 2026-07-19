using Basalt.Core;

namespace Basalt.Compliance.Tests;

/// <summary>
/// The golden compliance-circuit v2 vector shared by the circuit tests and the COMPL-C03 enforcement
/// tests. From <c>tools/basalt-prove membership</c>: gnark v0.15, depth-4 issuer tree, credential
/// commitment MiMC(secret=42, expiry=1893456000, tier=3) at leaf index 5, a revocation non-membership
/// proof against an empty revocation tree, and the v2 request binding (a canonical content id and a fixed
/// X25519 recipient key), with nullifier = MiMC(secret, cidField, recipientKeyHash). All seven public
/// inputs are bound: issuerRoot (membership), expiry and tier (in the leaf), nullifier, revocationRoot
/// (non-membership), and cidField/recipientKeyHash (folded into the nullifier). Tampering any of them fails
/// verification.
/// </summary>
internal static class MembershipVector
{
    public const string VkHex =
        "8c6e3cc8fc466a279d2fba9a32864c056dfc3f99fdf825c56a378fc9cf231ac6944be4233b30afbd18dfe55109c54087" +
        "964ac9c04f64fe2d2fbb0607b67cf5d98070a9ceeb251da0c04f7e602528e0f9b690c5afa54fddd591f3b52cc3fee37f" +
        "1424fa402fa55d3e2d701d237de8b85ea50090462feac45079e5b4cf408ab05d17cd51add5618948fc4b0ab2ba1d68bc" +
        "a63e0027a3ef399d28c58be4581b49486d4b21ecf023ffcd284bc9951c057c83d4f7e7d595fea453c1725de5cd9c0b36" +
        "054b0cc0ed5d0b5b0ae5d1fc10e53c0a810db3f5b550755e886659e7045251453efa4a8d8afa444ba6757c9c60622c95" +
        "a47cea2ddf8623d7ebf0fa3327e6eb12ab4d64b65264509dcbf4057056cd655499402c1a6a9e654da2f4789127e2ea2c" +
        "0416ef37a515b30bb21a1f07e69149f291d862173955d20bfc365a1153547521e0a66389ad2b21613310a7aa44626624" +
        "080000008e69e4aa4858d8c315d8c4f00e1efb572c24693bc5055014a8917b1ae5375ce58c9700cd941db8371915df7a" +
        "77aa8a76b31f2c2a2639483003c3f4b36974633589b33b545366f3eea6c30463fed1c5a7170367b153f124af1210edc8" +
        "9cd8202cabdc5ce0f4f582fa31d3189fd0c311a8ebdea32c4cb36c58a3687a75920d3bb8789bd40100afeb66314adcc0" +
        "8f754caab30cefad8774e2c318b8debcf4ba7dbc1b9e13bd708aca39edec27302df42b0a1fb0e376d859682fe7b2de4f" +
        "ee2342e698a7f1e1db1e0ea6c4563d31c8e6d67f6ac351d5a586942cd836ab26896019166413b63f1f69e625a8cddca7" +
        "97c1ae99b4e2c0a7cc78f1ecbd54da9b5feaef6c4eaa9c7605418b1c95996a1dc49a474f4d3c5e0c79da3362ee998ace" +
        "f8f476e9813ebbf714bcbe98357e58fe887b40105c4af0346446a608709d2c64508f16f4845bea7c492121bce89839b3" +
        "2244fa28a02fc21fcaa866675d424349de59b81f52393bcff69545606c929931a6d4d00f2a93945c761c2bec852f733d" +
        "1fc2adc4";

    public const string ProofHex =
        "a3a933a8cda937a726c720d2112525f5a002d435ec0d6165a76be2d4330f917a563805915de87faf5b906ea25e74cddd" +
        "93d3d6734a3776bd63c307bf6e98d2facc50a4c204699c1538214a8b045db1ed4ca9c0310cc26281f28e674901016ef0" +
        "0d086d3ebaf6dfe5790436fb4a5bc853ee5b728044cddbd5a7cca1a296cb4172e7b532da777f82373da47373823ed82e" +
        "aa662860289c5ba1a7f008884c0b5b22760b940db8706a8685601bfd5882310b5329d81e58dce49cae88e3dc658f4c08";

    // v2 layout order: issuerRoot, nullifier, expiry (1893456000), tier (3), revocationRoot (empty tree),
    // cidField (canonical cid), recipientKeyHash (fixed X25519 key 87b6fc9b...).
    public static readonly string[] PublicInputHex =
    {
        "17e1ee6c2f7d633c138faaabc0eb014f127bc2c8c5290929e293034c590c07ab",
        "1048b10a6914428094744d63c93bcd419a115a9d9e5ecd58b699236b946701e2",
        "0000000000000000000000000000000000000000000000000000000070dbd880",
        "0000000000000000000000000000000000000000000000000000000000000003",
        "5cf842758b8b17627ab0e9144945946eb38940bb715085ac0e71754dacd72018",
        "5b00460edff2c55b8d48b7eb9ca61084974f9fd6903b76fe1eee95c3cae2cd84",
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
