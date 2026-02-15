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

    /// <summary>
    /// Create a disclosure proof for a Pedersen commitment.
    /// </summary>
    /// <param name="value">The committed value.</param>
    /// <param name="blindingFactor">32-byte blinding factor.</param>
    /// <returns>A disclosure proof that can be verified against the commitment.</returns>
    public static DisclosureProof Create(UInt256 value, byte[] blindingFactor)
    {
        if (blindingFactor == null || blindingFactor.Length != PairingEngine.ScalarSize)
            throw new ArgumentException($"Blinding factor must be {PairingEngine.ScalarSize} bytes.", nameof(blindingFactor));

        return new DisclosureProof
        {
            Value = value,
            BlindingFactor = (byte[])blindingFactor.Clone(),
        };
    }

    /// <summary>
    /// Verify a disclosure proof against a Pedersen commitment.
    /// Checks that Commit(proof.Value, proof.BlindingFactor) equals the given commitment.
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

        return PedersenCommitment.Open(commitment, proof.Value, proof.BlindingFactor);
    }
}
