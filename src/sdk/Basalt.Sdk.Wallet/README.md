# Basalt.Sdk.Wallet

Full-featured wallet SDK for the Basalt blockchain. Provides key management, HD wallets (BIP-39 / SLIP-0010), transaction building, RPC client, contract interaction, and real-time block subscriptions — all in a single .NET 9 library with zero external dependencies beyond the Basalt core.

## Installation

```xml
<ProjectReference Include="src/sdk/Basalt.Sdk.Wallet/Basalt.Sdk.Wallet.csproj" />
```

Or, once published as a NuGet package:

```
dotnet add package Basalt.Sdk.Wallet
```

## Quick Start

```csharp
using Basalt.Core;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.HdWallet;

// Create an HD wallet from a mnemonic
using var wallet = HdWallet.Create(24); // or HdWallet.FromMnemonic("your mnemonic ...")
Console.WriteLine($"Mnemonic: {wallet.MnemonicPhrase}");

// Derive accounts
var account = wallet.GetAccount(0); // m/44'/9000'/0'/0'/0'

// Connect to a node and send tokens
using var provider = new BasaltProvider("http://localhost:5100");

var balance = await provider.GetBalanceAsync(account.Address);
Console.WriteLine($"Balance: {balance}");

var result = await provider.TransferAsync(
    account,
    recipientAddress,
    new UInt256(1_000_000_000_000_000_000)); // 1 BSLT

Console.WriteLine($"Tx hash: {result.Hash}");
```

## Features

### Accounts

Create, import, and manage Ed25519 accounts with secure key disposal.

```csharp
// Generate a new account
using var account = Account.Create();

// Import from private key
using var imported = Account.FromPrivateKey("0x...");

// Sign a transaction
var signedTx = account.SignTransaction(unsignedTx);

// Sign an arbitrary message
var signature = account.SignMessage(messageBytes);
```

**Validator accounts** add BLS12-381 for consensus signing:

```csharp
using var validator = ValidatorAccount.Create();
byte[] blsSig = validator.SignConsensusMessage(message);
```

**Account manager** for multi-account workflows:

```csharp
using var manager = new AccountManager();
manager.Add(account1);
manager.Add(account2);
manager.SetActive(account2.Address);
```

**Encrypted keystore** (AES-256-GCM + Argon2id):

```csharp
await KeystoreManager.SaveAsync(privateKey, address, "password", "keystore.json");
var account = await KeystoreManager.LoadAsync("keystore.json", "password");
```

### HD Wallet (BIP-39 + SLIP-0010)

Hierarchical deterministic wallet with mnemonic phrase support. Implemented from scratch using `System.Security.Cryptography` — no NBitcoin or other external dependencies.

```csharp
// Create with 24-word mnemonic (256-bit entropy)
using var wallet = HdWallet.Create(24);

// Recover from existing mnemonic
using var recovered = HdWallet.FromMnemonic("abandon ability ...", passphrase: "");

// Derive accounts at different indices
var account0 = wallet.GetAccount(0); // m/44'/9000'/0'/0'/0'
var account1 = wallet.GetAccount(1); // m/44'/9000'/0'/0'/1'

// Derive validator accounts (Ed25519 + BLS)
var validator = wallet.GetValidatorAccount(0); // BLS from m/44'/9000'/1'/0'/0'

// Custom derivation path
var custom = wallet.GetAccountAtPath(DerivationPath.Parse("m/44'/9000'/2'/0'/0'"));
```

**Mnemonic utilities:**

```csharp
string mnemonic = Mnemonic.Generate(12);      // 12 or 24 words
bool valid = Mnemonic.Validate(mnemonic);       // checksum verification
byte[] seed = Mnemonic.ToSeed(mnemonic, "");    // PBKDF2-HMAC-SHA512, 64 bytes
```

| Detail             | Value                               |
|--------------------|-------------------------------------|
| Coin type          | 9000                                |
| Default path       | `m/44'/9000'/0'/0'/{index}'`        |
| Validator BLS path | `m/44'/9000'/1'/0'/{index}'`        |
| Derivation         | SLIP-0010 (hardened only)           |
| Seed derivation    | PBKDF2-HMAC-SHA512, 2048 iterations |

