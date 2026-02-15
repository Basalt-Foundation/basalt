using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Basalt.Core;
using Basalt.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Basalt.Api.Rest;

/// <summary>
/// Faucet endpoint for distributing testnet tokens.
/// Rate-limited per address with configurable drip amount and cooldown.
/// </summary>
public static class FaucetEndpoint
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequest = new();

    /// <summary>Drip amount in base units (default: 100 BSLT = 100 * 10^18).</summary>
    public static ulong DripAmount { get; set; } = 100;

    /// <summary>Cooldown between requests per address in seconds.</summary>
    public static int CooldownSeconds { get; set; } = 60;

    /// <summary>Faucet source address.</summary>
    public static Address FaucetAddress { get; set; } = Address.Zero;

    public static void MapFaucetEndpoint(
        IEndpointRouteBuilder app,
        Storage.IStateDatabase stateDb)
    {
        app.MapPost("/v1/faucet", (FaucetRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Address))
                return Results.BadRequest(new FaucetResponse
                {
                    Success = false,
                    Message = "Address is required.",
                });

            if (!Address.TryFromHexString(request.Address, out var recipientAddr))
                return Results.BadRequest(new FaucetResponse
                {
                    Success = false,
                    Message = "Invalid address format.",
                });

            var addrKey = request.Address.ToUpperInvariant();

            // Rate limit check
            if (_lastRequest.TryGetValue(addrKey, out var lastTime))
            {
                var elapsed = DateTimeOffset.UtcNow - lastTime;
                if (elapsed.TotalSeconds < CooldownSeconds)
                {
                    var remaining = CooldownSeconds - (int)elapsed.TotalSeconds;
                    return Results.BadRequest(new FaucetResponse
                    {
                        Success = false,
                        Message = $"Rate limited. Try again in {remaining} seconds.",
                    });
                }
            }

            // Check faucet balance
            var dripValue = (UInt256)DripAmount;
            var faucetAccount = stateDb.GetAccount(FaucetAddress);
            if (faucetAccount == null || faucetAccount.Value.Balance < dripValue)
                return Results.BadRequest(new FaucetResponse
                {
                    Success = false,
                    Message = "Faucet is empty.",
                });

            // Debit faucet
            var faucetState = faucetAccount.Value;
            stateDb.SetAccount(FaucetAddress, new Storage.AccountState
            {
                Balance = faucetState.Balance - dripValue,
                Nonce = faucetState.Nonce + 1,
                StorageRoot = faucetState.StorageRoot,
                CodeHash = faucetState.CodeHash,
                AccountType = faucetState.AccountType,
                ComplianceHash = faucetState.ComplianceHash,
            });

            // Credit recipient
            var recipientAccount = stateDb.GetAccount(recipientAddr);
            var recipientBalance = recipientAccount?.Balance ?? UInt256.Zero;
            var recipientNonce = recipientAccount?.Nonce ?? 0ul;
            stateDb.SetAccount(recipientAddr, new Storage.AccountState
            {
                Balance = recipientBalance + dripValue,
                Nonce = recipientNonce,
            });

            // Record the request time
            _lastRequest[addrKey] = DateTimeOffset.UtcNow;

            return Results.Ok(new FaucetResponse
            {
                Success = true,
                Message = $"Sent {DripAmount} BSLT to {request.Address}",
                TxHash = Hash256.Zero.ToHexString(),
            });
        });
    }
}

public sealed class FaucetRequest
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
}

public sealed class FaucetResponse
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("txHash")] public string? TxHash { get; set; }
}
