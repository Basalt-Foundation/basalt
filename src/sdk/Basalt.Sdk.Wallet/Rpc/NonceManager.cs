using System.Collections.Concurrent;

namespace Basalt.Sdk.Wallet.Rpc;

/// <summary>
/// Tracks and manages transaction nonces for one or more accounts.
/// Fetches the on-chain nonce on first use and then tracks locally,
/// avoiding redundant RPC calls for rapid sequential transactions.
/// </summary>
public sealed class NonceManager
{
    private readonly ConcurrentDictionary<string, ulong> _nonces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the next nonce to use for the given address.
    /// On first call for an address, fetches the current nonce from the chain.
    /// Subsequent calls return the locally tracked (incremented) value.
    /// </summary>
    /// <param name="address">The account address in "0x..." hex format.</param>
    /// <param name="client">The Basalt client used to query the on-chain nonce.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The nonce to use for the next transaction from this address.</returns>
    public async Task<ulong> GetNextNonceAsync(string address, IBasaltClient client, CancellationToken ct = default)
    {
        if (_nonces.TryGetValue(address, out var localNonce))
            return localNonce;

        // Fetch from chain — store the value we return; IncrementNonce will advance it after submission
        var account = await client.GetAccountAsync(address, ct).ConfigureAwait(false);
        var chainNonce = account?.Nonce ?? 0;
        _nonces[address] = chainNonce;
        return chainNonce;
    }

    /// <summary>
    /// Increments the locally tracked nonce for the given address.
    /// Call this after a transaction has been successfully submitted.
    /// </summary>
    /// <param name="address">The account address in "0x..." hex format.</param>
    public void IncrementNonce(string address)
    {
        _nonces.AddOrUpdate(address, 1, static (_, n) => n + 1);
    }

    /// <summary>
    /// Resets the locally tracked nonce for the given address, forcing
    /// the next call to <see cref="GetNextNonceAsync"/> to re-fetch from chain.
    /// </summary>
    /// <param name="address">The account address in "0x..." hex format.</param>
    public void Reset(string address)
    {
        _nonces.TryRemove(address, out _);
    }

    /// <summary>
    /// Resets all locally tracked nonces, forcing a re-fetch from chain
    /// on the next <see cref="GetNextNonceAsync"/> call for any address.
    /// </summary>
    public void ResetAll()
    {
        _nonces.Clear();
    }
}
