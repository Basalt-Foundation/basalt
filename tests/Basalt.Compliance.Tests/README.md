# Basalt.Compliance.Tests

Unit tests for the Basalt compliance module: identity registry, sanctions list, compliance engine, and KYC providers. **43 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| ComplianceEngine | 20 | Full 8-step transfer check pipeline: pause, sender KYC, receiver KYC, sanctions, geo-restriction, holding limit, lockup, travel rule; audit log for approved/blocked transfers; policy retrieval |
| IdentityRegistry | 12 | Provider approval/revocation, attestation issuance/revocation, KYC level checks, expiration, country code queries, audit log recording and filtering |
| MockKycProvider | 6 | Auto-approval, basic/enhanced/institutional attestation issuance, revocation, custom claim hash |
| SanctionsList | 5 | Add/remove sanctions, query status, independent address tracking, audit trail |

**Total: 43 tests**

## Test Files

- `ComplianceEngineTests.cs` -- 8-step compliance pipeline: pause check, KYC sender/receiver, sanctions, geo-restriction, holding limit, lockup, travel rule, audit logging
- `IdentityRegistryTests.cs` -- Identity registry: provider management, attestation lifecycle, KYC levels, expiry, country codes, audit log
- `MockKycProviderTests.cs` -- Mock KYC provider: auto-approval, basic/enhanced/institutional issuance, revocation
- `SanctionsListTests.cs` -- Sanctions list: add/remove sanctions, query status, audit trail

## Running

```bash
dotnet test tests/Basalt.Compliance.Tests
```
