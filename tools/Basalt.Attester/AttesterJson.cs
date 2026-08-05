using System.Text.Json.Serialization;

namespace Basalt.Attester;

/// <summary>The transaction shape the node's REST surface accepts, matching the CLI's own request type.</summary>
internal sealed class TxRequest
{
    [JsonPropertyName("type")] public byte Type { get; set; }
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("sender")] public string Sender { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "0";
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
    [JsonPropertyName("gasPrice")] public string GasPrice { get; set; } = "1";
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("priority")] public byte Priority { get; set; }
    [JsonPropertyName("chainId")] public uint ChainId { get; set; }
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("senderPublicKey")] public string SenderPublicKey { get; set; } = "";
}

internal sealed class AccountInfo
{
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
}

internal sealed class NodeStatus
{
    [JsonPropertyName("blockHeight")] public ulong BlockHeight { get; set; }
}

internal sealed class BlockInfo
{
    [JsonPropertyName("baseFee")] public string BaseFee { get; set; } = "0";
}

// Source generated rather than reflective, because the repository treats trim and AOT analysis as build
// errors and because an attester is a binary that operators should be able to publish self contained.
[JsonSerializable(typeof(TxRequest))]
[JsonSerializable(typeof(AccountInfo))]
[JsonSerializable(typeof(NodeStatus))]
[JsonSerializable(typeof(BlockInfo))]
internal partial class AttesterJsonContext : JsonSerializerContext;
