using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Rpc.Models;

/// <summary>
/// Represents the result of a faucet drip request.
/// </summary>
public sealed class FaucetResult
{
    /// <summary>
    /// Whether the faucet request succeeded.
    /// </summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// A human-readable message describing the result.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    /// <summary>
    /// The transaction hash of the faucet transfer, or null on failure.
    /// </summary>
    [JsonPropertyName("txHash")]
    public string? TxHash { get; set; }
}
