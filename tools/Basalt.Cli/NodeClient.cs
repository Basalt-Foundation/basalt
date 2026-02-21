using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Basalt.Cli;

/// <summary>
/// HTTP client for communicating with a Basalt node's REST API.
/// </summary>
internal sealed class NodeClient : IDisposable
{
    private readonly HttpClient _http;

    public NodeClient(string nodeUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(nodeUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "BasaltCLI/1.0");
    }

    public async Task<NodeStatus?> GetStatusAsync()
    {
        var response = await _http.GetAsync("v1/status");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync(CliJsonContext.Default.NodeStatus);
    }

    public async Task<BlockInfo?> GetLatestBlockAsync()
    {
        var response = await _http.GetAsync("v1/blocks/latest");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync(CliJsonContext.Default.BlockInfo);
    }

    public async Task<BlockInfo?> GetBlockAsync(string id)
    {
        var response = await _http.GetAsync($"v1/blocks/{id}");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync(CliJsonContext.Default.BlockInfo);
    }

    public async Task<AccountInfo?> GetAccountAsync(string address)
    {
        var response = await _http.GetAsync($"v1/accounts/{address}");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync(CliJsonContext.Default.AccountInfo);
    }

    public async Task<TxResult?> SendTransactionAsync(TxRequest tx)
    {
        var response = await _http.PostAsJsonAsync("v1/transactions", tx, CliJsonContext.Default.TxRequest);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var error = JsonSerializer.Deserialize(content, CliJsonContext.Default.ApiError);
            return new TxResult { Hash = "", Status = "error", Error = error?.Message ?? content };
        }
        return JsonSerializer.Deserialize(content, CliJsonContext.Default.TxResult);
    }

    public async Task<FaucetResult?> RequestFaucetAsync(string address)
    {
        var response = await _http.PostAsJsonAsync("v1/faucet", new FaucetRequest { Address = address }, CliJsonContext.Default.FaucetRequest);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            return new FaucetResult { Success = false, Message = error };
        }
        return await response.Content.ReadFromJsonAsync(CliJsonContext.Default.FaucetResult);
    }

    public void Dispose() => _http.Dispose();
}

// API DTOs
internal sealed class NodeStatus
{
    [JsonPropertyName("blockHeight")] public ulong BlockHeight { get; set; }
    [JsonPropertyName("latestBlockHash")] public string LatestBlockHash { get; set; } = "";
    [JsonPropertyName("mempoolSize")] public int MempoolSize { get; set; }
    [JsonPropertyName("protocolVersion")] public uint ProtocolVersion { get; set; }
}

internal sealed class BlockInfo
{
    [JsonPropertyName("number")] public ulong Number { get; set; }
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("parentHash")] public string ParentHash { get; set; } = "";
    [JsonPropertyName("stateRoot")] public string StateRoot { get; set; } = "";
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("proposer")] public string Proposer { get; set; } = "";
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
    [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
}

internal sealed class AccountInfo
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("balance")] public string Balance { get; set; } = "0";
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("accountType")] public string AccountType { get; set; } = "";
}

internal sealed class TxRequest
{
    [JsonPropertyName("type")] public byte Type { get; set; }
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("sender")] public string Sender { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "0";
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; } = 21000;
    [JsonPropertyName("gasPrice")] public string GasPrice { get; set; } = "1";
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("priority")] public byte Priority { get; set; }
    [JsonPropertyName("chainId")] public uint ChainId { get; set; } = 1;
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("senderPublicKey")] public string SenderPublicKey { get; set; } = "";
}

internal sealed class TxResult
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    public string? Error { get; set; }
}

internal sealed class FaucetRequest
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
}

internal sealed class FaucetResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("txHash")] public string? TxHash { get; set; }
}

internal sealed class ApiError
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

[JsonSerializable(typeof(NodeStatus))]
[JsonSerializable(typeof(BlockInfo))]
[JsonSerializable(typeof(AccountInfo))]
[JsonSerializable(typeof(TxRequest))]
[JsonSerializable(typeof(TxResult))]
[JsonSerializable(typeof(FaucetRequest))]
[JsonSerializable(typeof(FaucetResult))]
[JsonSerializable(typeof(ApiError))]
internal partial class CliJsonContext : JsonSerializerContext;
