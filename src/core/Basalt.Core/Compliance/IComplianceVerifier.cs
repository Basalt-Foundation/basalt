namespace Basalt.Core;

/// <summary>
/// Abstraction for ZK compliance verification, allowing the execution layer
/// to verify transaction compliance proofs without direct dependency on
/// the compliance or confidentiality modules.
/// </summary>
public interface IComplianceVerifier
{
    /// <summary>
    /// Verify all compliance proofs attached to a transaction.
    /// Each proof is a Groth16 ZK-SNARK demonstrating the sender holds
    /// a valid credential satisfying a required schema, without revealing
    /// the credential, issuer, or identity details.
    /// </summary>
    ComplianceCheckOutcome VerifyProofs(
        ComplianceProof[] proofs,
        ProofRequirement[] requirements,
        long blockTimestamp);
}

/// <summary>
/// Result of a compliance verification.
/// </summary>
public readonly struct ComplianceCheckOutcome
{
    public bool Allowed { get; init; }
    public BasaltErrorCode ErrorCode { get; init; }
    public string Reason { get; init; }

    public static ComplianceCheckOutcome Success { get; } = new()
    {
        Allowed = true,
        ErrorCode = BasaltErrorCode.Success,
        Reason = "",
    };

    public static ComplianceCheckOutcome Fail(BasaltErrorCode code, string reason) => new()
    {
        Allowed = false,
        ErrorCode = code,
        Reason = reason,
    };
}
