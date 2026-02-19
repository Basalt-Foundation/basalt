# Basalt.Sdk.Contracts

Smart contract SDK for Basalt. Provides the attributes, storage primitives, and runtime context that contract developers use to build Basalt smart contracts in C#.

## Getting Started

```csharp
using Basalt.Sdk.Contracts;

[BasaltContract]
public class MyToken
{
    private readonly StorageMap<byte[], UInt256> _balances = new("balances");
    private readonly StorageValue<UInt256> _totalSupply = new("totalSupply");

    [BasaltConstructor]
    public void Initialize(UInt256 initialSupply)
    {
        _balances.Set(Context.Caller, initialSupply);
        _totalSupply.Set(initialSupply);
    }

    [BasaltEntrypoint]
    public void Transfer(byte[] to, UInt256 amount)
    {
        var sender = Context.Caller;
        var senderBalance = _balances.Get(sender);
        Context.Require(senderBalance >= amount, "Insufficient balance");

        _balances.Set(sender, senderBalance - amount);
        _balances.Set(to, _balances.Get(to) + amount);

        Context.Emit(new TransferEvent { From = sender, To = to, Amount = amount });
    }

    [BasaltView]
    public UInt256 BalanceOf(byte[] account) => _balances.Get(account);

    [BasaltEvent]
    public class TransferEvent
    {
        [Indexed] public byte[] From { get; set; }
        [Indexed] public byte[] To { get; set; }
        public UInt256 Amount { get; set; }
    }
}
```

## Attributes

| Attribute | Target | Description |
|-----------|--------|-------------|
| `[BasaltContract]` | Class | Marks a class as a Basalt smart contract |
| `[BasaltEntrypoint]` | Method | State-mutating entry point |
| `[BasaltView]` | Method | Read-only view function (no state changes) |
| `[BasaltConstructor]` | Method | One-time initialization on deploy |
| `[BasaltEvent]` | Class | Event emitted by contracts |
| `[Indexed]` | Property | Indexed event parameter for efficient filtering |
| `[BasaltSerializable]` | Class/Struct | Marks a type for automatic binary codec generation (WriteTo/ReadFrom/GetSerializedSize) |
| `[BasaltJsonSerializable]` | Class/Struct | Marks a type for automatic JSON serialization support with Basalt primitive converters |

## Storage Primitives

### StorageValue\<T\> where T : struct

Single value with a fixed storage key. The type parameter `T` is constrained to `struct` (value types only).

```csharp
var totalSupply = new StorageValue<UInt256>("totalSupply");
totalSupply.Set(1_000_000);
UInt256 current = totalSupply.Get();
```

### StorageMap\<TKey, TValue\>

Key-value mapping. `TKey` is constrained to `notnull`, but `TValue` is unconstrained -- it allows both value types and reference types (including `string`).

```csharp
var balances = new StorageMap<byte[], UInt256>("balances");
balances.Set(account, 1000);
UInt256 balance = balances.Get(account);
bool exists = balances.ContainsKey(account);
balances.Delete(account);

// Reference type values are supported:
var names = new StorageMap<string, string>("names");
names.Set("alice", "Alice");
```

### StorageList\<T\>

Ordered collection with index-based access.

```csharp
var items = new StorageList<ulong>("items");
items.Add(42);
ulong first = items.Get(0);
int count = items.Count;
```

## Runtime Context

Access blockchain state from within contracts via `Context`:

| Property | Type | Description |
|----------|------|-------------|
| `Context.Caller` | `byte[]` | Address of the caller |
| `Context.Self` | `byte[]` | Address of the current contract |
| `Context.TxValue` | `UInt256` | Value sent with the call |
| `Context.BlockTimestamp` | `long` | Current block timestamp (Unix seconds) |
| `Context.BlockHeight` | `ulong` | Current block number |
| `Context.ChainId` | `uint` | Chain identifier |
| `Context.GasRemaining` | `ulong` | Remaining gas |
| `Context.CallDepth` | `int` | Current call depth (0 = top-level call) |
| `Context.MaxCallDepth` | `int` | Maximum cross-contract call depth (const `8`) |
| `Context.ReentrancyGuard` | `HashSet<string>` | Set of contract addresses currently on the call stack |
| `Context.CrossContractCallHandler` | `Func<...>?` | Delegate for cross-contract calls (set by the runtime/test host) |

### Utility Methods

