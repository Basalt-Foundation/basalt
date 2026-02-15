using Basalt.Core;

namespace Basalt.Execution.VM.Sandbox;

/// <summary>
/// Tracks and enforces memory limits for sandboxed contract execution.
/// Thread-safe: uses <see cref="Interlocked"/> operations so host callbacks
/// originating from different threads are handled correctly.
/// </summary>
public sealed class ResourceLimiter
{
    private readonly long _memoryLimitBytes;
    private long _currentUsage;

    /// <summary>
    /// Create a new ResourceLimiter with the given memory budget.
    /// </summary>
    /// <param name="memoryLimitBytes">Maximum number of bytes the sandbox may allocate.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the limit is not positive.</exception>
    public ResourceLimiter(long memoryLimitBytes)
    {
        if (memoryLimitBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes), "Memory limit must be positive.");

        _memoryLimitBytes = memoryLimitBytes;
    }

    /// <summary>
    /// Current tracked memory usage in bytes.
    /// </summary>
    public long CurrentUsage => Interlocked.Read(ref _currentUsage);

    /// <summary>
    /// The configured memory limit in bytes.
    /// </summary>
    public long MemoryLimitBytes => _memoryLimitBytes;

    /// <summary>
    /// Allocate <paramref name="bytes"/> from the memory budget.
    /// </summary>
    /// <param name="bytes">Number of bytes to allocate.</param>
    /// <exception cref="BasaltException">
    /// Thrown with <see cref="BasaltErrorCode.MemoryLimitExceeded"/> when the allocation
    /// would exceed the configured memory limit.
    /// </exception>
    public void Allocate(long bytes)
    {
        if (bytes <= 0)
            return;

        var newUsage = Interlocked.Add(ref _currentUsage, bytes);

        if (newUsage > _memoryLimitBytes)
        {
            // Roll back the allocation before throwing
            Interlocked.Add(ref _currentUsage, -bytes);
            throw new BasaltException(
                BasaltErrorCode.MemoryLimitExceeded,
                $"Memory limit exceeded: attempted to allocate {bytes} bytes, " +
                $"current usage {newUsage - bytes} bytes, limit {_memoryLimitBytes} bytes.");
        }
    }

    /// <summary>
    /// Free previously allocated bytes, decrementing the tracked usage.
    /// </summary>
    /// <param name="bytes">Number of bytes to free.</param>
    public void Free(long bytes)
    {
        if (bytes <= 0)
            return;

        Interlocked.Add(ref _currentUsage, -bytes);
    }

    /// <summary>
    /// Reset tracked usage to zero. Called when the sandbox is recycled.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _currentUsage, 0);
    }
}
