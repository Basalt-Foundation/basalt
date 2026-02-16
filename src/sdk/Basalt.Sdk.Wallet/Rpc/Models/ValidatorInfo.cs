using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Rpc.Models;

/// <summary>
/// Represents validator information returned by the REST API.
/// </summary>
public sealed class ValidatorInfo
{
    /// <summary>
    /// The validator address in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    /// <summary>
    /// The total stake (self + delegated) as a decimal string (UInt256).
    /// </summary>
    [JsonPropertyName("stake")]
    public string Stake { get; set; } = "0";

    /// <summary>
    /// The validator's own stake as a decimal string (UInt256).
    /// </summary>
    [JsonPropertyName("selfStake")]
    public string SelfStake { get; set; } = "0";

    /// <summary>
    /// The delegated stake to this validator as a decimal string (UInt256).
    /// </summary>
    [JsonPropertyName("delegatedStake")]
    public string DelegatedStake { get; set; } = "0";

    /// <summary>
    /// The validator's Ed25519 public key in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = "";

    /// <summary>
    /// The current validator status (e.g. "active", "unbonding", "slashed").
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}
