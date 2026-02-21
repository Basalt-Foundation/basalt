# SDK Layer Audit Report

## Executive Summary

The SDK layer (contract framework, token standards, system contracts, wallet/provider) reveals **10 critical issues**, **16 high-severity issues**, and numerous medium/low findings. The most severe problems are: (1) all `Context` and `ContractStorage` state is global static with no thread isolation, enabling cross-contract state corruption under concurrency; (2) `UInt256` arithmetic throughout token standards uses unchecked addition, allowing silent balance/supply overflow; (3) Governance accepts caller-supplied `totalStake`, enabling trivial quorum manipulation and governance takeover; (4) BridgeETH `Unlock` has no balance check, allowing relayers to drain more than is locked; and (5) EIP-1559 fields are silently dropped in the wallet provider, breaking dynamic fee transactions end-to-end. Test coverage is extensive (~560 tests) with good breadth, but key gaps exist around UInt256 edge values, inflation attack simulation, HD wallet derivation vectors, and RPC error paths.

---

## Critical Issues

### C-1: All Context State is Static Mutable — No Thread Isolation

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Context.cs:9-178`
- **Description**: The entire `Context` class uses plain `static` properties (`Caller`, `Self`, `TxValue`, `BlockTimestamp`, `BlockHeight`, `GasRemaining`, `CallDepth`, `ReentrancyGuard`, `EventEmitted`, `NativeTransferHandler`, `CrossContractCallHandler`). There is no `[ThreadStatic]`, `ThreadLocal<T>`, or `AsyncLocal<T>`. Every field is a single shared mutable slot across the entire process. `ContractBridge.Setup()` saves/restores via a disposable scope, but this is inherently fragile under concurrency.
- **Impact**: If two contract executions ever run concurrently (even via async gRPC view calls during block execution), they will corrupt each other's state. Thread A could execute with Thread B's `Caller`, `Self`, or `NativeTransferHandler`. Exploitable in any future parallel execution model.
- **Recommendation**: Replace all `static` properties with `AsyncLocal<T>` fields wrapped in a scoped context object, or create a `ContractExecutionContext` instance class threaded through the call chain.
- **Severity**: Critical

### C-2: ContractStorage is Static — Same Thread Safety Issue

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Storage.cs:59-123`
- **Description**: `ContractStorage` is a static class with a single static `_provider` field. All `StorageValue<T>`, `StorageMap<TKey, TValue>`, and `StorageList<T>` delegate through this single global provider. `ContractBridge.Setup()` swaps the provider per execution and restores on dispose.
- **Impact**: Concurrent contract execution will see the wrong storage provider. Contract A's storage reads could go to Contract B's state database, enabling cross-contract state corruption and potential theft of funds.
- **Recommendation**: Same as C-1 — make the storage provider scoped per execution context.
- **Severity**: Critical

### C-3: BST-20 `Mint` Silently Wraps on UInt256 Overflow

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST20Token.cs:94-98`
- **Description**: `Mint` performs two unchecked additions: `_totalSupply.Set(supply + amount)` and `_balances.Set(ToKey(to), balance + amount)`. The `UInt256` `operator +` silently wraps on overflow. If `supply + amount` wraps past `UInt256.MaxValue`, `TotalSupply` becomes a small number while the recipient's balance increases, breaking the `SUM(balances) == TotalSupply` invariant.
- **Impact**: An attacker (or buggy derived contract) that can call `Mint` repeatedly could overflow the total supply, creating tokens from thin air.
- **Recommendation**: Use `UInt256.CheckedAdd` for both the total supply and balance addition in `Mint`.
- **Severity**: Critical

### C-4: BST-20 `TransferInternal` Silently Wraps on Receiver Balance Overflow

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST20Token.cs:136`
- **Description**: While the sender's balance is checked (`fromBalance >= amount`), the receiver's `toBalance + amount` uses unchecked `operator +`. If the receiver's balance approaches `UInt256.MaxValue`, the addition wraps silently.
- **Impact**: Loss of funds via silent balance wrap. Practically difficult with 256-bit space but represents a fundamental correctness violation in a financial contract.
- **Recommendation**: Use `UInt256.CheckedAdd(toBalance, amount)` for the receiver balance update.
- **Severity**: Critical

### C-5: BST-4626 Vault `Withdraw` Burns Shares Before Verifying Asset Transfer

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST4626Vault.cs:109-113`
- **Description**: `Withdraw` executes: (1) `Burn(Context.Caller, shares)`, (2) `_totalAssets.Set(TotalAssets() - assets)`, (3) `Context.CallContract(_assetAddress, "Transfer", ...)`. If the external call reverts and the runtime does NOT atomically roll back state changes, shares are burned without assets being transferred. `Redeem` (lines 132-136) has the same issue.
- **Impact**: Users can lose shares without receiving underlying assets, depending on runtime rollback behavior.
- **Recommendation**: Confirm and document that the runtime atomically reverts all state changes on exception, or reorder to perform the external transfer first and validate success before burning.
- **Severity**: Critical

### C-6: BST-4626 `Harvest` Does Not Verify Actual Asset Transfer — Phantom Yield Injection

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST4626Vault.cs:148-165`
- **Description**: `Harvest` simply increments `_totalAssets` by `yieldAmount` without any corresponding transfer of underlying assets into the vault. A compromised admin can call `Harvest` with an arbitrarily large `yieldAmount`, inflating perceived share value. Early redeemers get real assets; late redeemers find the vault insolvent.
- **Impact**: Complete vault insolvency through phantom yield. Admin can extract value by minting shares cheaply before `Harvest`, then redeeming at inflated rate.
- **Recommendation**: Require a corresponding `TransferFrom` of actual underlying assets during `Harvest`, or use a pull-based model querying actual vault balance.
- **Severity**: Critical

