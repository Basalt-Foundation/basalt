# Basalt.Sdk.Analyzers.Tests

Unit tests for the Basalt Roslyn analyzers and source generators using the Microsoft.CodeAnalysis.Testing framework. **114 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| Source Generators | 54 | Contract boilerplate generation, storage field detection, entry point generation, event type generation, ABI export, error handling for malformed contracts |
| Analyzers | 60 | BST001-BST012 diagnostic rules: forbidden API usage, missing attributes, invalid storage types, contract structure validation, unsafe operations, unchecked returns (BST009), storage mutation ordering (BST010), non-deterministic collections (BST011), missing policy enforcement (BST012) |

**Total: 114 tests**

## Test Files

- `GeneratorTests.cs` -- Roslyn source generator tests: contract boilerplate, storage fields, entry points, events, ABI, error recovery
- `AnalyzerTests.cs` -- Roslyn analyzer tests: BST001-BST012 diagnostic rules, forbidden APIs, attribute validation, storage type checks, policy enforcement, collection ordering

## Running

```bash
dotnet test tests/Basalt.Sdk.Analyzers.Tests
```
