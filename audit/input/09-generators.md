# Basalt Security & Quality Audit — Source Generators & Analyzers

## Scope

Audit the Roslyn source generators that emit AOT-safe dispatch, serialization, and JSON code, plus the Roslyn analyzers that enforce smart contract safety rules:

| Project | Path | Description |
|---|---|---|
| `Basalt.Generators.Contracts` | `src/generators/Basalt.Generators.Contracts/` | Contract dispatch source generator — emits `IDispatchable.Dispatch()` |
| `Basalt.Generators.Codec` | `src/generators/Basalt.Generators.Codec/` | Binary codec source generator — emits `WriteTo`/`ReadFrom`/`GetSerializedSize` |
| `Basalt.Generators.Json` | `src/generators/Basalt.Generators.Json/` | JSON converter source generator — emits AOT-safe `System.Text.Json` converters |
| `Basalt.Sdk.Analyzers` | `src/sdk/Basalt.Sdk.Analyzers/` | Roslyn analyzers for smart contract safety (8 diagnostic rules) |

Corresponding test project: `tests/Basalt.Sdk.Analyzers.Tests/`

---

## Files to Audit

### Basalt.Generators.Contracts
- `ContractGenerator.cs` (~605 lines) — `ContractGenerator : IIncrementalGenerator`, inner types: `ContractInfo`, `MethodInfo`, `ParameterInfo`, `EventInfo`, `EventPropertyInfo`

### Basalt.Generators.Codec
- `CodecGenerator.cs` (~415 lines) — `CodecGenerator : IIncrementalGenerator`, inner types: `MemberInfo`, `TypeToGenerate`, `EquatableArray<T>`

### Basalt.Generators.Json
- `JsonGenerator.cs` (~525 lines) — `JsonGenerator : IIncrementalGenerator`, inner types: `AnnotatedTypeInfo`, `EquatableArray<T>`

### Basalt.Sdk.Analyzers
- `AotCompatibilityAnalyzer.cs` (~98 lines) — BSA001: detects AOT-incompatible patterns
- `DeterminismAnalyzer.cs` (~114 lines) — BSA002: detects non-deterministic operations
- `GasEstimationAnalyzer.cs` (~86 lines) — BSA003: estimates gas cost
- `NoDynamicAnalyzer.cs` (~117 lines) — BSA004: blocks `dynamic` keyword
- `NoReflectionAnalyzer.cs` (~159 lines) — BSA005: blocks reflection usage
- `OverflowAnalyzer.cs` (~33 lines) — BSA006: detects potential overflow
- `ReentrancyAnalyzer.cs` (~106 lines) — BSA007: detects reentrancy risks
- `StorageAccessAnalyzer.cs` (~40 lines) — BSA008: validates storage access patterns
- `AnalyzerHelper.cs` (~27 lines) — Shared helper utilities
- `DiagnosticIds.cs` (~71 lines) — Diagnostic ID constants and messages

---

## Audit Objectives

### 1. Contract Dispatch Generator Correctness (CRITICAL)
- Verify that `ContractGenerator` correctly emits `Dispatch(byte[] selector, byte[] args)` methods that match the FNV-1a selector scheme.
- Check that `SelectorHelper.ComputeSelectorBytes(methodName)` produces correct little-endian uint32 FNV-1a hashes.
- Verify that selector collisions are detected or handled — two methods with the same FNV-1a hash would be silently broken.
- Check that method parameter serialization/deserialization is correct for all supported types:
  - `byte`, `ushort`, `uint`, `ulong`, `UInt256`, `byte[]`, `string`, `Address`, `bool`
- Verify that unsupported parameter types (`byte[][]`, `ulong[]`, `DIDDocument?`) are correctly skipped (not dispatchable).
- Check that the `new` modifier for derived types (e.g., `WBSLT : BST20Token`) correctly shadows base dispatch.
- Verify that `[BasaltEntrypoint]` vs `[BasaltView]` attributes are respected (views should not mutate state).
- Check that event emission code is correctly generated for `[BasaltEvent]` types.
- Verify generated code compiles without warnings under AOT.

### 2. Codec Generator Correctness
- Verify that `CodecGenerator` correctly emits `WriteTo(ref BasaltWriter)` and `ReadFrom(ref BasaltReader)`.
- Check that `GetSerializedSize()` returns accurate sizes for all field types.
- Verify field ordering: fields must be serialized in a deterministic order (declaration order or explicit ordering).
- Check that optional/nullable fields are correctly handled.
- Verify that `ref struct` constraints on `BasaltWriter`/`BasaltReader` are respected in generated code.
- Check that generated codec code is compatible with existing manually-written codec code.

