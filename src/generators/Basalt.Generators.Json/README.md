# Basalt.Generators.Json

Incremental source generator for AOT-compatible JSON serialization. Generates `System.Text.Json` converters for all Basalt blockchain primitive types and wires them into annotated types via a cached `JsonSerializerOptions` property.

## Trigger

Annotate any `partial` type with `[BasaltJsonSerializable]` (full name: `Basalt.Sdk.Contracts.BasaltJsonSerializableAttribute`). The generator will emit two outputs:

1. A shared `BasaltJsonConverters` class with all 7 converters.
2. A per-type partial extension adding a `BasaltJsonOptions` property.

```csharp
[BasaltJsonSerializable]
public partial class StatusResponse
{
    public Hash256 BlockHash { get; set; }
    public UInt256 Balance { get; set; }
}

// Generated: StatusResponse.BasaltJsonOptions returns pre-configured JsonSerializerOptions
var json = JsonSerializer.Serialize(response, StatusResponse.BasaltJsonOptions);
```

## Generated Converters

The `BasaltJsonConverters` class (emitted in `BasaltJsonConverters.g.cs`, namespace `Basalt.Generators.Json`) contains 7 nested converter classes:

| Converter | Basalt Type | JSON Format |
|-----------|-------------|-------------|
| `Hash256Converter` | `Hash256` (32 bytes) | `"0x"` + 64 hex chars |
| `AddressConverter` | `Address` (20 bytes) | `"0x"` + 40 hex chars |
| `UInt256Converter` | `UInt256` (256-bit) | Decimal string (e.g. `"1000000000000000000"`) |
| `SignatureConverter` | `Signature` (64 bytes) | `"0x"` + 128 hex chars |
| `PublicKeyConverter` | `PublicKey` (32 bytes) | `"0x"` + 64 hex chars |
| `BlsSignatureConverter` | `BlsSignature` (96 bytes) | `"0x"` + 192 hex chars |
| `BlsPublicKeyConverter` | `BlsPublicKey` (48 bytes) | `"0x"` + 96 hex chars |

All converters use `ToHexString()` / `FromHexString()` for Basalt types that natively support it (`Hash256`, `Address`), and `Convert.ToHexString` / `Convert.FromHexString` with `0x` prefix handling for raw-byte types (`Signature`, `PublicKey`, `BlsSignature`, `BlsPublicKey`). `UInt256` uses `ToString()` / `Parse()` for decimal representation.

### Static Helpers

- **`BasaltJsonConverters.CreateOptions()`** -- Returns a new `JsonSerializerOptions` with all 7 converters registered.
- **`BasaltJsonConverters.AddConverters(JsonSerializerOptions)`** -- Adds all 7 converters to an existing options instance.

## Per-Type Output

For each annotated type, the generator emits `{FullyQualifiedName}.JsonOptions.g.cs` containing a partial type extension with:

```csharp
private static JsonSerializerOptions? _basaltJsonOptions;

public static JsonSerializerOptions BasaltJsonOptions =>
    _basaltJsonOptions ??= BasaltJsonConverters.CreateOptions();
```

The generator handles structs, classes, records, record structs, and nested types. The `BasaltJsonOptions` property is lazily initialized and cached.

## Architecture

`JsonGenerator` implements `IIncrementalGenerator`. It collects all types annotated with `[BasaltJsonSerializable]` via `ForAttributeWithMetadataName`, extracts minimal type metadata into an equatable `AnnotatedTypeInfo` struct (with `EquatableArray<string>` for containing types), and emits source only when the model changes. The converter class is emitted once when at least one annotated type is found.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp` | Roslyn compiler APIs |
| `Microsoft.CodeAnalysis.Analyzers` | Analyzer development helpers |

Target framework: `netstandard2.0` (required for source generators).
