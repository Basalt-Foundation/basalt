# Source Generators & Analyzers Audit Report

## Executive Summary

The three Roslyn source generators (Contract dispatch, Codec serialization, JSON converters) and eight Roslyn analyzers are generally well-implemented, following incremental generator best practices with proper `IIncrementalGenerator` usage, value-equality caching, and clean code emission. However, the audit identified several issues: the FNV-1a selector scheme lacks collision detection (a critical gap for contract safety), the `GasEstimationAnalyzer` uses string-contains matching that produces false positives, the `AnalyzerHelper.IsInsideBasaltContract` check uses syntactic-only matching that can be bypassed, and the codec generator's variable-size `GetSerializedSize` uses arbitrary hardcoded estimates instead of computing actual lengths. No selector collisions exist among current contracts, but the absence of a compile-time guard is a significant risk as the contract ecosystem grows.

## Critical Issues

### C-01: No FNV-1a Selector Collision Detection
- **Location**: `src/generators/Basalt.Generators.Contracts/ContractGenerator.cs:263-270` (`ComputeSelector`)
- **Description**: The contract dispatch generator computes FNV-1a selectors for each method name and emits a switch statement, but **never checks for collisions**. If two methods in the same contract produce the same 4-byte FNV-1a hash, only the first case would match and the second method would be silently unreachable. The generator does not emit a compiler error or diagnostic in this scenario.
- **Impact**: A contract developer could unknowingly deploy a contract where one method shadows another. Funds or state could be irreversibly corrupted because the wrong method executes. FNV-1a's 32-bit space makes collisions increasingly likely as method counts grow (birthday problem: ~77,000 methods for a 50% collision probability, but targeted collisions are trivial to construct).
- **Recommendation**: After computing selectors for all dispatchable methods, check for duplicates and emit a `DiagnosticDescriptor` error via `spc.ReportDiagnostic()`. Example:
  ```csharp
  var selectorMap = new Dictionary<uint, string>();
  foreach (var method in dispatchable) {
      var sel = ComputeSelector(method.Name);
      if (selectorMap.TryGetValue(sel, out var existing))
          spc.ReportDiagnostic(Diagnostic.Create(..., $"Selector collision: '{method.Name}' and '{existing}' both map to 0x{sel:X8}"));
      selectorMap[sel] = method.Name;
  }
  ```
- **Severity**: Critical

### C-02: FNV-1a Truncates Non-ASCII Characters
- **Location**: `src/generators/Basalt.Generators.Contracts/ContractGenerator.cs:267` and `src/sdk/Basalt.Sdk.Contracts/SelectorHelper.cs:17`
- **Description**: The line `hash ^= (byte)c;` truncates `char` values above 0xFF to their low 8 bits. C# method names can contain Unicode characters (e.g., `Trąnsfer`). Two method names that differ only in high-codepoint characters could produce the same selector.
- **Impact**: Silently wrong dispatch for contracts using non-ASCII method names. While no current contracts use non-ASCII names, this is a latent correctness bug that violates the principle of least surprise.
- **Recommendation**: Either (a) reject method names with non-ASCII characters via a diagnostic, or (b) XOR the full 16-bit char value: `hash ^= c; hash *= 16777619;` (changing the hash scheme requires updating both the generator and `SelectorHelper` in lockstep and is a breaking change for existing deployed contracts).
- **Severity**: Critical (latent — no current exposure, but no guard either)

## High Severity

