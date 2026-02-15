namespace Basalt.Integration.Tests;

/// <summary>
/// Helper utilities for integration tests.
/// </summary>
internal static class TestHelper
{
    /// <summary>
    /// Create a deterministic test address from a byte seed.
    /// </summary>
    public static Basalt.Core.Address MakeAddress(byte seed)
    {
        var bytes = new byte[20];
        bytes[19] = seed;
        return new Basalt.Core.Address(bytes);
    }
}
