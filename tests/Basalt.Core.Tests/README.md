# Basalt.Core.Tests

Unit tests for the Basalt core types: `Hash256`, `Address`, `UInt256`, and codec roundtrip serialization. **122 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| Hash256 | 9 | Construction, hex roundtrip, equality, comparison, `IsZero`, `ToArray`, `WriteTo` |
| UInt256 | 20 | Arithmetic (`+`, `-`, `*`, `/`, `%`), bitwise ops, shifts, comparison, overflow, `Parse`, hex/decimal roundtrip, big-endian/little-endian serialization, `DivRem` |
| CodecRoundtrip | 10 | Serialization roundtrips for core types (Transaction, Block, BlockHeader, AccountState, Receipt) via BasaltWriter/Reader |
| Address | 5 | Construction, hex roundtrip, equality, zero detection, `IsSystemContract` |

**Total: 122 tests**

## Test Files

- `Hash256Tests.cs` -- Hash256 struct construction, equality, comparison, hex formatting, zero detection
- `UInt256Tests.cs` -- 256-bit unsigned integer arithmetic, parsing, serialization, boundary conditions
- `CodecRoundtripTests.cs` -- Binary serialization roundtrips for all core domain types
- `AddressTests.cs` -- Address construction, hex encoding, equality, system contract detection

## Running

```bash
dotnet test tests/Basalt.Core.Tests
```
