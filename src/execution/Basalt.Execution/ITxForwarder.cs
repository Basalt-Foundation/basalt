namespace Basalt.Execution;

/// <summary>
/// Forwards transactions to an upstream node (e.g., from RPC to validator).
/// </summary>
public interface ITxForwarder
{
    Task ForwardAsync(Transaction tx, CancellationToken ct);
}

/// <summary>
/// Mutable reference to an <see cref="ITxForwarder"/>. Allows the RPC mode branch in
/// Program.cs to set the forwarder after endpoint registration, since
/// MapBasaltEndpoints is called before mode detection.
/// </summary>
public sealed class TxForwarderRef : ITxForwarder
{
    private volatile ITxForwarder? _inner;

    public void Set(ITxForwarder forwarder) => _inner = forwarder;

    public Task ForwardAsync(Transaction tx, CancellationToken ct)
        => _inner?.ForwardAsync(tx, ct) ?? Task.CompletedTask;
}
