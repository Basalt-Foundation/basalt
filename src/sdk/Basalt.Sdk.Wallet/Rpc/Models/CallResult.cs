using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Rpc.Models;

/// <summary>
/// Result of a read-only contract call via POST /v1/call.
/// </summary>
public sealed class CallResult
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("returnData")] public string? ReturnData { get; set; }
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}
