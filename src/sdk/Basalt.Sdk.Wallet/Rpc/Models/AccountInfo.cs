using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Rpc.Models;

/// <summary>
/// Represents on-chain account information returned by the REST API.
/// </summary>
public sealed class AccountInfo
{
    /// <summary>
    /// The account address in "0x..." hex format.
    /// </summary>
    [JsonPropertyName("address")]
    public string Address { get; set; } = "";

    /// <summary>
    /// The account balance as a decimal string (UInt256).
    /// </summary>
    [JsonPropertyName("balance")]
    public string Balance { get; set; } = "0";

    /// <summary>
    /// The next expected transaction nonce for this account.
    /// </summary>
    [JsonPropertyName("nonce")]
    public ulong Nonce { get; set; }

    /// <summary>
    /// The account type (e.g. "EOA", "Contract").
    /// </summary>
    [JsonPropertyName("accountType")]
    public string AccountType { get; set; } = "";
}
