# Basalt.Sdk.Analyzers

Roslyn analyzers for Basalt smart contract safety. Catches common errors at build time and ensures contracts are compatible with the BasaltVM sandbox and Native AOT compilation. All analyzers are scoped to classes annotated with `[BasaltContract]`.

## Analyzers

| Rule | Severity | Title | What it Detects |
|------|----------|-------|-----------------|
| BST001 | Error | Reflection is not allowed in Basalt contracts | `using System.Reflection`, `Type.GetType()`, `MethodInfo.Invoke()`, `Type.GetMethod()`, `Type.GetProperty()`, `Type.GetField()`, `Assembly.GetTypes()`. Checks both invocations inside `[BasaltContract]` classes and top-level using directives in files containing a contract class. |
| BST002 | Error | Dynamic types are not allowed in Basalt contracts | Usage of the `dynamic` keyword, `ExpandoObject`, and `DynamicObject` -- both as type references and in `new` expressions. Verified through the semantic model to avoid false positives. |
| BST003 | Warning | Non-deterministic API usage detected | `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, `Guid.NewGuid`, `Environment.TickCount`, `Environment.TickCount64`, `new Random()`, and floating-point type declarations (`float`, `double`, `decimal`). |
| BST004 | Warning | Potential reentrancy vulnerability | Storage write calls (`.Set()`, `.Add()`, `.Remove()`) that occur after an external call (`Context.CallContract`, `.Call`). Recommends the checks-effects-interactions pattern. |
| BST005 | Warning | Unchecked arithmetic may overflow | `unchecked` expressions and `unchecked` statements inside contract code. |
| BST006 | Warning | Direct ContractStorage access detected | Raw `ContractStorage.Read` and `ContractStorage.Write` calls. Recommends using `StorageValue`/`StorageMap` wrappers instead. |
| BST007 | Info | Estimated gas cost | Reports a gas estimate on methods marked `[BasaltEntrypoint]` or `[BasaltView]`. Sums base gas (21,000) + loops (10,000 each) + storage operations (5,000 each) + external calls (25,000 each). |
| BST008 | Error | API incompatible with Basalt AOT sandbox | Banned member accesses: `Type.MakeGenericType`, `Activator.CreateInstance`, `Assembly.Load`/`LoadFrom`, `Task.Run`, `Parallel.For`/`ForEach`/`Invoke`, file I/O (`File.ReadAllText`/`WriteAllText`/`ReadAllBytes`/`WriteAllBytes`/`Exists`/`Delete`/`Open`, `Directory.Exists`/`CreateDirectory`). Banned constructor types: `Thread`, `HttpClient`, `TcpClient`, `Socket`, `WebClient`, `FileStream`, `StreamReader`, `StreamWriter`. |

## Diagnostic Categories

- **Basalt.Compatibility** -- BST001, BST002, BST008 (AOT and sandbox compatibility).
- **Basalt.Determinism** -- BST003 (non-deterministic APIs).
- **Basalt.Safety** -- BST004, BST005, BST006 (reentrancy, overflow, raw storage).
- **Basalt.Performance** -- BST007 (gas estimation).

## AnalyzerHelper

The `AnalyzerHelper` utility class provides `IsInsideBasaltContract(SyntaxNode)`, which walks up the syntax tree to find the nearest `ClassDeclarationSyntax` and checks for the `[BasaltContract]` or `[BasaltContractAttribute]` attribute. All analyzers use this to scope their diagnostics to contract code only.

## DiagnosticIds

All `DiagnosticDescriptor` instances are centralized in the `DiagnosticIds` static class, ensuring consistent rule IDs, titles, message formats, categories, and severities across all analyzers.

## Usage

The analyzers are automatically applied when referencing `Basalt.Sdk.Contracts`:

```xml
<ProjectReference Include="Basalt.Sdk.Analyzers"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

## Dependencies

| Package | Purpose |
|---------|---------|
| `Microsoft.CodeAnalysis.CSharp` | Roslyn compiler APIs |
| `Microsoft.CodeAnalysis.Analyzers` | Analyzer development helpers |

Target framework: `netstandard2.0` (required for Roslyn analyzers).
