namespace Basalt.Sdk.Contracts;

/// <summary>
/// Interface implemented by source-generated contract dispatch code.
/// The Basalt contract generator emits a Dispatch method on partial classes
/// marked with [BasaltContract], routing FNV-1a selectors to typed methods.
/// </summary>
public interface IDispatchable
{
    /// <summary>
    /// Dispatch a contract call by selector.
    /// </summary>
    /// <param name="selector">4-byte FNV-1a method selector (little-endian).</param>
    /// <param name="calldata">BasaltWriter-encoded method arguments.</param>
    /// <returns>BasaltWriter-encoded return value, or empty for void methods.</returns>
    byte[] Dispatch(byte[] selector, byte[] calldata);
}