### C-7: Governance `totalStake` is Caller-Supplied, Allowing Quorum Manipulation

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/Governance.cs:91-97, 105-112`
- **Description**: `CreateProposal` and `CreateExecutableProposal` accept `totalStake` as a caller-provided parameter, stored as the quorum snapshot. This value is used in `QueueProposal` to compute the quorum threshold: `IntegerSqrt(totalStake * quorumBps / 10000)`. A malicious proposer can pass `UInt256.Zero` as `totalStake`, reducing the quorum to zero, making it trivial for a single voter with minimal stake to pass any proposal.
- **Impact**: Complete governance takeover. An attacker with minimal stake can create and pass executable proposals to drain funds, change admin addresses on other system contracts, or manipulate protocol parameters.
- **Recommendation**: Read `totalStake` from on-chain state via cross-contract call to the StakingPool rather than accepting it as a parameter.
- **Severity**: Critical

### C-8: Governance Has No Proposal Threshold Enforcement

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/Governance.cs:342-375`
- **Description**: The `_proposalThreshold` storage value is set in the constructor but never checked in `CreateProposalInternal`. Anyone can create proposals regardless of their stake.
- **Impact**: Denial-of-service on governance via proposal spam. Combined with C-7, trivially exploitable for governance takeover.
- **Recommendation**: In `CreateProposalInternal`, query the proposer's stake and require it to be at least `_proposalThreshold.Get()`.
- **Severity**: Critical

### C-9: BridgeETH `Unlock` Can Drain More Than Is Locked (No Balance Check)

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BridgeETH.cs:248-300`
- **Description**: `Unlock` transfers `amount` to `recipient` and decrements `_totalLocked` by `amount`, but there is no check that `amount <= _totalLocked.Get()`. If relayers sign a withdrawal exceeding the locked balance, the transfer may succeed (if the contract's native balance is sufficient from other sources), and `_totalLocked` underflows. The `depositNonce` used for replay protection has no requirement to correspond to an actual deposit.
- **Impact**: Bridge insolvency. Relayers can authorize withdrawals for amounts exceeding actual locked deposits.
- **Recommendation**: Add `Context.Require(amount <= _totalLocked.Get(), "BRIDGE: amount exceeds locked balance")` before the transfer. Validate that the nonce corresponds to a real finalized deposit.
- **Severity**: Critical

### C-10: EIP-1559 Fields Silently Dropped in Provider and RPC Serialization

- **Location**: `src/sdk/Basalt.Sdk.Wallet/BasaltProvider.cs:198-210`, `src/sdk/Basalt.Sdk.Wallet/Rpc/BasaltClient.cs:130-146`
- **Description**: `BasaltProvider.SendTransactionAsync` reconstructs the transaction from the builder output but does not copy `MaxFeePerGas`, `MaxPriorityFeePerGas`, or `ComplianceProofs`. The `TransactionRequest` JSON DTO has no fields for these either. Any EIP-1559 transaction will have its fee fields silently reset to zero when submitted through the provider.
- **Impact**: EIP-1559 transactions are broken end-to-end through the wallet SDK. ZK compliance proofs are also silently dropped. Users cannot use dynamic fee pricing or privacy compliance from the wallet layer.
- **Recommendation**: Add `MaxFeePerGas`, `MaxPriorityFeePerGas`, and `ComplianceProofs` to the `Transaction` reconstruction and to `TransactionRequest`.
- **Severity**: Critical

---

## High Severity

### H-1: CrossContractCallHandler Not Wired in Production

- **Location**: `src/execution/Basalt.Execution/VM/ContractBridge.cs` (full file), `src/sdk/Basalt.Sdk.Contracts/Context.cs:114, 125`
- **Description**: `ContractBridge.Setup()` wires `Caller`, `Self`, `NativeTransferHandler`, `EventEmitted`, and `ContractStorage`, but never sets `Context.CrossContractCallHandler`. This handler is only set in test code. In production, any contract calling `Context.CallContract<T>(...)` will hit `Require(CrossContractCallHandler != null, "Cross-contract calls not available")` and revert.
- **Impact**: Cross-contract calls are entirely non-functional in production. Governance calling StakingPool, Vault calling the underlying token, etc. will all revert at runtime.
- **Recommendation**: Implement a production `CrossContractCallHandler` in `ContractBridge.Setup()` that loads the target contract, creates a nested `VmExecutionContext`, and recursively invokes `ManagedContractRuntime.Execute()`.
- **Severity**: High

### H-2: Reentrancy Guard Does Not Protect Current Contract's Own Address

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Context.cs:119-150`
- **Description**: `CallContract<T>` adds the **target** address to `ReentrancyGuard` and checks if the **target** is already guarded. However, the **current contract's** address (`Self`) is never added at the top-level entry point. An indirect re-entry path A → B → C → A is not blocked because A was never added to the guard.
- **Impact**: Reentrancy attacks through indirect call chains. A malicious contract C could call back into Contract A while A's state modifications are incomplete.
- **Recommendation**: Add `Context.Self` to `ReentrancyGuard` at the beginning of top-level dispatch. Consider a full call-stack model where any address already on the stack cannot be re-entered.
- **Severity**: High