```csharp
Context.Require(condition, "Error message");         // Revert if false
Context.Revert("Error message");                     // Unconditional revert
Context.Emit(new MyEvent { ... });                   // Emit event
Context.CallContract<T>(addr, "method", args);       // Cross-contract call (returns T)
Context.CallContract(addr, "method", args);           // Cross-contract call (void return)
Context.Reset();                                      // Reset all context state (used between test runs)
```

Cross-contract calls enforce reentrancy protection via `ReentrancyGuard` and a maximum call depth of `MaxCallDepth` (8). The calling contract's address becomes the `Caller` for the target, and context is restored after the call returns.

## Token Standards (`Standards/`)

The SDK includes interfaces and reference implementations for common token standards, located in the `Standards/` subdirectory.

### BST-20 Fungible Token (ERC-20 equivalent)

**Interface**: `IBST20` -- defines `Name()`, `Symbol()`, `Decimals()`, `TotalSupply()` returns `UInt256`, `BalanceOf(byte[])` returns `UInt256`, `Transfer(byte[], UInt256)`, `Allowance(byte[], byte[])` returns `UInt256`, `Approve(byte[], UInt256)`, `TransferFrom(byte[], byte[], UInt256)`.

**Implementation**: `BST20Token` -- reference implementation with full allowance mechanics, `Mint(byte[], UInt256)` and `Burn(byte[], UInt256)` protected methods for derived contracts. Uses hex-encoded address strings as internal storage keys.

**Events**: `TransferEvent` (From, To, Amount: `UInt256`), `ApprovalEvent` (Owner, Spender, Amount: `UInt256`).

```csharp
var token = new BST20Token("MyToken", "MTK", decimals: 18);
```

### BST-721 Non-Fungible Token (ERC-721 equivalent)

**Interface**: `IBST721` -- defines `Name()`, `Symbol()`, `OwnerOf(UInt256)`, `BalanceOf(byte[])` returns `UInt256`, `Transfer(byte[], UInt256)`, `Approve(byte[], UInt256)`, `GetApproved(UInt256)`, `TokenURI(UInt256)`.

**Implementation**: `BST721Token` -- reference implementation with auto-incrementing token IDs (`UInt256`), per-token approval, and metadata URI storage. Includes a public `Mint(byte[], string)` entrypoint that returns the new token ID as `UInt256`.

**Events**: `NftTransferEvent` (From, To, TokenId: `UInt256`), `NftApprovalEvent` (Owner, Approved, TokenId: `UInt256`).

```csharp
var nft = new BST721Token("MyNFT", "MNFT");
UInt256 tokenId = nft.Mint(recipient, "https://example.com/metadata/0");
```

### BST-1155 Multi-Token (ERC-1155 equivalent)

**Interface**: `IBST1155` -- defines `BalanceOf(byte[], UInt256)` returns `UInt256`, `BalanceOfBatch(byte[][], UInt256[])`, `SafeTransferFrom(byte[], byte[], UInt256, UInt256)`, `SafeBatchTransferFrom(byte[], byte[], UInt256[], UInt256[])`, `SetApprovalForAll(byte[], bool)`, `IsApprovedForAll(byte[], byte[])`, `Uri(UInt256)`.

**Implementation**: `BST1155Token` -- reference implementation supporting both fungible and non-fungible tokens in a single contract. Includes `Mint(byte[], UInt256, UInt256, string)` and `Create(byte[], UInt256, string)` for creating new token types with optional initial supply.

**Events**: `TransferSingleEvent` (Operator, From, To, TokenId: `UInt256`, Amount: `UInt256`), `TransferBatchEvent` (Operator, From, To, TokenIds, Amounts), `ApprovalForAllEvent` (Owner, Operator, Approved).

```csharp
var multi = new BST1155Token("https://example.com/tokens/");
UInt256 tokenId = multi.Create(recipient, initialSupply: 1000, "https://example.com/tokens/0");
```

### BST-DID Decentralized Identity

**Interface**: `IBSTDID` -- defines `RegisterDID(byte[])`, `ResolveDID(string)`, `AddAttestation(string, string, string, long, byte[])`, `RevokeAttestation(string, string)`, `HasValidAttestation(string, string)`, `TransferDID(string, byte[])`, `DeactivateDID(string)`.

