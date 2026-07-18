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
/// (COMPL-C03) meaningful. See <see cref="MembershipVector"/> for the golden vector.
/// </summary>
public class ComplianceMembershipTests
{
    private static VerificationKey Vk() => Groth16Codec.DecodeVerificationKey(MembershipVector.VkBytes());
    private static Groth16Proof Proof() => Groth16Codec.DecodeProof(MembershipVector.ProofBytes());

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
        Groth16Verifier.Verify(Vk(), Proof(), MembershipVector.PublicInputs()).Should().BeTrue(
            "the credential membership proof binds issuerRoot, expiry, and tier, and must verify");
    }

    [Fact]
    public void TamperedIssuerRoot_FailsVerification()
    {
        var pis = MembershipVector.PublicInputs();
        pis[CircuitV1Layout.IssuerRoot] = Word(0xdeadbeef);
        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }

    [Fact]
    public void TamperedExpiry_FailsVerification()
    {
        // The milestone: expiry is hashed into the credential leaf, so a prover cannot claim a later
        // expiry than the issuer attested. In circuit v1 this tamper still verified.
        var pis = MembershipVector.PublicInputs();
        pis[CircuitV1Layout.Expiry] = Word(4102444800); // year 2100, not the attested 1893456000
        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }

    [Fact]
    public void TamperedTier_FailsVerification()
    {
        var pis = MembershipVector.PublicInputs();
        pis[CircuitV1Layout.IssuerTier] = Word(9);
        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }

    [Fact]
    public void TamperedNullifier_FailsVerification()
    {
        var pis = MembershipVector.PublicInputs();
        pis[CircuitV1Layout.Nullifier] = Word(1);
        Groth16Verifier.Verify(Vk(), Proof(), pis).Should().BeFalse();
    }
}
