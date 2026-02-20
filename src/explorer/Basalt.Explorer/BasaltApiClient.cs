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

    public async Task<PoolDto[]?> GetPoolsAsync()
    {
        try { return await _http.GetFromJsonAsync("v1/pools", ExplorerJsonContext.Default.PoolDtoArray); }
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

    public async Task<ContractInfoDto?> GetContractInfoAsync(string address)
    {
        try { return await _http.GetFromJsonAsync($"v1/contracts/{address}", ExplorerJsonContext.Default.ContractInfoDto); }
        catch { return null; }
    }

    public async Task<StorageReadResponseDto?> ReadContractStorageAsync(string address, string key)
    {
        try { return await _http.GetFromJsonAsync($"v1/contracts/{address}/storage?key={Uri.EscapeDataString(key)}", ExplorerJsonContext.Default.StorageReadResponseDto); }
        catch { return null; }
    }

    public async Task<ReceiptDto?> GetReceiptAsync(string hash)
    {
        try { return await _http.GetFromJsonAsync($"v1/receipts/{hash}", ExplorerJsonContext.Default.ReceiptDto); }
        catch { return null; }
    }

    public async Task<FaucetResponseDto?> RequestFaucetAsync(string address)
    {
        try
        {
            var request = new FaucetRequestDto { Address = address };
            var response = await _http.PostAsJsonAsync("v1/faucet", request, ExplorerJsonContext.Default.FaucetRequestDto);
            return await response.Content.ReadFromJsonAsync(ExplorerJsonContext.Default.FaucetResponseDto);
        }
        catch { return null; }
    }

    public async Task<ContractCallResponseDto?> CallContractAsync(ContractCallRequestDto request)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("v1/call", request, ExplorerJsonContext.Default.ContractCallRequestDto);
            return await response.Content.ReadFromJsonAsync(ExplorerJsonContext.Default.ContractCallResponseDto);
        }
        catch { return null; }
    }

    public async Task<string?> GetMetricsRawAsync()
    {
        try { return await _http.GetStringAsync("v1/metrics"); }
        catch
        {
            try { return await _http.GetStringAsync("metrics"); }
            catch { return null; }
        }
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
    [JsonPropertyName("baseFee")] public string BaseFee { get; set; } = "0";

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
    [JsonPropertyName("maxFeePerGas")] public string? MaxFeePerGas { get; set; }
    [JsonPropertyName("maxPriorityFeePerGas")] public string? MaxPriorityFeePerGas { get; set; }
    [JsonPropertyName("gasUsed")] public ulong? GasUsed { get; set; }
    [JsonPropertyName("success")] public bool? Success { get; set; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
    [JsonPropertyName("effectiveGasPrice")] public string? EffectiveGasPrice { get; set; }
    [JsonPropertyName("logs")] public LogDto[]? Logs { get; set; }
    [JsonPropertyName("complianceProofCount")] public int ComplianceProofCount { get; set; }
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

public sealed class PoolDto
{
    [JsonPropertyName("poolId")] public ulong PoolId { get; set; }
    [JsonPropertyName("operator")] public string Operator { get; set; } = "";
    [JsonPropertyName("totalStake")] public string TotalStake { get; set; } = "0";
    [JsonPropertyName("totalRewards")] public string TotalRewards { get; set; } = "0";
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

public sealed class ContractInfoDto
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("codeSize")] public int CodeSize { get; set; }
    [JsonPropertyName("codeHash")] public string CodeHash { get; set; } = "";
    [JsonPropertyName("deployer")] public string? Deployer { get; set; }
    [JsonPropertyName("deployTxHash")] public string? DeployTxHash { get; set; }
    [JsonPropertyName("deployBlockNumber")] public ulong? DeployBlockNumber { get; set; }
}

public sealed class StorageReadResponseDto
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("keyHash")] public string KeyHash { get; set; } = "";
    [JsonPropertyName("found")] public bool Found { get; set; }
    [JsonPropertyName("valueHex")] public string? ValueHex { get; set; }
    [JsonPropertyName("valueUtf8")] public string? ValueUtf8 { get; set; }
    [JsonPropertyName("valueSize")] public int ValueSize { get; set; }
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
}

public sealed class LogDto
{
    [JsonPropertyName("contract")] public string Contract { get; set; } = "";
    [JsonPropertyName("eventSignature")] public string EventSignature { get; set; } = "";
    [JsonPropertyName("topics")] public string[] Topics { get; set; } = [];
    [JsonPropertyName("data")] public string? Data { get; set; }
}

public sealed class ReceiptDto
{
    [JsonPropertyName("transactionHash")] public string TransactionHash { get; set; } = "";
    [JsonPropertyName("blockHash")] public string BlockHash { get; set; } = "";
    [JsonPropertyName("blockNumber")] public ulong BlockNumber { get; set; }
    [JsonPropertyName("transactionIndex")] public int TransactionIndex { get; set; }
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("errorCode")] public string ErrorCode { get; set; } = "";
    [JsonPropertyName("postStateRoot")] public string PostStateRoot { get; set; } = "";
    [JsonPropertyName("effectiveGasPrice")] public string EffectiveGasPrice { get; set; } = "0";
    [JsonPropertyName("logs")] public LogDto[] Logs { get; set; } = [];
}

public sealed class FaucetRequestDto
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
}

public sealed class FaucetResponseDto
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("txHash")] public string? TxHash { get; set; }
}

public sealed class ContractCallRequestDto
{
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; } = 1_000_000;
}

public sealed class ContractCallResponseDto
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("returnData")] public string? ReturnData { get; set; }
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class WebSocketEnvelopeDto
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("block")] public WebSocketBlockEvent? Block { get; set; }
}

public sealed class WebSocketBlockEvent
{
    [JsonPropertyName("number")] public ulong Number { get; set; }
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("proposer")] public string Proposer { get; set; } = "";
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
    [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
}

[JsonSerializable(typeof(WebSocketEnvelopeDto))]
[JsonSerializable(typeof(WebSocketBlockEvent))]
[JsonSerializable(typeof(NodeStatusDto))]
[JsonSerializable(typeof(BlockDto))]
[JsonSerializable(typeof(BlockDto[]))]
[JsonSerializable(typeof(PaginatedBlocksDto))]
[JsonSerializable(typeof(AccountDto))]
[JsonSerializable(typeof(TransactionDto))]
[JsonSerializable(typeof(TransactionDto[]))]
[JsonSerializable(typeof(ValidatorDto))]
[JsonSerializable(typeof(ValidatorDto[]))]
[JsonSerializable(typeof(PoolDto))]
[JsonSerializable(typeof(PoolDto[]))]
[JsonSerializable(typeof(MempoolResponseDto))]
[JsonSerializable(typeof(MempoolTransactionDto))]
[JsonSerializable(typeof(MempoolTransactionDto[]))]
[JsonSerializable(typeof(FaucetStatusDto))]
[JsonSerializable(typeof(ContractInfoDto))]
[JsonSerializable(typeof(StorageReadResponseDto))]
[JsonSerializable(typeof(LogDto))]
[JsonSerializable(typeof(LogDto[]))]
[JsonSerializable(typeof(ReceiptDto))]
[JsonSerializable(typeof(FaucetRequestDto))]
[JsonSerializable(typeof(FaucetResponseDto))]
[JsonSerializable(typeof(ContractCallRequestDto))]
[JsonSerializable(typeof(ContractCallResponseDto))]
internal partial class ExplorerJsonContext : JsonSerializerContext;
