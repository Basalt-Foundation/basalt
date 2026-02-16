using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Subscriptions;

/// <summary>
/// Represents a block event received via WebSocket subscription.
/// </summary>
public sealed class BlockEvent
{
    /// <summary>
    /// The event type: "current_block" (initial) or "new_block" (ongoing).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    /// <summary>
    /// The block data.
    /// </summary>
    [JsonPropertyName("block")]
    public BlockEventData Block { get; set; } = new();
}

/// <summary>
/// Block data payload within a WebSocket block event.
/// </summary>
public sealed class BlockEventData
{
    /// <summary>Block number.</summary>
    [JsonPropertyName("number")]
    public ulong Number { get; set; }

    /// <summary>Block hash in hex format.</summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    /// <summary>Parent block hash in hex format.</summary>
    [JsonPropertyName("parentHash")]
    public string ParentHash { get; set; } = "";

    /// <summary>State root hash in hex format.</summary>
    [JsonPropertyName("stateRoot")]
    public string StateRoot { get; set; } = "";

    /// <summary>Block timestamp (Unix milliseconds).</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>Proposer address in hex format.</summary>
    [JsonPropertyName("proposer")]
    public string Proposer { get; set; } = "";

    /// <summary>Total gas used by all transactions in this block.</summary>
    [JsonPropertyName("gasUsed")]
    public ulong GasUsed { get; set; }

    /// <summary>Gas limit for this block.</summary>
    [JsonPropertyName("gasLimit")]
    public ulong GasLimit { get; set; }

    /// <summary>Number of transactions in this block.</summary>
    [JsonPropertyName("transactionCount")]
    public int TransactionCount { get; set; }
}
