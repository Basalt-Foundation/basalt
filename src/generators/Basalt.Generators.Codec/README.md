# Basalt.Generators.Codec

Incremental source generator for deterministic binary serialization. Generates `WriteTo`, `ReadFrom`, and `GetSerializedSize` methods for types annotated with `[BasaltSerializable]`.

## Trigger

Annotate any `partial struct` or `partial class` with `[BasaltSerializable]` (full name: `Basalt.Sdk.Contracts.BasaltSerializableAttribute`). The generator discovers all public instance fields and properties (with both getter and setter) in declaration order.

```csharp
[BasaltSerializable]
public partial struct MyStruct
{
    public ulong Id { get; set; }
    public Address Owner { get; set; }
    public UInt256 Amount { get; set; }
}
```

## Generated Methods

For each annotated type, the generator emits a partial type extension in `{TypeName}.BasaltCodec.g.cs` containing:

- **`void WriteTo(ref BasaltWriter writer)`** -- Serializes each member in declaration order using the corresponding `BasaltWriter.Write*` method.
- **`static T ReadFrom(ref BasaltReader reader)`** -- Deserializes a new instance by reading each member in order via the corresponding `BasaltReader.Read*` method. Structs use `default(T)`, classes use `new T()`.
- **`int GetSerializedSize()`** -- Returns the exact byte count for fixed-size types. Variable-size types (`string`, `byte[]`) use a 64-byte estimate.

## Supported Types

| Type | Write Method | Read Method | Fixed Size |
|------|-------------|-------------|------------|
| `byte` | `WriteByte` | `ReadByte` | 1 |
| `ushort` | `WriteUInt16` | `ReadUInt16` | 2 |
| `uint` | `WriteUInt32` | `ReadUInt32` | 4 |
| `int` | `WriteInt32` | `ReadInt32` | 4 |
| `ulong` | `WriteUInt64` | `ReadUInt64` | 8 |
| `long` | `WriteInt64` | `ReadInt64` | 8 |
| `bool` | `WriteBool` | `ReadBool` | 1 |
| `string` | `WriteString` | `ReadString` | variable |
| `byte[]` | `WriteBytes` | `ReadBytes().ToArray` | variable |
| `Hash256` | `WriteHash256` | `ReadHash256` | 32 |
| `Address` | `WriteAddress` | `ReadAddress` | 20 |
| `UInt256` | `WriteUInt256` | `ReadUInt256` | 32 |
| `Signature` | `WriteSignature` | `ReadSignature` | 64 |
| `PublicKey` | `WritePublicKey` | `ReadPublicKey` | 32 |
| `BlsSignature` | `WriteBlsSignature` | `ReadBlsSignature` | 96 |
| `BlsPublicKey` | `WriteBlsPublicKey` | `ReadBlsPublicKey` | 48 |

Unsupported field types are skipped with a comment in the generated code.

## Architecture

`CodecGenerator` implements `IIncrementalGenerator`. It uses `ForAttributeWithMetadataName` to locate annotated types, extracts member metadata into an equatable model (`TypeToGenerate` with `EquatableArray<MemberInfo>`), and emits source only when the model changes. Fully-qualified Roslyn type names (e.g. `global::System.UInt64`) are normalized to C# keyword forms before mapping to read/write methods.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp` | Roslyn compiler APIs |
| `Microsoft.CodeAnalysis.Analyzers` | Analyzer development helpers |

Target framework: `netstandard2.0` (required for source generators).
