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

    /// <summary>
    /// Get the proof requirements for a given contract/token address.
    /// Returns the required schemas from the compliance policy, if any.
    /// </summary>
    ProofRequirement[] GetRequirements(byte[] contractAddress);

    /// <summary>
    /// Check traditional (non-ZK) compliance rules for a transfer.
    /// Includes KYC, sanctions, geo-restrictions, holding limits, lockup, and travel rule.
    /// Returns Success if no compliance policy exists for the token address.
    /// </summary>
    ComplianceCheckOutcome CheckTransferCompliance(
        byte[] tokenAddress, byte[] sender, byte[] receiver,
        ulong amount, long currentTimestamp, ulong receiverCurrentBalance);

    /// <summary>
    /// Reset the nullifier set. Called at block boundaries to bound memory usage
    /// and allow cross-block proof reuse (COMPL-07).
    /// </summary>
    void ResetNullifiers();
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
