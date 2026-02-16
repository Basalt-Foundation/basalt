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

    public async Task<PaginatedBlocksDto?> GetBlocksPageAsync(int page = 1, int pageSize = 20)
    {
        try { return await _http.GetFromJsonAsync($"v1/blocks?page={page}&pageSize={pageSize}", ExplorerJsonContext.Default.PaginatedBlocksDto); }
        catch { return null; }
    }

    public async Task<AccountDto?> GetAccountAsync(string address)
    {
        try { return await _http.GetFromJsonAsync($"v1/accounts/{address}", ExplorerJsonContext.Default.AccountDto); }
        catch { return null; }
    }

    public async Task<TransactionDto[]?> GetRecentTransactionsAsync()
    {
        try { return await _http.GetFromJsonAsync("v1/transactions/recent?count=50", ExplorerJsonContext.Default.TransactionDtoArray); }
        catch { return []; }
    }

    public async Task<TransactionDto[]?> GetBlockTransactionsAsync(string blockNumber)
    {
        try { return await _http.GetFromJsonAsync($"v1/blocks/{blockNumber}/transactions", ExplorerJsonContext.Default.TransactionDtoArray); }
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

    public async Task<TransactionDto[]?> GetAccountTransactionsAsync(string address, int count = 25)
    {
        try { return await _http.GetFromJsonAsync($"v1/accounts/{address}/transactions?count={count}", ExplorerJsonContext.Default.TransactionDtoArray); }
        catch { return []; }
    }

    public async Task<MempoolResponseDto?> GetMempoolAsync()
    {
        try { return await _http.GetFromJsonAsync("v1/debug/mempool", ExplorerJsonContext.Default.MempoolResponseDto); }
        catch { return null; }
    }

    public async Task<FaucetStatusDto?> GetFaucetStatusAsync()
    {
        try { return await _http.GetFromJsonAsync("v1/faucet/status", ExplorerJsonContext.Default.FaucetStatusDto); }
        catch { return null; }
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

    public string FormattedTime => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).ToString("u");
    public double GasPercent => GasLimit > 0 ? (double)GasUsed / GasLimit * 100 : 0;
}

public sealed class PaginatedBlocksDto
{
    [JsonPropertyName("items")] public BlockDto[] Items { get; set; } = [];
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }
    [JsonPropertyName("totalItems")] public long TotalItems { get; set; }
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
    [JsonPropertyName("blockNumber")] public ulong? BlockNumber { get; set; }
    [JsonPropertyName("blockHash")] public string? BlockHash { get; set; }
    [JsonPropertyName("transactionIndex")] public int? TransactionIndex { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("dataSize")] public int DataSize { get; set; }
}

public sealed class ValidatorDto
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("stake")] public string Stake { get; set; } = "0";
    [JsonPropertyName("selfStake")] public string SelfStake { get; set; } = "0";
    [JsonPropertyName("delegatedStake")] public string DelegatedStake { get; set; } = "0";
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
}

public sealed class MempoolResponseDto
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("transactions")] public MempoolTransactionDto[] Transactions { get; set; } = [];
}

public sealed class MempoolTransactionDto
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("sender")] public string Sender { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "0";
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
    [JsonPropertyName("gasPrice")] public string GasPrice { get; set; } = "0";
    [JsonPropertyName("signatureValid")] public bool SignatureValid { get; set; }
    [JsonPropertyName("validationOk")] public bool ValidationOk { get; set; }
    [JsonPropertyName("validationError")] public string? ValidationError { get; set; }
    [JsonPropertyName("senderExists")] public bool SenderExists { get; set; }
    [JsonPropertyName("senderNonce")] public ulong SenderNonce { get; set; }
    [JsonPropertyName("senderBalance")] public string SenderBalance { get; set; } = "0";
}

public sealed class FaucetStatusDto
{
    [JsonPropertyName("faucetAddress")] public string FaucetAddress { get; set; } = "";
    [JsonPropertyName("balance")] public string Balance { get; set; } = "0";
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("pendingNonce")] public ulong PendingNonce { get; set; }
    [JsonPropertyName("mempoolSize")] public int MempoolSize { get; set; }
}

[JsonSerializable(typeof(NodeStatusDto))]
[JsonSerializable(typeof(BlockDto))]
[JsonSerializable(typeof(BlockDto[]))]
[JsonSerializable(typeof(PaginatedBlocksDto))]
[JsonSerializable(typeof(AccountDto))]
[JsonSerializable(typeof(TransactionDto))]
[JsonSerializable(typeof(TransactionDto[]))]
[JsonSerializable(typeof(ValidatorDto))]
[JsonSerializable(typeof(ValidatorDto[]))]
[JsonSerializable(typeof(MempoolResponseDto))]
[JsonSerializable(typeof(MempoolTransactionDto))]
[JsonSerializable(typeof(MempoolTransactionDto[]))]
[JsonSerializable(typeof(FaucetStatusDto))]
internal partial class ExplorerJsonContext : JsonSerializerContext;
