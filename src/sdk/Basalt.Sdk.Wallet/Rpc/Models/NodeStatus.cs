using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Rpc.Models;

/// <summary>
/// Represents the current status of a Basalt node.
/// </summary>
public sealed class NodeStatus
{
    /// <summary>
    /// The current block height of the chain.
    /// </summary>
    [JsonPropertyName("blockHeight")]
    public ulong BlockHeight { get; set; }

    /// <summary>
    /// The hash of the latest finalized block.
    /// </summary>
    [JsonPropertyName("latestBlockHash")]
    public string LatestBlockHash { get; set; } = "";

    /// <summary>
    /// The number of pending transactions in the mempool.
    /// </summary>
    [JsonPropertyName("mempoolSize")]
    public int MempoolSize { get; set; }

    /// <summary>
    /// The protocol version supported by this node.
    /// </summary>
    [JsonPropertyName("protocolVersion")]
    public uint ProtocolVersion { get; set; }
}