### H-01: `AnalyzerHelper.IsInsideBasaltContract` Uses Syntactic-Only Matching
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/AnalyzerHelper.cs:9-27`
- **Description**: The helper checks for `[BasaltContract]` or `[BasaltContractAttribute]` by comparing the attribute's string name. This is purely syntactic — it does not resolve the attribute via the semantic model. A user-defined attribute named `BasaltContract` in a different namespace (e.g., `MyLib.BasaltContractAttribute`) would trigger all 8 analyzers on non-contract code, creating false positives. Conversely, using a `using` alias (e.g., `using BC = Basalt.Sdk.Contracts.BasaltContractAttribute; [BC]`) would bypass all analyzers, creating false negatives.
- **Impact**: Analyzer protection can be silently bypassed with aliased or fully-qualified attribute usage, leaving dangerous patterns undetected in contracts.
- **Recommendation**: Use the semantic model to resolve the attribute symbol and compare against the fully-qualified name `Basalt.Sdk.Contracts.BasaltContractAttribute`. This requires changing the analyzers to pass `SemanticModel` to the helper, or switching to `context.RegisterSymbolAction` with `SymbolKind.NamedType` to filter at the type level.
- **Severity**: High

### H-02: `GasEstimationAnalyzer` Storage Operation Detection Uses Substring Matching
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/GasEstimationAnalyzer.cs:46-54`
- **Description**: The storage operation counter uses `text.Contains(".Get(")` / `text.Contains(".Set(")` / etc. This matches ANY method ending in `.Get(`, `.Set(`, `.Add(`, `.Remove(` — including `Dictionary.Add()`, `List.Remove()`, `HashSet.Add()`, and similar non-storage operations. The check also fails to count `StorageValue.Get()` vs `StorageMap.Get()` distinctly.
- **Impact**: Gas estimates are inflated by non-storage operations, providing misleading information to developers. More seriously, false counting could lead developers to over-optimize or ignore the estimate entirely.
- **Recommendation**: Use the semantic model to check that the receiver type is `StorageValue<T>`, `StorageMap<TKey,TValue>`, or `StorageList<T>` before counting as a storage operation. Alternatively, check for `ContractStorage` as the expression root.
- **Severity**: High

### H-03: `AotCompatibilityAnalyzer` Uses `Contains()` for Member Access Matching
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/AotCompatibilityAnalyzer.cs:63-75`
- **Description**: The check `fullName.Contains(banned)` where `fullName = memberAccess.ToString()` will match substrings in any position. For example, a user-defined class `MyFile` with a method `ReadAllText` would trigger BST008 because the string contains `File.ReadAllText`. Similarly, `TaskFile.Run()` would match `Task.Run`.
- **Impact**: False positives on legitimate code that happens to contain banned substrings, eroding developer trust in the analyzer.
- **Recommendation**: Use the semantic model to resolve the actual containing type and member name, or at minimum use a more precise string match (e.g., check that the expression before the dot exactly matches the banned type name).
- **Severity**: High

## Medium Severity

### M-01: Codec Generator `GetSerializedSize` Uses Arbitrary Estimates for Variable-Length Types
- **Location**: `src/generators/Basalt.Generators.Codec/CodecGenerator.cs:244-245`, `256-258`
- **Description**: For variable-length fields (`string`, `byte[]`), `GetSerializedSize()` emits `size += 64; // estimate for <Name>`. The value 64 is arbitrary and not based on the actual field data. This makes the method unreliable for callers who need accurate size information (e.g., pre-allocating buffers).
- **Impact**: Under-sized buffers if data exceeds 64 bytes (leading to resizing or truncation), or over-allocation for small data. The method name implies it returns the actual size, but it returns an approximation.
- **Recommendation**: Either (a) compute the actual size at runtime by examining the field value (e.g., `size += 4 + System.Text.Encoding.UTF8.GetByteCount(Name);` for strings, `size += 4 + (Data?.Length ?? 0);` for byte arrays), or (b) rename the method to `EstimateSerializedSize()` to clarify its approximate nature, or (c) add a XML doc comment clearly stating the estimate.
- **Severity**: Medium

### M-02: Codec Generator Does Not Handle `byte[]` ReadFrom Correctly
- **Location**: `src/generators/Basalt.Generators.Codec/CodecGenerator.cs:307`
- **Description**: The `GetReadMethod` returns `"ReadBytes().ToArray"` for `byte[]`, and the generated code is `result.Name = reader.ReadBytes().ToArray();`. However, `ReadBytes()` returns `ReadOnlySpan<byte>` on a `ref struct` reader — calling `.ToArray()` is correct here, but the method return string `"ReadBytes().ToArray"` omits the parentheses for `ToArray`. The generated code appends `()` via the template at line 210: `reader.{method}()` → `reader.ReadBytes().ToArray()`. This works because the `()` added at line 210 gets appended after `ToArray`, producing `reader.ReadBytes().ToArray()`. While this works, it relies on an implicit contract between `GetReadMethod`'s return value and the template — `ToArray` is part of the method name string, which is confusing.
- **Impact**: No runtime bug, but fragile code generation pattern that could break under refactoring.
- **Recommendation**: Return `("ReadBytes", true)` as a tuple and handle the `.ToArray()` suffix explicitly in the template.
- **Severity**: Medium (code quality)

