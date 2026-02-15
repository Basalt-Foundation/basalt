using Basalt.Core;
using Nethermind.Crypto;

namespace Basalt.Confidentiality.Crypto;

/// <summary>
/// Verification key for the Groth16 ZK-SNARK proof system.
/// All point data is stored as compressed BLS12-381 points.
/// </summary>
public sealed class VerificationKey
{
    /// <summary>Alpha element in G1 (48 bytes compressed).</summary>
    public required byte[] AlphaG1 { get; init; }

    /// <summary>Beta element in G2 (96 bytes compressed).</summary>
    public required byte[] BetaG2 { get; init; }

    /// <summary>Gamma element in G2 (96 bytes compressed).</summary>
    public required byte[] GammaG2 { get; init; }

    /// <summary>Delta element in G2 (96 bytes compressed).</summary>
    public required byte[] DeltaG2 { get; init; }

    /// <summary>
    /// IC (input commitment) array: one G1 point per public input plus one.
    /// IC[0] is the constant term; IC[i+1] corresponds to public input i.
    /// Each element is 48 bytes compressed.
    /// </summary>
    public required byte[][] IC { get; init; }
}

/// <summary>
/// A Groth16 ZK-SNARK proof consisting of three elliptic curve elements.
/// </summary>
public sealed class Groth16Proof
{
    /// <summary>Proof element A in G1 (48 bytes compressed).</summary>
    public required byte[] A { get; init; }

    /// <summary>Proof element B in G2 (96 bytes compressed).</summary>
    public required byte[] B { get; init; }

    /// <summary>Proof element C in G1 (48 bytes compressed).</summary>
    public required byte[] C { get; init; }
}

/// <summary>
/// Groth16 ZK-SNARK verifier for BLS12-381.
///
/// Verification checks the pairing equation:
///   e(A, B) = e(alpha, beta) * e(vk_x, gamma) * e(C, delta)
///
/// where vk_x = IC[0] + sum(publicInputs[i] * IC[i+1]).
///
/// This is equivalent to checking:
///   e(A, B) * e(-alpha, beta) * e(-vk_x, gamma) * e(-C, delta) = 1
///
/// The verifier uses multi-Miller-loop with a single final exponentiation
/// for efficiency.
/// </summary>
public static class Groth16Verifier
{
    /// <summary>
    /// Verify a Groth16 proof against a verification key and public inputs.
    /// </summary>
    /// <param name="vk">The verification key from the trusted setup.</param>
    /// <param name="proof">The proof to verify.</param>
    /// <param name="publicInputs">
    /// Array of 32-byte big-endian scalar values representing the public inputs.
    /// Must have exactly <c>vk.IC.Length - 1</c> elements.
    /// </param>
    /// <returns><c>true</c> if the proof is valid; otherwise <c>false</c>.</returns>
    public static bool Verify(VerificationKey vk, Groth16Proof proof, byte[][] publicInputs)
    {
        // Validate inputs
        if (vk == null || proof == null || publicInputs == null)
            return false;

        if (publicInputs.Length + 1 != vk.IC.Length)
            return false;

        // Validate point sizes
        if (proof.A.Length != PairingEngine.G1CompressedSize ||
            proof.C.Length != PairingEngine.G1CompressedSize ||
            proof.B.Length != PairingEngine.G2CompressedSize)
            return false;

        if (vk.AlphaG1.Length != PairingEngine.G1CompressedSize ||
            vk.BetaG2.Length != PairingEngine.G2CompressedSize ||
            vk.GammaG2.Length != PairingEngine.G2CompressedSize ||
            vk.DeltaG2.Length != PairingEngine.G2CompressedSize)
            return false;

        try
        {
            // Step 1: Compute vk_x = IC[0] + sum(publicInputs[i] * IC[i+1])
            byte[] vkX = ComputeVkX(vk.IC, publicInputs);

            // Step 2: Multi-pairing check using product of Miller loops.
            // We check: e(A, B) * e(-alpha, beta) * e(-vk_x, gamma) * e(-C, delta) == 1
            //
            // Compute each Miller loop and multiply the GT elements together,
            // then apply a single final exponentiation and check identity.

            var negAlpha = PairingEngine.NegG1(vk.AlphaG1);
            var negVkX = PairingEngine.NegG1(vkX);
            var negC = PairingEngine.NegG1(proof.C);

            // Miller loops
            Bls.PT ml1 = PairingEngine.ComputeMillerLoop(proof.A, proof.B);
            Bls.PT ml2 = PairingEngine.ComputeMillerLoop(negAlpha, vk.BetaG2);
            Bls.PT ml3 = PairingEngine.ComputeMillerLoop(negVkX, vk.GammaG2);
            Bls.PT ml4 = PairingEngine.ComputeMillerLoop(negC, vk.DeltaG2);

            // Multiply all Miller loop results
            ml1.Mul(ml2);
            ml1.Mul(ml3);
            ml1.Mul(ml4);

            // Single final exponentiation
            Bls.PT result = ml1.FinalExp();

            // Check if result is the GT identity (which means the pairing equation holds).
            // The GT identity is e(identity_G1, any_G2) = 1.
            // We compare against e(0, G2) which is the identity in GT.
            var identityG1 = new byte[PairingEngine.G1CompressedSize];
            identityG1[0] = 0xC0; // compressed identity
            Bls.PT gtIdentity = PairingEngine.ComputeMillerLoop(identityG1, vk.BetaG2);
            gtIdentity = gtIdentity.FinalExp();

            return result.IsEqual(gtIdentity);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compute the verification key linear combination:
    /// vk_x = IC[0] + sum(publicInputs[i] * IC[i+1])
    /// </summary>
    private static byte[] ComputeVkX(byte[][] ic, byte[][] publicInputs)
    {
        // Start with IC[0]
        byte[] vkX = (byte[])ic[0].Clone();

        for (int i = 0; i < publicInputs.Length; i++)
        {
            if (publicInputs[i].Length != PairingEngine.ScalarSize)
                throw new ArgumentException($"Public input {i} must be {PairingEngine.ScalarSize} bytes.");

            // Compute publicInputs[i] * IC[i+1]
            byte[] term = PairingEngine.ScalarMultG1(ic[i + 1], publicInputs[i]);

            // Accumulate: vkX = vkX + term
            vkX = PairingEngine.AddG1(vkX, term);
        }

        return vkX;
    }
}