### Transaction Builders

Fluent API for constructing all 6 transaction types:

```csharp
// Transfer
var tx = TransactionBuilder.Transfer()
    .WithTo(recipient)
    .WithValue(new UInt256(1_000))
    .WithGasLimit(21_000)
    .WithChainId(4242)
    .Build();

// Or use the convenience builder
var tx = new TransferBuilder(recipient, amount)
    .WithChainId(4242)
    .Build();

// Contract deployment
var tx = new ContractDeployBuilder(bytecode)
    .WithGasLimit(500_000)
    .Build();

// Contract call with BLAKE3 method selector
var tx = new ContractCallBuilder(contractAddress, "transfer")
    .WithArgs(AbiEncoder.EncodeAddress(to), AbiEncoder.EncodeUInt256(amount))
    .WithGasLimit(100_000)
    .Build();

// Staking
var tx = StakingBuilder.Deposit(new UInt256(100_000)).Build();
var tx = StakingBuilder.Withdraw(new UInt256(50_000)).Build();
var tx = StakingBuilder.RegisterValidator(blsPublicKey).Build();
```

### RPC Client

HTTP client for the Basalt REST API with source-generated JSON serialization (Native AOT safe).

```csharp
using var client = new BasaltClient("http://localhost:5100");

var status = await client.GetStatusAsync();
var account = await client.GetAccountAsync("0x...");
var block = await client.GetLatestBlockAsync();
var tx = await client.GetTransactionAsync("0x...");
var result = await client.SendTransactionAsync(signedTx);
var faucet = await client.RequestFaucetAsync("0x...");
var validators = await client.GetValidatorsAsync();
```

The `IBasaltClient` interface is fully mockable for testing.

**Nonce manager** tracks pending nonces to avoid redundant RPC calls:

```csharp
var nonceManager = new NonceManager();
var nonce = await nonceManager.GetNextNonceAsync("0x...", client);
nonceManager.IncrementNonce("0x...");
```

### ABI Encoder

Encode and decode typed arguments for contract calls:

```csharp
// Encode a full call
byte[] callData = AbiEncoder.EncodeCall("transfer",
    AbiEncoder.EncodeAddress(recipient),
    AbiEncoder.EncodeUInt256(amount));

// Encode individual types
byte[] encoded = AbiEncoder.EncodeUInt256(value);    // 32 bytes, big-endian
byte[] encoded = AbiEncoder.EncodeAddress(addr);      // 20 bytes
byte[] encoded = AbiEncoder.EncodeUInt64(42);          // 8 bytes, big-endian
byte[] encoded = AbiEncoder.EncodeBool(true);          // 1 byte
byte[] encoded = AbiEncoder.EncodeString("hello");     // 4-byte length prefix + UTF-8
byte[] encoded = AbiEncoder.EncodeBytes(data);         // 4-byte length prefix + raw

// Decode
int offset = 0;
UInt256 val = AbiEncoder.DecodeUInt256(data, ref offset);
Address addr = AbiEncoder.DecodeAddress(data, ref offset);
string str = AbiEncoder.DecodeString(data, ref offset);
```

### Contract Client

High-level contract interaction combining RPC, nonce management, and ABI encoding:

```csharp
var contract = provider.GetContract(contractAddress);

var result = await contract.CallAsync(
    account,
    "transfer",
    gasLimit: 100_000,
    args: [AbiEncoder.EncodeAddress(to), AbiEncoder.EncodeUInt256(amount)]);

// Deploy a new contract
var result = await ContractClient.DeployAsync(account, bytecode, client, nonceManager);
```

### Block Subscriptions

Real-time block events via WebSocket with automatic reconnection:

```csharp
await using var subscription = new BlockSubscription("http://localhost:5100");

await foreach (var block in subscription.SubscribeAsync(cancellationToken))
{
    Console.WriteLine($"Block #{block.Block.Number} — {block.Block.TransactionCount} txs");
}
```

Configure reconnection behavior:

