using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Rpc.Models;

/// <summary>
/// Represents a block returned by the REST API.
/// </summary>
public sealed class BlockInfo
{
    /// <summary>
    /// The block number (height).
    /// </summary>
    [JsonPropertyName("number")]
    public ulong Number { get; set; }

    /// <summary>
    /// The block hash in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    /// <summary>
    /// The parent block hash in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("parentHash")]
    public string ParentHash { get; set; } = "";

    /// <summary>
    /// The state root hash after applying this block.
    /// </summary>
    [JsonPropertyName("stateRoot")]
    public string StateRoot { get; set; } = "";

    /// <summary>
    /// The block timestamp as a Unix epoch value in milliseconds.
    /// </summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>
    /// The address of the block proposer (validator) in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("proposer")]
    public string Proposer { get; set; } = "";

    /// <summary>
    /// The total gas used by all transactions in this block.
    /// </summary>
    [JsonPropertyName("gasUsed")]
    public ulong GasUsed { get; set; }

    /// <summary>
    /// The gas limit for this block.
    /// </summary>
    [JsonPropertyName("gasLimit")]
    public ulong GasLimit { get; set; }

    /// <summary>
    /// The EIP-1559 base fee for this block as a decimal string (UInt256).
    /// </summary>
    [JsonPropertyName("baseFee")]
    public string BaseFee { get; set; } = "0";

    /// <summary>
    /// The number of transactions included in this block.
    /// </summary>
    [JsonPropertyName("transactionCount")]
    public int TransactionCount { get; set; }
}