### M-03: `DeterminismAnalyzer` Does Not Catch Fully-Qualified API Calls
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/DeterminismAnalyzer.cs:33-63`
- **Description**: The analyzer checks `expressionText == "DateTime"` and `expressionText == "DateTimeOffset"`, etc. This is purely syntactic and misses fully-qualified calls like `System.DateTime.Now`, `global::System.DateTime.Now`, or calls through type aliases. The same issue applies to `Guid.NewGuid` and `Environment.TickCount`.
- **Impact**: A developer can bypass the determinism check by using fully-qualified names, which undermines the safety guarantee the analyzer is supposed to provide. The test file even documents this limitation in a comment (line 189).
- **Recommendation**: Use the semantic model to resolve the expression to its type symbol and check against `System.DateTime`, `System.DateTimeOffset`, `System.Guid`, `System.Environment`.
- **Severity**: Medium

### M-04: `ReentrancyAnalyzer` Uses Positional Heuristic Without Control Flow Analysis
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/ReentrancyAnalyzer.cs:80-95`
- **Description**: The analyzer compares `SpanStart` positions to determine if a storage write occurs "after" an external call. This simple positional check doesn't account for control flow: a storage write in an early `if` branch that's mutually exclusive with a later external call in an `else` branch would be falsely flagged. Conversely, storage writes in called helper methods are missed entirely (no interprocedural analysis).
- **Impact**: False positives for branched code, and false negatives for storage writes in helper methods called after external calls.
- **Recommendation**: Document the limitations clearly. For a more robust solution, perform basic control-flow analysis using Roslyn's `ControlFlowGraph` API, or at minimum walk the AST to identify branching.
- **Severity**: Medium

### M-05: Contract Generator `default:` Case for Derived Contracts Calls `base.Dispatch` Without Checking Base Existence
- **Location**: `src/generators/Basalt.Generators.Contracts/ContractGenerator.cs:248-249`
- **Description**: When `info.HasContractBase` is true, the default case emits `return base.Dispatch(selector, calldata);`. This assumes the base class also has a `Dispatch` method generated. If the base class's `[BasaltContract]` attribute is removed or the base class is in a different assembly that wasn't processed by the generator, this will cause a compile error.
- **Impact**: Confusing compile errors in inheritance scenarios where the base class is not generator-processed.
- **Recommendation**: This is acceptable behavior (compile error is better than silent failure), but consider emitting a clearer comment or diagnostic when the base contract attribute is detected but may not have generated dispatch.
- **Severity**: Medium (low practical impact)

### M-06: `JsonGenerator` Per-Type `BasaltJsonOptions` Is Not Thread-Safe
- **Location**: `src/generators/Basalt.Generators.Json/JsonGenerator.cs:459-460`
- **Description**: The generated code uses lazy initialization without thread safety:
  ```csharp
  public static JsonSerializerOptions BasaltJsonOptions =>
      _basaltJsonOptions ??= CreateOptions();
  ```
  The `??=` operator is not atomic. In a multi-threaded scenario, two threads could both see `null` and create separate instances. While `JsonSerializerOptions` would still work (just wasting memory), this is a correctness concern because `JsonSerializerOptions` caches resolvers internally and multiple instances could cause subtle differences.
- **Impact**: Minor memory waste and potential inconsistency in heavily concurrent scenarios.
- **Recommendation**: Use `Lazy<JsonSerializerOptions>` or `Interlocked.CompareExchange` for thread-safe initialization.
- **Severity**: Medium (low practical impact in most blockchain node scenarios)

## Low Severity / Recommendations

### L-01: `OverflowAnalyzer` Only Detects Explicit `unchecked` Blocks
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/OverflowAnalyzer.cs:10-33`
- **Description**: The analyzer only flags `unchecked { }` blocks and `unchecked(expr)` expressions. In C# with the default project setting (`<CheckForOverflowUnderflow>false</CheckForOverflowUnderflow>`), all arithmetic is implicitly unchecked — meaning raw `a + b` on `int` types will overflow silently without the analyzer detecting it.
- **Impact**: The analyzer gives a false sense of security. Most overflow vulnerabilities come from implicit unchecked arithmetic, not explicit `unchecked` blocks.
- **Recommendation**: Consider checking the project's `<CheckForOverflowUnderflow>` setting and warning if it's not enabled, or expand the analyzer to flag all arithmetic operations on integer types in contracts that don't use `checked`.

### L-02: `StorageAccessAnalyzer` Is Very Narrow
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/StorageAccessAnalyzer.cs:10-40`
- **Description**: Only flags `ContractStorage.Read` and `ContractStorage.Write`. Does not flag `ContractStorage.Delete`, `ContractStorage.Exists`, or any other raw storage operations.
- **Impact**: Incomplete coverage of raw storage access patterns.
- **Recommendation**: Expand the check to cover all `ContractStorage.*` member accesses, or flag any member access on `ContractStorage` regardless of method name.

