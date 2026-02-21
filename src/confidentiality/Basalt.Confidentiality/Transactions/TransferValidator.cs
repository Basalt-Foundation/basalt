using Basalt.Core;
using Basalt.Confidentiality.Crypto;
using Basalt.Crypto;

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
    /// Validate the Groth16 range proof attached to the transfer.
    /// F-02: Range proofs MUST be provided for confidential transfers to prevent hidden inflation.
    /// </summary>
    /// <param name="transfer">The confidential transfer containing the range proof.</param>
    /// <param name="rangeProofVk">
    /// The verification key for the range proof circuit. Required.
    /// </param>
    /// <returns><c>true</c> if the range proof verifies successfully.</returns>
    public static bool ValidateRangeProof(ConfidentialTransfer transfer, VerificationKey? rangeProofVk)
    {
        // F-02: Range proofs MUST be provided for confidential transfers
        if (transfer?.RangeProof == null)
            return false;

        if (rangeProofVk == null)
            return false;

        // F-04: Hash full 48-byte commitment to 32-byte scalar for sound binding.
        // Truncating 48-byte commitments to 32 bytes is not a sound binding;
        // instead we hash the full commitment to derive the public input scalar.
        var publicInputs = new byte[transfer.OutputCommitments.Length][];
        for (int i = 0; i < transfer.OutputCommitments.Length; i++)
        {
            publicInputs[i] = Blake3Hasher.Hash(transfer.OutputCommitments[i]).ToArray();
        }

        return Groth16Verifier.Verify(rangeProofVk, transfer.RangeProof, publicInputs);
    }

    /// <summary>
    /// Full transfer validation: checks balance preservation and range proof.
    /// </summary>
    /// <param name="transfer">The confidential transfer to validate.</param>
    /// <param name="rangeProofVk">
    /// M-03: The verification key for range proof validation. Required — range proofs
    /// are mandatory for confidential transfers to prevent hidden inflation (F-02).
    /// A null VK will cause the range proof check to fail.
    /// </param>
    /// <returns><c>true</c> if all checks pass.</returns>
    public static bool ValidateTransfer(ConfidentialTransfer transfer, VerificationKey? rangeProofVk)
    {
        if (!ValidateBalance(transfer))
            return false;

        if (!ValidateRangeProof(transfer, rangeProofVk))
            return false;

        return true;
    }
}
