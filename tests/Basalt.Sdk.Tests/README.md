# Basalt.Sdk.Tests

Unit tests for the Basalt smart contract SDK: storage primitives, contract context, all BST token standards, system contracts, policy hooks, cross-contract calls, and the test host. **652 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| BridgeETH | 68 | EVM bridge: lock/unlock, multisig relayer verification, admin pattern, pause/unpause, deposit lifecycle |
| Governance | 61 | On-chain governance: proposals, quadratic voting, delegation, timelock, executable proposals |
| BST-3525 | 49 | Semi-fungible token (ERC-3525): slots, value transfers, approvals, minting, burning |
| BST-VC | 42 | Verifiable credentials registry: issuance, verification, revocation, schema validation |
| IssuerRegistry | 37 | ZK issuer registry: registration, credential verification, trust anchoring |
| BST-4626 | 31 | Tokenized vault (ERC-4626): deposit, withdraw, share pricing, yield accrual |
| BST-1155 | 27 | Multi-token standard: mint, burn, batch transfer, balance queries, approval, URI, supply |
| BST-DID | 27 | Decentralized identity: DID creation, resolution, document updates, deactivation, controllers |
| StakingPool | 26 | Staking pool: delegation, reward distribution, withdrawal, validator management |
| BST-20 | 24 | Fungible token standard: initialize, transfer, approve, transferFrom, balanceOf, totalSupply |
| BST-721 | 21 | Non-fungible token standard: mint, transfer, approve, balanceOf, ownerOf, token URI, burn |
| StoragePrimitives | 21 | StorageValue, StorageMap, StorageList: get/set/delete/contains, type safety, boundaries |
| Escrow | 18 | Escrow contract: create, release, dispute, refund, deadline enforcement |
| SchemaRegistry | 17 | ZK schema registry: registration, lookup, schema management |
| BSTDIDRegistry | 16 | DID registry: registration, lookup, document management, access control |
| BasaltNameService | 16 | BNS: name registration, resolution, ownership transfer |
| PolicyEnforcer | 15 | Policy registration, multi-policy enforcement, NFT enforcement, max policy limit |
| Context | 15 | Contract context: caller, block info, events, require/revert, reentrancy guard |
| BST-1155Token | 14 | BST-1155 token instance: mint, transfer, batch operations, approval management |
| WBSLT | 12 | Wrapped BSLT: deposit, withdraw, transfer, ERC-20 compatibility |
| SanctionsPolicy | 11 | Sanctions screening: add/remove sanctioned addresses, two-step admin transfer |
| BST-20Token | 10 | BST-20 token instance: deployment, transfer, approval flows |
| BST-20 Policy | 9 | BST-20 transfer policy integration: holding limit, lockup, jurisdiction, sanctions |
| JurisdictionPolicy | 9 | Jurisdiction whitelist/blacklist modes, country registration, unregistered address handling |
| LockupPolicy | 9 | Time-based lockup: period management, transfer blocking during lockup, admin transfer |
| BST-721Token | 9 | BST-721 token instance: minting, transfer, ownership verification |
| CrossContractCall | 8 | Cross-contract calls: invocation, return value forwarding, reentrancy checks |
| HoldingLimitPolicy | 8 | Max balance enforcement: limit configuration, overflow prevention, admin transfer |
| TestHost | 8 | BasaltTestHost: deploy, call, view, expect revert, snapshots, event capture |
| BST-721 Policy | 5 | BST-721 NFT transfer policy integration: per-token policy enforcement |
| EndToEndPolicy | 5 | Multi-policy enforcement, static call protection, cross-contract re-entry |
| BST-1155 Policy | 4 | BST-1155 multi-token policy integration: batch transfer enforcement |

**Total: 652 tests**

## Test Files

- `BridgeETHTests.cs` -- EVM bridge: lock/unlock, multisig, admin, pause, deposit lifecycle
- `GovernanceTests.cs` -- On-chain governance: proposals, voting, delegation, timelock
- `BST3525TokenTests.cs` -- BST-3525 semi-fungible token: slots, value transfers, approvals
- `BSTVCRegistryTests.cs` -- Verifiable credentials: issuance, verification, revocation
- `IssuerRegistryTests.cs` -- ZK issuer registry: registration, credential verification
- `BST4626VaultTests.cs` -- BST-4626 tokenized vault: deposit, withdraw, share pricing
- `BST1155Tests.cs` -- BST-1155 multi-token: mint, burn, batch transfer, balances, approvals
- `BSTDIDTests.cs` -- Decentralized identity: DID creation, resolution, updates, deactivation
- `StakingPoolTests.cs` -- Staking pool: delegation, rewards, withdrawal
- `BST20Tests.cs` -- BST-20 fungible token: initialization, transfer, approval, allowance
- `BST721Tests.cs` -- BST-721 non-fungible token: minting, transfer, ownership, token URI
- `StorageTests.cs` -- Storage primitives: StorageValue, StorageMap, StorageList
- `EscrowTests.cs` -- Escrow: create, release, dispute, refund
- `SchemaRegistryTests.cs` -- ZK schema registry: registration, lookup
- `BSTDIDRegistryTests.cs` -- DID registry: registration, lookup, document management
- `BasaltNameServiceTests.cs` -- BNS: name registration, resolution, ownership
- `ContextTests.cs` -- Contract execution context: caller, block info, events, revert, reentrancy
- `BST1155TokenTests.cs` -- BST-1155 token instance operations
- `WBSLTTests.cs` -- Wrapped BSLT: deposit, withdraw, transfer
- `BST20TokenTests.cs` -- BST-20 token instance operations
- `BST721TokenTests.cs` -- BST-721 token instance operations
- `CrossContractCallTests.cs` -- Cross-contract call: invocation, return values, reentrancy guard
- `TestHostTests.cs` -- Test host: contract deployment, invocation, snapshots, event capture
- `PolicyTests/PolicyEnforcerTests.cs` -- Policy registration, multi-policy enforcement, max limit
- `PolicyTests/SanctionsPolicyTests.cs` -- Sanctions screening, admin transfer pattern
- `PolicyTests/BST20PolicyIntegrationTests.cs` -- BST-20 transfer policy integration
- `PolicyTests/JurisdictionPolicyTests.cs` -- Jurisdiction whitelist/blacklist modes
- `PolicyTests/LockupPolicyTests.cs` -- Time-based lockup enforcement
- `PolicyTests/HoldingLimitPolicyTests.cs` -- Max balance enforcement
- `PolicyTests/BST721PolicyIntegrationTests.cs` -- BST-721 NFT policy integration
- `PolicyTests/EndToEndPolicyTests.cs` -- Multi-policy enforcement, static call, re-entry
- `PolicyTests/BST1155PolicyIntegrationTests.cs` -- BST-1155 batch transfer policy

## Running

```bash
dotnet test tests/Basalt.Sdk.Tests
```
