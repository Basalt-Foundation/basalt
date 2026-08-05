using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that skips (visibly, with a reason) when the native RocksDB library
/// cannot be loaded on the current platform. The RocksDB 8.9.1 NuGet package ships only x64 binaries;
/// its <c>osx-arm64</c>/<c>linux-arm64</c> entries are placeholder stubs. So these integration tests run
/// on x64 CI (where the real <c>.so</c> is present) and report as <b>skipped</b> — never failed, never a
/// silent green pass — on an Apple-Silicon dev host.
/// </summary>
public sealed class RocksDbFactAttribute : FactAttribute
{
    public RocksDbFactAttribute()
    {
        if (!RocksDbNativeProbe.IsAvailable)
            Skip = "RocksDB native library not loadable on this platform " +
                   "(package ships no arm64 binary; runs on x64 CI).";
    }
}

/// <summary>One-time probe for whether the native RocksDB library loads on this process/platform.</summary>
internal static class RocksDbNativeProbe
{
    public static readonly bool IsAvailable = Probe();

    private static bool Probe()
    {
        try
        {
            // Constructing DbOptions forces RocksDbSharp to dlopen the native library — the exact call
            // that throws TypeInitializationException/NativeLoadException when no usable binary exists.
            _ = new RocksDbSharp.DbOptions();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
