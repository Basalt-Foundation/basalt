# Basalt.Sdk.Analyzers.Tests

Unit tests for the Basalt Roslyn analyzers and source generators using the Microsoft.CodeAnalysis.Testing framework. **96 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| Source Generators | 54 | Contract boilerplate generation, storage field detection, entry point generation, event type generation, ABI export, error handling for malformed contracts |
| Analyzers | 42 | BST001-BST008 diagnostic rules: forbidden API usage, missing attributes, invalid storage types, contract structure validation, unsafe operations detection |

**Total: 96 tests**

## Test Files

- `GeneratorTests.cs` -- Roslyn source generator tests: contract boilerplate, storage fields, entry points, events, ABI, error recovery
- `AnalyzerTests.cs` -- Roslyn analyzer tests: BST001-BST008 diagnostic rules, forbidden APIs, attribute validation, storage type checks

## Running

```bash
dotnet test tests/Basalt.Sdk.Analyzers.Tests
```
