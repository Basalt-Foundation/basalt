using System.Security.Cryptography;
using Basalt.Confidentiality.Crypto;
using Basalt.Core;

namespace Basalt.Confidentiality.Disclosure;

/// <summary>
/// A disclosure proof reveals the contents of a Pedersen commitment to a verifier.
/// This is a simple opening proof: the prover reveals (value, blindingFactor) and
/// the verifier checks that Commit(value, blindingFactor) == commitment.
/// </summary>
public sealed class DisclosureProof
{
    /// <summary>The committed value.</summary>
    public required UInt256 Value { get; init; }

    /// <summary>32-byte blinding factor used in the commitment.</summary>
    public required byte[] BlindingFactor { get; init; }

    /// <summary>F-15: Reference to the commitment being opened.</summary>
    public byte[]? CommitmentRef { get; init; }

    /// <summary>
    /// Create a disclosure proof for a Pedersen commitment.
    /// </summary>
    /// <param name="value">The committed value.</param>
    /// <param name="blindingFactor">32-byte blinding factor.</param>
    /// <param name="commitmentRef">F-15: Optional reference to the commitment being opened.</param>
    /// <returns>A disclosure proof that can be verified against the commitment.</returns>
    public static DisclosureProof Create(UInt256 value, byte[] blindingFactor, byte[]? commitmentRef = null)
    {
        if (blindingFactor == null || blindingFactor.Length != PairingEngine.ScalarSize)
            throw new ArgumentException($"Blinding factor must be {PairingEngine.ScalarSize} bytes.", nameof(blindingFactor));

        return new DisclosureProof
        {
            Value = value,
            BlindingFactor = (byte[])blindingFactor.Clone(),
            CommitmentRef = commitmentRef != null ? (byte[])commitmentRef.Clone() : null,
        };
    }

    /// <summary>
    /// Verify a disclosure proof against a Pedersen commitment.
    /// Checks that Commit(proof.Value, proof.BlindingFactor) equals the given commitment.
    /// F-15: If the proof contains a CommitmentRef, it must match the provided commitment.
    /// </summary>
    /// <param name="commitment">48-byte compressed G1 Pedersen commitment.</param>
    /// <param name="proof">The disclosure proof to verify.</param>
    /// <returns><c>true</c> if the proof is valid for the commitment.</returns>
    public static bool Verify(ReadOnlySpan<byte> commitment, DisclosureProof proof)
    {
        if (proof == null)
            return false;

        if (commitment.Length != PairingEngine.G1CompressedSize)
            return false;

        if (proof.BlindingFactor == null || proof.BlindingFactor.Length != PairingEngine.ScalarSize)
            return false;

        // F-15: If a commitment reference is provided, it must match the given commitment
        // LOW-02: Use constant-time comparison to prevent timing side-channel attacks
        if (proof.CommitmentRef != null &&
            !CryptographicOperations.FixedTimeEquals(proof.CommitmentRef, commitment))
            return false;

        return PedersenCommitment.Open(commitment, proof.Value, proof.BlindingFactor);
    }
}