### H-3: Context.CallContract Saves/Restores Caller/Self but Not TxValue, Handlers, or Storage

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Context.cs:127-149`
- **Description**: `CallContract<T>` only saves and restores `Caller`, `Self`, and `CallDepth`. It does NOT save/restore `TxValue`, `BlockTimestamp`, `BlockHeight`, `ChainId`, `GasRemaining`, `EventEmitted`, `NativeTransferHandler`, or `ContractStorage.Provider`. If the `CrossContractCallHandler` modifies these, they will not be restored after the call returns.
- **Impact**: Context corruption after cross-contract calls — the calling contract continues with the callee's `TxValue`, storage provider, event handler, etc.
- **Recommendation**: Move all save/restore logic into `CallContract<T>` or enforce that the handler always uses `ContractBridge.Setup()` internally.
- **Severity**: High

### H-4: BST-20 Classic Approve Front-Running Race Condition

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST20Token.cs:60-74`
- **Description**: `Approve` sets the allowance to an absolute value. Alice approves Bob for 100, then wants to change to 50. Bob front-runs by spending the original 100, then gets 50 more from the new approval (150 total instead of 50).
- **Impact**: Spenders can extract more tokens than the owner intended during allowance changes.
- **Recommendation**: Add `IncreaseAllowance`/`DecreaseAllowance` methods, or implement the "set to zero first" pattern.
- **Severity**: High

### H-5: BST-721 `Mint` is Publicly Callable with No Access Control

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST721Token.cs:98-118`
- **Description**: `Mint` is marked `[BasaltEntrypoint]` with no owner check, no minter role, and no access control. Anyone can mint unlimited NFTs.
- **Impact**: Any user can mint arbitrary NFTs, destroying scarcity and collection value.
- **Recommendation**: Add an owner/minter role check.
- **Severity**: High

### H-6: BST-1155 `Mint` and `Create` are Publicly Callable with No Access Control

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST1155Token.cs:127-161`
- **Description**: Both `Mint` and `Create` are `[BasaltEntrypoint]` with no caller authorization.
- **Impact**: Total loss of token supply integrity — unlimited minting by any party.
- **Recommendation**: Add minter role / access control.
- **Severity**: High

### H-7: BST-3525 `Mint` is Publicly Callable with No Access Control

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST3525Token.cs:98-103`
- **Description**: `Mint` is `[BasaltEntrypoint]` with no caller restriction.
- **Impact**: Unlimited minting destroys token value and accounting integrity.
- **Recommendation**: Add access control.
- **Severity**: High

### H-8: BST-721 Missing `SetApprovalForAll` (Operator Approval)

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST721Token.cs` (entire file)
- **Description**: ERC-721 requires `setApprovalForAll(operator, approved)` for marketplace integrations. BST-721 only supports per-token `Approve`, with no operator mechanism.
- **Impact**: Marketplaces and delegated transfer workflows cannot function. Significant deviation from the standard.
- **Recommendation**: Add `SetApprovalForAll`, `IsApprovedForAll`, and check operator approval in `Transfer`.
- **Severity**: High

### H-9: BST-DID `AddAttestation` Authorization Check Is Bypassable

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BSTDIDRegistry.cs:70-71`
- **Description**: The check compares `callerHex == issuer`, but `issuer` is a `string` parameter passed by the caller, not a `byte[]` address. The caller can set `issuer` to their own hex-encoded address to pass authorization. Any address can add attestations to any DID.
- **Impact**: Attestation integrity is completely undermined. Any user can inject arbitrary attestations (KYC credentials, identity claims) into any DID.
- **Recommendation**: Change `issuer` to `byte[]` and verify against a trusted issuer registry, or remove the `issuer` comparison.
- **Severity**: High

### H-10: BridgeETH `SetThreshold` Allows Threshold of 1, Bypassing Constructor Safety

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BridgeETH.cs:130-140`
- **Description**: Constructor enforces `threshold >= 2`, but `SetThreshold` only requires `newThreshold >= 1`. A compromised admin can lower to 1-of-N, then a single compromised relayer can unilaterally authorize withdrawals.
- **Impact**: If the admin key is compromised, multisig security can be reduced to 1-of-N, enabling single-relayer bridge drain.
- **Recommendation**: Change to `Context.Require(newThreshold >= 2, ...)` to match the constructor invariant.
- **Severity**: High (Critical if admin key is a single EOA)

