using Basalt.Core;
using Basalt.Confidentiality.Crypto;

namespace Basalt.Confidentiality.Transactions;

/// <summary>
/// Validates <see cref="ConfidentialTransfer"/> instances by checking:
/// - Balance preservation (commitment arithmetic)
/// - Optional Groth16 range proof
/// - Structural integrity
/// </summary>
public static class TransferValidator
{
    /// <summary>
    /// Validate that the sum of input commitments equals the sum of output commitments.
    ///
    /// Computes: diff = sum(InputCommitments) - sum(OutputCommitments)
    /// Then verifies: Open(diff, 0, BalanceProofBlinding) == true
    ///
    /// This works because Pedersen commitments are additively homomorphic:
    /// if each input commits to v_i with blinding r_i, and each output commits
    /// to v_j with blinding r_j, then:
    ///   diff = Commit(sum(v_i) - sum(v_j), sum(r_i) - sum(r_j))
    /// For the transfer to balance, sum(v_i) = sum(v_j), so:
    ///   diff = Commit(0, sum(r_i) - sum(r_j))
    /// The BalanceProofBlinding is sum(r_i) - sum(r_j).
    /// </summary>
    /// <param name="transfer">The confidential transfer to validate.</param>
    /// <returns><c>true</c> if the balance proof is valid.</returns>
    public static bool ValidateBalance(ConfidentialTransfer transfer)
    {
        if (transfer == null)
            return false;

        if (transfer.InputCommitments.Length == 0 || transfer.OutputCommitments.Length == 0)
            return false;

        if (transfer.BalanceProofBlinding.Length != PairingEngine.ScalarSize)
            return false;

        // Validate commitment sizes
        foreach (var c in transfer.InputCommitments)
        {
            if (c == null || c.Length != PairingEngine.G1CompressedSize)
                return false;
        }

        foreach (var c in transfer.OutputCommitments)
        {
            if (c == null || c.Length != PairingEngine.G1CompressedSize)
                return false;
        }

        try
        {
            // Sum input commitments
            byte[] inputSum = PedersenCommitment.AddCommitments(transfer.InputCommitments);

            // Sum output commitments
            byte[] outputSum = PedersenCommitment.AddCommitments(transfer.OutputCommitments);

            // Compute diff = inputSum - outputSum
            byte[] diff = PedersenCommitment.SubtractCommitments(inputSum, outputSum);

            // The diff should be a commitment to zero with the provided blinding factor:
            // diff == Commit(0, BalanceProofBlinding)
            return PedersenCommitment.Open(diff, UInt256.Zero, transfer.BalanceProofBlinding);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Validate the optional Groth16 range proof attached to the transfer.
    /// If no range proof is present, returns <c>true</c> (range proofs are optional).
    /// </summary>
    /// <param name="transfer">The confidential transfer containing the range proof.</param>
    /// <param name="rangeProofVk">
    /// The verification key for the range proof circuit. Required if a range proof is present.
    /// </param>
    /// <returns><c>true</c> if no range proof exists or it verifies successfully.</returns>
    public static bool ValidateRangeProof(ConfidentialTransfer transfer, VerificationKey? rangeProofVk)
    {
        if (transfer?.RangeProof == null)
            return true;

        if (rangeProofVk == null)
            return false;

        // The public inputs for the range proof are the output commitments
        // (serialized as 32-byte scalars by taking the x-coordinate mod field order).
        // For simplicity, we pass the first 32 bytes of each compressed commitment.
        var publicInputs = new byte[transfer.OutputCommitments.Length][];
        for (int i = 0; i < transfer.OutputCommitments.Length; i++)
        {
            publicInputs[i] = new byte[PairingEngine.ScalarSize];
            Buffer.BlockCopy(transfer.OutputCommitments[i], 0, publicInputs[i], 0,
                Math.Min(transfer.OutputCommitments[i].Length, PairingEngine.ScalarSize));
        }

        return Groth16Verifier.Verify(rangeProofVk, transfer.RangeProof, publicInputs);
    }

    /// <summary>
    /// Full transfer validation: checks balance preservation and optional range proof.
    /// </summary>
    /// <param name="transfer">The confidential transfer to validate.</param>
    /// <param name="rangeProofVk">
    /// Optional verification key for range proof validation.
    /// </param>
    /// <returns><c>true</c> if all checks pass.</returns>
    public static bool ValidateTransfer(ConfidentialTransfer transfer, VerificationKey? rangeProofVk = null)
    {
        if (!ValidateBalance(transfer))
            return false;

        if (!ValidateRangeProof(transfer, rangeProofVk))
            return false;

        return true;
    }
}
