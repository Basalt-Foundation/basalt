namespace Basalt.Sdk.Wallet.Rpc;

/// <summary>
/// Configuration options for <see cref="BasaltClient"/>.
/// </summary>
public sealed class BasaltClientOptions
{
    /// <summary>
    /// The base URL of the Basalt node REST API (e.g. "http://localhost:5100").
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// The HTTP request timeout in seconds. Defaults to 30.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;
}
