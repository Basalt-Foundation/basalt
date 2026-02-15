using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Basalt.Explorer;

public sealed class BasaltApiClient
{
    private readonly HttpClient _http;

    public BasaltApiClient(HttpClient http) => _http = http;

    public async Task<NodeStatusDto?> GetStatusAsync()
    {
        try { return await _http.GetFromJsonAsync("v1/status", ExplorerJsonContext.Default.NodeStatusDto); }
        catch { return null; }
    }

    public async Task<BlockDto?> GetLatestBlockAsync()
    {
        try { return await _http.GetFromJsonAsync("v1/blocks/latest", ExplorerJsonContext.Default.BlockDto); }
        catch { return null; }
    }

    public async Task<BlockDto?> GetBlockAsync(string id)
    {
        try { return await _http.GetFromJsonAsync($"v1/blocks/{id}", ExplorerJsonContext.Default.BlockDto); }
        catch { return null; }
    }

    public async Task<AccountDto?> GetAccountAsync(string address)
    {
        try { return await _http.GetFromJsonAsync($"v1/accounts/{address}", ExplorerJsonContext.Default.AccountDto); }
        catch { return null; }
    }

    public async Task<TransactionDto[]?> GetRecentTransactionsAsync()
    {
        try
        {
            // Fetch transactions from latest blocks
            var latest = await GetLatestBlockAsync();
            if (latest == null || latest.TransactionCount == 0) return [];
            return await _http.GetFromJsonAsync($"v1/blocks/{latest.Number}/transactions", ExplorerJsonContext.Default.TransactionDtoArray);
        }
        catch { return []; }
    }

    public async Task<TransactionDto?> GetTransactionAsync(string hash)
    {
        try { return await _http.GetFromJsonAsync($"v1/transactions/{hash}", ExplorerJsonContext.Default.TransactionDto); }
        catch { return null; }
    }

    public async Task<ValidatorDto[]?> GetValidatorsAsync()
    {
        try { return await _http.GetFromJsonAsync("v1/validators", ExplorerJsonContext.Default.ValidatorDtoArray); }
        catch { return []; }
    }
}

public sealed class NodeStatusDto
{
    [JsonPropertyName("blockHeight")] public ulong BlockHeight { get; set; }
    [JsonPropertyName("latestBlockHash")] public string LatestBlockHash { get; set; } = "";
    [JsonPropertyName("mempoolSize")] public int MempoolSize { get; set; }
    [JsonPropertyName("protocolVersion")] public uint ProtocolVersion { get; set; }
}

public sealed class BlockDto
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

    public string FormattedTime => DateTimeOffset.FromUnixTimeSeconds(Timestamp).ToString("u");
    public double GasPercent => GasLimit > 0 ? (double)GasUsed / GasLimit * 100 : 0;
}

public sealed class AccountDto
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("balance")] public string Balance { get; set; } = "0";
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("accountType")] public string AccountType { get; set; } = "";
}

public sealed class TransactionDto
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("sender")] public string Sender { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "0";
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
    [JsonPropertyName("gasPrice")] public string GasPrice { get; set; } = "0";
    [JsonPropertyName("priority")] public byte Priority { get; set; }
}

public sealed class ValidatorDto
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("stake")] public string Stake { get; set; } = "0";
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
}

[JsonSerializable(typeof(NodeStatusDto))]
[JsonSerializable(typeof(BlockDto))]
[JsonSerializable(typeof(AccountDto))]
[JsonSerializable(typeof(TransactionDto))]
[JsonSerializable(typeof(TransactionDto[]))]
[JsonSerializable(typeof(ValidatorDto))]
[JsonSerializable(typeof(ValidatorDto[]))]
internal partial class ExplorerJsonContext : JsonSerializerContext;
