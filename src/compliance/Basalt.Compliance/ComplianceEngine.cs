using Basalt.Core;
using Basalt.Execution;

namespace Basalt.Compliance;

/// <summary>
/// Compliance Engine with hybrid verification paths.
/// Supports two compliance models:
/// - ZK proofs (privacy-preserving): Groth16 proofs attached to transactions
/// - On-chain attestations (legacy): KYC levels and sanctions checks
///
/// The engine checks for ZK proofs first. If present, validates them via
/// ZkComplianceVerifier. Otherwise, falls back to on-chain attestation checks.
/// </summary>
public sealed class ComplianceEngine : IComplianceVerifier
{
    private readonly IdentityRegistry _identityRegistry;
    private readonly SanctionsList _sanctionsList;
    private readonly ZkComplianceVerifier? _zkVerifier;
    private readonly Dictionary<string, CompliancePolicy> _policies = new();
    private readonly List<ComplianceEvent> _auditLog = new();
    private readonly object _lock = new();

    public ComplianceEngine(IdentityRegistry identityRegistry, SanctionsList sanctionsList)
    {
        _identityRegistry = identityRegistry;
        _sanctionsList = sanctionsList;
    }

    public ComplianceEngine(IdentityRegistry identityRegistry, SanctionsList sanctionsList, ZkComplianceVerifier zkVerifier)
        : this(identityRegistry, sanctionsList)
    {
        _zkVerifier = zkVerifier;
    }

    /// <summary>
    /// Hybrid compliance check: ZK proofs first, attestation fallback.
    /// </summary>
    public ComplianceCheckOutcome VerifyProofs(
        ComplianceProof[] proofs,
        ProofRequirement[] requirements,
        long blockTimestamp)
    {
        if (_zkVerifier != null)
            return _zkVerifier.VerifyProofs(proofs, requirements, blockTimestamp);

        // No ZK verifier configured — fail if requirements exist (C-01)
        if (requirements.Length > 0)
            return ComplianceCheckOutcome.Fail(
                BasaltErrorCode.ComplianceProofMissing,
                "Compliance requirements exist but ZK verification is not available");

        // Proofs provided but no verifier to validate them
        if (proofs.Length > 0)
            return ComplianceCheckOutcome.Fail(
                BasaltErrorCode.ComplianceProofInvalid,
                "ZK verification not available");

        return ComplianceCheckOutcome.Success;
    }

    /// <summary>
    /// Traditional compliance check delegated from execution layer (H-02).
    /// Maps ComplianceCheckResult to ComplianceCheckOutcome for the Core interface.
    /// </summary>
    public ComplianceCheckOutcome CheckTransferCompliance(
        byte[] tokenAddress, byte[] sender, byte[] receiver,
        ulong amount, long currentTimestamp, ulong receiverCurrentBalance)
    {
        var result = CheckTransfer(tokenAddress, sender, receiver, amount, currentTimestamp, receiverCurrentBalance);
        if (result.Allowed)
            return ComplianceCheckOutcome.Success;

        var errorCode = result.ErrorCode switch
        {
            ComplianceErrorCode.KycMissing => BasaltErrorCode.KycRequired,
            ComplianceErrorCode.Sanctioned => BasaltErrorCode.SanctionedAddress,
            ComplianceErrorCode.GeoRestricted => BasaltErrorCode.GeoRestricted,
            ComplianceErrorCode.HoldingLimit => BasaltErrorCode.HoldingLimitExceeded,
            ComplianceErrorCode.Lockup => BasaltErrorCode.LockupPeriodActive,
            ComplianceErrorCode.Paused => BasaltErrorCode.TransferRestricted,
            ComplianceErrorCode.TravelRuleMissing => BasaltErrorCode.TransferRestricted,
            _ => BasaltErrorCode.TransferRestricted,
        };

        return ComplianceCheckOutcome.Fail(errorCode, result.Reason);
    }

    /// <summary>
    /// Get the proof requirements for a given contract/token address.
    /// Returns the RequiredProofs from the compliance policy, or empty if no policy.
    /// </summary>
    public ProofRequirement[] GetRequirements(byte[] contractAddress)
    {
        lock (_lock)
        {
            if (_policies.TryGetValue(ToHex(contractAddress), out var policy))
                return policy.RequiredProofs;
            return [];
        }
    }

