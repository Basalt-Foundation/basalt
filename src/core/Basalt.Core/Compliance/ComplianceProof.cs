namespace Basalt.Core;

/// <summary>
/// A zero-knowledge compliance proof attached to a transaction.
/// Proves the sender satisfies a credential schema requirement
/// without revealing identity, issuer, or credential details.
/// </summary>
public readonly struct ComplianceProof
{
    /// <summary>BLAKE3 hash of the schema name this proof satisfies.</summary>
    public Hash256 SchemaId { get; init; }

    /// <summary>Groth16 proof bytes (A[48] + B[96] + C[48] = 192 bytes).</summary>
    public byte[] Proof { get; init; } = Array.Empty<byte>();

    /// <summary>Public inputs for the Groth16 verifier (N × 32-byte scalars).</summary>
    public byte[] PublicInputs { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Unique nullifier per proof, preventing cross-transaction correlation.
    /// Derived from the credential + randomness so two proofs from the same
    /// credential cannot be linked.
    /// </summary>
    public Hash256 Nullifier { get; init; }

    /// <summary>Fixed size of a Groth16 proof (A + B + C).</summary>
    public const int Groth16ProofSize = 192;

    /// <summary>Parameterless constructor for default initialization with safe defaults.</summary>
    public ComplianceProof() { }
}