### L-03: `NoReflectionAnalyzer` Has Redundant Logic
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/NoReflectionAnalyzer.cs:25-59`
- **Description**: The `AnalyzeUsingDirective` method has a confusing code structure: it calls `IsInsideBasaltContract()` (line 29) and then ignores the result (lines 30-38 are a comment block), then calls `IsInsideBasaltContract()` again (line 47). The first call is wasted.
- **Impact**: No runtime bug, but confusing code that suggests incomplete refactoring.
- **Recommendation**: Clean up the dead code path.

### L-04: `ContractGenerator` Does Not Validate Method Visibility
- **Location**: `src/generators/Basalt.Generators.Contracts/ContractGenerator.cs:43-70`
- **Description**: The generator processes all methods with `[BasaltEntrypoint]` or `[BasaltView]` regardless of accessibility. A `private` method annotated with `[BasaltEntrypoint]` would be included in the dispatch table, generating code that calls `this.PrivateMethod()` — which would fail to compile because the generated partial class is in a different file but the same class, so it CAN access private members. This is technically correct but semantically surprising.
- **Impact**: Developers might accidentally expose private methods via contract dispatch without realizing it.
- **Recommendation**: Consider emitting a warning diagnostic when `[BasaltEntrypoint]` or `[BasaltView]` is applied to a non-public method.

### L-05: `ContractGenerator` Return Buffer Size Could Be Insufficient
- **Location**: `src/generators/Basalt.Generators.Contracts/ContractGenerator.cs:238`
- **Description**: For variable-length return types (`string`, `byte[]`), a fixed 4096-byte buffer is allocated: `var _buf = new byte[4096];`. If the return value exceeds 4096 bytes, the `BasaltWriter` will write past the buffer, causing an `IndexOutOfRangeException` at runtime.
- **Impact**: Runtime crash for contract methods returning large strings or byte arrays.
- **Recommendation**: Use `ArrayPool<byte>` with dynamic resizing, or compute the required size first and allocate accordingly. Alternatively, use `MemoryStream` or `ArrayBufferWriter<byte>` for growable output.

### L-06: No Inherited Member Serialization in Codec Generator
- **Location**: `src/generators/Basalt.Generators.Codec/CodecGenerator.cs:96`
- **Description**: `typeSymbol.GetMembers()` only returns directly declared members, not inherited ones. A class `B : A` with `[BasaltSerializable]` on both would only serialize B's own members, missing A's members.
- **Impact**: Incomplete serialization for inherited types.
- **Recommendation**: Use `typeSymbol.GetMembers()` combined with walking `BaseType` to include inherited public properties/fields, or document that each type in the hierarchy must have its own `[BasaltSerializable]` attribute.

### L-07: `NoDynamicAnalyzer` Has Redundant Type Checks
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/NoDynamicAnalyzer.cs:68-92`
- **Description**: The check for `ExpandoObject`/`DynamicObject` tests both `ITypeSymbol` and `INamedTypeSymbol` in sequence (lines 68-92), but `INamedTypeSymbol` inherits from `ITypeSymbol`, so the second branch (`else if symbol is INamedTypeSymbol`) would never be reached if the first `is ITypeSymbol` check matches.
- **Impact**: Dead code path — the `INamedTypeSymbol` branch is unreachable.
- **Recommendation**: Remove the redundant `else if` branch.

### L-08: `GasEstimationAnalyzer` Does Not Account for Nested Loops
- **Location**: `src/sdk/Basalt.Sdk.Analyzers/GasEstimationAnalyzer.cs:42-43`
- **Description**: Each loop contributes a flat `10,000` gas regardless of nesting. A `for` inside a `for` should contribute multiplicatively (or at least be flagged as higher-cost), not additively.
- **Impact**: Under-estimated gas for nested loop patterns.
- **Recommendation**: Detect loop nesting depth and apply multipliers, or at minimum add a note in the diagnostic message when nested loops are detected.

## Test Coverage Gaps

### T-01: No Test for Selector Collision Detection
- There is no test verifying behavior when two methods in the same contract produce the same FNV-1a hash (expected: error diagnostic).
- Since collision detection doesn't exist yet (C-01), this is expected.

### T-02: No Test for Inheritance-Based Dispatch (`new` modifier)
- The test file has no test for a derived contract class inheriting from a base contract class. This is a critical scenario (WBSLT inherits from BST20Token) that should verify:
  - The `new` modifier is emitted on `Dispatch` and `ContractAbi`
  - The `default:` case calls `base.Dispatch()`
  - Base class methods are accessible through the derived dispatch

