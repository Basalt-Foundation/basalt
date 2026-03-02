# Basalt.Compliance.Tests

Unit tests for the Basalt compliance module: identity registry, sanctions list, compliance engine, KYC providers, ZK compliance verification, nullifier windows, and identity governance. **78 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| ComplianceEngine | 28 | Full 8-step transfer check pipeline: pause, sender KYC, receiver KYC, sanctions, geo-restriction, holding limit, lockup, travel rule; audit log for approved/blocked transfers; policy retrieval; ZK proof integration |
| ZkComplianceVerifier | 14 | Groth16 proof verification, schema validation, issuer validation, nullifier tracking, proof expiry, public input validation |
| IdentityRegistry | 14 | Provider approval/revocation, attestation issuance/revocation, KYC level checks, expiration, country code queries, audit log recording and filtering |
| MockKycProvider | 6 | Auto-approval, basic/enhanced/institutional attestation issuance, revocation, custom claim hash |
| NullifierWindow | 6 | Nullifier window management, anti-correlation, double-spend prevention, window expiry |
| SanctionsList | 5 | Add/remove sanctions, query status, independent address tracking, audit trail |
| IdentityRegistryGovernance | 5 | Governance-controlled provider management, authority delegation, permission checks |

**Total: 78 tests**

## Test Files

- `ComplianceEngineTests.cs` -- 8-step compliance pipeline: pause check, KYC sender/receiver, sanctions, geo-restriction, holding limit, lockup, travel rule, audit logging
- `ZkComplianceVerifierTests.cs` -- ZK compliance: Groth16 proof verification, schema/issuer validation, nullifier tracking
- `IdentityRegistryTests.cs` -- Identity registry: provider management, attestation lifecycle, KYC levels, expiry, country codes, audit log
- `MockKycProviderTests.cs` -- Mock KYC provider: auto-approval, basic/enhanced/institutional issuance, revocation
- `NullifierWindowTests.cs` -- Nullifier window: anti-correlation, double-spend prevention, window management
- `SanctionsListTests.cs` -- Sanctions list: add/remove sanctions, query status, audit trail
- `IdentityRegistryGovernanceTests.cs` -- Governance-controlled identity provider management

## Running

```bash
dotnet test tests/Basalt.Compliance.Tests
```
