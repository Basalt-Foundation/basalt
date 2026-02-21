# Basalt Security & Quality Audit — SDK Layer

## Scope

Audit the smart contract SDK (contract framework, token standards, system contracts) and the wallet/provider SDK:

| Project | Path | Description |
|---|---|---|
| `Basalt.Sdk.Contracts` | `src/sdk/Basalt.Sdk.Contracts/` | Contract base infrastructure, token standards (BST-20/721/1155/3525/4626/DID/VC), 8 system contracts |
| `Basalt.Sdk.Wallet` | `src/sdk/Basalt.Sdk.Wallet/` | Wallet, accounts, HD wallet (BIP-39/44), RPC client, transaction builders, subscriptions |
| `Basalt.Sdk.Testing` | `src/sdk/Basalt.Sdk.Testing/` | In-memory contract testing harness (`BasaltTestHost`) |

Corresponding test projects: `tests/Basalt.Sdk.Tests/` (26 test files), `tests/Basalt.Sdk.Wallet.Tests/`

---

## Files to Audit

### Basalt.Sdk.Contracts — Infrastructure
- `Context.cs` (~186 lines) — Static `Context` class: `TxValue`, `Sender`, `Origin`, `BlockNumber`, `Timestamp`, `ContractAddress`, `TransferNative`, `EmitEvent`, `CrossContractCall`
- `ContractAttributes.cs` (~49 lines) — Attribute definitions for source generator
- `IDispatchable.cs` (~17 lines) — Dispatch interface
- `SelectorHelper.cs` (~37 lines) — FNV-1a selector computation
- `Storage.cs` (~177 lines) — `IStorageProvider`, `InMemoryStorageProvider`, `ContractStorage`, `StorageValue<T>`, `StorageMap<TKey, TValue>`, `StorageList<T>`

### Basalt.Sdk.Contracts — Token Standards
- `BST20Token.cs` (~152 lines) — Fungible token (ERC-20 analogue)
- `BST721Token.cs` (~145 lines) — NFT (ERC-721 analogue)
- `BST1155Token.cs` (~175 lines) — Multi-token (ERC-1155 analogue)
- `BST3525Token.cs` (~289 lines) — Semi-fungible token (ERC-3525 analogue)
- `BST4626Vault.cs` (~166 lines) — Vault (ERC-4626 analogue)
- `BSTDIDRegistry.cs` (~140 lines) — W3C DID registry (ERC-DID analogue)
- `BSTVCRegistry.cs` (~210 lines) — W3C Verifiable Credentials + eIDAS 2.0

### Basalt.Sdk.Contracts — System Contracts
- `WBSLT.cs` (~36 lines) — Wrapped BST (native token wrapper, 0x...1001)
- `BasaltNameService.cs` (~136 lines) — Name registry (0x...1002)
- `Governance.cs` (~450 lines) — Stake-weighted quadratic voting, delegation, timelock, executable proposals (0x...1003)
- `Escrow.cs` (~143 lines) — Multi-party escrow (0x...1004)
- `StakingPool.cs` (~210 lines) — Delegated staking pools (0x...1005)
- `SchemaRegistry.cs` (~127 lines) — ZK compliance credential schemas (0x...1006)
- `IssuerRegistry.cs` (~338 lines) — ZK compliance issuer management (0x...1007)
- `BridgeETH.cs` (~468 lines) — EVM bridge: lock/unlock, M-of-N multisig, deposit lifecycle (0x...1008)

