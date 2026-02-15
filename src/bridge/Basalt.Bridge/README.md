# Basalt.Bridge

EVM bridge for cross-chain asset transfers between Basalt and Ethereum. Implements a lock/unlock model with multisig relayer verification and BLAKE3 Merkle proof validation.

## Components

### BridgeState

Bridge system contract (address `0x0...0010`) managing deposits and withdrawals.

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `BasaltChainId` | `uint` | Basalt chain ID (default: `1`) |
| `EthereumChainId` | `uint` | Ethereum chain ID (default: `11155111` / Sepolia) |
| `CurrentNonce` | `ulong` | Next deposit ID (read-only) |

```csharp
var bridge = new BridgeState();

// Lock tokens on Basalt (deposit) — optional tokenAddress for non-native tokens
BridgeDeposit deposit = bridge.Lock(sender, recipient, amount);
BridgeDeposit erc20Deposit = bridge.Lock(sender, recipient, amount, tokenAddress: tokenAddr);
deposit.Status == BridgeTransferStatus.Pending;

// Confirm deposit (relayer attestation with target chain block height)
bridge.ConfirmDeposit(nonce: 0, blockHeight: 42);

// Finalize deposit
bridge.FinalizeDeposit(nonce: 0);

// Unlock tokens (withdrawal with multisig verification)
bridge.Unlock(withdrawal, multisigRelayer);

// Query state
BridgeDeposit? d = bridge.GetDeposit(nonce);
IReadOnlyList<BridgeDeposit> pending = bridge.GetPendingDeposits(); // Pending + Confirmed deposits
ulong locked = bridge.GetLockedBalance();           // Native token locked balance
ulong tokenLocked = bridge.GetLockedBalance(tokenAddr); // Specific token locked balance
bool processed = bridge.IsWithdrawalProcessed(nonce);
```

Transfer lifecycle: `Pending` -> `Confirmed` -> `Finalized` (or `Failed`).

### BridgeException

Bridge-specific errors throw `BridgeException` (e.g., attempting to bridge a zero amount).

```csharp
try { bridge.Lock(sender, recipient, 0); }
catch (BridgeException ex) { Console.WriteLine(ex.Message); } // "Cannot bridge zero amount"
```

### MultisigRelayer

M-of-N Ed25519 signature verification for bridge relay messages.

```csharp
var relayer = new MultisigRelayer(threshold: 2);
relayer.AddRelayer(relayerPubKey1);
relayer.AddRelayer(relayerPubKey2);
relayer.AddRelayer(relayerPubKey3);

// Relayers sign withdrawal hash
byte[] msgHash = BridgeState.ComputeWithdrawalHash(withdrawal);
RelayerSignature sig = MultisigRelayer.Sign(msgHash, privateKey, publicKey);

// Verify M-of-N signatures
withdrawal.Signatures.Add(sig1);
withdrawal.Signatures.Add(sig2);
bool valid = relayer.VerifyMessage(msgHash, withdrawal.Signatures);
```

### BridgeProofVerifier

BLAKE3-based Merkle proof construction and verification for cross-chain state proofs.

```csharp
// Build tree from deposit leaves
byte[] root = BridgeProofVerifier.ComputeMerkleRoot(leaves);

// Generate proof for a specific leaf
var (proofRoot, proof) = BridgeProofVerifier.BuildMerkleProof(leaves, leafIndex);

// Verify proof
bool valid = BridgeProofVerifier.VerifyMerkleProof(leaf, proof, leafIndex, root);
```

### Bridge Flow

1. **Basalt -> Ethereum**: User calls `Lock()` on Basalt, relayers observe the deposit, sign the withdrawal hash, and submit it to the Ethereum bridge contract with Merkle proof
2. **Ethereum -> Basalt**: User locks tokens in the Ethereum contract, relayers observe and submit a signed withdrawal to `Unlock()` on Basalt

### Solidity Contracts

The Ethereum-side bridge contracts live in the repository root at `contracts/`:

- `contracts/BasaltBridge.sol` -- Main bridge contract with Basalt light client verification
- `contracts/WBST.sol` -- Wrapped BSLT (wBST) ERC-20 token

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Hash256, Address, UInt256 |
| `Basalt.Crypto` | BLAKE3 for Merkle hashing, Ed25519 for relayer signatures |
| `Basalt.Storage` | State storage interfaces |
