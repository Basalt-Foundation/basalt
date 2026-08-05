using System.Buffers.Binary;

namespace Basalt.Compliance;

/// <summary>
/// Frozen public-input layout for compliance circuit v1. This is the load-bearing contract between the
/// gnark prover, which exposes these values as Groth16 public inputs in this exact order, and the
/// on-chain verifier, which reads them at these indices to enforce credential expiry, issuer tier, and
/// nullifier binding (COMPL-C03). Each public input is a 32-byte big-endian BLS12-381 scalar. Changing an
/// index is a breaking change that requires a new circuit and re-registering every verification key.
/// </summary>
public static class CircuitV1Layout
{
    /// <summary>Merkle root of the issuer's credential accumulator.</summary>
    public const int IssuerRoot = 0;

    /// <summary>Per-credential-per-epoch nullifier. Must equal <c>ComplianceProof.Nullifier</c>.</summary>
    public const int Nullifier = 1;

    /// <summary>Credential expiry as unix seconds (big-endian). Enforced against the block timestamp.</summary>
    public const int Expiry = 2;

    /// <summary>Issuer trust tier. Enforced against the schema's minimum required tier.</summary>
    public const int IssuerTier = 3;

    /// <summary>Revocation accumulator root.</summary>
    public const int RevocationRoot = 4;

    /// <summary>Number of public inputs a v1 proof exposes. Equals <c>vk.IC.Length - 1</c>.</summary>
    public const int PublicInputCount = 5;

    /// <summary>
    /// Reads a public input (32-byte big-endian scalar) as a <see cref="ulong"/>, used for the expiry and
    /// tier fields. Throws if the value does not fit in 64 bits (the top 24 bytes must be zero), which
    /// signals a malformed or out-of-layout proof.
    /// </summary>
    public static ulong ReadUInt64(byte[] publicInput)
    {
        ArgumentNullException.ThrowIfNull(publicInput);
        if (publicInput.Length != 32)
            throw new ArgumentException($"Public input must be 32 bytes, got {publicInput.Length}.");
        for (int i = 0; i < 24; i++)
        {
            if (publicInput[i] != 0)
                throw new ArgumentException("Public input exceeds 64 bits.");
        }
        return BinaryPrimitives.ReadUInt64BigEndian(publicInput.AsSpan(24, 8));
    }
}