**Implementation**: `BSTDIDRegistry` -- on-chain decentralized identifier registry. DIDs are formatted as `did:basalt:<hex-index>`. Supports verifiable credential attestations (add/revoke), controller transfer, and DID deactivation. Only the DID controller can manage its attestations and lifecycle.

**Data Types**: `DIDDocument` (Id, Controller, CreatedAt, UpdatedAt, Active), `Attestation` (Id, CredentialType, Issuer, IssuedAt, ExpiresAt, Revoked, Data).

**Events**: `DIDRegisteredEvent` (DID, Controller), `AttestationAddedEvent` (DID, CredentialType, Issuer, AttestationId), `AttestationRevokedEvent` (DID, AttestationId).

```csharp
var registry = new BSTDIDRegistry("did:basalt:");
string did = registry.RegisterDID(controller);
registry.AddAttestation(did, "KYC", issuerHex, expiresAt, data);
bool valid = registry.HasValidAttestation(did, "KYC");
```

### BST-3525 Semi-Fungible Token (ERC-3525 equivalent)

**Interface**: `IBST3525` -- three-component model: (tokenId, slot, value). Tokens in the same slot are fungible by value. Each token has a unique ID (like BST-721) but carries a fungible value (like BST-20) that is transferable within the same slot.

**Implementation**: `BST3525Token` -- reference implementation with auto-incrementing token IDs, per-token value allowances, slot-based URI metadata, and ERC-721-compatible token ownership transfer. Value transfers require matching slots between source and destination.

**Events**: `TransferValueEvent` (FromTokenId, ToTokenId, Value), `SftTransferEvent` (From, To, TokenId), `ApproveValueEvent` (TokenId, Operator, Value), `SftApprovalEvent` (Owner, Approved, TokenId), `SftMintEvent` (To, TokenId, Slot, Value).

```csharp
var sft = new BST3525Token("Bond Token", "BOND", valueDecimals: 6);

// Mint: create tokens in a slot (slot = maturity date, value = principal)
UInt256 bondA = sft.Mint(alice, slot: 20251231, value: 1_000_000);
UInt256 bondB = sft.Mint(bob, slot: 20251231, value: 500_000);

// Transfer value between same-slot tokens
sft.TransferValueToId(bondA, bondB, 200_000);  // alice → bob: 200k

// Transfer value to a new address (creates new token)
UInt256 bondC = sft.TransferValueToAddress(bondA, charlie, 100_000);

// Value allowance (operator pattern)
sft.ApproveValue(bondA, operator, 50_000);
UInt256 remaining = sft.ValueAllowance(bondA, operator);

// ERC-721 compatible token transfer
sft.TransferToken(dave, bondA);
```

### BST-4626 Tokenized Vault (ERC-4626 equivalent)

**Interface**: `IBST4626` (extends `IBST20`) -- standardized vault pattern. Deposit underlying BST-20 assets, receive vault share tokens. Shares are themselves BST-20 fungible tokens. Exchange rate adjusts as yield accrues.

**Implementation**: `BST4626Vault` (extends `BST20Token`) -- reference implementation with virtual shares/assets offset to prevent inflation attacks, ceiling-division rounding for withdrawal/mint previews (favors the vault), and admin-only yield reporting via `Harvest()`. Uses cross-contract calls to the underlying BST-20 asset.

**Events**: `VaultDepositEvent` (Caller, Assets, Shares), `VaultWithdrawEvent` (Caller, Assets, Shares), `VaultHarvestEvent` (Caller, YieldAmount, NewTotalAssets).

```csharp
var vault = new BST4626Vault("Vault Shares", "vUND", decimals: 18, assetAddress);

// Deposit underlying assets → receive shares
UInt256 shares = vault.Deposit(1000);

// Check exchange rate
UInt256 sharesPerAsset = vault.ConvertToShares(100);
UInt256 assetsPerShare = vault.ConvertToAssets(100);

// Preview operations (includes rounding)
UInt256 sharesToBurn = vault.PreviewWithdraw(500);  // rounds up
UInt256 assetsNeeded = vault.PreviewMint(100);      // rounds up

// Withdraw/redeem
UInt256 burned = vault.Withdraw(500);    // withdraw exact assets, burn shares
UInt256 received = vault.Redeem(shares); // redeem exact shares, receive assets

// Admin: report yield (increases exchange rate)
vault.Harvest(100);  // totalAssets += 100, shares unchanged → each share worth more
```