### H-11: Governance Delegation Has No Cycle Prevention

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/Governance.cs:163-186`
- **Description**: `DelegateVote` only checks self-delegation. If A delegates to B and B delegates to C, A's stake is added to B's `_delegatedPower` but B cannot vote (blocked by "GOV: vote delegated"). A's stake is effectively lost for that proposal. Delegated power accounting becomes inconsistent.
- **Impact**: Delegated voting power can be silently lost in multi-hop scenarios, leading to voter disenfranchisement.
- **Recommendation**: Require that the delegatee has not themselves delegated (enforce single-hop properly).
- **Severity**: High

### H-12: StakingPool Reward Distribution Exploitable via Flash-Staking

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/StakingPool.cs:118-146`
- **Description**: Reward calculation uses spot values: `entitled = delegation * totalRewards / totalStake`. An attacker can delegate a large amount right before `ClaimRewards`, claim a disproportionate share, then immediately undelegate. No time-weighted tracking, no lockup, no unbonding delay.
- **Impact**: Existing delegators' rewards are diluted by flash-stakers.
- **Recommendation**: Implement time-weighted reward accounting (Synthetix/MasterChef reward-per-share accumulator pattern) or enforce a minimum delegation period.
- **Severity**: High

### H-13: Governance Executable Proposals Can Call Arbitrary Contracts

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/Governance.cs:260-270`
- **Description**: `ExecuteProposal` calls `Context.CallContract(target, method)` with no restriction on targets or methods. Combined with C-7 (manipulable quorum), governance can call `TransferAdmin` on BridgeETH or any other admin function.
- **Impact**: Governance proposals can compromise the entire system by calling admin functions on any contract.
- **Recommendation**: Implement a whitelist of callable targets or require multi-step approval for sensitive targets.
- **Severity**: High

### H-14: NonceManager Race Condition on Concurrent Transaction Submission

- **Location**: `src/sdk/Basalt.Sdk.Wallet/Rpc/NonceManager.cs:23-33`
- **Description**: `GetNextNonceAsync` uses a non-atomic check-then-act pattern. If two threads call it for the same address simultaneously when no cached nonce exists, both will fetch and use the same nonce value.
- **Impact**: Concurrent transaction submissions produce duplicate nonces, causing transaction failures.
- **Recommendation**: Use `SemaphoreSlim` per address or `ConcurrentDictionary.GetOrAdd` with a `Lazy<Task<ulong>>` pattern.
- **Severity**: High

### H-15: Private Key Material Not Zeroed in Multiple Wallet Paths

- **Location**: `src/sdk/Basalt.Sdk.Wallet/Accounts/Account.cs:80-90`, `src/sdk/Basalt.Sdk.Wallet/HdWallet/HdKeyDerivation.cs:62-72`, `src/sdk/Basalt.Sdk.Wallet/HdWallet/HdWallet.cs:17`
- **Description**: (a) `Account.FromPrivateKey(string)` creates a `keyBytes` array that is never zeroed. (b) `HdKeyDerivation.DerivePath` replaces `key` and `chainCode` at each derivation level without zeroing previous values. (c) `HdWallet.MnemonicPhrase` is stored as an immutable `string` that cannot be zeroed — the ultimate root secret persists in the managed heap indefinitely.
- **Impact**: Private key material lingers in memory, increasing the window for extraction via memory forensics.
- **Recommendation**: (a,b) Add `CryptographicOperations.ZeroMemory` in `finally` blocks. (c) Use `char[]` or `byte[]` for the mnemonic, or avoid storing it.
- **Severity**: High

### H-16: `AccountManager.Remove` Does Not Dispose the Removed Account

- **Location**: `src/sdk/Basalt.Sdk.Wallet/Accounts/AccountManager.cs:53-64`
- **Description**: Removed accounts are taken out of the dictionary but `Dispose()` is never called, so private key material remains in memory indefinitely.
- **Impact**: Removed accounts leak private key material. Long-running applications rotating accounts create a growing footprint of exposed keys.
- **Recommendation**: Dispose the account on removal, or return it so the caller can manage disposal.
- **Severity**: High

---

## Medium Severity

### M-1: StorageMap Key Format Allows Injection via TKey.ToString()

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Storage.cs:146`
- **Description**: `StorageMap<TKey, TValue>` builds keys as `$"{_prefix}:{key}"`. If `TKey` is `string`, a key containing `:` can collide with another map. A `StorageMap<string, T>` with prefix `"a"` and key `"b:c"` produces `"a:b:c"`, same as prefix `"a:b"` with key `"c"`.
- **Impact**: Cross-map storage collisions if prefixes or keys contain the `:` separator.
- **Recommendation**: Use length-prefixed or hash-based key composition instead of string concatenation with a delimiter.
- **Severity**: Medium

### M-2: StorageList Has No Bounds Checking or Deletion Support

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Storage.cs:157-177`
- **Description**: `Get(int index)` returns `default(T)` for out-of-bounds access instead of reverting. `Set(int index, T value)` can write beyond `Count`, creating invisible phantom entries. No `Remove` method exists.
- **Impact**: Contracts may silently read zero/default values for out-of-bounds indices.
- **Recommendation**: Add bounds checking to `Get`/`Set`. Add a swap-and-pop `Remove` method.
- **Severity**: Medium

### M-3: InMemoryStorageProvider and HostStorageProvider Behave Differently for Null Values

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Storage.cs:22, 27-29`
- **Description**: `InMemoryStorageProvider.Set(key, null)` stores a null entry (key exists). `HostStorageProvider.Set(key, null)` writes empty bytes (key effectively absent). `ContainsKey` semantics differ between providers.
- **Impact**: Tests using `InMemoryStorageProvider` may pass while production behaves differently for null/default values.
- **Recommendation**: Ensure `InMemoryStorageProvider` handles null consistently with `HostStorageProvider`.
- **Severity**: Medium

