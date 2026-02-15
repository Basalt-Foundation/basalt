# Basalt.Sdk.Testing

Testing framework for Basalt smart contracts. Provides an in-process blockchain emulator that lets you deploy, call, and test contracts without running a full node.

## Usage

```csharp
using Basalt.Sdk.Testing;
using Xunit;

public class MyTokenTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly MyToken _token = new();

    [Fact]
    public void Transfer_Updates_Balances()
    {
        var deployer = BasaltTestHost.CreateAddress(1);
        var recipient = BasaltTestHost.CreateAddress(2);

        _host.SetCaller(deployer);
        _host.Call(() => _token.Initialize(1_000_000));

        _host.Call(() => _token.Transfer(recipient, 500));

        var balance = _host.Call(() => _token.BalanceOf(recipient));
        Assert.Equal(500ul, balance);
    }

    [Fact]
    public void Transfer_Insufficient_Balance_Reverts()
    {
        var user = BasaltTestHost.CreateAddress(1);
        _host.SetCaller(user);

        string? error = _host.ExpectRevert(() => _token.Transfer(user, 999));
        Assert.Contains("Insufficient", error);
    }

    public void Dispose() => _host.Dispose();
}
```

## BasaltTestHost API

### Setup

```csharp
var host = new BasaltTestHost();
host.SetCaller(address);              // Set msg.sender
host.SetBlockTimestamp(1700000000);    // Set block timestamp
host.SetBlockHeight(100);             // Set block number
host.AdvanceBlocks(10);               // Advance by N blocks (400ms per block)
host.Deploy(contractAddress, contract); // Deploy a contract for cross-contract calls
```

`AdvanceBlocks(count)` increments the block height by `count` and advances the timestamp by `count * 400` (400ms per block).

Cross-contract calls are automatically wired: `BasaltTestHost` sets `Context.CrossContractCallHandler` to a reflection-based dispatcher that routes calls to contracts registered via `Deploy`. The handler looks up the target contract by address and invokes the named method via reflection.

### Execution

```csharp
host.Call(() => contract.Method());           // Execute state-mutating call
T result = host.Call(() => contract.View());  // Execute view call
string? err = host.ExpectRevert(() => ...);   // Expect revert
```

### Snapshots

```csharp
host.TakeSnapshot();     // Save current state
// ... make changes ...
host.RestoreSnapshot();  // Roll back to snapshot
```

### Events

```csharp
host.Call(() => contract.DoSomething());
var events = host.GetEvents<TransferEvent>();       // Get events of a specific type
var all = host.EmittedEvents;                       // All emitted events as IReadOnlyList<(string EventName, object Event)>
host.ClearEvents();
```

### Address Helpers

```csharp
byte[] addr1 = BasaltTestHost.CreateAddress(1);       // From seed byte (20 bytes, seed in last byte)
byte[] addr2 = BasaltTestHost.CreateAddress("0A");     // From hex string (left-padded to 40 hex chars)
byte[] addr3 = BasaltTestHost.CreateAddress("0x1234"); // "0x" prefix is stripped automatically
```

Note: `CreateAddress(string hex)` expects hex-encoded strings, not arbitrary names. The input is left-padded with zeros to 40 hex characters (20 bytes) and converted via `Convert.FromHexString`.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Hash256, Address, UInt256 |
| `Basalt.Execution` | TransactionExecutor, BlockHeader |
| `Basalt.Storage` | InMemoryStateDb |
| `Basalt.Sdk.Contracts` | Contract attributes, Context, storage |
