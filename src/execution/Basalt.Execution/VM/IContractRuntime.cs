using Basalt.Core;

namespace Basalt.Execution.VM;

/// <summary>
/// Interface for contract runtime implementations.
/// Phase 1: In-process managed execution.
/// Phase 2+: AOT-compiled native sandbox or WASM runtime.
/// </summary>
public interface IContractRuntime
{
    /// <summary>
    /// Deploy a contract, returning the ABI metadata.
    /// </summary>
    ContractDeployResult Deploy(byte[] code, byte[] constructorArgs, VmExecutionContext ctx);

    /// <summary>
    /// Execute a contract method.
    /// </summary>
    ContractCallResult Execute(byte[] code, byte[] callData, VmExecutionContext ctx);
}

/// <summary>
/// Result of deploying a contract.
/// </summary>
public sealed class ContractDeployResult
{
    public required bool Success { get; init; }
    public required byte[] Code { get; init; }
    public byte[]? AbiMetadata { get; init; }
    public string? ErrorMessage { get; init; }
    public List<EventLog> Logs { get; init; } = [];
}

/// <summary>
/// Result of calling a contract method.
/// </summary>
public sealed class ContractCallResult
{
    public required bool Success { get; init; }
    public byte[]? ReturnData { get; init; }
    public string? ErrorMessage { get; init; }
    public List<EventLog> Logs { get; init; } = [];
}
