using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Basalt.Api.Rest;

/// <summary>
/// Faucet endpoint for distributing testnet tokens.
/// Creates proper signed transactions submitted through the mempool.
/// Rate-limited per address with configurable drip amount and cooldown.
/// </summary>
public static class FaucetEndpoint
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequest = new();

    /// <summary>Drip amount: 100 BSLT (100 * 10^18 base units).</summary>
    public static readonly UInt256 DripAmount = UInt256.Parse("100000000000000000000");

    /// <summary>Cooldown between requests per address in seconds.</summary>
    public static int CooldownSeconds { get; set; } = 60;

    /// <summary>Faucet source address (derived from faucet private key).</summary>
    public static Address FaucetAddress { get; set; } = Address.Zero;

    /// <summary>
    /// Well-known deterministic faucet private key for testnet/devnet.
    /// All validators share this key so any node can serve faucet requests.
    /// </summary>
    private static byte[] _faucetPrivateKey = [];

    /// <summary>
    /// Local nonce tracker to handle rapid sequential requests
    /// before the previous transaction is mined.
    /// </summary>
    private static ulong _pendingNonce;
    private static bool _nonceInitialized;
    private static readonly object _nonceLock = new();

    public static void MapFaucetEndpoint(
        IEndpointRouteBuilder app,
        IStateDatabase stateDb,
        Mempool mempool,
        ChainParameters chainParams,
        byte[] faucetPrivateKey)
    {
        _faucetPrivateKey = faucetPrivateKey;

        // Derive the faucet address from its key
        var faucetPublicKey = Ed25519Signer.GetPublicKey(faucetPrivateKey);
        FaucetAddress = Ed25519Signer.DeriveAddress(faucetPublicKey);

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
            var faucetAccount = stateDb.GetAccount(FaucetAddress);
            var gasCost = chainParams.MinGasPrice * new UInt256(chainParams.TransferGasCost);
            var totalCost = DripAmount + gasCost;
            if (faucetAccount == null || faucetAccount.Value.Balance < totalCost)
                return Results.BadRequest(new FaucetResponse
                {
                    Success = false,
                    Message = "Faucet is empty.",
                });

            // Get the next nonce (track locally for rapid requests)
            ulong nonce;
            lock (_nonceLock)
            {
                var onChainNonce = faucetAccount.Value.Nonce;
                if (!_nonceInitialized || onChainNonce > _pendingNonce)
                {
                    _pendingNonce = onChainNonce;
                    _nonceInitialized = true;
                }
                nonce = _pendingNonce++;
            }

            // Create and sign a proper transaction
            var unsignedTx = new Transaction
            {
                Type = TransactionType.Transfer,
                Nonce = nonce,
                Sender = FaucetAddress,
                To = recipientAddr,
                Value = DripAmount,
                GasLimit = chainParams.TransferGasCost,
                GasPrice = chainParams.MinGasPrice,
                Data = [],
                Priority = 0,
                ChainId = chainParams.ChainId,
            };

            var signedTx = Transaction.Sign(unsignedTx, _faucetPrivateKey);

            // Submit to mempool (will be picked up by consensus and included in a block)
            if (!mempool.Add(signedTx))
                return Results.BadRequest(new FaucetResponse
                {
                    Success = false,
                    Message = "Transaction rejected by mempool.",
                });

            // Record the request time
            _lastRequest[addrKey] = DateTimeOffset.UtcNow;

            return Results.Ok(new FaucetResponse
            {
                Success = true,
                Message = $"Sent 100 BSLT to {request.Address}",
                TxHash = signedTx.Hash.ToHexString(),
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
