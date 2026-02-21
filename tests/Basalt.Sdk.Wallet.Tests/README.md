# Basalt.Sdk.Wallet.Tests

Unit tests for the Basalt wallet SDK: HD wallets, transaction builders, ABI encoding, account management, provider client, subscriptions, and nonce management. **77 tests.**

## Test Files

- `HdWalletTests.cs` -- HD wallet: mnemonic generation, key derivation, deterministic addresses
- `TransactionBuilderTests.cs` -- Transaction builder: transfer, deploy, call, EIP-1559 fields, signing
- `AbiEncoderTests.cs` -- ABI encoding/decoding for contract call parameters
- `AccountTests.cs` -- Account: key management, signing, address derivation
- `AccountManagerTests.cs` -- Account manager: create, import, export, list accounts
- `BasaltProviderTests.cs` -- Provider client: RPC calls, status queries, transaction submission
- `SubscriptionTests.cs` -- Block subscription: WebSocket connection, event handling
- `NonceManagerTests.cs` -- Nonce manager: auto-increment, pending nonce tracking

**Total: 77 tests**

## Running

```bash
dotnet test tests/Basalt.Sdk.Wallet.Tests
```
