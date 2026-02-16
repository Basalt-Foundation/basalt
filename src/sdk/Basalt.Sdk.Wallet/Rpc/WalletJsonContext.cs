using System.Text.Json.Serialization;
using Basalt.Sdk.Wallet.Rpc.Models;

namespace Basalt.Sdk.Wallet.Rpc;

/// <summary>
/// JSON request body for submitting a signed transaction to the REST API.
/// </summary>
public sealed class TransactionRequest
{
    /// <summary>
    /// The transaction type ordinal value.
    /// </summary>
    [JsonPropertyName("type")]
    public byte Type { get; set; }

    /// <summary>
    /// The transaction nonce.
    /// </summary>
    [JsonPropertyName("nonce")]
    public ulong Nonce { get; set; }

    /// <summary>
    /// The sender address in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("sender")]
    public string Sender { get; set; } = "";

    /// <summary>
    /// The recipient address in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    /// <summary>
    /// The transfer value as a decimal string (UInt256).
    /// </summary>
    [JsonPropertyName("value")]
    public string Value { get; set; } = "0";

    /// <summary>
    /// The gas limit for this transaction.
    /// </summary>
    [JsonPropertyName("gasLimit")]
    public ulong GasLimit { get; set; }

    /// <summary>
    /// The gas price as a decimal string (UInt256).
    /// </summary>
    [JsonPropertyName("gasPrice")]
    public string GasPrice { get; set; } = "1";

    /// <summary>
    /// The transaction data payload as a hex string, or empty.
    /// </summary>
    [JsonPropertyName("data")]
    public string Data { get; set; } = "";

    /// <summary>
    /// The transaction priority (0 = normal).
    /// </summary>
    [JsonPropertyName("priority")]
    public byte Priority { get; set; }

    /// <summary>
    /// The chain ID this transaction targets.
    /// </summary>
    [JsonPropertyName("chainId")]
    public uint ChainId { get; set; }

    /// <summary>
    /// The Ed25519 signature in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("signature")]
    public string Signature { get; set; } = "";

    /// <summary>
    /// The sender's Ed25519 public key in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("senderPublicKey")]
    public string SenderPublicKey { get; set; } = "";
}

/// <summary>
/// JSON request body for the faucet endpoint.
/// </summary>
public sealed class FaucetRequest
{
    /// <summary>
    /// The recipient address in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";
}

/// <summary>
/// JSON request body for read-only contract calls.
/// </summary>
public sealed class CallReadOnlyRequest
{
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
}

/// <summary>
/// Source-generated JSON serializer context for AOT-safe serialization of all
/// wallet RPC request and response types.
/// </summary>
[JsonSerializable(typeof(NodeStatus))]
[JsonSerializable(typeof(AccountInfo))]
[JsonSerializable(typeof(BlockInfo))]
[JsonSerializable(typeof(TransactionInfo))]
[JsonSerializable(typeof(TransactionInfo[]))]
[JsonSerializable(typeof(TransactionSubmitResult))]
[JsonSerializable(typeof(FaucetResult))]
[JsonSerializable(typeof(ValidatorInfo[]))]
[JsonSerializable(typeof(TransactionRequest))]
[JsonSerializable(typeof(FaucetRequest))]
[JsonSerializable(typeof(CallReadOnlyRequest))]
[JsonSerializable(typeof(CallResult))]
internal partial class WalletJsonContext : JsonSerializerContext;
