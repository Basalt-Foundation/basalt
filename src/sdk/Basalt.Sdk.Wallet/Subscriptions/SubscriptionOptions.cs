namespace Basalt.Sdk.Wallet.Subscriptions;

/// <summary>
/// Configuration options for a WebSocket block subscription.
/// </summary>
public sealed class SubscriptionOptions
{
    /// <summary>
    /// Whether to automatically reconnect on disconnection. Default: true.
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// Maximum number of reconnection attempts before giving up. Default: 10.
    /// Set to 0 for unlimited retries.
    /// </summary>
    public int MaxRetries { get; set; } = 10;

    /// <summary>
    /// Initial delay in milliseconds before the first reconnection attempt. Default: 1000.
    /// Subsequent retries use exponential backoff.
    /// </summary>
    public int InitialDelayMs { get; set; } = 1000;

    /// <summary>
    /// Maximum delay in milliseconds between reconnection attempts. Default: 30000 (30 seconds).
    /// </summary>
    public int MaxDelayMs { get; set; } = 30_000;

    /// <summary>
    /// Size of the WebSocket receive buffer in bytes. Default: 8192.
    /// </summary>
    public int ReceiveBufferSize { get; set; } = 8192;
}