```csharp
var options = new SubscriptionOptions
{
    AutoReconnect = true,
    MaxRetries = 10,
    InitialDelayMs = 1000,
    MaxDelayMs = 30_000,
};

await using var sub = new BlockSubscription("http://localhost:5100", options);
```

### BasaltProvider (Facade)

The `BasaltProvider` is the recommended entry point, combining RPC, nonce management, and subscriptions:

```csharp
using var provider = new BasaltProvider("http://testnet.basalt.foundation:5100", chainId: 4242);

// Query
var balance = await provider.GetBalanceAsync(account.Address);
var nonce = await provider.GetNonceAsync(account.Address);
var status = await provider.GetStatusAsync();
var block = await provider.GetLatestBlockAsync();

// Transfer (auto-fills nonce, signs, submits)
var result = await provider.TransferAsync(account, recipient, amount);

// Deploy contract
var result = await provider.DeployContractAsync(account, bytecode, gasLimit: 500_000);

// Interact with a contract
var contract = provider.GetContract(contractAddress);
var result = await contract.CallAsync(account, "method_name", args: [...]);

// Subscribe to blocks
await using var sub = BasaltProvider.CreateBlockSubscription("http://localhost:5100");
```

## Architecture

```
Basalt.Sdk.Wallet/
├── Accounts/
│   ├── IAccount.cs              # Account interface
│   ├── Account.cs               # Ed25519 account
│   ├── ValidatorAccount.cs      # Ed25519 + BLS12-381
│   ├── AccountManager.cs        # Multi-account container
│   └── KeystoreManager.cs       # Encrypted keystore I/O
├── HdWallet/
│   ├── Mnemonic.cs              # BIP-39 generation & validation
│   ├── Bip39Wordlist.cs         # 2048-word English wordlist
│   ├── HdKeyDerivation.cs       # SLIP-0010 Ed25519 derivation
│   ├── DerivationPath.cs        # Path parsing (m/44'/9000'/...)
│   └── HdWallet.cs              # HD wallet facade
├── Transactions/
│   ├── TransactionBuilder.cs    # Core fluent builder (6 tx types)
│   ├── TransferBuilder.cs       # Transfer convenience
│   ├── ContractDeployBuilder.cs # Deploy convenience
│   ├── ContractCallBuilder.cs   # Call with BLAKE3 selector
│   └── StakingBuilder.cs        # Stake/unstake/register
├── Rpc/
│   ├── IBasaltClient.cs         # Mockable RPC interface
│   ├── BasaltClient.cs          # HttpClient implementation
│   ├── BasaltClientOptions.cs   # Client configuration
│   ├── NonceManager.cs          # Nonce tracking
│   ├── WalletJsonContext.cs     # Source-gen JSON (AOT safe)
│   └── Models/                  # 7 response DTOs
├── Contracts/
│   ├── AbiEncoder.cs            # ABI encode/decode
│   └── ContractClient.cs        # Contract interaction
├── Subscriptions/
│   ├── IBlockSubscription.cs    # Subscription interface
│   ├── BlockSubscription.cs     # WebSocket client
│   ├── BlockEvent.cs            # Event DTOs
│   └── SubscriptionOptions.cs   # Reconnection config
└── BasaltProvider.cs            # High-level facade
```

## Dependencies

| Package          | Purpose                               |
|------------------|---------------------------------------|
| Basalt.Core      | Address, UInt256, Hash256, PublicKey  |
| Basalt.Codec     | Transaction serialization             |
| Basalt.Crypto    | Ed25519, BLS12-381, BLAKE3, Keystore  |
| Basalt.Execution | Transaction type                      |
| System.Text.Json | JSON serialization (source-generated) |

No external NuGet packages. BIP-39 mnemonic generation, SLIP-0010 key derivation, and all cryptographic operations use `System.Security.Cryptography` and the existing Basalt crypto primitives.

## Testing

```bash
dotnet test tests/Basalt.Sdk.Wallet.Tests/
```

77 tests covering all components: accounts, HD wallets, mnemonics, transaction builders, nonce manager, ABI encoding, provider integration (mocked), and subscriptions.

## License

Apache-2.0
