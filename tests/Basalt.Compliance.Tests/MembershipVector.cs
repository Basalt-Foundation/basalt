using Basalt.Core;

namespace Basalt.Compliance.Tests;

/// <summary>
/// The golden membership-circuit vector shared by the circuit tests and the COMPL-C03 enforcement tests.
/// From <c>tools/basalt-prove membership</c>: gnark v0.15, depth-4 issuer tree, credential commitment
/// MiMC(secret=42, expiry=1893456000, tier=3) at leaf index 5, nullifier MiMC(42, 7). issuerRoot, expiry,
/// and tier are all bound (tampering any fails verification).
/// </summary>
internal static class MembershipVector
{
    public const string VkHex =
        "a6374cfbb15730721c24fc29e5bf378235af42091427ac151f161c02b1b9a2eb2a8b585db9695cf2a24345039118df0b" +
        "90d8ed8cfd4d6349c125e40e34495ff475d02b13561892c809ca5af300bac95835e4060546f41c8ba7f450ff2d870557" +
        "044394eadd246080a731499ea2c2eeac475988eeac5ebecd86ce6948c5e7747a548e988bffff1e88903f710cf328f07d" +
        "98e425a09b909aede650ab3233719500789c0712d28c6835336560005cefbd82a05cd6b87769c082d394195737f2826a" +
        "0a0b2ba7c6aeecd06abdaeabbcc61b301d06c82260ce40bc273db7fe4736db1b2d9b5ce8f7b444ee5b47132b664c9de9" +
        "aaa923f4d46a5f2b76a25453abe8a8d44ca54847002939cb413b68a687e4abd908e40793cd16e758278b872f6a80a7b7" +
        "03404e111ed30dcc2899d1fa87ffcba72a00862bc9aec3524515caf24a3dcf573330a5c76bbd25c993064dcca57fc5fe" +
        "060000008bed772f9f22d8f1e3e7da9d775862580fa3d7030db7d4081f636d066616b2bdf1d1d41a8c3b9dee8be27fd9" +
        "843c73998f2ef20d0f3ae13be166476cd6fd8c60eea118dc8783665ccdd714f3cdb2322e5a7b3cb1eb7bc78010062a39" +
        "da339bfb830125d4f2eeae04a94707dc7fc47c796ec5a5e9d2741e68afc028aca028968fa6e6c5e06b5d6074ce6ecd63" +
        "5d3ff92cac847bf03dff842ab1374d2500b36a47a4563f2cad9b8b50f9887aa8977178f9b27e51fc4e7c09024b32ecca" +
        "e8d0bbbbae4f9a9caae913cd1a5c7b8991b047c3e5db9c9b6c78323bc8c73298431efa5d977995e40dc816b36119f6fe" +
        "f6bf0a18c000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000";

    public const string ProofHex =
        "91002a73d3fd3f3009c2350f9953ea0be3bfc1be7be03da77a9fde2d091a71eaaface06f557c84be82f1e4840155035b" +
        "a47c11986b752b65e46992aca45afb1526129260e3070aaa3cd4373dc4f6ea03987f458b86d947580e6f88e03a52068e" +
        "12a9b5a4b439a987db62b45beb66c6bb0bf341ad64411b3d46dc76ea4f0a31a1c4914fc679aef865e76674ad9958aed0" +
        "b82c410c84fbc50266d1f8f468d5937a4c0e7ef6ff8660de54d8cd5e8648ba0f4e8bf2cbc70ec943ae2f366f7d6b62fd";

    // Frozen layout order: issuerRoot, nullifier, expiry (1893456000), tier (3), revocationRoot.
    public static readonly string[] PublicInputHex =
    {
        "17e1ee6c2f7d633c138faaabc0eb014f127bc2c8c5290929e293034c590c07ab",
        "706e7b7578eccdb3df4775460af7243b71d2a3168763c75c4347f42a863c1749",
        "0000000000000000000000000000000000000000000000000000000070dbd880",
        "0000000000000000000000000000000000000000000000000000000000000003",
        "0000000000000000000000000000000000000000000000000000000055555555",
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

    /// <summary>The five public inputs concatenated, as a transaction carries them.</summary>
    public static byte[] FlatPublicInputs()
    {
        var flat = new byte[PublicInputHex.Length * 32];
        var pis = PublicInputs();
        for (int i = 0; i < pis.Length; i++)
            Array.Copy(pis[i], 0, flat, i * 32, 32);
        return flat;
    }

    /// <summary>The nullifier the circuit proved (public input index 1), as a Hash256.</summary>
    public static Hash256 CircuitNullifier() => new(PublicInputs()[CircuitV1Layout.Nullifier]);
}
