using Basalt.Compliance;
using Basalt.Confidentiality.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Compliance.Tests;

/// <summary>
/// Freezes the compliance circuit v1 public-input layout against a real gnark proof. Proves that a
/// gnark-produced Groth16 proof exposing the five compliance public inputs (issuerRoot, nullifier,
/// expiry, tier, revocationRoot, in that order) verifies under Basalt's blst verifier, and that the
/// frozen indices in <see cref="CircuitV1Layout"/> line up with what the prover actually emits. This is
/// the foundation the verifier-side expiry/tier enforcement (COMPL-C03) builds on.
/// </summary>
/// <remarks>
/// Vector from <c>tools/basalt-prove compliance</c>: gnark v0.15, ComplianceCircuitV1 with
/// Nullifier == MiMC(Secret=42, Epoch=7), expiry 1893456000 (2030-01-01), tier 3, issuerRoot 0x11111111,
/// revocationRoot 0x55555555.
/// </remarks>
public class ComplianceCircuitV1LayoutTests
{
    private const string VkHex =
        "a97fd3d25e59c7ac167c839293da106fcb340950566c20282b48bacf908d0f69115e9597bdec2585a8fdc150efd80e8e" +
        "b601410ac6df6967ed67e6c1cb346863a6af1d0b943cad5087b3647efedcaefa1a9ef6f83a24224a35b1dac1d0f7923a" +
        "19d8c9f063ece4bba75a1caffeedfd7e45556970485c0dc0a0d29fd794f74edda982d2c03517498e9759235de52d41ad" +
        "a75293825d5f6fdeb42186682191e689733307a99af9d5a5d97eb89d493ce02e5e383e6b21e24930043e561e95ca3198" +
        "0b514ef5ae99abb425a789cba933d1c347ac61b71e6453db5e69fa9b6bd3e52af829eca4b775e74e18e3b39565ef01ee" +
        "aafc8559d29b4d9776b3e0770d5317b33d28e35ad9711cce6adbb44864d3184b682dbce058655e2a3d592160416c5660" +
        "11591759a6c0e3bbd2d7e132534dcdca91227b1d941cf6839c40bcd9a716bfb87bd1f4334b4bec8477fd59b73113f963" +
        "060000008ae36f099afa1e580131b9e06b22ec5b5d7fd61fe246e7290a89c1827e3fa0af6b81f0dd699d2461305462ac" +
        "f62dfce7c000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "000000008f5b0f63dcf7db793606844e07b04cbbe0b6f65cc5eab8e3c7e97e1db5ea1cea6c21f83d033a6ecd9872d9e4" +
        "21e200aec000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000c000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000c000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "00000000";

    private const string ProofHex =
        "8f226ca0c4a29666e8fa8e104fa34c2f9ac403b5e3503080841d3b69e6bb50c183b57154fd1b16235d704ab5c002d474" +
        "a2510c29daf3b636f9bf044a9fff9420e0cc8646b92f0f8ab9591505a66b07af4b7ff0603020d1f2e485324944a50c83" +
        "143b48b094275393c4fd2bec06a16c59147c6817e8a2aa97177919f275fa29aac1fff002e50c3a8a31b14449a8d1f0ca" +
        "9390951afe9e28547a467b93e3cce3adfa3cbd35e87dc7db62f91e70211fb832cf6cefd7331e6edad17a16782fc9880c";

    // The five public inputs in frozen order.
    private static readonly string[] PublicInputHex =
    {
        "0000000000000000000000000000000000000000000000000000000011111111", // [0] issuerRoot
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

    [Fact]
    public void ComplianceProof_VerifiesUnderBlst_WithFiveInputs()
    {
        var vk = Groth16Codec.DecodeVerificationKey(Convert.FromHexString(VkHex));
        var proof = Groth16Codec.DecodeProof(Convert.FromHexString(ProofHex));

        // vk.IC.Length is PublicInputCount + 1 (the constant term IC[0] plus one IC per public input).
        vk.IC.Should().HaveCount(CircuitV1Layout.PublicInputCount + 1);

        Groth16Verifier.Verify(vk, proof, PublicInputs()).Should().BeTrue(
            "a compliance-shaped 5-public-input gnark proof must verify under blst");
    }

    [Fact]
    public void FrozenLayout_IndicesMatchProverEmission()
    {
        var pis = PublicInputs();

        // The prover set expiry and tier to distinctive, predictable values at their frozen indices.
        CircuitV1Layout.ReadUInt64(pis[CircuitV1Layout.Expiry]).Should().Be(1893456000UL);
        CircuitV1Layout.ReadUInt64(pis[CircuitV1Layout.IssuerTier]).Should().Be(3UL);
        CircuitV1Layout.ReadUInt64(pis[CircuitV1Layout.IssuerRoot]).Should().Be(0x1111_1111UL);
        CircuitV1Layout.ReadUInt64(pis[CircuitV1Layout.RevocationRoot]).Should().Be(0x5555_5555UL);
        // Nullifier is a full-width field element, so it does not fit in 64 bits.
        pis[CircuitV1Layout.Nullifier].Should().NotEqual(new byte[32]);
    }

    [Fact]
    public void TamperedNullifier_FailsVerification()
    {
        var vk = Groth16Codec.DecodeVerificationKey(Convert.FromHexString(VkHex));
        var proof = Groth16Codec.DecodeProof(Convert.FromHexString(ProofHex));

        // The nullifier is the one input a v1 constraint binds (Nullifier == MiMC(secret, epoch)), so
        // changing it moves vk_x and the pairing must reject the proof (the verify is not trivially true).
        var pis = PublicInputs();
        pis[CircuitV1Layout.Nullifier] = Convert.FromHexString(
            "0000000000000000000000000000000000000000000000000000000000000001");

        Groth16Verifier.Verify(vk, proof, pis).Should().BeFalse();
    }

    [Fact]
    public void UnconstrainedInputs_DoNotBindTheProof_SecurityCaveat()
    {
        // IMPORTANT: circuit v1 exposes issuerRoot/expiry/tier/revocationRoot as public inputs but does
        // NOT yet constrain them, so their IC points are the identity and changing them leaves vk_x (and
        // thus verification) unchanged. This is why verifier-side expiry/tier enforcement (COMPL-C03) is
        // necessary-but-not-sufficient: the full circuit (3B.7) must bind these inputs to the credential,
        // otherwise a prover can claim any expiry/tier with a still-valid proof. This test pins that gap
        // so it is not mistaken for a soundness property v1 does not have.
        var vk = Groth16Codec.DecodeVerificationKey(Convert.FromHexString(VkHex));
        var proof = Groth16Codec.DecodeProof(Convert.FromHexString(ProofHex));

        var pis = PublicInputs();
        pis[CircuitV1Layout.Expiry] = Convert.FromHexString(
            "0000000000000000000000000000000000000000000000000000000080000000");

        // Still verifies, because expiry is unconstrained in v1. Documents the caveat, not a guarantee.
        Groth16Verifier.Verify(vk, proof, pis).Should().BeTrue();
    }
}