    /// <summary>
    /// Reset nullifiers in the underlying ZK verifier (COMPL-07).
    /// Called at block boundaries to bound memory usage.
    /// </summary>
    public void ResetNullifiers()
    {
        (_zkVerifier as ZkComplianceVerifier)?.ResetNullifiers();
    }

    /// <summary>
    /// Register or update a compliance policy for a token.
    /// Only callable by the token issuer/owner (COMPL-05).
    /// </summary>
    public bool SetPolicy(byte[] tokenAddress, CompliancePolicy policy, byte[]? caller = null)
    {
        lock (_lock)
        {
            // COMPL-05: If a policy already exists, verify the caller is the original issuer
            if (caller != null && _policies.TryGetValue(ToHex(tokenAddress), out var existing)
                && existing.Issuer != null && ToHex(existing.Issuer) != ToHex(caller))
                return false;

            policy.Issuer ??= caller;
            _policies[ToHex(tokenAddress)] = policy;
            _auditLog.Add(new ComplianceEvent
            {
                EventType = ComplianceEventType.PolicyChanged,
                Subject = tokenAddress,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Details = $"Policy updated: KYC={policy.RequiredSenderKycLevel}/{policy.RequiredReceiverKycLevel}",
            });

            return true;
        }
    }

    /// <summary>
    /// Get the compliance policy for a token.
    /// </summary>
    public CompliancePolicy? GetPolicy(byte[] tokenAddress)
    {
        lock (_lock)
        {
            _policies.TryGetValue(ToHex(tokenAddress), out var policy);
            return policy;
        }
    }

