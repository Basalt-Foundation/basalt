using Basalt.Confidentiality.Crypto;

namespace Basalt.Confidentiality.Transactions;

/// <summary>
/// Represents a confidential value transfer where amounts are hidden behind
/// Pedersen commitments.
///
/// The sender proves:
/// 1. Balance preservation: sum(InputCommitments) - sum(OutputCommitments) is a
///    commitment to zero, verifiable using <see cref="BalanceProofBlinding"/>.
/// 2. Non-negativity: each output amount is non-negative (optional Groth16 range proof).
///
/// Encrypted amounts allow recipients to learn their received value without
/// revealing it to others.
/// </summary>
public sealed class ConfidentialTransfer
{
    /// <summary>
    /// Pedersen commitments to the input amounts (48-byte compressed G1 points each).
    /// </summary>
    public required byte[][] InputCommitments { get; init; }

    /// <summary>
    /// Pedersen commitments to the output amounts (48-byte compressed G1 points each).
    /// </summary>
    public required byte[][] OutputCommitments { get; init; }

    /// <summary>
    /// 32-byte big-endian blinding factor that opens the balance difference
    /// commitment to zero. Specifically, if r_in = sum of input blinding factors
    /// and r_out = sum of output blinding factors, then
    /// BalanceProofBlinding = r_in - r_out (mod scalar field order).
    /// </summary>
    public required byte[] BalanceProofBlinding { get; init; }

    /// <summary>
    /// Encrypted amounts for each output, allowing recipients to learn their
    /// received value. Each entry corresponds to an output commitment at the
    /// same index. Format is implementation-defined (e.g., X25519 + AES-GCM).
    /// </summary>
    public byte[][] EncryptedAmounts { get; init; } = [];

    /// <summary>
    /// Optional Groth16 range proof demonstrating all output amounts are
    /// non-negative (within [0, 2^64)). If null, range proof verification is skipped.
    /// </summary>
    public Groth16Proof? RangeProof { get; init; }
}
