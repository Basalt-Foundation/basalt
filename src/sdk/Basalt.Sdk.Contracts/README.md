# Basalt.Sdk.Contracts

Smart contract SDK for Basalt. Provides the attributes, storage primitives, and runtime context that contract developers use to build Basalt smart contracts in C#.

## Getting Started

```csharp
using Basalt.Sdk.Contracts;

[BasaltContract]
public class MyToken
{
    private readonly StorageMap<byte[], ulong> _balances = new("balances");
    private readonly StorageValue<ulong> _totalSupply = new("totalSupply");

    [BasaltConstructor]
    public void Initialize(ulong initialSupply)
    {
        _balances.Set(Context.Caller, initialSupply);
        _totalSupply.Set(initialSupply);
    }

    [BasaltEntrypoint]
    public void Transfer(byte[] to, ulong amount)
    {
        var sender = Context.Caller;
        var senderBalance = _balances.Get(sender);
        Context.Require(senderBalance >= amount, "Insufficient balance");

        _balances.Set(sender, senderBalance - amount);
        _balances.Set(to, _balances.Get(to) + amount);

        Context.Emit(new TransferEvent { From = sender, To = to, Amount = amount });
    }

    [BasaltView]
    public ulong BalanceOf(byte[] account) => _balances.Get(account);

    [BasaltEvent]
    public class TransferEvent
    {
        [Indexed] public byte[] From { get; set; }
        [Indexed] public byte[] To { get; set; }
        public ulong Amount { get; set; }
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
var totalSupply = new StorageValue<ulong>("totalSupply");
totalSupply.Set(1_000_000);
ulong current = totalSupply.Get();
```

### StorageMap\<TKey, TValue\>

Key-value mapping. `TKey` is constrained to `notnull`, but `TValue` is unconstrained -- it allows both value types and reference types (including `string`).

```csharp
var balances = new StorageMap<byte[], ulong>("balances");
balances.Set(account, 1000);
ulong balance = balances.Get(account);
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
| `Context.TxValue` | `ulong` | Value sent with the call |
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

**Interface**: `IBST20` -- defines `Name()`, `Symbol()`, `Decimals()`, `TotalSupply()`, `BalanceOf(byte[])`, `Transfer(byte[], ulong)`, `Allowance(byte[], byte[])`, `Approve(byte[], ulong)`, `TransferFrom(byte[], byte[], ulong)`.

**Implementation**: `BST20Token` -- reference implementation with full allowance mechanics, `Mint(byte[], ulong)` and `Burn(byte[], ulong)` protected methods for derived contracts. Uses hex-encoded address strings as internal storage keys.

**Events**: `TransferEvent` (From, To, Amount), `ApprovalEvent` (Owner, Spender, Amount).

```csharp
var token = new BST20Token("MyToken", "MTK", decimals: 18);
```

### BST-721 Non-Fungible Token (ERC-721 equivalent)

**Interface**: `IBST721` -- defines `Name()`, `Symbol()`, `OwnerOf(ulong)`, `BalanceOf(byte[])`, `Transfer(byte[], ulong)`, `Approve(byte[], ulong)`, `GetApproved(ulong)`, `TokenURI(ulong)`.

**Implementation**: `BST721Token` -- reference implementation with auto-incrementing token IDs, per-token approval, and metadata URI storage. Includes a public `Mint(byte[], string)` entrypoint that returns the new token ID.

**Events**: `NftTransferEvent` (From, To, TokenId), `NftApprovalEvent` (Owner, Approved, TokenId).

```csharp
var nft = new BST721Token("MyNFT", "MNFT");
ulong tokenId = nft.Mint(recipient, "https://example.com/metadata/0");
```

### BST-1155 Multi-Token (ERC-1155 equivalent)

**Interface**: `IBST1155` -- defines `BalanceOf(byte[], ulong)`, `BalanceOfBatch(byte[][], ulong[])`, `SafeTransferFrom(byte[], byte[], ulong, ulong)`, `SafeBatchTransferFrom(byte[], byte[], ulong[], ulong[])`, `SetApprovalForAll(byte[], bool)`, `IsApprovedForAll(byte[], byte[])`, `Uri(ulong)`.

**Implementation**: `BST1155Token` -- reference implementation supporting both fungible and non-fungible tokens in a single contract. Includes `Mint(byte[], ulong, ulong, string)` and `Create(byte[], ulong, string)` for creating new token types with optional initial supply.

**Events**: `TransferSingleEvent` (Operator, From, To, TokenId, Amount), `TransferBatchEvent` (Operator, From, To, TokenIds, Amounts), `ApprovalForAllEvent` (Owner, Operator, Approved).

```csharp
var multi = new BST1155Token("https://example.com/tokens/");
ulong tokenId = multi.Create(recipient, initialSupply: 1000, "https://example.com/tokens/0");
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
ulong bondA = sft.Mint(alice, slot: 20251231, value: 1_000_000);
ulong bondB = sft.Mint(bob, slot: 20251231, value: 500_000);

// Transfer value between same-slot tokens
sft.TransferValueToId(bondA, bondB, 200_000);  // alice → bob: 200k

// Transfer value to a new address (creates new token)
ulong bondC = sft.TransferValueToAddress(bondA, charlie, 100_000);

// Value allowance (operator pattern)
sft.ApproveValue(bondA, operator, 50_000);
ulong remaining = sft.ValueAllowance(bondA, operator);

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
ulong shares = vault.Deposit(1000);

// Check exchange rate
ulong sharesPerAsset = vault.ConvertToShares(100);
ulong assetsPerShare = vault.ConvertToAssets(100);

// Preview operations (includes rounding)
ulong sharesToBurn = vault.PreviewWithdraw(500);  // rounds up
ulong assetsNeeded = vault.PreviewMint(100);      // rounds up

// Withdraw/redeem
ulong burned = vault.Withdraw(500);    // withdraw exact assets, burn shares
ulong received = vault.Redeem(shares); // redeem exact shares, receive assets

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
ulong count = vcRegistry.GetIssuerCredentialCount(issuerAddr);
bool issued = vcRegistry.HasIssuerIssuedCredential(issuerAddr, credentialHash);
bool verified = vcRegistry.VerifyCredentialSet(credentialHash);
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
ulong stake = issuerRegistry.GetCollateralStake(issuerAddr);
bool supports = issuerRegistry.SupportsSchema(issuerAddr, schemaIdBytes);
```

**Events**: `IssuerRegisteredEvent`, `CollateralStakedEvent`, `RevocationRootUpdatedEvent`, `IssuerSlashedEvent`, `IssuerDeactivatedEvent`, `IssuerReactivatedEvent`, `AdminTransferredEvent`.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Crypto` | BLAKE3 hashing (SchemaRegistry schema ID derivation) |

No dependency on the node or execution engine.
