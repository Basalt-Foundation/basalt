using System.Text.Json.Serialization;

namespace Basalt.Sdk.Wallet.Rpc.Models;

/// <summary>
/// DEX swap quote information returned by the quote endpoint.
/// </summary>
public sealed class DexQuoteInfo
{
    [JsonPropertyName("poolId")] public ulong PoolId { get; set; }
    [JsonPropertyName("tokenIn")] public string TokenIn { get; set; } = "";
    [JsonPropertyName("tokenOut")] public string TokenOut { get; set; } = "";
    [JsonPropertyName("amountIn")] public string AmountIn { get; set; } = "0";
    [JsonPropertyName("amountOut")] public string AmountOut { get; set; } = "0";
    [JsonPropertyName("effectiveFeeBps")] public uint EffectiveFeeBps { get; set; }
    [JsonPropertyName("priceImpactBps")] public uint PriceImpactBps { get; set; }
    [JsonPropertyName("spotPrice")] public string SpotPrice { get; set; } = "0";
    [JsonPropertyName("twap")] public string Twap { get; set; } = "0";
    [JsonPropertyName("volatilityBps")] public uint VolatilityBps { get; set; }
    [JsonPropertyName("isConcentrated")] public bool IsConcentrated { get; set; }
}