    /// <summary>
    /// Check if a transfer complies with the token's compliance policy.
    /// Executes the full compliance pipeline.
    /// </summary>
    public ComplianceCheckResult CheckTransfer(
        byte[] tokenAddress,
        byte[] sender,
        byte[] receiver,
        ulong amount,
        long currentTimestamp,
        ulong receiverCurrentBalance = 0,
        bool hasTravelRuleData = false)
    {
        lock (_lock)
        {
            if (!_policies.TryGetValue(ToHex(tokenAddress), out var policy))
                return ComplianceCheckResult.Success; // No policy = unrestricted

            // Step 0: Paused check
            if (policy.Paused)
            {
                LogCheckResult(tokenAddress, sender, receiver, amount, false, "TOKEN_PAUSED");
                return ComplianceCheckResult.Fail(ComplianceErrorCode.Paused, "Token transfers are paused", "PAUSED");
            }

            // Step 1: Sender KYC check
            if (policy.RequiredSenderKycLevel > KycLevel.None)
            {
                if (!_identityRegistry.HasValidAttestation(sender, policy.RequiredSenderKycLevel, currentTimestamp))
                {
                    LogCheckResult(tokenAddress, sender, receiver, amount, false, "SENDER_KYC");
                    return ComplianceCheckResult.Fail(ComplianceErrorCode.KycMissing,
                        $"Sender lacks required KYC level {policy.RequiredSenderKycLevel}", "KYC_SENDER");
                }
            }

            // Step 2: Receiver KYC check
            if (policy.RequiredReceiverKycLevel > KycLevel.None)
            {
                if (!_identityRegistry.HasValidAttestation(receiver, policy.RequiredReceiverKycLevel, currentTimestamp))
                {
                    LogCheckResult(tokenAddress, sender, receiver, amount, false, "RECEIVER_KYC");
                    return ComplianceCheckResult.Fail(ComplianceErrorCode.KycMissing,
                        $"Receiver lacks required KYC level {policy.RequiredReceiverKycLevel}", "KYC_RECEIVER");
                }
            }

            // Step 3: Sanctions check
            if (policy.SanctionsCheckEnabled)
            {
                if (_sanctionsList.IsSanctioned(sender))
                {
                    LogCheckResult(tokenAddress, sender, receiver, amount, false, "SANCTIONS_SENDER");
                    return ComplianceCheckResult.Fail(ComplianceErrorCode.Sanctioned,
                        "Sender is on sanctions list", "SANCTIONS_SENDER");
                }

                if (_sanctionsList.IsSanctioned(receiver))
                {
                    LogCheckResult(tokenAddress, sender, receiver, amount, false, "SANCTIONS_RECEIVER");
                    return ComplianceCheckResult.Fail(ComplianceErrorCode.Sanctioned,
                        "Receiver is on sanctions list", "SANCTIONS_RECEIVER");
                }
            }

            // Step 4: Geographic restrictions
            if (policy.BlockedCountries.Count > 0)
            {
                var senderCountry = _identityRegistry.GetCountryCode(sender);
                if (senderCountry > 0 && policy.BlockedCountries.Contains(senderCountry))
                {
                    LogCheckResult(tokenAddress, sender, receiver, amount, false, "GEO_SENDER");
                    return ComplianceCheckResult.Fail(ComplianceErrorCode.GeoRestricted,
                        $"Sender country {senderCountry} is geo-restricted", "GEO_SENDER");
                }

                var receiverCountry = _identityRegistry.GetCountryCode(receiver);
                if (receiverCountry > 0 && policy.BlockedCountries.Contains(receiverCountry))
                {
                    LogCheckResult(tokenAddress, sender, receiver, amount, false, "GEO_RECEIVER");
                    return ComplianceCheckResult.Fail(ComplianceErrorCode.GeoRestricted,
                        $"Receiver country {receiverCountry} is geo-restricted", "GEO_RECEIVER");
                }
            }

            // Step 5: Holding limit (concentration check, H-01: overflow-safe)
            if (policy.MaxHoldingAmount > 0)
            {
                if (receiverCurrentBalance > policy.MaxHoldingAmount
                    || amount > policy.MaxHoldingAmount - receiverCurrentBalance)
                {
                    LogCheckResult(tokenAddress, sender, receiver, amount, false, "HOLDING_LIMIT");
                    return ComplianceCheckResult.Fail(ComplianceErrorCode.HoldingLimit,
                        $"Transfer would exceed holding limit of {policy.MaxHoldingAmount}", "HOLDING_LIMIT");
                }
            }

            // Step 6: Lock-up check
            if (policy.LockupEndTimestamp > 0 && currentTimestamp < policy.LockupEndTimestamp)
            {
                LogCheckResult(tokenAddress, sender, receiver, amount, false, "LOCKUP");
                return ComplianceCheckResult.Fail(ComplianceErrorCode.Lockup,
                    $"Tokens locked until {policy.LockupEndTimestamp}", "LOCKUP");
            }

            // Step 7: Travel Rule
            if (policy.TravelRuleThreshold > 0 && amount >= policy.TravelRuleThreshold && !hasTravelRuleData)
            {
                LogCheckResult(tokenAddress, sender, receiver, amount, false, "TRAVEL_RULE");
                return ComplianceCheckResult.Fail(ComplianceErrorCode.TravelRuleMissing,
                    $"Travel Rule data required for transfers >= {policy.TravelRuleThreshold}", "TRAVEL_RULE");
            }

            // All checks passed
            LogCheckResult(tokenAddress, sender, receiver, amount, true, "PASS");
            return ComplianceCheckResult.Success;
        }
    }

    /// <summary>
    /// Get the audit log for compliance checks.
    /// </summary>
    public IReadOnlyList<ComplianceEvent> GetAuditLog()
    {
        lock (_lock)
            return _auditLog.ToList();
    }

    /// <summary>
    /// Get audit events filtered by type.
    /// </summary>
    public IReadOnlyList<ComplianceEvent> GetAuditLog(ComplianceEventType eventType)
    {
        lock (_lock)
            return _auditLog.Where(e => e.EventType == eventType).ToList();
    }

    private void LogCheckResult(byte[] tokenAddress, byte[] sender, byte[] receiver, ulong amount, bool passed, string ruleId)
    {
        _auditLog.Add(new ComplianceEvent
        {
            EventType = passed ? ComplianceEventType.TransferApproved : ComplianceEventType.TransferBlocked,
            Subject = sender,
            Receiver = receiver,
            TokenAddress = tokenAddress,
            Amount = amount,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Details = $"Rule={ruleId}, Amount={amount}",
        });
    }

    private static string ToHex(byte[] data) => Convert.ToHexString(data);
}
