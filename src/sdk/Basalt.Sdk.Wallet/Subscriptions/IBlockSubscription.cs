namespace Basalt.Sdk.Wallet.Subscriptions;

/// <summary>
/// Interface for subscribing to new block events from a Basalt node via WebSocket.
/// </summary>
public interface IBlockSubscription : IAsyncDisposable
{
    /// <summary>
    /// Subscribes to block events as an async enumerable stream.
    /// The first event is typically a "current_block" with the latest block,
    /// followed by "new_block" events as new blocks are finalized.
    /// </summary>
    /// <param name="ct">Cancellation token to stop the subscription.</param>
    /// <returns>An async enumerable of block events.</returns>
    IAsyncEnumerable<BlockEvent> SubscribeAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets whether the subscription is currently connected.
    /// </summary>
    bool IsConnected { get; }
}