### T-03: No Test for Unsupported Parameter Types Being Skipped
- No test verifies that a method with an unsupported parameter type (e.g., `byte[][]`, `DIDDocument?`) is correctly excluded from dispatch generation.

### T-04: No Test for Contract with No Dispatchable Methods
- No test covers a `[BasaltContract]` class where all methods have unsupported types, resulting in an empty dispatch (no switch cases).

### T-05: No Test for `BasaltConstructor` Attribute
- The generator defines `BasaltConstructorAttribute` as a constant (line 16) but the tests never exercise constructor-attributed methods. It's unclear if this attribute has any effect on code generation.

### T-06: Analyzer Tests Missing Edge Cases
- No test for `System.DateTime.Now` (fully-qualified) in BST003
- No test for aliased attributes (`using BC = ...BasaltContractAttribute; [BC]`) for any analyzer
- No test for BST004 with reentrancy across helper methods
- No test for BST005 with implicit unchecked arithmetic
- No test for BST007 with nested loops or storage operations via semantic model
- No test for BST008 with `Socket` or `WebClient` constructor
- No test for BST006 with `ContractStorage.Delete`

### T-07: No Integration Test Between Generator and Runtime Dispatch
- No test verifies that the selectors emitted by `ContractGenerator.ComputeSelector()` match the selectors computed by `SelectorHelper.ComputeSelectorBytes()` at runtime. These two implementations must be kept in sync. A test that computes both for the same method name and asserts equality would prevent drift.

## Positive Findings

### P-01: Correct Incremental Generator Architecture
All three generators use `IIncrementalGenerator` (not the deprecated `ISourceGenerator`), `ForAttributeWithMetadataName` for efficient filtering, and `EquatableArray<T>` for proper incremental caching. This ensures good IDE performance.

### P-02: FNV-1a Implementation Matches Between Generator and Runtime
The `ComputeSelector` method in `ContractGenerator.cs:263-270` is byte-for-byte identical to `SelectorHelper.ComputeSelector` in `SelectorHelper.cs:12-20`. Both use the same FNV-1a offset basis (2166136261) and prime (16777619) with the same `(byte)c` cast.

### P-03: Robust Event Collection in Contract Generator
The `CollectEvents` and `CollectReferencedEvents` methods (lines 88-142) properly walk base types, interfaces, and containing namespaces to collect all event types. This ensures events declared in base classes or alongside interfaces are included in the ABI.

### P-04: Correct `new` Modifier Handling for Inheritance
The generator correctly detects base classes with `[BasaltContract]` and emits the `new` modifier on `Dispatch` and `ContractAbi` members, plus delegates to `base.Dispatch()` in the default case. This matches the documented design for derived types like WBSLT.

### P-05: Comprehensive Type Support in Codec Generator
The codec generator supports all 15 Basalt types including BLS types (`BlsSignature`, `BlsPublicKey`) with correct byte sizes (96, 48 respectively). Type normalization handles both `global::` prefixed and CLR type names correctly.

### P-06: Thorough JSON Converter Implementation
All 7 JSON converters handle `0x` prefix stripping, size validation, lowercase hex output, and null checking consistently. The `UInt256Converter` uses decimal string representation (not hex), which is appropriate for large numeric values.

### P-07: Good Analyzer Design Patterns
All analyzers correctly call `ConfigureGeneratedCodeAnalysis(None)` and `EnableConcurrentExecution()`. They correctly scope to contract code via `AnalyzerHelper.IsInsideBasaltContract()` (despite the syntactic limitation noted in H-01).

### P-08: Comprehensive Test Coverage for Analyzers
The test suite covers all 8 analyzer rules with both positive (diagnostic expected) and negative (no diagnostic) cases. The `GeneratorTests` class covers all three generators with edge cases like empty structs, multiple types, field exclusion, and all primitive types. Total: 45 generator tests + 32 analyzer tests = 77 tests for this subsystem.

### P-09: No Selector Collisions in Current Codebase
Analysis of all 137 unique method names across all SDK contracts confirmed zero FNV-1a hash collisions. The minimum delta between any two hashes is comfortable, indicating the current method namespace is well-distributed.

### P-10: Clean AOT Compatibility
All generated code avoids reflection, dynamic types, and other AOT-incompatible patterns. The generators produce pure switch-based dispatch, direct method calls, and static type references.
