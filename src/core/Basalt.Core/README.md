# Basalt.Core

Foundation types for the Basalt blockchain. This library has zero external dependencies and defines the core value types, error handling, and chain parameters used throughout the entire stack.

## Types

### Hash256

32-byte immutable hash value (`readonly struct`), used for block hashes, state roots, and transaction hashes. Implements `IEquatable<Hash256>` and `IComparable<Hash256>`.

```csharp
var hash = new Hash256(bytes32);              // From ReadOnlySpan<byte> (exactly 32 bytes)
string hex = hash.ToHexString();              // "0xabc..."
Hash256 parsed = Hash256.FromHexString(hex);
bool ok = Hash256.TryFromHexString(hex, out Hash256 result);
byte[] bytes = hash.ToArray();
hash.WriteTo(destination);                    // Write to Span<byte>
bool empty = hash.IsZero;
```

Static fields: `Hash256.Zero`.

### Address

20-byte account address (`readonly struct`) derived from a public key via Keccak-256. Implements `IEquatable<Address>` and `IComparable<Address>`.

```csharp
var addr = new Address(bytes20);              // From ReadOnlySpan<byte> (exactly 20 bytes)
string hex = addr.ToHexString();              // "0x..."
Address parsed = Address.FromHexString(hex);
bool ok = Address.TryFromHexString(hex, out Address result);
byte[] bytes = addr.ToArray();
addr.WriteTo(destination);                    // Write to Span<byte>
bool isSystem = addr.IsSystemContract;        // System contract range
bool empty = addr.IsZero;
```

Static fields: `Address.Zero`.

### UInt256

256-bit unsigned integer (`readonly struct`) for balances, stakes, and token amounts. Stored as two `UInt128` fields (`Lo`, `Hi`) in little-endian order. Implements `IEquatable<UInt256>` and `IComparable<UInt256>`.

```csharp
var balance = new UInt256(1_000_000);                      // From ulong
var big = new UInt256(lo, hi);                             // From UInt128 pair
var fromBytes = new UInt256(bytes32, isBigEndian: false);  // From ReadOnlySpan<byte>
var total = balance + new UInt256(500);
bool canAfford = balance >= cost;
string hex = balance.ToHexString();
var parsed = UInt256.Parse("0xff");                        // Hex or decimal
byte[] bytes = balance.ToArray(isBigEndian: false);
balance.WriteTo(destination, isBigEndian: false);
ulong small = (ulong)balance;                              // Explicit, throws on overflow
```

Supports full arithmetic (`+`, `-`, `*`, `/`, `%`), bitwise (`&`, `|`, `^`, `~`, `<<`, `>>`), and comparison operators. Implicit conversions from `ulong`, `uint`, and `int`. Explicit conversion to `ulong` (throws `OverflowException` if value exceeds `ulong` range). Static `DivRem` method for combined quotient/remainder.

Static fields: `UInt256.Zero`, `UInt256.One`, `UInt256.MaxValue`.

### Signature

64-byte Ed25519 signature (`readonly struct`). Implements `IEquatable<Signature>`.

```csharp
var sig = new Signature(bytes64);
byte[] bytes = sig.ToArray();
sig.WriteTo(destination);
bool empty = sig.IsEmpty;
```

Static fields: `Signature.Empty`.

### PublicKey

32-byte Ed25519 public key (`readonly struct`). Implements `IEquatable<PublicKey>`. Has `ToArray()` but no `.Bytes` property.

```csharp
var pub = new PublicKey(bytes32);
byte[] bytes = pub.ToArray();
pub.WriteTo(destination);
bool empty = pub.IsEmpty;
```

Static fields: `PublicKey.Empty`.

### BlsSignature

96-byte BLS12-381 signature (`readonly struct`, compressed G2 point), used for consensus vote aggregation. Implements `IEquatable<BlsSignature>`.

```csharp
var blsSig = new BlsSignature(bytes96);
byte[] raw = blsSig.ToArray();
blsSig.WriteTo(destination);
bool empty = blsSig.IsEmpty;
```

Static fields: `BlsSignature.Empty`.

### BlsPublicKey

48-byte BLS12-381 public key (`readonly struct`, compressed G1 point), used for validator identity in consensus. Implements `IEquatable<BlsPublicKey>`.

```csharp
var blsPub = new BlsPublicKey(bytes48);
byte[] raw = blsPub.ToArray();
blsPub.WriteTo(destination);
bool empty = blsPub.IsEmpty;
```

Static fields: `BlsPublicKey.Empty`.

### ChainParameters

Immutable network configuration (`sealed class`) with pre-defined profiles.

