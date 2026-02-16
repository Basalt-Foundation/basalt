using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basalt.Execution;
using Basalt.Sdk.Wallet.Rpc.Models;

namespace Basalt.Sdk.Wallet.Rpc;

/// <summary>
/// HttpClient-based implementation of <see cref="IBasaltClient"/> for communicating
/// with a Basalt node via its REST API.
/// </summary>
public sealed class BasaltClient : IBasaltClient
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Creates a new <see cref="BasaltClient"/> with the specified options.
    /// </summary>
    /// <param name="options">Client configuration options.</param>
    public BasaltClient(BasaltClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _http = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Creates a new <see cref="BasaltClient"/> with the specified base URL
    /// and default timeout of 30 seconds.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Basalt node REST API.</param>
    public BasaltClient(string baseUrl)
        : this(new BasaltClientOptions { BaseUrl = baseUrl })
    {
    }

    /// <summary>
    /// Creates a new <see cref="BasaltClient"/> using an externally managed <see cref="HttpClient"/>.
    /// The caller is responsible for disposing the HttpClient.
    /// </summary>
    /// <param name="httpClient">An externally managed HttpClient instance.</param>
    public BasaltClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
        _ownsHttpClient = false;
    }

    /// <inheritdoc />
    public async Task<NodeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/v1/status", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.NodeStatus, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Failed to deserialize node status response.");
    }

    /// <inheritdoc />
    public async Task<AccountInfo?> GetAccountAsync(string address, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/v1/accounts/{Uri.EscapeDataString(address)}", ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.AccountInfo, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/v1/blocks/latest", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.BlockInfo, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Failed to deserialize latest block response.");
    }

    /// <inheritdoc />
    public async Task<BlockInfo?> GetBlockAsync(string blockId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/v1/blocks/{Uri.EscapeDataString(blockId)}", ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.BlockInfo, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TransactionInfo?> GetTransactionAsync(string hash, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/v1/transactions/{Uri.EscapeDataString(hash)}", ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.TransactionInfo, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TransactionInfo[]> GetRecentTransactionsAsync(int count = 50, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"/v1/transactions/recent?count={count}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.TransactionInfoArray, ct).ConfigureAwait(false) ?? [];
    }

    /// <inheritdoc />
    public async Task<TransactionSubmitResult> SendTransactionAsync(Transaction signedTx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signedTx);

        var request = new TransactionRequest
        {
            Type = (byte)signedTx.Type,
            Nonce = signedTx.Nonce,
            Sender = signedTx.Sender.ToHexString(),
            To = signedTx.To.ToHexString(),
            Value = signedTx.Value.ToString(),
            GasLimit = signedTx.GasLimit,
            GasPrice = signedTx.GasPrice.ToString(),
            Data = signedTx.Data.Length > 0
                ? "0x" + Convert.ToHexString(signedTx.Data).ToLowerInvariant()
                : "",
            Priority = signedTx.Priority,
            ChainId = signedTx.ChainId,
            Signature = signedTx.Signature.ToString(),
            SenderPublicKey = signedTx.SenderPublicKey.ToString(),
        };

        var response = await _http.PostAsJsonAsync("/v1/transactions", request, WalletJsonContext.Default.TransactionRequest, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.TransactionSubmitResult, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Failed to deserialize transaction submit response.");
    }

    /// <inheritdoc />
    public async Task<FaucetResult> RequestFaucetAsync(string address, CancellationToken ct = default)
    {
        var request = new FaucetRequest { Address = address };
        var response = await _http.PostAsJsonAsync("/v1/faucet", request, WalletJsonContext.Default.FaucetRequest, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.FaucetResult, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Failed to deserialize faucet response.");
    }

    /// <inheritdoc />
    public async Task<ValidatorInfo[]> GetValidatorsAsync(CancellationToken ct = default)
    {
        var response = await _http.GetAsync("/v1/validators", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.ValidatorInfoArray, ct).ConfigureAwait(false) ?? [];
    }

    /// <inheritdoc />
    public async Task<CallResult> CallReadOnlyAsync(string to, string data, string? from = null, ulong gasLimit = 1_000_000, CancellationToken ct = default)
    {
        var request = new CallReadOnlyRequest
        {
            To = to,
            Data = data,
            From = from,
            GasLimit = gasLimit,
        };

        var response = await _http.PostAsJsonAsync("/v1/call", request, WalletJsonContext.Default.CallReadOnlyRequest, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, WalletJsonContext.Default.CallResult, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Failed to deserialize call response.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }
}
