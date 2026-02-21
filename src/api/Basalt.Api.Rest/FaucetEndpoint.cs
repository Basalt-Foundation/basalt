using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Basalt.Api.Rest;

/// <summary>
/// Faucet endpoint for distributing testnet tokens.
/// Creates proper signed transactions submitted through the mempool.
/// Rate-limited per address with configurable drip amount and cooldown.
/// </summary>
public static class FaucetEndpoint
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _lastRequest = new();

    /// <summary>Maximum number of tracked rate-limit entries.</summary>
    private const int MaxRateLimitEntries = 100_000;

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

    private static ILogger? _logger;

    public static void MapFaucetEndpoint(
        IEndpointRouteBuilder app,
        IStateDatabase stateDb,
        Mempool mempool,
        ChainParameters chainParams,
        byte[] faucetPrivateKey,
        ILogger? logger = null,
        ChainManager? chainManager = null)
    {
        _faucetPrivateKey = faucetPrivateKey;
        _logger = logger;

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

            // M-1: Normalize address by stripping 0x prefix before uppercasing
            // to prevent rate limit bypass via "0xABC" vs "ABC" toggle
            var normalized = request.Address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? request.Address[2..] : request.Address;
            var addrKey = normalized.ToUpperInvariant();

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

            // HIGH-3: Evict stale entries to prevent unbounded memory growth
            if (_lastRequest.Count > MaxRateLimitEntries)
            {
                var cutoff = DateTimeOffset.UtcNow.AddSeconds(-CooldownSeconds * 2);
                foreach (var kvp in _lastRequest)
                {
                    if (kvp.Value < cutoff)
                        _lastRequest.TryRemove(kvp.Key, out _);
                }
            }

            // Use the higher of MinGasPrice and current base fee
            var baseFee = chainManager?.LatestBlock?.Header.BaseFee ?? UInt256.Zero;
            var gasPrice = chainParams.MinGasPrice > baseFee ? chainParams.MinGasPrice : baseFee;

            // Check faucet balance
            var faucetAccount = stateDb.GetAccount(FaucetAddress);
            var gasCost = gasPrice * new UInt256(chainParams.TransferGasCost);
            var totalCost = DripAmount + gasCost;
            if (faucetAccount == null || faucetAccount.Value.Balance < totalCost)
                return Results.BadRequest(new FaucetResponse
                {
                    Success = false,
                    Message = "Faucet is empty.",
                });

            // HIGH-5: Reserve nonce but only commit after successful mempool.Add()
            ulong nonce;
            lock (_nonceLock)
            {
                var onChainNonce = faucetAccount.Value.Nonce;
                if (!_nonceInitialized || onChainNonce > _pendingNonce)
                {
                    _pendingNonce = onChainNonce;
                    _nonceInitialized = true;
                }
                nonce = _pendingNonce; // Don't increment yet
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
                GasPrice = gasPrice,
                Data = [],
                Priority = 0,
                ChainId = chainParams.ChainId,
            };

            var signedTx = Transaction.Sign(unsignedTx, _faucetPrivateKey);

            _logger?.LogInformation(
                "Faucet tx created: hash={Hash}, nonce={Nonce}, sender={Sender}, to={To}, chainId={ChainId}, mempoolSize={MempoolSize}",
                signedTx.Hash.ToHexString()[..18] + "...", nonce,
                FaucetAddress.ToHexString()[..18] + "...", request.Address[..18] + "...",
                chainParams.ChainId, mempool.Count);

            // Submit to mempool (will be picked up by consensus and included in a block)
            if (!mempool.Add(signedTx))
            {
                _logger?.LogWarning("Faucet tx {Hash} rejected by mempool", signedTx.Hash.ToHexString()[..18] + "...");
                return Results.BadRequest(new FaucetResponse
                {
                    Success = false,
                    Message = "Transaction rejected by mempool.",
                });
            }

            // HIGH-5: Only increment nonce after successful mempool addition
            lock (_nonceLock)
            {
                _pendingNonce = nonce + 1;
            }

            _logger?.LogInformation("Faucet tx {Hash} added to mempool (size={Size})",
                signedTx.Hash.ToHexString()[..18] + "...", mempool.Count);

            // Record the request time
            _lastRequest[addrKey] = DateTimeOffset.UtcNow;

            return Results.Ok(new FaucetResponse
            {
                Success = true,
                Message = $"Sent 100 BSLT to {request.Address}",
                TxHash = signedTx.Hash.ToHexString(),
            });
        });

        // M-8: Status endpoint — expose public-facing faucet state
        app.MapGet("/v1/faucet/status", () =>
        {
            var faucetAccount = stateDb.GetAccount(FaucetAddress);
            var balance = faucetAccount?.Balance ?? UInt256.Zero;
            var onChainNonce = faucetAccount?.Nonce ?? 0UL;
            ulong pendingNonce;
            lock (_nonceLock)
            {
                pendingNonce = _nonceInitialized ? _pendingNonce : onChainNonce;
            }
            return Results.Ok(new FaucetStatusResponse
            {
                FaucetAddress = FaucetAddress.ToHexString(),
                Available = balance > UInt256.Zero,
                Balance = balance.ToString(),
                Nonce = onChainNonce,
                PendingNonce = pendingNonce,
                CooldownSeconds = CooldownSeconds,
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

/// <summary>Public-facing faucet status.</summary>
public sealed class FaucetStatusResponse
{
    [JsonPropertyName("faucetAddress")] public string FaucetAddress { get; set; } = "";
    [JsonPropertyName("available")] public bool Available { get; set; }
    [JsonPropertyName("balance")] public string Balance { get; set; } = "0";
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("pendingNonce")] public ulong PendingNonce { get; set; }
    [JsonPropertyName("cooldownSeconds")] public int CooldownSeconds { get; set; }
}