```csharp
var devnet  = ChainParameters.Devnet;   // ChainId=31337, 4 validators
var testnet = ChainParameters.Testnet;  // ChainId=2
var mainnet = ChainParameters.Mainnet;  // ChainId=1
```

Key properties:

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ChainId` | `uint` | (required) | Chain ID for replay protection |
| `NetworkName` | `string` | (required) | Human-readable network name |
| `BlockTimeMs` | `uint` | 400 | Target block time in milliseconds |
| `MaxBlockSizeBytes` | `uint` | 2 MB | Maximum block size |
| `MaxTransactionsPerBlock` | `uint` | 10,000 | Max transactions per block |
| `MaxTransactionDataBytes` | `uint` | 128 KB | Max transaction data size |
| `MinGasPrice` | `UInt256` | 1 | Minimum gas price |
| `BlockGasLimit` | `ulong` | 100,000,000 | Block gas limit |
| `TransferGasCost` | `ulong` | 21,000 | Base transfer gas cost |
| `ContractDeployGasCost` | `ulong` | 500,000 | Contract deploy base gas |
| `ContractCallGasCost` | `ulong` | 50,000 | Contract call base gas |
| `ValidatorSetSize` | `uint` | 100 | Active validator count |
| `MinValidatorStake` | `UInt256` | 100,000 tokens | Minimum validator stake |
| `EpochLength` | `uint` | 1,000 | Epoch length in blocks |
| `UnbondingPeriod` | `uint` | 907,200 | Unbonding period in blocks (~21 days) |
| `TokenDecimals` | `byte` | 18 | Token decimals |
| `TokenSymbol` | `string` | "BSLT" | Token symbol |
| `ProtocolVersion` | `uint` | 1 | Protocol version |
| `InitialBaseFee` | `UInt256` | 1 | Genesis block base fee |
| `BaseFeeChangeDenominator` | `uint` | 8 | Max base fee change rate (12.5%) |
| `ElasticityMultiplier` | `uint` | 2 | Target gas = gasLimit / multiplier |

### BasaltResult

Strongly-typed error handling without exceptions.

```csharp
BasaltResult result = BasaltResult.Ok;
BasaltResult error = BasaltResult.Error(BasaltErrorCode.InvalidSignature, "bad sig");
if (!result.IsSuccess)
    Console.WriteLine($"Error {result.ErrorCode}: {result.Message}");

BasaltResult<Block> blockResult = BasaltResult<Block>.Ok(block);
BasaltResult<Block> blockError = BasaltResult<Block>.Error(BasaltErrorCode.InvalidBlockHash);
Block value = blockResult.Value!;
```

### IStakingState

Interface for cross-layer staking access, allowing the execution layer to interact with staking without a direct dependency on the consensus assembly.

```csharp
public interface IStakingState
{
    UInt256 MinValidatorStake { get; }
    StakingOperationResult RegisterValidator(Address validatorAddress, UInt256 initialStake,
        ulong blockNumber = 0, string? p2pEndpoint = null);
    StakingOperationResult AddStake(Address validatorAddress, UInt256 amount);
    StakingOperationResult InitiateUnstake(Address validatorAddress, UInt256 amount, ulong currentBlock);
    UInt256? GetSelfStake(Address validatorAddress);
}
```

**StakingOperationResult**: `readonly struct` with `IsSuccess`, `ErrorMessage`. Factory: `StakingOperationResult.Ok()`, `StakingOperationResult.Error(message)`.

### IComplianceVerifier

Interface for ZK compliance proof verification, allowing the execution layer to verify transaction compliance proofs without a direct dependency on the compliance assembly.

```csharp
public interface IComplianceVerifier
{
    ComplianceCheckResult VerifyProofs(ComplianceProof[] proofs,
        ProofRequirement[] requirements, long blockTimestamp);
    void ResetNullifiers();
}
```

### BasaltErrorCode

Categorized error codes: transaction validation (1xxx), block errors (2xxx), consensus (3xxx), execution (4xxx), storage (5xxx), network (6xxx), compliance (7xxx), staking (8xxx), internal (9xxx).

Staking error codes:
- `StakeBelowMinimum` (8001) -- Stake amount below minimum validator stake
- `ValidatorAlreadyRegistered` (8002) -- Validator address already in staking state
- `ValidatorNotRegistered` (8003) -- Validator address not found in staking state
- `StakingNotAvailable` (8004) -- Staking state not configured (no `IStakingState` provided)

### BasaltException

Exception type for unrecoverable errors, carries a `BasaltErrorCode`.

```csharp
throw new BasaltException(BasaltErrorCode.InternalError, "something broke");
```

## Dependencies

None.