### BST-VC Verifiable Credential Registry (W3C VC + eIDAS 2.0)

**Interface**: `IBSTVC` -- on-chain registry for verifiable credential lifecycle. Full VCs stored off-chain (IPFS), only hashes on-chain. Supports issuance, revocation, suspension, and reinstatement. Only the original issuer can manage a credential's status.

**Implementation**: `BSTVCRegistry` -- reference implementation with status tracking (Active/Revoked/Suspended), issuer-only lifecycle management, per-issuer credential counting, and temporal validity checks. Credential IDs are deterministic: `vc:{issuerHex}:{hexIndex}`.

**Status Transitions**: `(none) → Active → Suspended ↔ Reinstated → Revoked` (terminal).

**Events**: `CredentialIssuedEvent` (CredentialHash, Issuer, Subject, CredentialId), `CredentialRevokedEvent` (CredentialHash, Issuer, Reason), `CredentialSuspendedEvent` (CredentialHash, Issuer, Reason), `CredentialReinstatedEvent` (CredentialHash, Issuer).

```csharp
var vcRegistry = new BSTVCRegistry();

// Issue a credential (hash stored on-chain, full VC off-chain)
string credId = vcRegistry.IssueCredential(credentialHash, subjectDid, schemaId,
    validUntil: 1735689600, metadataUri: "ipfs://Qm...");

// Check status
byte status = vcRegistry.GetCredentialStatus(credentialHash);  // 1 = Active
bool valid = vcRegistry.IsCredentialValid(credentialHash);     // true if active + not expired

// Lifecycle management (issuer only)
vcRegistry.SuspendCredential(credentialHash, "Under review");
vcRegistry.ReinstateCredential(credentialHash);
vcRegistry.RevokeCredential(credentialHash, "Fraud detected");

// Query
UInt256 count = vcRegistry.GetIssuerCredentialCount(issuerAddr);
bool issued = vcRegistry.HasIssuerIssuedCredential(issuerAddr, credentialHash);
bool verified = vcRegistry.VerifyCredentialSet(credentialHash);
```

### Governance (System Contract)

On-chain governance with stake-weighted quadratic voting, single-hop delegation, timelock, and executable proposals. Integrates with StakingPool for vote weight derivation.

**Type ID**: `0x0102` | **Genesis address**: `0x...1003`

```csharp
var gov = new Governance();

// Create a proposal
string proposalId = gov.CreateProposal("Upgrade block gas limit", "technical",
    targetContract, callData, votingPeriod: 604800);

// Vote (weight derived from StakingPool stake via cross-contract call)
gov.Vote(proposalIdBytes, voteYes: true);

// Delegate voting power
gov.DelegateVote(delegateAddr);
gov.UndelegateVote();

// Execute after timelock
gov.ExecuteProposal(proposalIdBytes);

// Query
UInt256 weight = gov.GetVotingWeight(voterAddr);
byte status = gov.GetProposalStatus(proposalIdBytes);  // 0=Active, 1=Passed, 2=Failed, 3=Executed, 4=Expired
```

### BridgeETH (System Contract)

EVM bridge contract for locking/unlocking native BST with M-of-N Ed25519 multisig relayer verification, admin controls, pause/unpause, and deposit lifecycle management.

**Type ID**: `0x0107` | **Genesis address**: `0x...1008`

```csharp
var bridge = new BridgeETH();

// Lock BST (call with TxValue)
bridge.Lock(ethRecipientAddr);

// Admin: add relayer, set threshold
bridge.AddRelayer(relayerAddr);
bridge.SetThreshold(3);

// Relayer: confirm and finalize deposits
bridge.ConfirmDeposit(depositId, 42);
bridge.FinalizeDeposit(depositId);

// Unlock (with relayer signatures)
bridge.Unlock(bstRecipientAddr, amount, depositId, signatures);

// Admin controls
bridge.Pause();
bridge.Unpause();
bridge.TransferAdmin(newAdminAddr);

// Query
UInt256 locked = bridge.GetLockedBalance();
byte status = bridge.GetDepositStatus(depositId);
bool paused = bridge.IsPaused();
```

### SchemaRegistry (ZK Compliance)

On-chain registry of credential schema definitions. Anyone can register a schema (permissionless). A schema defines WHAT can be proved via ZK proofs. SchemaId is derived from `BLAKE3(name)`.