### Basalt.Sdk.Wallet
- `Accounts/Account.cs` (~150 lines) — Account with signing capability
- `Accounts/ValidatorAccount.cs` (~50 lines) — Validator-specific account
- `Accounts/AccountManager.cs` (~125 lines) — Multi-account management
- `Accounts/KeystoreManager.cs` (~100 lines) — Encrypted keystore persistence
- `HdWallet/HdWallet.cs` (~146 lines) — HD wallet (BIP-44 derivation)
- `HdWallet/Mnemonic.cs` (~178 lines) — BIP-39 mnemonic generation/validation
- `HdWallet/HdKeyDerivation.cs` (~73 lines) — HMAC-SHA512 key derivation
- `HdWallet/Bip39Wordlist.cs` (~2079 lines) — BIP-39 English wordlist
- `BasaltProvider.cs` (~352 lines) — High-level provider for chain interaction
- `Rpc/BasaltClient.cs` (~270 lines) — HTTP RPC client
- `Contracts/AbiEncoder.cs` (~205 lines) — ABI encoding for contract calls
- `Contracts/SdkContractEncoder.cs` (~210 lines) — SDK contract call encoding
- `Contracts/ContractClient.cs` (~242 lines) — Typed contract interaction
- `Transactions/TransactionBuilder.cs` (~226 lines) — Base transaction builder
- `Transactions/TransferBuilder.cs` (~105 lines) — Transfer-specific builder
- `Transactions/ContractCallBuilder.cs` (~190 lines) — Contract call builder
- `Transactions/ContractDeployBuilder.cs` (~120 lines) — Contract deploy builder
- `Transactions/StakingBuilder.cs` (~135 lines) — Staking operation builder
- `Subscriptions/BlockSubscription.cs` (~170 lines) — WebSocket block subscription

### Basalt.Sdk.Testing
- `BasaltTestHost.cs` (~218 lines) — In-memory contract testing harness

---

## Audit Objectives

### 1. Token Standard Security (CRITICAL)
For each token standard (BST-20, BST-721, BST-1155, BST-3525, BST-4626, DID, VC):

- **BST-20 (Fungible)**:
  - Verify `Transfer`, `Approve`, `TransferFrom` follow ERC-20 semantics correctly.
  - Check for the classic ERC-20 approve race condition (front-running `approve` changes).
  - Verify balance overflow/underflow protection with `UInt256`.
  - Check that `TotalSupply` is maintained correctly on mint/burn.

- **BST-721 (NFT)**:
  - Verify ownership tracking is correct.
  - Check that transfers update ownership atomically.
  - Verify approval mechanics (single token + operator approval).
  - Check that non-existent token IDs are handled correctly.

- **BST-1155 (Multi-token)**:
  - Verify batch operations are atomic.
  - Note: batch arrays (`ulong[] amounts`) remain `ulong[]` because source gen doesn't support `UInt256[]`.
  - Check that batch operations handle empty arrays and mismatched array lengths.

- **BST-3525 (Semi-fungible)**:
  - Verify slot-based value transfer is correct.
  - Check that value splits and merges maintain total value invariants.

- **BST-4626 (Vault)**:
  - Verify share/asset conversion formulas are correct (no rounding exploitation).
  - Check for inflation/donation attacks (ERC-4626 vault inflation vulnerability).
  - Verify deposit/withdraw/redeem mechanics.

- **DID Registry**:
  - Verify DID document lifecycle (create, update, deactivate, resolve).
  - Check authorization for DID operations (only controller can modify).

- **VC Registry**:
  - Verify credential issuance, verification, and revocation.
  - Check eIDAS 2.0 compliance claims.

### 2. System Contract Security (CRITICAL)

- **Governance (0x...1003)**:
  - Verify quadratic voting weight calculation: `IntegerSqrt` for `UInt256` using Newton's method.
  - Check for vote manipulation: can a user split stake across accounts to increase quadratic voting power?
  - Verify delegation is single-hop and cannot form cycles.
  - Check timelock enforcement: proposals cannot be executed before timelock expires.
  - Verify executable proposals cannot call arbitrary contracts with malicious payloads.

- **BridgeETH (0x...1008)**:
  - Verify M-of-N Ed25519 multisig: signature count ≥ threshold, all signers authorized.
  - Check deposit lifecycle: pending → confirmed → finalized. Verify no state can be skipped.
  - Verify pause/unpause admin pattern — who can pause? Is the admin key secure?
  - Check replay protection: `ComputeWithdrawalHash` with 32-byte LE amount serialization.
  - Verify lock/unlock balance accounting.

- **StakingPool (0x...1005)**:
  - Verify delegator share tracking is correct.
  - Check reward distribution proportionality.
  - Verify withdrawal mechanics and unbonding integration.

- **Escrow (0x...1004)**:
  - Verify escrow lifecycle: create, fund, release, refund, dispute.
  - Check that only authorized parties can release/refund.
  - Verify timeout handling for abandoned escrows.

