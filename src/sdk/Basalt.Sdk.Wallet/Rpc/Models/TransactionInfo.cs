using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Rpc.Models;

/// <summary>
/// Represents detailed transaction information returned by the REST API.
/// </summary>
public sealed class TransactionInfo
{
    /// <summary>
    /// The transaction hash in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    /// <summary>
    /// The transaction type name (e.g. "Transfer", "ContractCall").
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

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
    public string GasPrice { get; set; } = "0";

    /// <summary>
    /// The transaction priority (0 = normal).
    /// </summary>
    [JsonPropertyName("priority")]
    public byte Priority { get; set; }

    /// <summary>
    /// The block number this transaction was included in, or null if pending.
    /// </summary>
    [JsonPropertyName("blockNumber")]
    public ulong? BlockNumber { get; set; }

    /// <summary>
    /// The block hash this transaction was included in, or null if pending.
    /// </summary>
    [JsonPropertyName("blockHash")]
    public string? BlockHash { get; set; }

    /// <summary>
    /// The index of this transaction within the block, or null if pending.
    /// </summary>
    [JsonPropertyName("transactionIndex")]
    public int? TransactionIndex { get; set; }
}
