using Basalt.Core;

namespace Basalt.Execution.VM.Sandbox;

/// <summary>
/// Thrown when a sandboxed contract execution exceeds the configured wall-clock timeout.
/// </summary>
public sealed class SandboxTimeoutException : BasaltException
{
    public TimeSpan Timeout { get; }

    public SandboxTimeoutException(TimeSpan timeout)
        : base(BasaltErrorCode.CpuTimeLimitExceeded,
               $"Contract execution timed out after {timeout.TotalSeconds:F1}s.")
    {
        Timeout = timeout;
    }
}

/// <summary>
/// Thrown when a sandboxed contract exceeds its memory allocation budget.
/// </summary>
public sealed class SandboxMemoryLimitException : BasaltException
{
    public long LimitBytes { get; }
    public long RequestedBytes { get; }

    public SandboxMemoryLimitException(long limitBytes, long requestedBytes)
        : base(BasaltErrorCode.MemoryLimitExceeded,
               $"Sandbox memory limit of {limitBytes} bytes exceeded (requested {requestedBytes} bytes).")
    {
        LimitBytes = limitBytes;
        RequestedBytes = requestedBytes;
    }
}

/// <summary>
/// Thrown when the sandbox detects an isolation violation, such as an attempt to load
/// a disallowed assembly or access a restricted API.
/// </summary>
public sealed class SandboxIsolationException : BasaltException
{
    public SandboxIsolationException(string message)
        : base(BasaltErrorCode.ContractCallFailed, message) { }
}
