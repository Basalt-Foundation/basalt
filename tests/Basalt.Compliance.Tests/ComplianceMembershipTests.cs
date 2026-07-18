using Basalt.Compliance;
using Basalt.Confidentiality.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

/// <summary>
/// The membership increment of the compliance circuit (3B.7): <c>IssuerRoot</c> is now <b>bound</b> to the
/// credential by an in-circuit MiMC Merkle membership proof (identity commitment MiMC(secret) is a member
/// of the issuer tree whose root is the public IssuerRoot). The milestone assertion is
/// <see cref="TamperedIssuerRoot_FailsVerification"/>: unlike circuit v1 (where IssuerRoot was an
/// unconstrained public input and tampering it still verified), tampering IssuerRoot here makes the proof
/// fail. This is what makes IssuerRoot trustworthy on-chain.
/// </summary>
/// <remarks>
/// Vector from <c>tools/basalt-prove membership</c>: gnark v0.15, ComplianceCircuitMembership, depth-4
/// issuer tree, identity commitment MiMC(secret=42) at leaf index 5, nullifier MiMC(42, 7), expiry
/// 1893456000, tier 3. The hand-rolled off-circuit MiMC tree root matched the in-circuit recomputation
/// (gnark's own verify passed at generation), confirming in/off-circuit hash compatibility.
/// </remarks>
public class ComplianceMembershipTests
{
    private const string VkHex =
        "8ea9ef20358d91b4ca03ae29a62cc474db949cda89ecf38270c2290cb13e70b6f7b20467ac337dde4af8770f510ed439" +
        "b228a2c11d4fd5df25d2598e44cbf4f508f9551add002bdad7b620d8c7a9569343f4bc214f9ffcc7b97494ff90dcb513" +
        "0cfd22882a1e96cdb8d04fa8bca1da4956fd3ee1687880fd0da8b9cb859b64ccaf8574dd5e0a72ffc859584fbef6c95f" +
        "a85b6d6b68624cd1157b133de7c76c01e3da8b2356f1f2a45be88a1e80ce9f8f37f1304a87d9cbfd6af774f4d6a4d4a1" +
        "0b4995cdca9702c446c34503f37f7680fb9338f062fa1acf18adac4470a619a95c5a4ee9c54699204bacd6f132b54432" +
        "890ea9af6b7d28f42ae1499a97d48da813bd3527aa994929239c3332b03b087535cc6eb3db23604c15dbb9f61fbb3b47" +
        "1082bebfb00960fc9b9f0893b5eb6372005ba725cbec289a929776ad9b7b416a945454a8a3457084c2881a730c6c1027" +
        "0600000080b0013ecf04bed5fb766265a33995183a3fd5fe682f19a9bd5fcde0587b9a456cfcc4e4a37d0243a367ced6" +
        "cb87447cb10e9f44d01e3a2adee3f44b7bd9ddc963e5798cc525d4a28022a594f330a41ab6962129a92d90a6a7a18412" +
        "41844bb899a71b78b683794bdd7c5eea9d88ed4bc38461f77be7e7e13530d8bbdfb2634ac66c1704bdccfdda7e779378" +
        "0d84172ac000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000c000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000c000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000";

    private const string ProofHex =
        "885da63455ab0add7f5c75bc52a52a06dc10f4c5d487c7e658d14692086d80b0d282ff537bd029a3a720aa6beb3256a0" +
        "997a097e6f653ea23ca1ed2c1d5f0643917304d52dc6660d9bc0c1c56c088997d4777a979c1f21c0bbdd2e63fb382ae9" +
        "02a0507228beb28b912f8aac43351aaaf5e15db1dbfde4f5948827259bf2708e997135c8263c9decb2354cfbfd66b96e" +
        "8cb3c4d781c18fec00d6b0b0801f5f6a77e699810c74da41b2b02fd424f99c53cd2582843014007fd3045f96669ec49c";

    private static readonly string[] PublicInputHex =
    {
        "3b83b253c8cdf07fb5cff75e7b68d5fb9a04045b34c022061a26a58725f812db", // [0] issuerRoot (Merkle root, BOUND)
        "706e7b7578eccdb3df4775460af7243b71d2a3168763c75c4347f42a863c1749", // [1] nullifier = MiMC(42,7)
        "0000000000000000000000000000000000000000000000000000000070dbd880", // [2] expiry = 1893456000
        "0000000000000000000000000000000000000000000000000000000000000003", // [3] tier = 3
        "0000000000000000000000000000000000000000000000000000000055555555", // [4] revocationRoot
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

    [Fact]
    public void MembershipProof_Verifies()
    {
        Vk().IC.Should().HaveCount(CircuitV1Layout.PublicInputCount + 1); // same frozen layout
        Groth16Verifier.Verify(Vk(), Proof(), PublicInputs()).Should().BeTrue(
            "the membership proof binds the identity commitment to the issuer root and must verify");
    }

    [Fact]
    public void TamperedIssuerRoot_FailsVerification()
    {
        // THE MILESTONE: IssuerRoot is now constrained by the Merkle membership proof. A prover cannot
        // substitute an arbitrary issuer root, so tampering it must fail (in circuit v1 this passed).
        var pis = PublicInputs();
        pis[CircuitV1Layout.IssuerRoot] = Convert.FromHexString(
            "00000000000000000000000000000000000000000000000000000000deadbeef");

        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }

    [Fact]
    public void TamperedNullifier_FailsVerification()
    {
        var pis = PublicInputs();
        pis[CircuitV1Layout.Nullifier] = Convert.FromHexString(
            "0000000000000000000000000000000000000000000000000000000000000001");

        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }
}
