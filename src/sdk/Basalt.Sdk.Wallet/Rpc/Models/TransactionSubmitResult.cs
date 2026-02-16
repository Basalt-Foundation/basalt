using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Rpc.Models;

/// <summary>
/// Represents the result of submitting a transaction to the node.
/// </summary>
public sealed class TransactionSubmitResult
{
    /// <summary>
    /// The transaction hash in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    /// <summary>
    /// The status of the submitted transaction (typically "pending").
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}
