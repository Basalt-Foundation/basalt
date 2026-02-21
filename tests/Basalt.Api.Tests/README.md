# Basalt.Api.Tests

Unit tests for the Basalt API layer: GraphQL queries, GraphQL mutations, and REST DTO mapping. **52 tests.**

## Test Coverage

| Category | Tests | Description |
|----------|-------|-------------|
| GraphQL Queries | 18 | Query resolvers for blocks, transactions, accounts, validators, chain status, block-by-number, block-by-hash, transaction-by-hash |
| REST DTOs | 11 | DTO mapping and validation for status, block, transaction, account, and validator response types |
| GraphQL Mutations | 7 | Mutation resolvers for sending transactions, deploying contracts, faucet requests |

**Total: 52 tests**

## Test Files

- `GraphQLQueryTests.cs` -- HotChocolate query resolver tests: block, transaction, account, validator, chain queries
- `RestDtoTests.cs` -- REST API data transfer object mapping and serialization
- `GraphQLMutationTests.cs` -- HotChocolate mutation resolver tests: transaction submission, contract deployment, faucet

## Running

```bash
dotnet test tests/Basalt.Api.Tests
```