### M-4: BST-4626 Virtual Offset Too Small for Effective Inflation Attack Mitigation

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST4626Vault.cs:19-20`
- **Description**: `VirtualShares = 1` and `VirtualAssets = 1`. The EIP-4626 inflation attack mitigation typically requires `10^decimals` as the offset. With offset of 1, an attacker can donate large amounts to inflate the exchange rate and steal subsequent depositors' assets.
- **Impact**: First-depositor inflation attack remains viable.
- **Recommendation**: Set virtual offsets proportional to the token's decimals (e.g., `10^18` for 18-decimal tokens).
- **Severity**: Medium

### M-5: BNS Does Not Clear Reverse Lookup or Address Mapping on Name Transfer

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BasaltNameService.cs:94-109`
- **Description**: `TransferName` updates `_owners` but does not clear `_reverse` (old owner's reverse lookup) or update `_addresses` (name resolution). After transfer, `Resolve(name)` returns the old owner's address.
- **Impact**: Stale reverse lookups and stale address resolution after name transfers.
- **Recommendation**: Update `_addresses` to the new owner and clear the old owner's reverse entry in `TransferName`.
- **Severity**: Medium

### M-6: BNS Registration Fees Permanently Locked (No Withdrawal)

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BasaltNameService.cs:30-43`
- **Description**: Registration fees are sent to the BNS contract with no admin withdrawal function and no refund mechanism. Excess value above the fee is silently absorbed.
- **Impact**: Registration fees permanently locked, reducing circulating supply.
- **Recommendation**: Add an admin withdrawal function or document as intentional burn behavior.
- **Severity**: Medium

### M-7: Governance Delegated Power is Stale After Stake Changes

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/Governance.cs:173-179`
- **Description**: When Alice delegates to Bob, her current stake is cached. If Alice later withdraws stake, the governance contract's `_delegatedPower` remains stale. No mechanism exists to refresh delegated power.
- **Impact**: Delegated voting power can be higher or lower than actual stake, distorting governance outcomes.
- **Recommendation**: Query stake in real-time during voting or provide a `RefreshDelegation` entrypoint.
- **Severity**: Medium

### M-8: Governance Voting Power Uses Single Pool, Not Total Stake

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/Governance.cs:119, 135`
- **Description**: `Vote` takes a `poolId` parameter and queries only that single pool. Users with stake across multiple pools can only use one pool's stake to vote.
- **Impact**: Fragmented voting power. Users with diversified delegations have reduced governance influence.
- **Recommendation**: Aggregate stake across all pools or clearly document per-pool voting.
- **Severity**: Medium

### M-9: BridgeETH Confirmed Deposits Can Be Stuck Indefinitely

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BridgeETH.cs:176-195`
- **Description**: `CancelDeposit` allows cancellation of pending deposits after expiry, but there is no cancellation mechanism for deposits stuck in the "confirmed" state. If admin confirms but never finalizes, user funds are locked permanently.
- **Impact**: Admin can grief users by confirming but never finalizing deposits.
- **Recommendation**: Allow `CancelDeposit` for "confirmed" deposits after a longer expiry period.
- **Severity**: Medium

### M-10: SchemaRegistry Schema IDs Predictable — Front-Running Risk

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/SchemaRegistry.cs:32-54`
- **Description**: Schema IDs are `BLAKE3(name)`, making them deterministic. An attacker can front-run a `RegisterSchema` transaction to claim the name and control its verification key.
- **Impact**: Schema squatting / front-running.
- **Recommendation**: Include the creator's address in the schema ID derivation.
- **Severity**: Medium

### M-11: BST-1155 `SafeBatchTransferFrom` Non-Atomic on Failure

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST1155Token.cs:76-85`
- **Description**: The batch loop processes transfers sequentially. If the balance check fails on item `i`, items `0..i-1` have already had storage written. Depends on runtime atomicity.
- **Impact**: Partial state mutation if runtime does not roll back storage on revert.
- **Recommendation**: Confirm runtime rollback semantics or implement two-pass validation.
- **Severity**: Medium

### M-12: BST-DID Deactivated DIDs Remain Fully Functional

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BSTDIDRegistry.cs:63-127`
- **Description**: `DeactivateDID` sets a flag, but `AddAttestation`, `RevokeAttestation`, and `TransferDID` do not check the flag. Operations continue on deactivated DIDs.
- **Impact**: Deactivation is cosmetic — DIDs remain fully functional for all mutating operations.
- **Recommendation**: Check the deactivation flag in all mutating methods.
- **Severity**: Medium

### M-13: BST-DID Attestation ID Collision Using Block Height

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BSTDIDRegistry.cs:74`
- **Description**: Attestation IDs are `$"{credentialType}:{Context.BlockHeight}"`. Two attestations of the same type in the same block produce identical IDs; the second overwrites the first.
- **Impact**: Silent data loss when multiple same-type attestations are added in the same block.
- **Recommendation**: Use a monotonically increasing counter instead of block height.
- **Severity**: Medium

### M-14: BST-4626 Does Not Verify Cross-Contract `TransferFrom` Succeeded

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST4626Vault.cs:86-87`
- **Description**: `Deposit` calls `Context.CallContract(_assetAddress, "TransferFrom", ...)` with void return. BST-20's `TransferFrom` returns `bool`, but the return value is never checked. If the token has a non-reverting failure mode, shares are minted without assets.
- **Impact**: Free share minting if the underlying asset silently fails.
- **Recommendation**: Use `Context.CallContract<bool>` and check the return value.
- **Severity**: Medium

### M-15: MaxCallDepth Inconsistency Between SDK (8) and VM (1024)

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Context.cs:98`, `src/execution/Basalt.Execution/VM/ExecutionContext.cs:27`
- **Description**: `Context.MaxCallDepth = 8` while `VmExecutionContext.MaxCallDepth = 1024`. Two different depth limits in two layers of the same system.
- **Impact**: Confusing semantics. VM-level cross-contract calls could bypass the SDK depth limit.
- **Recommendation**: Unify to a single `ChainParameters` constant.
- **Severity**: Medium

### M-16: FNV-1a 32-bit Selector Has No Collision Detection

- **Location**: `src/sdk/Basalt.Sdk.Contracts/SelectorHelper.cs:12-21`
- **Description**: The source generator computes FNV-1a selectors but never checks for collisions. A collision would produce duplicate `case` values in the generated `switch`, causing a compile error, but the error message would be cryptic.
- **Impact**: Denial-of-development with a confusing error message.
- **Recommendation**: Add explicit collision detection in the source generator with a clear diagnostic.
- **Severity**: Medium

### M-17: Keystore File Written Without Restrictive Permissions

- **Location**: `src/sdk/Basalt.Sdk.Wallet/Accounts/KeystoreManager.cs:52`
- **Description**: `File.WriteAllTextAsync` creates the keystore with default OS permissions, potentially group/world-readable on shared systems.
- **Impact**: Other users could read the encrypted keystore and attempt offline password brute-force.
- **Recommendation**: Use `UnixFileMode.UserRead | UnixFileMode.UserWrite` (0600) on Unix platforms.
- **Severity**: Medium

### M-18: `SdkContractEncoder` Stackalloc Buffer Overflow for Large Strings

- **Location**: `src/sdk/Basalt.Sdk.Wallet/Contracts/SdkContractEncoder.cs:28-56`
- **Description**: Several methods use fixed-size `stackalloc` buffers (512 bytes). If constructor argument strings are long, the buffer overflows. `EncodeBytes` uses `stackalloc byte[10 + data.Length]` which could overflow the stack for large data.
- **Impact**: Runtime crashes or stack overflows for large contract constructor arguments.
- **Recommendation**: Use `ArrayPool<byte>` for dynamically-sized buffers or add size guards.
- **Severity**: Medium

### M-19: WebSocket Subscription No Message Size Limit

- **Location**: `src/sdk/Basalt.Sdk.Wallet/Subscriptions/BlockSubscription.cs:98-103`
- **Description**: No size limit on `messageBuffer`. A malicious node could send an extremely large message to exhaust memory.
- **Impact**: Memory exhaustion from oversized messages.
- **Recommendation**: Add a maximum message size check (e.g., 1 MB).
- **Severity**: Medium

### M-20: `BasaltProvider.SubscribeToBlocks` Uses Broken URL Derivation

- **Location**: `src/sdk/Basalt.Sdk.Wallet/BasaltProvider.cs:331`
- **Description**: Attempts to derive WebSocket URL by casting `_client` to `BasaltClient` and calling `ToString()`, but `BasaltClient` does not override `ToString()`. The fallback `localhost:5100` is used regardless of actual node URL.
- **Impact**: Block subscriptions connect to the wrong node or fail silently.
- **Recommendation**: Store the base URL as a field and use it for WebSocket URL derivation.
- **Severity**: Medium

### M-21: `AbiEncoder.DecodeBytes` Does Not Validate Length Against Available Data

- **Location**: `src/sdk/Basalt.Sdk.Wallet/Contracts/AbiEncoder.cs:186-193`
- **Description**: Reads a 4-byte length prefix then slices without checking if the claimed length exceeds remaining data. A length of `0xFFFFFFFF` causes `OutOfMemoryException`.
- **Impact**: Denial of service via crafted RPC responses.
- **Recommendation**: Validate `length <= data.Length - offset` before allocation.
- **Severity**: Medium

### M-22: Event Data Lost — Only Event Name Hashed and Stored

- **Location**: `src/sdk/Basalt.Sdk.Contracts/Context.cs:68`, `src/execution/Basalt.Execution/VM/ContractBridge.cs:46-50`
- **Description**: `Context.Emit<TEvent>` passes the event object, but `ContractBridge.Setup()` only hashes the event name with BLAKE3, discarding the actual event data entirely. The rich event payload is never persisted on-chain.
- **Impact**: Events are the primary way off-chain systems track contract activity. Loss of event data undermines contract observability.
- **Recommendation**: Serialize event properties into the event log data using source-generated serialization.
- **Severity**: Medium

---

## Low Severity / Recommendations

### L-1: BST-20 Missing Zero-Amount Transfer Check
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST20Token.cs:128`
- Zero-amount transfers succeed and emit events, enabling event spam.

### L-2: BST-20 Allows Transfers to Zero Address Without Updating TotalSupply
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST20Token.cs:128-146`
- Effectively burns tokens while `TotalSupply` overcounts.

### L-3: BST-721 / BST-3525 Token ID Inconsistency (0-Based vs 1-Based)
- BST-721 starts at ID 0; BST-3525 starts at ID 1. Inconsistent.

### L-4: BST-1155 Empty Arrays Accepted in Batch Operations
- Empty arrays produce events with no transfers, wasting gas.

### L-5: BST-3525 `SetSlotUri` Has No Access Control
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BST3525Token.cs:223-227`
- Anyone can overwrite slot metadata URIs.

### L-6: IssuerRegistry `RegisterIssuer` for Tier 1/3 Registers Admin as Issuer
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/IssuerRegistry.cs:60-88`
- Admin cannot register third-party addresses as Tier 1/3 issuers.

### L-7: BridgeETH `newAdmin` Validation Only Checks Length > 0
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BridgeETH.cs:74-76`
- Should validate 20-byte address length.

### L-8: Multiple Contracts Missing Event Emissions for State Changes
- StakingPool.AddRewards, IssuerRegistry.AddSchemaSupport/RemoveSchemaSupport lack events.
- Governance emits `ProposalExecutedEvent` for rejected proposals (misleading).

### L-9: StorageValue<T> Constrained to `struct` While StorageMap TValue is Unconstrained
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Storage.cs:128`
- Forces workarounds for string storage.

### L-10: ContractStorage.Clear() Silently Disconnects From On-Chain Storage for Non-InMemory Providers
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Storage.cs:95-101`

### L-11: BNS `OwnerOf` Returns Zero Address for Unregistered Names Instead of Reverting
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BasaltNameService.cs:115-120`

### L-12: BST-VC `SuspendCredential` Overwrites Revocation Reason
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BSTVCRegistry.cs:115`

### L-13: BST-VC `ReinstateCredential` Deletes Revocation Reason, Losing Audit Trail
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Standards/BSTVCRegistry.cs:134`

### L-14: `GetPrivateKey()` Returns Direct Reference to Private Key Array
- **Location**: `src/sdk/Basalt.Sdk.Wallet/Accounts/Account.cs:115-119`
- External callers can modify or retain the key past disposal.

### L-15: `ValidatorAccount.BlsPublicKey` Exposed as Mutable `byte[]`
- **Location**: `src/sdk/Basalt.Sdk.Wallet/Accounts/ValidatorAccount.cs:24`

### L-16: `StakingBuilder.RegisterValidator` Data Field Only Contains BLS Key, Missing P2P Endpoint
- **Location**: `src/sdk/Basalt.Sdk.Wallet/Transactions/StakingBuilder.cs:49-54`

### L-17: `ContractDeployBuilder` Default Gas Limit (21,000) Too Low for Deployments
- **Location**: `src/sdk/Basalt.Sdk.Wallet/Transactions/ContractDeployBuilder.cs:33-38`

### L-18: SelectorHelper Truncates Non-ASCII Characters
- **Location**: `src/sdk/Basalt.Sdk.Contracts/SelectorHelper.cs:16`
- Casts `char` to `byte`, only using low byte for characters above U+00FF.

### L-19: ReentrancyGuard is a Shared Static HashSet, Never Cleared Between Top-Level Calls
- **Location**: `src/sdk/Basalt.Sdk.Contracts/Context.cs:108`

---

## Test Coverage Gaps

### Harness Issues

| ID | Issue | Severity |
|----|-------|----------|
| TH-1 | `Context.TxValue` not reset between calls in `PrepareContext()` — stale values persist | High |
| TH-2 | `Context.Self` never set by test host — contracts using `Context.Self` require fragile manual setup | Medium |
| TH-3 | `Context.NativeTransferHandler` not wired by test host — native transfer tests require manual wiring | Medium |
| TH-4 | Cross-contract calls use reflection (diverges from production FNV-1a dispatch) | Low |
| TH-5 | No per-contract storage isolation — contracts sharing prefixes collide in tests but not on-chain | Medium |
| TH-6 | `ExpectRevert` returns `null` on success instead of failing — easy to write tests that always pass | Medium |
| TH-7 | Duplicate test classes (e.g., `BST20TokenTests` + `BST20Tests`) increase maintenance burden | Low |
| TH-8 | Direct `Context.TxValue` mutation pattern bypasses `PrepareContext()` | Low |

### Key Missing Test Scenarios

| Area | Missing Coverage |
|------|-----------------|
| **All Token Standards** | UInt256 values near `UInt256.MaxValue`, overflow/underflow edge cases |
| **BST-20** | Transfer to zero address, self-transfer, mint/burn access control |
| **BST-721** | Burn, operator-for-all approval, transfer to zero address |
| **BST-1155** | Empty batch arrays, burn |
| **BST-4626** | Full inflation attack simulation (not just `ConvertToShares(1) == 1`), empty vault redeem, multiple harvests |
| **HD Wallet** | No BIP-39/BIP-44 published test vectors — derivation correctness unverified |
| **HD Wallet** | No passphrase-protected wallet tests |
| **Transaction Builders** | EIP-1559 fields (`MaxFeePerGas`, `MaxPriorityFeePerGas`) |
| **RPC Client** | Network errors, timeouts, malformed JSON responses, rate limiting |
| **ContractClient** | No tests for contract method calls via `CallAsync`/`SendTransactionAsync` |
| **AbiEncoder** | Empty string/byte encoding, UInt256 overflow, decode buffer overrun |
| **NonceManager** | Thread safety / concurrent access |
| **WebSocket Subscription** | Connection lifecycle, disconnection, reconnection, error handling (only model tests exist) |
| **HostStorageProvider** | No direct tests — only tested indirectly via contract tests using InMemoryStorageProvider |

---

## Positive Findings

### P-1: BridgeETH Has Strong Replay Protection
The `_processedWithdrawals` map prevents double-processing. `ComputeWithdrawalHash` includes version byte, chain ID, contract address, nonce, recipient, amount (32-byte LE), and state root, preventing cross-chain and cross-contract replay.

### P-2: BridgeETH Multisig Correctly Deduplicates Signers
`seenRelayers` HashSet prevents same-relayer double-counting. Only registered relayers are counted.

### P-3: BridgeETH Relayer Removal Prevents Going Below Threshold
`RemoveRelayer` checks `newCount >= _threshold.Get()` before allowing removal.

### P-4: BridgeETH CancelDeposit Is Well-Designed
Correctly checks: pending status, original sender, expiry elapsed, properly refunds and decrements `_totalLocked`.

### P-5: Governance IntegerSqrt Is Correct
Newton's method for UInt256 handles zero/one edge cases and converges correctly.

### P-6: Escrow Has Clean Lifecycle Management
Proper "locked/released/refunded" state separation with correct authorization and temporal constraints.

### P-7: Context Has Reentrancy Guard Structure (Partial)
`CallContract` implements per-target reentrancy protection via `ReentrancyGuard` HashSet and call depth limits. The structure is correct, though incomplete (see H-2).

### P-8: HostStorageProvider Type-Tagged Serialization Is AOT-Safe
Tag-byte system (`0x01`-`0x0A`) provides clear type discrimination without reflection.

### P-9: Excellent CSPRNG Usage in Mnemonic Generation
`RandomNumberGenerator.GetBytes()` used for entropy. BIP-39 checksum verification properly implemented with SHA-256.

### P-10: Proper BIP-39 Implementation
PBKDF2-HMAC-SHA512 with 2048 iterations and NFKD normalization per BIP-39 spec.

### P-11: SLIP-0010 Compliance in HD Key Derivation
Hardened-only derivation with `"ed25519 seed"` HMAC key per SLIP-0010. `DerivationPath.Parse` enforces hardened-only levels.

### P-12: Strong Keystore Encryption
AES-256-GCM with Argon2id (3 iterations, 64 MB memory, 4 lanes). Derived key material properly zeroed. Random 32-byte salt and 12-byte nonce per encryption.

### P-13: URI Injection Prevention in RPC Client
Path parameters properly escaped with `Uri.EscapeDataString`.

### P-14: No Private Key Transmission
Verified that `BasaltClient.SendTransactionAsync` transmits only signature and public key — never the private key.

### P-15: WebSocket Reconnection with Exponential Backoff
Well-implemented reconnection with configurable max retries, initial delay, and exponential backoff.

### P-16: BST-20 Core Transfer and Allowance Semantics Are Correct
Check-then-modify pattern is correct. `TransferFrom` properly decrements allowance before delegating.

### P-17: BST-3525 Slot-Matching Enforcement
`TransferValueToId` correctly verifies `fromSlot == toSlot`, maintaining slot integrity.

### P-18: BST-3525 Value Allowance System Is Well-Designed
`RequireValueAuthorized` checks owner-or-allowance, deducts from allowance, and enforces slot matching.

### P-19: BST-VC Credential Lifecycle State Machine Is Correct
Status transitions (Active → Suspended → Active via reinstate, Active/Suspended → Revoked as terminal) properly enforced. `RequireIssuer` correctly gates mutating operations.

### P-20: BST-721 Approval Cleared on Transfer
`TransferInternal` correctly clears per-token approval via `_approvals.Delete`.

### P-21: Source Generator Dispatch Is AOT-Safe
Generated `Dispatch` uses `switch` on `uint` selector with static deserialization. No reflection, no `Activator.CreateInstance`.

### P-22: BLS Key Masking in ValidatorAccount
Private key generation correctly applies `privateKey[0] &= 0x3F` to ensure the scalar is within the BLS12-381 field modulus.

### P-23: Extensive Test Suite (~560 Tests Across SDK Layer)
Strong breadth across all token standards, system contracts, context, storage, and wallet operations. BridgeETH (47 tests) and Governance (36 tests) have particularly thorough coverage.
