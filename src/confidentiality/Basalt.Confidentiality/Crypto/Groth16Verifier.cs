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

        // C-02: Reject identity elements in proof — a proof containing identity
        // points is trivially forgeable and breaks Groth16 soundness.
        if (PairingEngine.IsG1Identity(proof.A) ||
            PairingEngine.IsG2Identity(proof.B) ||
            PairingEngine.IsG1Identity(proof.C))
            return false;

        // C-01: Validate subgroup membership. G2 has a non-trivial cofactor,
        // so points can be on the curve but NOT in the subgroup. Accepting
        // such points enables small-subgroup attacks on the pairing equation.
        // G1 cofactor is 1 so all on-curve G1 points are in the subgroup,
        // but we validate them anyway for defense-in-depth.
        if (!PairingEngine.IsValidG1(proof.A) ||
            !PairingEngine.IsValidG2(proof.B) ||
            !PairingEngine.IsValidG1(proof.C))
            return false;

        if (!PairingEngine.IsValidG1(vk.AlphaG1) ||
            !PairingEngine.IsValidG2(vk.BetaG2) ||
            !PairingEngine.IsValidG2(vk.GammaG2) ||
            !PairingEngine.IsValidG2(vk.DeltaG2))
            return false;

        foreach (var ic in vk.IC)
        {
            if (ic == null || ic.Length != PairingEngine.G1CompressedSize || !PairingEngine.IsValidG1(ic))
                return false;
        }

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

            // L-01: Check if result is the GT identity (1_GT) using IsOne().
            // This avoids recomputing the GT identity via a pairing on every call.
            return result.IsOne();
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
