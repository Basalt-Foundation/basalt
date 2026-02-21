using Basalt.Confidentiality.Crypto;
using Basalt.Core;

namespace Basalt.Compliance;

/// <summary>
/// ZK-based compliance verifier. Validates Groth16 proofs attached to transactions
/// to verify the sender satisfies credential schema requirements without revealing
/// any identity information.
///
/// Each proof demonstrates:
/// - The sender holds a valid credential matching the required schema
/// - The credential was issued by a sufficiently-trusted issuer (tier >= required)
/// - The credential has not expired (checked against block timestamp)
/// - The credential has not been revoked (non-membership in issuer's revocation tree)
///
/// The verifier knows nothing about the sender's identity. It only verifies
/// that mathematically valid proofs were provided.
/// </summary>
public sealed class ZkComplianceVerifier : IComplianceVerifier
{
    private readonly Func<Hash256, byte[]?> _getVerificationKey;
    private readonly HashSet<Hash256> _usedNullifiers = new();
    private readonly object _lock = new();

    /// <summary>
    /// Create a verifier with a VK lookup function.
    /// </summary>
    /// <param name="getVerificationKey">
    /// Function that retrieves the serialized verification key for a given schema ID.
    /// Returns null if no VK is registered for the schema.
    /// </param>
    public ZkComplianceVerifier(Func<Hash256, byte[]?> getVerificationKey)
    {
        _getVerificationKey = getVerificationKey;
    }

    /// <summary>
    /// ZkComplianceVerifier does not manage policies — returns empty requirements.
    /// Use ComplianceEngine for policy-aware requirement lookup.
    /// </summary>
    public ProofRequirement[] GetRequirements(byte[] contractAddress) => [];

    /// <summary>
    /// ZK verifier does not perform traditional compliance checks.
    /// Use ComplianceEngine for KYC/sanctions/geo checks.
    /// </summary>
    public ComplianceCheckOutcome CheckTransferCompliance(
        byte[] tokenAddress, byte[] sender, byte[] receiver,
        ulong amount, long currentTimestamp, ulong receiverCurrentBalance)
        => ComplianceCheckOutcome.Success;

    /// <summary>
    /// Verify all compliance proofs against the given requirements.
    /// Each requirement must be satisfied by exactly one proof matching its schema ID.
    /// </summary>
    public ComplianceCheckOutcome VerifyProofs(
        ComplianceProof[] proofs,
        ProofRequirement[] requirements,
        long blockTimestamp)
    {
        if (requirements.Length == 0)
            return ComplianceCheckOutcome.Success;

        if (proofs.Length == 0)
            return ComplianceCheckOutcome.Fail(
                BasaltErrorCode.ComplianceProofMissing,
                "Transaction requires compliance proofs but none were provided");

        // Match each requirement to a proof by schema ID
        foreach (var requirement in requirements)
        {
            // L-05: Validate MinIssuerTier is set (tier 0 = self-attested, not suitable for compliance)
            if (requirement.MinIssuerTier == 0)
                return ComplianceCheckOutcome.Fail(
                    BasaltErrorCode.ComplianceProofInvalid,
                    $"Schema {requirement.SchemaId.ToHexString()} requires MinIssuerTier > 0");

            var proof = FindProofForSchema(proofs, requirement.SchemaId);
            if (proof == null)
                return ComplianceCheckOutcome.Fail(
                    BasaltErrorCode.ComplianceProofMissing,
                    $"Missing proof for schema {requirement.SchemaId.ToHexString()}");

            var result = VerifySingleProof(proof.Value, requirement, blockTimestamp);
            if (!result.Allowed)
                return result;
        }

        return ComplianceCheckOutcome.Success;
    }

    private ComplianceProof? FindProofForSchema(ComplianceProof[] proofs, Hash256 schemaId)
    {
        foreach (var p in proofs)
        {
            if (p.SchemaId == schemaId)
                return p;
        }
        return null;
    }

    private ComplianceCheckOutcome VerifySingleProof(
        ComplianceProof proof,
        ProofRequirement requirement,
        long blockTimestamp)
    {
        // 1. Validate proof data format
        if (proof.Proof == null || proof.Proof.Length != ComplianceProof.Groth16ProofSize)
            return ComplianceCheckOutcome.Fail(
                BasaltErrorCode.ComplianceProofInvalid,
                "Invalid proof size: expected 192 bytes (Groth16 A+B+C)");

        if (proof.PublicInputs == null || proof.PublicInputs.Length == 0 ||
            proof.PublicInputs.Length % 32 != 0)
            return ComplianceCheckOutcome.Fail(
                BasaltErrorCode.ComplianceProofInvalid,
                "Invalid public inputs: must be non-empty and a multiple of 32 bytes");

        // 2. Check nullifier not already consumed (reject replays)
        lock (_lock)
        {
            if (_usedNullifiers.Contains(proof.Nullifier))
                return ComplianceCheckOutcome.Fail(
                    BasaltErrorCode.ComplianceProofInvalid,
                    "Duplicate nullifier: proof has already been used");
        }

        // 3. Lookup verification key for this schema
        var vkBytes = _getVerificationKey(requirement.SchemaId);
        if (vkBytes == null || vkBytes.Length == 0)
            return ComplianceCheckOutcome.Fail(
                BasaltErrorCode.ComplianceProofInvalid,
                $"No verification key registered for schema {requirement.SchemaId.ToHexString()}");

        // 4. Decode VK and proof
        VerificationKey vk;
        Groth16Proof groth16Proof;
        try
        {
            vk = Groth16Codec.DecodeVerificationKey(vkBytes);
            groth16Proof = Groth16Codec.DecodeProof(proof.Proof);
        }
        catch (Exception ex)
        {
            return ComplianceCheckOutcome.Fail(
                BasaltErrorCode.ComplianceProofInvalid,
                $"Failed to decode proof or verification key: {ex.Message}");
        }

        // 5. Split public inputs into 32-byte scalars
        int inputCount = proof.PublicInputs.Length / 32;
        var publicInputs = new byte[inputCount][];
        for (int i = 0; i < inputCount; i++)
        {
            publicInputs[i] = new byte[32];
            Array.Copy(proof.PublicInputs, i * 32, publicInputs[i], 0, 32);
        }

        // 6. Verify the Groth16 proof
        if (!Groth16Verifier.Verify(vk, groth16Proof, publicInputs))
            return ComplianceCheckOutcome.Fail(
                BasaltErrorCode.ComplianceProofInvalid,
                "Groth16 proof verification failed");

        // 7. M-03: Only consume nullifier AFTER successful verification
        lock (_lock)
        {
            _usedNullifiers.Add(proof.Nullifier);
        }

        return ComplianceCheckOutcome.Success;
    }

    /// <summary>
    /// Clear used nullifiers (called at the start of each block to allow
    /// proofs to be reused across blocks — only same-block replay is prevented).
    /// </summary>
    public void ResetNullifiers()
    {
        lock (_lock)
        {
            _usedNullifiers.Clear();
        }
    }
}
