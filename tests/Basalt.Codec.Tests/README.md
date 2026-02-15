# Basalt.Codec.Tests

Unit tests for the Basalt binary codec: BasaltWriter/BasaltReader roundtrips for all primitive types, core domain types, VarInt encoding, and the BasaltSerializer utility. **74 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| Primitive roundtrips | 14 | Byte, UInt16, UInt32, Int32, UInt64, Int64 write/read with boundary value theories |
| VarInt encoding | 10 | Variable-length integer encoding: small/medium/large values, byte count verification, boundary values (0, 127, 128, 16383, 16384, MaxValue) |
| Core type roundtrips | 14 | Hash256, Address, UInt256, Signature, PublicKey, BlsSignature, BlsPublicKey write/read with zero and random values |
| Buffer overflow/underflow | 11 | Writer capacity checks (too small buffer for each type), Reader end-of-buffer detection |
| Position tracking | 5 | Writer.Position, Writer.Remaining, Writer.WrittenSpan, Reader.Position, Reader.Remaining, Reader.IsAtEnd |
| String roundtrips | 4 | Empty, ASCII, Unicode, and long string serialization |
| Bytes roundtrips | 4 | Empty, small, large byte arrays, raw bytes without length prefix |
| Bool roundtrips | 2 | True/false write/read |
| Multi-type sequences | 3 | All types sequentially, mixed primitives and strings, all crypto types |
| Edge cases | 6 | Empty raw bytes, exact-fit buffer, VarInt length prefix tracking, UInt256 large values, string/bytes position includes length prefix |
| BasaltSerializer | 1 | IBasaltSerializable serialize/deserialize roundtrip |

**Total: 74 tests**

## Test Files

- `BasaltWriterReaderTests.cs` -- Comprehensive write/read roundtrip tests for all supported types, VarInt encoding specifics, position tracking, buffer overflow/underflow detection, multi-type sequences, and BasaltSerializer integration

## Running

```bash
dotnet test tests/Basalt.Codec.Tests
```