**Type ID**: `0x0105` | **Genesis address**: `0x...1006`

```csharp
var registry = new SchemaRegistry();

// Register a schema with its Groth16 verification key
string schemaIdHex = registry.RegisterSchema("KYC_Basic", fieldDefinitionsJson, verificationKeyBytes);

// Update VK (creator only)
registry.UpdateVerificationKey(schemaIdBytes, newVkBytes);

// Query
string name = registry.GetSchema(schemaIdBytes);
string vkHex = registry.GetVerificationKey(schemaIdBytes);
bool exists = registry.SchemaExists(schemaIdBytes);
```

**Events**: `SchemaRegisteredEvent` (SchemaId, Creator, Name), `VerificationKeyUpdatedEvent` (SchemaId, UpdatedBy).

### IssuerRegistry (ZK Compliance)

On-chain registry of credential issuers with trust tiers and collateral staking. Issuers maintain their own Sparse Merkle Tree for credential revocation off-chain and publish the root on-chain.

**Type ID**: `0x0106` | **Genesis address**: `0x...1007`

**Issuer Tiers**:
- Tier 0: Self-attestation (anyone, no collateral)
- Tier 1: Regulated entity (admin-approved, no collateral)
- Tier 2: Accredited provider (requires BST collateral stake)
- Tier 3: Sovereign/eIDAS (admin-approved, no collateral)

```csharp
var issuerRegistry = new IssuerRegistry();

// Register (Tier 0: anyone, Tier 1/3: admin only, Tier 2: anyone + stake)
issuerRegistry.RegisterIssuer("Acme KYC Provider", tier: 2);
issuerRegistry.StakeCollateral();  // sends BST via TxValue

// Manage schemas and revocation
issuerRegistry.AddSchemaSupport(schemaIdBytes);
issuerRegistry.UpdateRevocationRoot(newMerkleRootBytes);

// Admin operations
issuerRegistry.SlashIssuer(issuerAddr, "Fraud detected");
issuerRegistry.DeactivateIssuer(issuerAddr);
issuerRegistry.TransferAdmin(newAdminAddr);

// Query
byte tier = issuerRegistry.GetIssuerTier(issuerAddr);
bool active = issuerRegistry.IsActiveIssuer(issuerAddr);
string rootHex = issuerRegistry.GetRevocationRoot(issuerAddr);
UInt256 stake = issuerRegistry.GetCollateralStake(issuerAddr);
bool supports = issuerRegistry.SupportsSchema(issuerAddr, schemaIdBytes);
```

**Events**: `IssuerRegisteredEvent`, `CollateralStakedEvent`, `RevocationRootUpdatedEvent`, `IssuerSlashedEvent`, `IssuerDeactivatedEvent`, `IssuerReactivatedEvent`, `AdminTransferredEvent`.

## ContractRegistry Type IDs

All contract types are registered with `ContractRegistry.CreateDefault()` and identified by a 2-byte type ID in the deployment manifest (`[0xBA, 0x5A][typeId BE][ctor args]`).

### Token Standards

| Type ID | Contract | Description |
|---------|----------|-------------|
| `0x0001` | `BST20Token` | Fungible token (ERC-20) |
| `0x0002` | `BST721Token` | Non-fungible token (ERC-721) |
| `0x0003` | `BST1155Token` | Multi-token (ERC-1155) |
| `0x0004` | `BSTDIDRegistry` | Decentralized identity |
| `0x0005` | `BST3525Token` | Semi-fungible token (ERC-3525) |
| `0x0006` | `BST4626Vault` | Tokenized vault (ERC-4626) |
| `0x0007` | `BSTVCRegistry` | Verifiable credentials (W3C VC) |

### System Contracts

| Type ID | Contract | Genesis Address |
|---------|----------|-----------------|
| `0x0100` | `WBSLT` | `0x...1001` |
| `0x0101` | `BNS` | `0x...1002` |
| `0x0102` | `Governance` | `0x...1003` |
| `0x0103` | `Escrow` | `0x...1004` |
| `0x0104` | `StakingPool` | `0x...1005` |
| `0x0105` | `SchemaRegistry` | `0x...1006` |
| `0x0106` | `IssuerRegistry` | `0x...1007` |
| `0x0107` | `BridgeETH` | `0x...1008` |

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Crypto` | BLAKE3 hashing (SchemaRegistry schema ID derivation) |

No dependency on the node or execution engine.
