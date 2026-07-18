using Basalt.Compliance;
using Basalt.Confidentiality.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

/// <summary>
/// The credential-binding increment of the compliance circuit (3B.7). The credential commitment
/// leaf = MiMC(secret, expiry, tier) must be a member of the issuer's Merkle tree whose root is the
/// public <c>IssuerRoot</c>. Because issuerRoot, expiry, and tier all feed the proof (issuerRoot as the
/// membership root, expiry and tier hashed into the leaf), a prover cannot forge any of them: tampering
/// any one fails verification. This is the property that makes the on-chain expiry/tier enforcement
/// (COMPL-C03) meaningful, since the circuit v1 caveat (unconstrained expiry/tier) no longer applies.
/// </summary>
/// <remarks>
/// Vector from <c>tools/basalt-prove membership</c>: gnark v0.15, depth-4 issuer tree, credential
/// commitment MiMC(secret=42, expiry=1893456000, tier=3) at leaf index 5, nullifier MiMC(42, 7). The
/// hand-rolled off-circuit MiMC tree root matched the in-circuit recomputation (gnark verify passed at
/// generation), confirming in/off-circuit hash compatibility.
/// </remarks>
public class ComplianceMembershipTests
{
    private const string VkHex =
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

    private const string ProofHex =
        "91002a73d3fd3f3009c2350f9953ea0be3bfc1be7be03da77a9fde2d091a71eaaface06f557c84be82f1e4840155035b" +
        "a47c11986b752b65e46992aca45afb1526129260e3070aaa3cd4373dc4f6ea03987f458b86d947580e6f88e03a52068e" +
        "12a9b5a4b439a987db62b45beb66c6bb0bf341ad64411b3d46dc76ea4f0a31a1c4914fc679aef865e76674ad9958aed0" +
        "b82c410c84fbc50266d1f8f468d5937a4c0e7ef6ff8660de54d8cd5e8648ba0f4e8bf2cbc70ec943ae2f366f7d6b62fd";

    private static readonly string[] PublicInputHex =
    {
        "17e1ee6c2f7d633c138faaabc0eb014f127bc2c8c5290929e293034c590c07ab", // [0] issuerRoot (Merkle root, BOUND)
        "706e7b7578eccdb3df4775460af7243b71d2a3168763c75c4347f42a863c1749", // [1] nullifier = MiMC(42,7)
        "0000000000000000000000000000000000000000000000000000000070dbd880", // [2] expiry = 1893456000 (BOUND)
        "0000000000000000000000000000000000000000000000000000000000000003", // [3] tier = 3 (BOUND)
        "0000000000000000000000000000000000000000000000000000000055555555", // [4] revocationRoot (not yet bound)
    };

    private static byte[][] PublicInputs()
    {
        var r = new byte[PublicInputHex.Length][];
        for (int i = 0; i < r.Length; i++)
            r[i] = Convert.FromHexString(PublicInputHex[i]);
        return r;
    }

    private static VerificationKey Vk() => Groth16Codec.DecodeVerificationKey(Convert.FromHexString(VkHex));
    private static Groth16Proof Proof() => Groth16Codec.DecodeProof(Convert.FromHexString(ProofHex));

    private static byte[] Word(ulong v)
    {
        var b = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(24, 8), v);
        return b;
    }

    [Fact]
    public void CredentialProof_Verifies()
    {
        Vk().IC.Should().HaveCount(CircuitV1Layout.PublicInputCount + 1); // same frozen layout
        Groth16Verifier.Verify(Vk(), Proof(), PublicInputs()).Should().BeTrue(
            "the credential membership proof binds issuerRoot, expiry, and tier, and must verify");
    }

    [Fact]
    public void TamperedIssuerRoot_FailsVerification()
    {
        var pis = PublicInputs();
        pis[CircuitV1Layout.IssuerRoot] = Word(0xdeadbeef);
        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }

    [Fact]
    public void TamperedExpiry_FailsVerification()
    {
        // THE MILESTONE for this increment: expiry is now hashed into the credential leaf, so a prover
        // cannot claim a later expiry than the issuer attested. In circuit v1 this tamper still verified.
        var pis = PublicInputs();
        pis[CircuitV1Layout.Expiry] = Word(4102444800); // year 2100, not the attested 1893456000
        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }

    [Fact]
    public void TamperedTier_FailsVerification()
    {
        // Tier is bound too: a prover cannot claim a higher issuer tier than attested.
        var pis = PublicInputs();
        pis[CircuitV1Layout.IssuerTier] = Word(9);
        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }

    [Fact]
    public void TamperedNullifier_FailsVerification()
    {
        var pis = PublicInputs();
        pis[CircuitV1Layout.Nullifier] = Word(1);
        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }
}
