using Basalt.Core;
using Basalt.Storage;

namespace Basalt.Execution.VM;

/// <summary>
/// Execution context available to contracts during execution.
/// Provides access to blockchain state, caller info, and host functions.
/// </summary>
public sealed class VmExecutionContext
{
    public Address Caller { get; init; }
    public Address ContractAddress { get; init; }
    public UInt256 Value { get; init; }
    public ulong BlockTimestamp { get; init; }
    public ulong BlockNumber { get; init; }
    public Address BlockProposer { get; init; }
    public uint ChainId { get; init; }
    public GasMeter GasMeter { get; init; } = null!;
    public IStateDatabase StateDb { get; init; } = null!;
    public int CallDepth { get; init; }
    public List<EventLog> EmittedLogs { get; } = [];

    /// <summary>
    /// Maximum call depth to prevent stack overflow attacks.
    /// </summary>
    public const int MaxCallDepth = 1024;
}