### 3. JSON Generator Correctness
- Verify converters for `Hash256`, `Address`, `UInt256`, `Signature`, `PublicKey`, `BlsSignature`, `BlsPublicKey` are correct.
- Check hex encoding/decoding: "0x" prefix handling, case sensitivity, padding.
- Verify that generated converters are registered in `JsonSerializerContext` for AOT.
- Check that large `UInt256` values serialize/deserialize correctly (string representation).

### 4. Analyzer Coverage & Accuracy
- **BSA001 (AOT)**: Verify it catches reflection, dynamic codegen, and `Assembly.Load`. Check for false positives on legitimate patterns.
- **BSA002 (Determinism)**: Verify it catches `DateTime.Now`, `Random`, `Guid.NewGuid`, `Environment.*`, file I/O. Check that it doesn't flag safe clock usage in non-contract code.
- **BSA003 (Gas)**: Verify gas estimation logic. Check that it doesn't overcount or undercount.
- **BSA004 (Dynamic)**: Verify it catches all `dynamic` usage.
- **BSA005 (Reflection)**: Verify it catches `typeof(T).GetMethod()`, `Activator.CreateInstance`, etc.
- **BSA006 (Overflow)**: Verify it detects unchecked arithmetic in contracts.
- **BSA007 (Reentrancy)**: Verify it detects cross-contract calls followed by state modifications (checks-effects-interactions).
- **BSA008 (Storage)**: Verify storage access pattern validation.
- For ALL analyzers: check for false negatives (dangerous patterns that slip through).

### 5. Incremental Generator Performance
- Verify that all three generators use `IIncrementalGenerator` (not the older `ISourceGenerator`) for IDE performance.
- Check that the generators correctly implement caching and avoid regeneration when source hasn't changed.
- Verify `EquatableArray<T>` is correctly implemented for incremental pipeline equality.

### 6. Generator Robustness
- Check that generators handle malformed input gracefully (missing attributes, invalid types, partial compilation errors).
- Verify generators emit `#pragma warning disable` where needed and don't suppress legitimate warnings.
- Check that generated code includes sufficient comments/regions for debuggability.
- Verify generators work correctly with nullable reference types enabled.

### 7. Security Implications
- Verify that the contract dispatch generator cannot be tricked into generating unsafe code via crafted method names or parameter types.
- Check that the codec generator doesn't create buffer overflow opportunities in generated serialization code.
- Verify that analyzer rules are sufficient to prevent common smart contract vulnerabilities.

### 8. Test Coverage
- Review `tests/Basalt.Sdk.Analyzers.Tests/` for:
  - Generator output verification (snapshot tests or compilation tests)
  - Analyzer diagnostic verification (code that should trigger vs. code that shouldn't)
  - Edge cases: empty contracts, contracts with no methods, contracts with only views
  - Inheritance scenarios (base class + derived class dispatch)
  - All 8 analyzer rules with positive and negative test cases

---

## Key Context

- All generators target `netstandard2.0` (Roslyn requirement).
- Generators are referenced as `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`.
- Two selector schemes in the runtime: BLAKE3 (built-in VM methods), FNV-1a (SDK contracts via this generator).
- Magic bytes `[0xBA, 0x5A]` prefix SDK contract manifests: `[magic][2B typeId BE][ctor args]`.
- Source generator emits `IDispatchable.Dispatch()` on partial classes.
- Generator skips methods with unsupported types — only dispatchable types are serialized.
- NuGet: `Microsoft.CodeAnalysis.CSharp 4.12.0`, `Microsoft.CodeAnalysis.Analyzers 3.11.0`.

---

## Output Format

Write your findings to `audit/output/09-generators.md` with the following structure:

```markdown
# Source Generators & Analyzers Audit Report

## Executive Summary
[2-3 sentence overview of findings]

## Critical Issues
[Incorrect dispatch, selector collisions, security rule gaps]

## High Severity
[Significant correctness or security issues]

## Medium Severity
[Issues to address]

## Low Severity / Recommendations
[Code quality, performance, best practices]

## Test Coverage Gaps
[Untested scenarios]

## Positive Findings
[Well-implemented patterns]
```

For each finding, include:
1. **Location**: File path and line number(s)
2. **Description**: What the issue is
3. **Impact**: What could go wrong
4. **Recommendation**: How to fix it
5. **Severity**: Critical / High / Medium / Low