### 3. Context Static Class Security (CRITICAL)
- Verify `Context` thread-local or AsyncLocal storage is correctly isolated between concurrent contract executions.
- Check that `Context.TransferNative` correctly moves funds and updates balances.
- Verify `Context.CrossContractCall` handles reentrant calls safely.
- Check that `Context.EmitEvent` correctly serializes and records events.
- Verify that `Context.Sender` / `Context.Origin` cannot be spoofed by contracts.

### 4. Storage Abstraction Security
- Verify `ContractStorage` isolation: one contract cannot access another's storage.
- Check `StorageValue<T>`, `StorageMap<TKey, TValue>`, `StorageList<T>` for:
  - Key collision between different storage abstractions
  - Correct serialization/deserialization of all supported types
  - Behavior with missing/deleted values
- Verify `InMemoryStorageProvider` (tests) and `HostStorageProvider` (on-chain) produce identical behavior.
- Check `TagUInt256 = 0x0A` serialization: 32-byte LE encoding correctness.

### 5. Wallet & Key Management Security (CRITICAL)
- Verify `Mnemonic` generation uses cryptographic randomness (CSPRNG).
- Check BIP-39 mnemonic validation: checksum verification, wordlist matching.
- Verify `HdKeyDerivation` (BIP-44 path) produces correct keys for Basalt's Ed25519 curve.
- Check that private keys are zeroed after use (`IDisposable` on `Account`, `HdWallet`, `AccountManager`).
- Verify `KeystoreManager` encryption: Argon2 parameters, AES encryption, key derivation.
- Check that the RPC client does not transmit private keys or seeds.

### 6. Transaction Builder Safety
- Verify `TransactionBuilder` correctly sets all required fields.
- Check that EIP-1559 fields (`MaxFeePerGas`, `MaxPriorityFeePerGas`) are correctly set.
- Verify `ContractCallBuilder` and `ContractDeployBuilder` correctly encode call data.
- Check that `StakingBuilder` produces valid staking transactions.
- Verify `AbiEncoder` and `SdkContractEncoder` produce correct byte-level encoding.

### 7. RPC Client Security
- Verify `BasaltClient` handles HTTP errors, timeouts, and malformed responses.
- Check that `NonceManager` correctly tracks and increments nonces.
- Verify `BasaltProvider` does not cache sensitive data.
- Check that WebSocket `BlockSubscription` handles disconnections and reconnections.

### 8. Test Coverage
- Review `tests/Basalt.Sdk.Tests/` (26 files) for coverage of all contracts:
  - Each token standard: basic operations, edge cases, authorization
  - System contracts: governance voting, bridge deposit flow, staking pool rewards
  - Context: isolation, native transfers, cross-contract calls
  - Storage: all storage types, key patterns, missing values
- Review `tests/Basalt.Sdk.Wallet.Tests/` for:
  - HD wallet derivation test vectors
  - Mnemonic generation and validation
  - Transaction builder output verification
  - RPC client error handling

---

## Key Context

- All SDK contract amounts use `UInt256` (not `ulong`). IDs remain `ulong`.
- `Context.TxValue` is `UInt256`; `Context.TransferNative` takes `UInt256 amount`.
- BST-1155 batch arrays (`ulong[] amounts`) remain `ulong[]` — source gen doesn't support `UInt256[]`.
- `Governance.IntegerSqrt` uses Newton's method for `UInt256`.
- `BridgeETH.ComputeWithdrawalHash`: amount serialized as 32-byte LE.
- Source generator emits `IDispatchable.Dispatch()` on partial classes; `new` modifier for derived types.
- Contract type IDs: 0x0001-0x0007 (standards), 0x0100-0x0107 (system).
- System contract addresses: 0x...1001 through 0x...1008.
- `StorageMap` TValue constraint relaxed from `struct` to unconstrained for string support.

---

## Output Format

Write your findings to `audit/output/12-sdk.md` with the following structure:

```markdown
# SDK Layer Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Token vulnerabilities, fund loss, key exposure, governance manipulation]

## High Severity
[Significant security or correctness issues]

## Medium Severity
[Issues to address]

## Low Severity / Recommendations
[Code quality, best practices]

## Test Coverage Gaps
[Untested scenarios]

## Positive Findings
[Well-implemented patterns]
```

For each finding, include:
1. **Location**: File path and line number(s)
2. **Description**: What the issue is
3. **Impact**: What could go wrong
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
