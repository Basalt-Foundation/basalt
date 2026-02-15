# Basalt.Generators.Contracts

Incremental source generator for smart contract ABI dispatch. Generates method dispatch tables, parameter deserialization, return value serialization, and ABI metadata for classes annotated with `[BasaltContract]`.

## Trigger Attributes

| Attribute | Target | Purpose |
|-----------|--------|---------|
| `[BasaltContract]` | Class | Marks a class as a Basalt smart contract. Triggers code generation. |
| `[BasaltEntrypoint]` | Method | Marks a state-mutating entry point. Mutability: `"mutable"`. |
| `[BasaltView]` | Method | Marks a read-only view method. Mutability: `"view"`. |
| `[BasaltConstructor]` | Method | Declared in the SDK but not yet wired into dispatch generation. Reserved for future use. |
| `[BasaltEvent]` | Nested class/struct | Declares an event type that can be emitted by the contract. |
| `[Indexed]` | Property | Marks an event property as indexed (included in event topics). |

All attributes are in the `Basalt.Sdk.Contracts` namespace.

## Usage

```csharp
[BasaltContract]
public partial class MyToken
{
    [BasaltEntrypoint]
    public void Transfer(Address to, UInt256 amount) { /* ... */ }

    [BasaltView]
    public UInt256 BalanceOf(Address account) { /* ... */ }

    [BasaltEvent]
    public class TransferEvent
    {
        [Indexed] public Address From { get; set; }
        [Indexed] public Address To { get; set; }
        public UInt256 Amount { get; set; }
    }
}
```

## Generated Code

For each `[BasaltContract]` class, the generator emits `{Namespace}.{TypeName}.g.cs` containing a partial class extension with:

### Dispatch Method

```csharp
public byte[] Dispatch(byte[] selector, byte[] calldata)
```

Reads the first 4 bytes of `selector` as a little-endian `uint`, then dispatches to the matching contract method via a `switch` statement. Parameters are deserialized from `calldata` using `BasaltReader`. Return values are serialized into a `byte[]` using `BasaltWriter`. Void methods return `Array.Empty<byte>()`. An `InvalidOperationException` is thrown for unknown selectors or selectors shorter than 4 bytes.

### Selector Computation

Method selectors are computed using FNV-1a hash of the method name:

```
hash = 2166136261
for each char c in methodName:
    hash ^= (byte)c
    hash *= 16777619
```

The resulting 4-byte value is stored in little-endian order. Selectors appear as `0x{hash:X8}` in the ABI.

### ABI Metadata

A `const string ContractAbi` field is generated containing a JSON object with:

- **`methods`** -- array of `{ name, selector, mutability, params: [{ name, type }], returns }`.
- **`events`** -- array of `{ name, properties: [{ name, type, indexed }] }`.

### Supported Parameter / Return Types

| Type | Read Expression | Write Expression | Buffer Size |
|------|----------------|------------------|-------------|
| `byte` | `reader.ReadByte()` | `writer.WriteByte(v)` | 1 |
| `ushort` | `reader.ReadUInt16()` | `writer.WriteUInt16(v)` | 2 |
| `uint` | `reader.ReadUInt32()` | `writer.WriteUInt32(v)` | 4 |
| `int` | `reader.ReadInt32()` | `writer.WriteInt32(v)` | 4 |
| `ulong` | `reader.ReadUInt64()` | `writer.WriteUInt64(v)` | 8 |
| `long` | `reader.ReadInt64()` | `writer.WriteInt64(v)` | 8 |
| `bool` | `reader.ReadBool()` | `writer.WriteBool(v)` | 1 |
| `string` | `reader.ReadString()` | `writer.WriteString(v)` | variable (4096 buffer) |
| `byte[]` | `reader.ReadBytes().ToArray()` | `writer.WriteBytes(v)` | variable (4096 buffer) |
| `Hash256` | `reader.ReadHash256()` | `writer.WriteHash256(v)` | 32 |
| `Address` | `reader.ReadAddress()` | `writer.WriteAddress(v)` | 20 |
| `UInt256` | `reader.ReadUInt256()` | `writer.WriteUInt256(v)` | 32 |

Unsupported parameter or return types throw `NotSupportedException` at runtime.

## Event Discovery

Events are collected from three sources:

1. **Nested types** -- classes/structs nested directly inside the `[BasaltContract]` class that have `[BasaltEvent]`.
2. **Base type hierarchy** -- walks the base class chain and collects `[BasaltEvent]`-annotated nested types from each ancestor.
3. **Interfaces and namespaces** -- for each interface the contract implements, scans the interface's containing type, containing namespace, and the contract's own containing namespace for `[BasaltEvent]` types. This discovers events declared alongside interfaces (e.g. a `TransferEvent` next to an `IBST20` interface). A `HashSet<string>` prevents duplicate entries.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp` | Roslyn compiler APIs |
| `Microsoft.CodeAnalysis.Analyzers` | Analyzer development helpers |

Target framework: `netstandard2.0` (required for source generators).
