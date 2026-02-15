# Basalt.Sdk.Tests

Unit tests for the Basalt smart contract SDK: storage primitives, contract context, BST-20/BST-721/BST-1155/BST-DID reference implementations, cross-contract calls, and the test host. **197 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| BST-1155 | 27 | Multi-token standard: mint, burn, batch transfer, balance queries, approval, URI management, supply tracking |
| BST-DID | 27 | Decentralized identity: DID creation, resolution, document updates, deactivation, controller management, service endpoints |
| BST-20 | 24 | Fungible token standard: initialize, transfer, approve, transferFrom, balanceOf, totalSupply, allowance, edge cases |
| BST-721 | 21 | Non-fungible token standard: mint, transfer, approve, balanceOf, ownerOf, token URI, unauthorized access, burn |
| StoragePrimitives | 21 | StorageValue, StorageMap, StorageList: get/set/delete/contains, type safety, boundary conditions |
| BSTDIDRegistry | 16 | DID registry: registration, lookup, document management, access control, batch operations |
| Context | 15 | Contract context: caller, block timestamp, block height, chain ID, emit events, require/revert, cross-contract calls, reentrancy guard |
| BST-1155Token | 14 | BST-1155 token instance: mint, transfer, batch operations, approval management |
| BST-20Token | 10 | BST-20 token instance: deployment, transfer, approval flows |
| BST-721Token | 9 | BST-721 token instance: minting, transfer, ownership verification |
| TestHost | 8 | BasaltTestHost: deploy, call, view, expect revert, snapshots, block advancement, event capture |
| CrossContractCall | 5 | Cross-contract call mechanism: invocation, return value forwarding, reentrancy checks |

**Total: 197 tests**

## Test Files

- `BST1155Tests.cs` -- BST-1155 multi-token standard: mint, burn, batch transfer, balances, approvals
- `BSTDIDTests.cs` -- Decentralized identity: DID creation, resolution, updates, deactivation
- `BST20Tests.cs` -- BST-20 fungible token: initialization, transfer, approval, allowance
- `BST721Tests.cs` -- BST-721 non-fungible token: minting, transfer, ownership, token URI
- `StorageTests.cs` -- Storage primitives: StorageValue, StorageMap, StorageList
- `BSTDIDRegistryTests.cs` -- DID registry: registration, lookup, document management
- `ContextTests.cs` -- Contract execution context: caller, block info, events, revert, reentrancy
- `BST1155TokenTests.cs` -- BST-1155 token instance operations
- `BST20TokenTests.cs` -- BST-20 token instance operations
- `BST721TokenTests.cs` -- BST-721 token instance operations
- `TestHostTests.cs` -- Test host: contract deployment, invocation, snapshots, event capture
- `CrossContractCallTests.cs` -- Cross-contract call: invocation, return values, reentrancy guard

## Running

```bash
dotnet test tests/Basalt.Sdk.Tests
```
