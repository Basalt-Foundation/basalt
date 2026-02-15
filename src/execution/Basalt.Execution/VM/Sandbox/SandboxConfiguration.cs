namespace Basalt.Execution.VM.Sandbox;

/// <summary>
/// Configuration for the AOT contract sandbox environment.
/// Controls resource limits and timeout behavior for isolated contract execution.
/// </summary>
public sealed class SandboxConfiguration
{
    /// <summary>
    /// Maximum wall-clock time a contract execution may run before being terminated.
    /// </summary>
    public TimeSpan ExecutionTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum memory (in bytes) that a single contract invocation may allocate.
    /// Default is 100 MB.
    /// </summary>
    public long MemoryLimitBytes { get; init; } = 100 * 1024 * 1024; // 100 MB

    /// <summary>
    /// Whether to track per-allocation memory usage through the ResourceLimiter.
    /// When disabled, allocations are not metered (useful for trusted system contracts).
    /// </summary>
    public bool EnableMemoryTracking { get; init; } = true;
}
